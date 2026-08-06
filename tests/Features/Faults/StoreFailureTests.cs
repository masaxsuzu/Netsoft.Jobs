using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Faults;

/// <summary>
/// 永続化そのものが応えなくなったとき（ディスクの障害など）に、何が壊れずに残るか。
/// </summary>
/// <remarks>
/// <para>
/// 競合（書き戻しが false）は既存のテストが網羅している。あちらは<b>状態が食い違う</b>話で、
/// 読み直せば正しい行き先が決まる。ここで見るのは<b>書けたかどうかが分からない</b>話で、
/// 読み直しでは決着しない。この基盤が置いている答えは「その場では諦め、
/// 決着は次の起動の復旧に委ねる」で、それが本当に成り立つかを確かめる。
/// </para>
/// <para>
/// 故障はデコレータで注入する。実ファイルを壊す（権限を落とす・削除する）のは
/// 隔離環境が要るので別のプロジェクトが持つ。ここは他のテストと並行に走ってよい。
/// </para>
/// </remarks>
public sealed class StoreFailureTests : IDisposable
{
    private const string HandledJobType = "test-job";

    private static readonly DateTimeOffset Now = new(2026, 8, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly FailingJobStore _failing;
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly RunningJobRegistry _runningJobs = new();
    private readonly JobQueueSignal _signal = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly JobExecutionInstrumentation _instrumentation;

    public StoreFailureTests()
    {
        _failing = new FailingJobStore(_store);
        _instrumentation = new JobExecutionInstrumentation(
            _meterFactory, _store, _timeProvider, new NullJobTraceContextStore(), NullLogger<JobExecutionInstrumentation>.Instance);
    }

    public void Dispose()
    {
        _instrumentation.Dispose();
        _meterFactory.Dispose();
        _store.Dispose();
    }

    /// <summary>
    /// 結末を書けなかった Job は Running のまま残り、次の起動の復旧が Failed で閉じる。
    /// </summary>
    /// <remarks>
    /// ハンドラは完走しているのに、それを記録できないという最も嫌な形。ここで嘘
    /// （記録できていないのに Completed を返す、握りつぶして次へ進む）を書かないことと、
    /// 取り残された Job に必ず出口があることの 2 つを押さえる。
    /// 出口は起動時復旧しかない ── エンジンは待ち行列しか拾わないので、
    /// この Job を動かせるものは他に無い。
    /// </remarks>
    [Fact]
    public async Task 結末を書けなかったJobは次の起動の復旧が閉じる()
    {
        await AddRegisteredAsync("job-1");
        ControllableJobHandler handler = new(HandledJobType);
        JobExecutionEngine engine = await CreateEngineAsync(handler);

        Task<bool> execution = engine.RunOnceAsync(CancellationToken.None);

        // ハンドラに入った＝Running は書けている。落とすのはその次の書き戻し（結末）だけ。
        // 回数だけで狙うと、間に挟まる書き込みが増えたときに黙って別の場所を落とすようになる。
        await handler.Entered;
        _failing.UpdateFailures = 1;
        handler.Release();

        await Assert.ThrowsAsync<IOException>(() => execution);

        // ハンドラは走り切っている。それでも記録できていないので Running のまま。
        Assert.Single(handler.Executions);
        Assert.Equal(JobStatus.Running, (await FindAsync("job-1")).Status);

        // 次の起動。エンジンを作り直すことがプロセスの起動に相当する。
        _ = await CreateEngineAsync(handler);

        Job recovered = await FindAsync("job-1");
        Assert.Equal(JobStatus.Failed, recovered.Status);
        Assert.NotNull(recovered.FinishedAt);
    }

    /// <summary>
    /// 候補の読み出しが落ちてもループは死なず、次の合図で実行が再開する。
    /// </summary>
    /// <remarks>
    /// ここでループを抜けると、store が直った後も Job を 1 件も動かさないプロセスが残る。
    /// 画面には待機中の Job が並び続け、エラーはどこにも出ない（利用者から見て最も分かりにくい壊れ方）。
    /// </remarks>
    [Fact]
    public async Task 読み出しが落ちてもループは死なず次の合図で再開する()
    {
        await AddRegisteredAsync("job-1");
        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        JobExecutionEngine engine = await CreateEngineAsync(handler);

        _failing.FindOldestWaitingFailures = 1;

        using CancellationTokenSource stopping = new();
        Task loop = engine.RunAsync(stopping.Token);

        // 1 回目の周回は読み出しで落ちる。ループはそれを飲み込んで合図待ちへ降りるので、
        // ここで合図を鳴らせば 2 回目の周回が走る。合図は溜まらず 1 つだけ保たれるため、
        // 落ちる前に鳴らしても取りこぼしは起きない。
        _signal.Set();

        Job job = await WaitForStatusAsync("job-1", JobStatus.Completed, loop);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(0, _failing.FindOldestWaitingFailures);

        await stopping.CancelAsync();
        await loop;
    }

    /// <summary>
    /// サブタスクの記録が落ちたら Job は Failed になり、途中までの行は中断点として残る。
    /// </summary>
    /// <remarks>
    /// サブタスクの行は「どこまで進んでいたか」の記録なので、失敗しても消さない。
    /// 消すと、次に同じ Job を走らせたときに済んだ仕事をやり直すことになる。
    /// </remarks>
    [Fact]
    public async Task サブタスクの記録が落ちたらJobは失敗し途中までの行が残る()
    {
        using TemporarySubTaskStore subTasks = new();
        FailingSubTaskStore failingSubTasks = new(subTasks);
        ManualTimeProvider time = new();

        await AddRegisteredAsync("job-1", SubTaskJobHandler.SubTaskJobType, parameters: "3 1");

        SubTaskJobHandler handler = new(failingSubTasks, _store, time);
        JobExecutionEngine engine = await CreateEngineAsync(handler);

        Task<bool> execution = engine.RunOnceAsync(CancellationToken.None);

        // 待ちのタイマーが張られた＝1 つ目の開始は書けている。次の書き戻し（1 つ目の完了）を落とす。
        await time.WaitForTimersAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        failingSubTasks.UpdateFailures = 1;
        time.Advance(SubTaskJobHandler.Step);

        Assert.True(await execution);

        Job job = await FindAsync("job-1");
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains(FailingJobStore.FailureMessage, job.FailureMessage);

        // 1 つ目は着手済みのまま、残りは未着手のまま。行は消えていない。
        IReadOnlyList<SubTask> rows = await subTasks.ListByJobAsync(JobId.From("job-1"), CancellationToken.None);
        Assert.Equal(
            [SubTaskStatus.Running, SubTaskStatus.Pending, SubTaskStatus.Pending],
            rows.Select(row => row.Status));
    }

    private async Task<Job> WaitForStatusAsync(string id, JobStatus status, Task loop)
    {
        while (true)
        {
            Job job = await FindAsync(id);
            if (job.Status == status)
            {
                return job;
            }

            // ループが先に落ちていたら、待ち続けずにその例外を表に出す。
            Assert.False(loop.IsCompleted, $"実行ループが {loop.Status} で終わりました。");

            await Task.Yield();
        }
    }

    private Task<JobExecutionEngine> CreateEngineAsync(params IJobHandler[] handlers) =>
        JobExecutionEngine.StartAsync(
            _failing,
            new JobHandlerRegistry(handlers),
            _runningJobs,
            _signal,
            _timeProvider,
            _instrumentation,
            NullLogger<JobExecutionEngine>.Instance,
            CancellationToken.None);

    private static ControllableJobHandler Released(ControllableJobHandler handler)
    {
        handler.Release();
        return handler;
    }

    private async Task AddRegisteredAsync(string id, string jobType = HandledJobType, string parameters = "")
    {
        Job job = Job.Create(JobId.From(id), $"Job {id}", jobType, parameters, Now);
        await _store.AddAsync(job, CancellationToken.None);
    }

    private async Task<Job> FindAsync(string id) =>
        await _store.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");
}
