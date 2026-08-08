using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.PauseJob;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// 一時停止・再開を、実エンジン + 実ストア + 針を進める時計で通しで確かめる。
/// </summary>
/// <remarks>
/// ここで固定するのは分担の全体 ── ハンドラは境界で観測して抜けるだけ、
/// 受理（Paused）を書くのはエンジン、再開は Resumed へ戻して既存のディスパッチに乗る、
/// 続きはサブタスクの行が支える ── が実際に噛み合うこと。
/// 個々の部品の縁は PauseResumeHandlerTests と SubTaskJobHandlerTests が持つ。
/// </remarks>
public sealed class PauseResumeExecutionTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly JobId Job1 = JobId.From("job-1");

    private readonly TemporaryJobStore _jobs = new();
    private readonly TemporarySubTaskStore _subTasks = new();
    private readonly ManualTimeProvider _time = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly JobExecutionInstrumentation _instrumentation;
    private readonly PauseJobHandler _pause;
    private readonly ResumeJobHandler _resume;
    private readonly CancelJobHandler _cancel;

    public PauseResumeExecutionTests()
    {
        _instrumentation = new JobExecutionInstrumentation(
            _meterFactory, _jobs, _time, new NullJobTraceContextStore(),
            NullLogger<JobExecutionInstrumentation>.Instance);
        _pause = new PauseJobHandler(_jobs, _time, NullLogger<PauseJobHandler>.Instance);
        _resume = new ResumeJobHandler(_jobs, _time, NullLogger<ResumeJobHandler>.Instance);
        _cancel = new CancelJobHandler(_jobs, _time, NullLogger<CancelJobHandler>.Instance);
    }

    public void Dispose()
    {
        _instrumentation.Dispose();
        _meterFactory.Dispose();
        _subTasks.Dispose();
        _jobs.Dispose();
    }

    /// <summary>
    /// 停止 → 再開の一巡。境界で Paused になり、再開すると済んだサブタスクを
    /// やり直さずに続きから完走する。
    /// </summary>
    [Fact]
    public async Task 境界で一時停止し再開すると続きから完走する()
    {
        await RegisterAsync("3 1");
        JobExecutionEngine engine = await StartEngineAsync(_jobs);

        // 1 つ目のサブタスクが走り出したところで停止を要求する。
        Task<bool> first = engine.RunOnceAsync(CancellationToken.None);
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        Assert.True(((await _pause.HandleAsync("job-1", CancellationToken.None)).Value).IsSuccess);

        // 1 秒進めると 1 つ目が完了し、境界で Pausing が観測されて Paused になる。
        _time.Advance(SubTaskJobHandler.Step);
        Assert.True(await first.WaitAsync(WaitLimit));

        Job paused = await FindAsync();
        Assert.Equal(JobStatus.Paused, paused.Status);
        Assert.NotNull(paused.StartedAt);
        Assert.Equal(
            [SubTaskStatus.Completed, SubTaskStatus.Pending, SubTaskStatus.Pending],
            await SubTaskStatusesAsync());

        // 停止中は待ち行列に居ないので、エンジンは拾わない。
        Assert.False(await engine.RunOnceAsync(CancellationToken.None).WaitAsync(WaitLimit));

        // 再開すると Resumed へ戻り、既存のディスパッチがそのまま拾う。
        Assert.True(((await _resume.HandleAsync("job-1", CancellationToken.None)).Value).IsSuccess);

        Task<bool> second = engine.RunOnceAsync(CancellationToken.None);

        // 済んだ 1 つ目はやり直さない。残り 2 つ × 1 秒だけ進めば完走する。
        await _time.WaitForTimersAsync(2).WaitAsync(WaitLimit);
        _time.Advance(SubTaskJobHandler.Step);
        await _time.WaitForTimersAsync(3).WaitAsync(WaitLimit);
        _time.Advance(SubTaskJobHandler.Step);
        Assert.True(await second.WaitAsync(WaitLimit));

        Job completed = await FindAsync();
        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(
            [SubTaskStatus.Completed, SubTaskStatus.Completed, SubTaskStatus.Completed],
            await SubTaskStatusesAsync());
    }

    /// <summary>
    /// ハンドラが境界で抜けた後、エンジンが Paused を書く前に再開が割り込む競合。
    /// エンジンは抜けずに走り直し、Job は何事も無かったように完走する。
    /// </summary>
    [Fact]
    public async Task 受理より先に再開が届いたらエンジンは走り直して完走する()
    {
        await RegisterAsync("2 1");

        InterferingJobStore interfering = new(_jobs);
        JobExecutionEngine engine = await StartEngineAsync(interfering);

        Task<bool> execution = engine.RunOnceAsync(CancellationToken.None);
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        Assert.True(((await _pause.HandleAsync("job-1", CancellationToken.None)).Value).IsSuccess);

        // エンジンの次の Job 書き込みは Paused の受理。その直前に再開を割り込ませる。
        // ハンドラは境界で Pausing を見て抜けた後なので、受理と再開が正面衝突する。
        interfering.BeforeNextUpdate = async () =>
            Assert.True(((await _resume.HandleAsync("job-1", CancellationToken.None)).Value).IsSuccess);

        // 1 秒目で 1 つ目が完了 → 境界で Pausing 観測 → 受理が再開に負けて走り直し
        // → 2 つ目が始まる。走り直しの証拠は「Paused を経ずに完走する」こと。
        _time.Advance(SubTaskJobHandler.Step);
        await _time.WaitForTimersAsync(2).WaitAsync(WaitLimit);
        _time.Advance(SubTaskJobHandler.Step);
        Assert.True(await execution.WaitAsync(WaitLimit));

        Assert.Equal(JobStatus.Completed, (await FindAsync()).Status);
        Assert.Equal(
            [SubTaskStatus.Completed, SubTaskStatus.Completed],
            await SubTaskStatusesAsync());
    }

    /// <summary>
    /// ハンドラが境界で抜けた後、エンジンが Paused を書く前にキャンセルが割り込む競合。
    /// ハンドラはもう居ないので、エンジンが受理をキャンセルの確定に変換して閉じる。
    /// </summary>
    [Fact]
    public async Task 受理の窓でキャンセルが届いたらエンジンがCancelledで閉じる()
    {
        await RegisterAsync("3 1");

        InterferingJobStore interfering = new(_jobs);
        JobExecutionEngine engine = await StartEngineAsync(interfering);

        Task<bool> execution = engine.RunOnceAsync(CancellationToken.None);
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        Assert.True(((await _pause.HandleAsync("job-1", CancellationToken.None)).Value).IsSuccess);

        interfering.BeforeNextUpdate = async () =>
            Assert.True(((await _cancel.HandleAsync("job-1", CancellationToken.None)).Value).IsSuccess);

        _time.Advance(SubTaskJobHandler.Step);
        Assert.True(await execution.WaitAsync(WaitLimit));

        // Cancelling のまま残さない。決着を書けるのはエンジンだけ（ハンドラは抜けた後）。
        Assert.Equal(JobStatus.Cancelled, (await FindAsync()).Status);

        // 残ったサブタスクは畳まれず、中断点の記録として残る（Paused からのキャンセルと同じ）。
        Assert.Equal(
            [SubTaskStatus.Completed, SubTaskStatus.Pending, SubTaskStatus.Pending],
            await SubTaskStatusesAsync());
    }

    /// <summary>
    /// 受理の窓で他所が終端まで書き切っていたら、エンジンは何も記録せずその終端を受け入れる。
    /// </summary>
    /// <remarks>
    /// 起動時復旧が Failed で閉じた後、といった筋。ここを「走り直す」に間違えると、
    /// 終端に達した Job のハンドラをもう一度起動してしまう
    /// （**変異検査でこの分岐が素通りしていた**ので固定する）。
    /// </remarks>
    [Fact]
    public async Task 受理の窓で終端まで書かれていたらエンジンは走り直さない()
    {
        await RegisterAsync("3 1");

        InterferingJobStore interfering = new(_jobs);
        JobExecutionEngine engine = await StartEngineAsync(interfering);

        Task<bool> execution = engine.RunOnceAsync(CancellationToken.None);
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        Assert.True(((await _pause.HandleAsync("job-1", CancellationToken.None)).Value).IsSuccess);

        // 受理を書き戻す直前に、他所が Cancelling を経て終端まで進めてしまう。
        interfering.BeforeNextUpdate = async () =>
        {
            await ApplyAsync(JobTrigger.RequestCancel);
            await ApplyAsync(JobTrigger.ConfirmCancelled);
        };

        _time.Advance(SubTaskJobHandler.Step);
        Assert.True(await execution.WaitAsync(WaitLimit));

        // 終端はそのまま。Paused で上書きもしないし、ハンドラを起動し直しもしない。
        Assert.Equal(JobStatus.Cancelled, (await FindAsync()).Status);
        Assert.Equal(
            [SubTaskStatus.Completed, SubTaskStatus.Pending, SubTaskStatus.Pending],
            await SubTaskStatusesAsync());
    }

    private async Task ApplyAsync(JobTrigger trigger)
    {
        Job job = await FindAsync();
        Assert.True(job.Apply(trigger, Now).IsAllowed);
        Assert.True(await _jobs.UpdateAsync(job, CancellationToken.None));
    }

    private async Task RegisterAsync(string parameters)
    {
        Job job = Job.Create(Job1, "停止試験", SubTaskJobHandler.SubTaskJobType, parameters, Now);
        await _jobs.AddAsync(job, CancellationToken.None);
    }

    private async Task<JobExecutionEngine> StartEngineAsync(IJobStore engineStore) =>
        await JobExecutionEngine.StartAsync(
            engineStore,
            new JobHandlerRegistry([new SubTaskJobHandler(_subTasks, _jobs, _time)]),
            new JobQueueSignal(),
            _time,
            _instrumentation,
            NullLogger<JobExecutionEngine>.Instance,
            CancellationToken.None);

    private async Task<Job> FindAsync() =>
        await _jobs.FindAsync(Job1, CancellationToken.None)
        ?? throw new InvalidOperationException("Job が保存されていません。");

    private async Task<IReadOnlyList<SubTaskStatus>> SubTaskStatusesAsync() =>
        [.. (await _subTasks.ListByJobAsync(Job1, CancellationToken.None)).Select(subTask => subTask.Status)];
}
