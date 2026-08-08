using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.EditJob;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// 実行中の編集を、実エンジン + 実ストア + 針を進める時計で通しで確かめる。
/// </summary>
/// <remarks>
/// 固定するのは分担 ── 編集は Job 行の parameters を書くだけ、行の突き合わせ
/// （増やす・未着手を消す・m の採り直し）はサブタスクの境界が行う ── と、
/// 「N ≥ 着手済み」の本当の守りが境界の切り上げにあること。
/// </remarks>
public sealed class EditExecutionTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly JobId Job1 = JobId.From("job-1");

    private readonly TemporaryJobStore _jobs = new();
    private readonly TemporarySubTaskStore _subTasks = new();
    private readonly ManualTimeProvider _time = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly JobExecutionInstrumentation _instrumentation;
    private readonly EditJobHandler _edit;

    public EditExecutionTests()
    {
        _instrumentation = new JobExecutionInstrumentation(
            _meterFactory, _jobs, _time, new NullJobTraceContextStore(),
            NullLogger<JobExecutionInstrumentation>.Instance);
        _edit = new EditJobHandler(_jobs, _subTasks, TimeProvider.System, NullLogger<EditJobHandler>.Instance);
    }

    public void Dispose()
    {
        _instrumentation.Dispose();
        _meterFactory.Dispose();
        _subTasks.Dispose();
        _jobs.Dispose();
    }

    /// <summary>
    /// 実行中に N を増やすと、境界で行が足されてそのまま走り切る。
    /// 完走後も編集した parameters が残っている（エンジンの結末書き込みは読み直してから
    /// 書くので、手元の古い Job で編集を巻き戻さない）。
    /// </summary>
    [Fact]
    public async Task 実行中にNを増やすと境界で行が足されて走り切る()
    {
        await RegisterAsync("1 1");
        JobExecutionEngine engine = await StartEngineAsync();

        Task<bool> execution = engine.RunOnceAsync(CancellationToken.None);
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);

        // 1 つ目のサブタスクの実行中に 2 個へ増やす。
        Assert.True(((await _edit.HandleAsync("job-1", "2 1", CancellationToken.None)).Value).IsSuccess);

        // 1 秒で 1 つ目が完了 → 境界で突き合わせ → 2 つ目の行が生まれて走る。
        _time.Advance(SubTaskJobHandler.Step);
        await _time.WaitForTimersAsync(2).WaitAsync(WaitLimit);
        _time.Advance(SubTaskJobHandler.Step);
        Assert.True(await execution.WaitAsync(WaitLimit));

        Assert.Equal(JobStatus.Completed, (await FindAsync()).Status);
        Assert.Equal(
            [SubTaskStatus.Completed, SubTaskStatus.Completed],
            await SubTaskStatusesAsync());

        // 編集がエンジンの結末書き込みに巻き戻されていないこと。
        Assert.Equal("2 1", (await FindAsync()).Parameters);
    }

    /// <summary>
    /// 実行中に N を減らすと、境界で未着手の行が消えて早く終わる。
    /// </summary>
    [Fact]
    public async Task 実行中にNを減らすと未着手の行が消えて早く終わる()
    {
        await RegisterAsync("4 1");
        JobExecutionEngine engine = await StartEngineAsync();

        Task<bool> execution = engine.RunOnceAsync(CancellationToken.None);
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);

        // 1 つ目が実行中（着手済み 1）。2 個へ減らすのは N ≥ 着手済みなので通る。
        Assert.True(((await _edit.HandleAsync("job-1", "2 1", CancellationToken.None)).Value).IsSuccess);

        _time.Advance(SubTaskJobHandler.Step);
        await _time.WaitForTimersAsync(2).WaitAsync(WaitLimit);
        _time.Advance(SubTaskJobHandler.Step);
        Assert.True(await execution.WaitAsync(WaitLimit));

        // 4 行あったものが 2 行になり、両方完了。消えた 2 行は未着手だったもの。
        Assert.Equal(JobStatus.Completed, (await FindAsync()).Status);
        Assert.Equal(
            [SubTaskStatus.Completed, SubTaskStatus.Completed],
            await SubTaskStatusesAsync());
    }

    /// <summary>
    /// m の変更は次のサブタスクから効き、走っている最中のサブタスクの残り秒数は変えない。
    /// </summary>
    [Fact]
    public async Task mの変更は次のサブタスクから効く()
    {
        await RegisterAsync("2 3");
        JobExecutionEngine engine = await StartEngineAsync();

        Task<bool> execution = engine.RunOnceAsync(CancellationToken.None);
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);

        // 1 つ目（3 秒）の実行中に m=1 へ編集。1 つ目は 3 秒のまま走り切る。
        Assert.True(((await _edit.HandleAsync("job-1", "2 1", CancellationToken.None)).Value).IsSuccess);

        for (int i = 1; i <= 3; i++)
        {
            _time.Advance(SubTaskJobHandler.Step);
            await _time.WaitForTimersAsync(i + 1).WaitAsync(WaitLimit);
        }

        // ここまでで 1 つ目が完了し、2 つ目は編集後の m=1。1 秒で全体が終わる。
        _time.Advance(SubTaskJobHandler.Step);
        Assert.True(await execution.WaitAsync(WaitLimit));

        Assert.Equal(JobStatus.Completed, (await FindAsync()).Status);
    }

    /// <summary>
    /// 検証をすり抜けた「N &lt; 着手済み」（検証と反映の間に次のサブタスクが走った競合）でも、
    /// 着手済みの行は消えない。
    /// </summary>
    /// <remarks>
    /// 守っているのは RemovePendingFromAsync の SQL（未着手しか消さない）である。
    /// 以前この試験は「境界が着手済みへ切り上げる」という名前だったが、その切り上げを
    /// 消してもここは通った ── 検出していたのは最初から SQL の側だった。
    /// </remarks>
    [Fact]
    public async Task 着手済みの行はNを下回る編集でも消えない()
    {
        await RegisterAsync("3 1");
        JobExecutionEngine engine = await StartEngineAsync();

        Task<bool> execution = engine.RunOnceAsync(CancellationToken.None);
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        _time.Advance(SubTaskJobHandler.Step);

        // 2 つ目の実行中（着手済み 2）。検証はこの時点の着手済みを見るので、
        // ハンドラ経由では N=1 は書けない。競合で通ってしまった状況を、
        // エンティティ直書きで再現する（編集 API の検証は親切であって守りではない）。
        await _time.WaitForTimersAsync(2).WaitAsync(WaitLimit);
        Job raced = await FindAsync();
        raced.ChangeParameters("1 1");
        Assert.True(await _jobs.UpdateAsync(raced, CancellationToken.None));

        _time.Advance(SubTaskJobHandler.Step);
        Assert.True(await execution.WaitAsync(WaitLimit));

        // N=1 は着手済み 2 へ切り上げられ、未着手の 3 つ目だけが消える。
        Assert.Equal(JobStatus.Completed, (await FindAsync()).Status);
        Assert.Equal(
            [SubTaskStatus.Completed, SubTaskStatus.Completed],
            await SubTaskStatusesAsync());
    }

    /// <summary>
    /// 待機中に編集が入ると、エンジンの claim は成立せず、読み直して編集後の内容で走る。
    /// </summary>
    /// <remarks>
    /// エンジンが候補を読んでから InProgress を書き戻すまでの窓を、割り込みフックで決定的に作る。
    /// 編集は遷移ではないので状態は Registered のまま動かない。期待値が状態だったころは
    /// 「状態は変わっていない」として書き戻しが通り、全列の書き戻しで編集が消えていた。
    /// 版で守ると claim が一度負け、読み直して編集後の parameters で走る。
    /// </remarks>
    [Fact]
    public async Task 待機中の編集はエンジンのclaimに巻き戻されない()
    {
        await RegisterAsync("1 5");

        InterferingJobStore interfering = new(_jobs);
        JobExecutionEngine engine = await StartEngineAsync(interfering);

        // エンジンが候補を読んだ後、InProgress を書き戻す直前に編集を差し込む。
        interfering.BeforeNextUpdate = async () =>
            Assert.True(((await _edit.HandleAsync("job-1", "1 1", CancellationToken.None)).Value).IsSuccess);

        Task<bool> execution = engine.RunOnceAsync(CancellationToken.None);

        // 編集後の m=1 で走る。1 秒進めれば完走する（編集前の m=5 なら足りない）。
        await _time.WaitForTimersAsync(1).WaitAsync(WaitLimit);
        _time.Advance(SubTaskJobHandler.Step);
        Assert.True(await execution.WaitAsync(WaitLimit));

        Job settled = await FindAsync();
        Assert.Equal(JobStatus.Completed, settled.Status);
        Assert.Equal("1 1", settled.Parameters);

        // 書き戻しは 3 回。弾かれた claim、読み直してからの claim、そして結末。
        // 編集はこの store を通さない（生の store を持つハンドラが書く）ので数に入らない。
        Assert.Equal(3, interfering.UpdateAttempts);
    }

    private async Task RegisterAsync(string parameters)
    {
        Job job = Job.Create(Job1, "編集試験", SubTaskJobHandler.SubTaskJobType, parameters, Now);
        await _jobs.AddAsync(job, CancellationToken.None);
    }

    private async Task<JobExecutionEngine> StartEngineAsync(IJobStore? store = null) =>
        await JobExecutionEngine.StartAsync(
            store ?? _jobs,
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
