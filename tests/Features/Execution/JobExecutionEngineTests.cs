using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// 実行エンジンのテスト。
/// </summary>
/// <remarks>
/// 時間で待つテストを書かない。「ハンドラの中にいる」ことは
/// <see cref="ControllableJobHandler.Entered"/> で確定させてから次の操作をする。
/// </remarks>
public sealed class JobExecutionEngineTests : IDisposable
{
    private const string HandledJobType = "test-job";

    private static readonly DateTimeOffset Now = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly RunningJobRegistry _runningJobs = new();

    // エンジンには割り込み用のデコレータ越しに store を渡す。割り込みを仕掛けなければ素通しなので、
    // 競合を扱わないテストの見え方は変わらない。テスト自身は _store を直に使い、
    // 「他所からの書き込み」をエンジンの経路とは別に起こす。
    private readonly InterferingJobStore _engineStore;

    public JobExecutionEngineTests() => _engineStore = new InterferingJobStore(_store);

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task 待機中のJobが実行されて完了になる()
    {
        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        await AddQueuedAsync("job-1", parameters: "10");
        JobExecutionEngine engine = CreateEngine(handler);

        bool executed = await engine.RunOnceAsync(CancellationToken.None);

        Assert.True(executed);
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);
        Assert.Equal("10", Assert.Single(handler.Executions));
    }

    [Fact]
    public async Task 完了したJobには開始時刻と終了時刻が記録される()
    {
        ControllableJobHandler handler = new(HandledJobType);
        await AddQueuedAsync("job-1");
        JobExecutionEngine engine = CreateEngine(handler);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;

        // 開始と終了で時計を変えないと、どちらの時刻が記録されたのか区別できない。
        DateTimeOffset finishedAt = Now.AddMinutes(3);
        _timeProvider.UtcNow = finishedAt;
        handler.Release();
        await running;

        Job job = await FindAsync("job-1");
        Assert.Equal(Now, job.StartedAt);
        Assert.Equal(finishedAt, job.FinishedAt);
    }

    [Fact]
    public async Task 実行中のJobの状態はRunningになる()
    {
        ControllableJobHandler handler = new(HandledJobType);
        await AddQueuedAsync("job-1");
        JobExecutionEngine engine = CreateEngine(handler);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);

        // ハンドラの中にいることが確定してから確認する。時間では待たない。
        await handler.Entered;
        Assert.Equal(JobStatus.Running, (await FindAsync("job-1")).Status);

        handler.Release();
        await running;
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);
    }

    [Fact]
    public async Task ハンドラが例外を投げるとFailedになり理由が記録される()
    {
        ControllableJobHandler handler = new(HandledJobType);
        handler.Throw(new InvalidOperationException("集計元のファイルがありません。"));
        await AddQueuedAsync("job-1");
        JobExecutionEngine engine = CreateEngine(handler);

        bool executed = await engine.RunOnceAsync(CancellationToken.None);

        Assert.True(executed);
        Job job = await FindAsync("job-1");
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("集計元のファイルがありません。", job.FailureMessage);
        Assert.Contains(nameof(InvalidOperationException), job.FailureMessage);
    }

    [Fact]
    public async Task キャンセルを伝えるとハンドラのトークンが発火してCancelledになる()
    {
        ControllableJobHandler handler = new(HandledJobType);
        await AddQueuedAsync("job-1");
        JobExecutionEngine engine = CreateEngine(handler);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;

        // キャンセル機能（コマンド側）がやることと同じ順序で行う。
        // 状態を Cancelling へ進めてから、実行中のハンドラへ伝える。
        Job job = await FindAsync("job-1");
        Assert.True(job.Apply(JobTrigger.RequestCancel, Now).IsAllowed);
        Assert.True(await _store.UpdateAsync(job, JobStatus.Running, CancellationToken.None));

        Assert.True(_runningJobs.TryRequestCancel(JobId.From("job-1")));

        // Release を呼んでいないので、ここで終わるのはトークンが発火したから。
        Assert.True(await running);
        Assert.True(handler.CancellationObserved);
        Assert.Equal(JobStatus.Cancelled, (await FindAsync("job-1")).Status);
    }

    [Fact]
    public async Task キャンセルが間に合わずハンドラが完走したらCompletedになる()
    {
        ControllableJobHandler handler = new(HandledJobType);
        await AddQueuedAsync("job-1");
        JobExecutionEngine engine = CreateEngine(handler);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;

        // 状態だけ Cancelling にして、ハンドラには伝えずに完走させる。
        Job job = await FindAsync("job-1");
        job.Apply(JobTrigger.RequestCancel, Now);
        Assert.True(await _store.UpdateAsync(job, JobStatus.Running, CancellationToken.None));

        handler.Release();
        await running;

        // 実際に起きたことを記録する。状態機械の判断に従う。
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);
    }

    [Fact]
    public async Task 実行中でないJobへのキャンセル要求は伝わらない()
    {
        await AddQueuedAsync("job-1");

        Assert.False(_runningJobs.TryRequestCancel(JobId.From("job-1")));
    }

    [Fact]
    public async Task ハンドラが無いJobTypeはFailedになりエンジンは次のJobを実行できる()
    {
        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        await AddQueuedAsync("job-1", jobType: "誰も知らない種類");
        await AddQueuedAsync("job-2", createdAt: Now.AddSeconds(1));
        JobExecutionEngine engine = CreateEngine(handler);

        Assert.True(await engine.RunOnceAsync(CancellationToken.None));
        Assert.True(await engine.RunOnceAsync(CancellationToken.None));

        Job unknown = await FindAsync("job-1");
        Assert.Equal(JobStatus.Failed, unknown.Status);
        Assert.Contains("誰も知らない種類", unknown.FailureMessage);

        Assert.Equal(JobStatus.Completed, (await FindAsync("job-2")).Status);
    }

    [Fact]
    public async Task ひとつのJobが失敗しても次のJobが実行される()
    {
        ControllableJobHandler failing = new("failing");
        failing.Throw(new InvalidOperationException("失敗"));
        ControllableJobHandler succeeding = Released(new ControllableJobHandler(HandledJobType));

        await AddQueuedAsync("job-1", jobType: "failing");
        await AddQueuedAsync("job-2", createdAt: Now.AddSeconds(1));
        JobExecutionEngine engine = CreateEngine(failing, succeeding);

        Assert.True(await engine.RunOnceAsync(CancellationToken.None));
        Assert.True(await engine.RunOnceAsync(CancellationToken.None));

        Assert.Equal(JobStatus.Failed, (await FindAsync("job-1")).Status);
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-2")).Status);
    }

    [Fact]
    public async Task 実行対象が無いときは何もせずに返る()
    {
        JobExecutionEngine engine = CreateEngine(Released(new ControllableJobHandler(HandledJobType)));

        Assert.False(await engine.RunOnceAsync(CancellationToken.None));
        Assert.Empty(await _store.ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// 候補を取ってから Running を書き戻すまでの間に、他のエンジンが同じ Job を取った状況。
    /// ここで実行してしまうと同じ Job が 2 つのプロセスで動く。
    /// </summary>
    [Fact]
    public async Task 取得と開始の間に他から開始されたJobは実行しない()
    {
        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        await AddQueuedAsync("job-1");
        JobExecutionEngine engine = CreateEngine(handler);

        _engineStore.BeforeNextUpdate = () => StealAsync("job-1");

        Assert.False(await engine.RunOnceAsync(CancellationToken.None));

        Assert.Empty(handler.Executions);

        // 先に取った側が実行している。こちらは何も書いていない。
        Assert.Equal(JobStatus.Running, (await FindAsync("job-1")).Status);
    }

    [Fact]
    public async Task 取得と開始の間に取られても次の候補は実行される()
    {
        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        await AddQueuedAsync("job-1", parameters: "他に取られる", createdAt: Now);
        await AddQueuedAsync("job-2", parameters: "実行される", createdAt: Now.AddSeconds(1));
        JobExecutionEngine engine = CreateEngine(handler);

        _engineStore.BeforeNextUpdate = () => StealAsync("job-1");

        Assert.True(await engine.RunOnceAsync(CancellationToken.None));

        // 取られた 1 件で諦めず、次の候補を取り直している。
        Assert.Equal(["実行される"], handler.Executions);
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-2")).Status);
    }

    /// <summary>
    /// 結末を読み出してから書き戻すまでの間にキャンセル要求が入った状況。
    /// Running を前提に書いた Completed は弾かれるので、読み直して Cancelling から書き直す。
    /// </summary>
    [Fact]
    public async Task 結末の記録が競合に負けたら読み直して書き直す()
    {
        ControllableJobHandler handler = new(HandledJobType);
        await AddQueuedAsync("job-1");
        JobExecutionEngine engine = CreateEngine(handler);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;

        _engineStore.BeforeNextUpdate = async () =>
        {
            Job cancelling = await FindAsync("job-1");
            Assert.True(cancelling.Apply(JobTrigger.RequestCancel, Now).IsAllowed);
            Assert.True(await _store.UpdateAsync(cancelling, JobStatus.Running, CancellationToken.None));
        };

        handler.Release();
        Assert.True(await running);

        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);

        // 開始で 1 回、結末で 2 回（1 回目は競合で弾かれた）。やり直しが起きたことの裏付け。
        Assert.Equal(3, _engineStore.UpdateAttempts);
    }

    /// <summary>
    /// 別のプロセスの起動時復旧が、この Job を残骸とみなして Failed で閉じた状況。
    /// 読み直した先は終端なので、実行の結末で上書きしてはいけない。
    /// </summary>
    [Fact]
    public async Task 結末を書く前に他が終端を書いていたら上書きしない()
    {
        ControllableJobHandler handler = new(HandledJobType);
        await AddQueuedAsync("job-1");
        JobExecutionEngine engine = CreateEngine(handler);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;

        _engineStore.BeforeNextUpdate = async () =>
        {
            Job closed = await FindAsync("job-1");
            Assert.True(closed.Apply(JobTrigger.Fail, Now, "他のプロセスが閉じました。").IsAllowed);
            Assert.True(await _store.UpdateAsync(closed, JobStatus.Running, CancellationToken.None));
        };

        handler.Release();
        Assert.True(await running);

        Job job = await FindAsync("job-1");
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("他のプロセスが閉じました。", job.FailureMessage);
    }

    [Fact]
    public async Task 実行順序は登録順になる()
    {
        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));

        // Id の順と作成日時の順をわざとずらす。Id 順で拾っていたら気づける。
        await AddQueuedAsync("job-3", parameters: "1 番目", createdAt: Now);
        await AddQueuedAsync("job-1", parameters: "3 番目", createdAt: Now.AddSeconds(2));
        await AddQueuedAsync("job-2", parameters: "2 番目", createdAt: Now.AddSeconds(1));
        JobExecutionEngine engine = CreateEngine(handler);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(await engine.RunOnceAsync(CancellationToken.None));
        }

        Assert.Equal(["1 番目", "2 番目", "3 番目"], handler.Executions);
        Assert.False(await engine.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ループは待機中のJobを続けて実行し停止要求で終わる()
    {
        // 2 つ目のハンドラだけを止めておくことで、「1 つ目を終えて 2 つ目に入った」ことを
        // 時間で待たずに確定できる。
        ControllableJobHandler first = Released(new ControllableJobHandler(HandledJobType));
        ControllableJobHandler second = new("second");
        await AddQueuedAsync("job-1", createdAt: Now);
        await AddQueuedAsync("job-2", jobType: "second", createdAt: Now.AddSeconds(1));
        JobExecutionEngine engine = CreateEngine(first, second);

        using CancellationTokenSource stop = new();

        // 待機の間隔は十分に長くしておく。ここで待つ設計になっていたらテストが終わらない。
        Task loop = engine.RunAsync(TimeSpan.FromHours(1), stop.Token);

        await second.Entered;
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);

        second.Release();
        await stop.CancelAsync();
        await loop;

        Assert.Equal(JobStatus.Completed, (await FindAsync("job-2")).Status);
    }

    [Theory]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Cancelling)]
    public async Task 起動時復旧で前回の残骸はFailedになる(JobStatus status)
    {
        await AddLeftoverAsync("job-1", status);
        JobExecutionEngine engine = CreateEngine();

        await engine.EnsureRecoveredAsync(CancellationToken.None);

        Job job = await FindAsync("job-1");
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("異常終了", job.FailureMessage);
        Assert.Equal(Now, job.FinishedAt);
    }

    [Fact]
    public async Task 起動時復旧で待機中のJobは変化しない()
    {
        // Queued はハンドラを起動していないので副作用が無い。このプロセスがそのまま実行する。
        await AddQueuedAsync("job-1");
        JobExecutionEngine engine = CreateEngine();

        await engine.EnsureRecoveredAsync(CancellationToken.None);

        Job job = await FindAsync("job-1");
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Null(job.FinishedAt);
        Assert.Null(job.FailureMessage);
    }

    [Fact]
    public async Task 起動時復旧は実行より先に走る()
    {
        // 復旧を明示的に呼ばずに実行を始めても、残骸が Failed になっていること。
        // 順序が逆だと、このプロセスが Running にした Job を復旧が Failed で上書きしうる。
        await AddLeftoverAsync("job-1", JobStatus.Running);
        await AddQueuedAsync("job-2", createdAt: Now.AddSeconds(1));
        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        JobExecutionEngine engine = CreateEngine(handler);

        Assert.True(await engine.RunOnceAsync(CancellationToken.None));

        Assert.Equal(JobStatus.Failed, (await FindAsync("job-1")).Status);
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-2")).Status);
    }

    [Fact]
    public async Task 起動時復旧は2回目以降は何もしない()
    {
        JobExecutionEngine engine = CreateEngine();
        await engine.EnsureRecoveredAsync(CancellationToken.None);

        // 1 回目の後に残った Running は、このプロセスが実行している最中のもの。
        // ここで復旧が走ると、動いている Job を勝手に Failed にしてしまう。
        await AddLeftoverAsync("job-1", JobStatus.Running);

        await engine.EnsureRecoveredAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Running, (await FindAsync("job-1")).Status);
    }

    private JobExecutionEngine CreateEngine(params IJobHandler[] handlers) =>
        new(
            _engineStore,
            new JobHandlerRegistry(handlers),
            _runningJobs,
            _timeProvider,
            NullLogger<JobExecutionEngine>.Instance);

    private static ControllableJobHandler Released(ControllableJobHandler handler)
    {
        // 止める必要が無いテストでは、先に解放しておけば実行は即座に終わる。
        handler.Release();
        return handler;
    }

    private async Task<Job> AddQueuedAsync(
        string id,
        string jobType = HandledJobType,
        string parameters = "",
        DateTimeOffset? createdAt = null)
    {
        Job job = Job.Create(JobId.From(id), $"Job {id}", jobType, parameters, createdAt ?? Now);
        await _store.AddAsync(job, CancellationToken.None);
        return job;
    }

    /// <summary>
    /// 前回のプロセスが残した Job を作る。状態機械を通して作るので、ありえない状態にはならない。
    /// </summary>
    private async Task<Job> AddLeftoverAsync(string id, JobStatus status)
    {
        Job job = await AddQueuedAsync(id);
        job.Apply(JobTrigger.Start, Now);

        if (status == JobStatus.Cancelling)
        {
            job.Apply(JobTrigger.RequestCancel, Now);
        }

        // 保存されているのは Queued の状態。そこから書き戻す。
        await _store.UpdateAsync(job, JobStatus.Queued, CancellationToken.None);
        return job;
    }

    /// <summary>
    /// 他のエンジンが先に同じ Job を取った状況を作る。エンジンとは別に store へ直接書く。
    /// </summary>
    private async Task StealAsync(string id)
    {
        Job job = await FindAsync(id);
        Assert.True(job.Apply(JobTrigger.Start, Now).IsAllowed);
        Assert.True(await _store.UpdateAsync(job, JobStatus.Queued, CancellationToken.None));
    }

    private async Task<Job> FindAsync(string id) =>
        await _store.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");
}
