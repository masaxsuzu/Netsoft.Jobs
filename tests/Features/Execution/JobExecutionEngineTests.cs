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
public sealed class JobExecutionEngineTests
{
    private const string HandledJobType = "test-job";

    private static readonly DateTimeOffset Now = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryJobStore _store = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly RunningJobRegistry _runningJobs = new();

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
        await _store.UpdateAsync(job, CancellationToken.None);

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
        await _store.UpdateAsync(job, CancellationToken.None);

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
        Assert.Empty(_store.Jobs);
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
            _store,
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

        await _store.UpdateAsync(job, CancellationToken.None);
        return job;
    }

    private async Task<Job> FindAsync(string id) =>
        await _store.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");
}
