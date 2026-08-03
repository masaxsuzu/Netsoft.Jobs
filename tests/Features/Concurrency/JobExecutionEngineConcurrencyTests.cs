using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 実行エンジンを同時実行のもとで検査する。
/// </summary>
/// <remarks>
/// 待ち時間で同期しない。競合は割り込み用の store と合図で決定的に起こす。
/// 唯一の時間依存は「止まらないループ」を試験の停止として拾うための保険で、
/// 実装が正しければ発火しない。
/// </remarks>
public sealed class JobExecutionEngineConcurrencyTests : IDisposable
{
    private const string HandledJobType = "test-job";

    /// <summary>止まらないループを試験の停止に変えるための保険。実装が正しければ発火しない。</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Now = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);

    public void Dispose() => _store.Dispose();

    /// <summary>
    /// エンジンを 3 つ同じ DB へ向けて同時に回し、同じ Job のハンドラが 2 回起動しないこと。
    /// </summary>
    /// <remarks>
    /// 各エンジンは待機中の Job が尽きるまで回して自分で止まるので、時間で打ち切らずに済む。
    /// 復旧は Job を入れる前に済ませておく。復旧は「誰も走っていないところから始める」
    /// ものなので、走行中に別のエンジンを立ち上げる状況はここで見るものではない。
    /// </remarks>
    [Fact]
    public async Task 複数のエンジンが同じDBを回してもハンドラは一度しか起動しない()
    {
        const int Engines = 3;
        const int Jobs = 40;

        CountingJobHandler handler = new(HandledJobType);
        JobExecutionEngine[] engines =
            [.. Enumerable.Range(0, Engines).Select(_ => CreateEngine(_store, handler))];

        foreach (JobExecutionEngine engine in engines)
        {
            await engine.EnsureRecoveredAsync(CancellationToken.None);
        }

        for (int i = 0; i < Jobs; i++)
        {
            // 登録は実行より前に起きたことにする。固定時計より後の CreatedAt を作ると、
            // StartedAt が CreatedAt より前になり、実装ではなく試験の作り方で不変条件が破れる。
            await AddQueuedAsync($"job-{i:D3}", parameters: $"job-{i:D3}", createdAt: Now.AddSeconds(i - Jobs));
        }

        AsyncStartGate start = new(Engines);

        await Task.WhenAll(engines.Select(engine => Task.Run(async () =>
        {
            await start.SignalAndWaitAsync();
            while (await engine.RunOnceAsync(CancellationToken.None))
            {
                // 待機中の Job が尽きるまで回す。
            }
        })));

        for (int i = 0; i < Jobs; i++)
        {
            string id = $"job-{i:D3}";
            Job job = await FindAsync(id);

            Assert.Equal(JobStatus.Completed, job.Status);
            Assert.Null(JobInvariants.FindViolation(job));
            Assert.True(
                handler.Executions.TryGetValue(id, out int count) && count == 1,
                $"{id} のハンドラが {handler.Executions.GetValueOrDefault(id)} 回起動しました。");
        }
    }

    /// <summary>
    /// 結末の記録が競合し続けても、読み直しのループが止まること。
    /// </summary>
    /// <remarks>
    /// 実装は「状態機械が一方通行だから有限で止まる」と主張している。
    /// 毎回横から状態を進める store を当てて、その主張を実際に確かめる。
    /// </remarks>
    [Fact]
    public async Task 結末の記録が競合し続けても読み直しのループは止まる()
    {
        ControllableJobHandler handler = new(HandledJobType);
        await AddQueuedAsync("job-1");

        RelentlessJobStore hostile = new(_store, Now);
        JobExecutionEngine engine = CreateEngine(hostile, handler);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;

        // 開始の書き戻しが済んでから割り込みを入れる。ここより前で入れると、
        // 開始そのものが奪われて結末の記録まで辿り着かない。
        hostile.Interfering = true;
        int attemptsBeforeFinish = hostile.UpdateAttempts;

        handler.Release();
        Assert.True(await running.WaitAsync(HangGuard));

        Job job = await FindAsync("job-1");
        Assert.True(job.Status.IsTerminal(), $"競合の後に {job.Status} で残りました。");
        Assert.Null(JobInvariants.FindViolation(job));

        // 状態は Running → Cancelling → 終端 の高々 2 手しか進めない。
        // 読み直しがその範囲で収まっていることを回数で裏付ける。
        Assert.InRange(hostile.UpdateAttempts - attemptsBeforeFinish, 1, 4);
    }

    /// <summary>
    /// 候補を取っては奪われ続けても、候補の取り直しが止まること。
    /// </summary>
    [Fact]
    public async Task 候補が奪われ続けても取り直しのループは止まる()
    {
        const int Jobs = 6;

        CountingJobHandler handler = new(HandledJobType);
        for (int i = 0; i < Jobs; i++)
        {
            await AddQueuedAsync($"job-{i:D3}", createdAt: Now.AddSeconds(i - Jobs));
        }

        RelentlessJobStore hostile = new(_store, Now) { Interfering = true };
        JobExecutionEngine engine = CreateEngine(hostile, handler);

        // どの候補も書き戻す直前に奪われるので、実行できるものは 1 件も無い。
        Assert.False(await engine.RunOnceAsync(CancellationToken.None).WaitAsync(HangGuard));

        Assert.Empty(handler.Executions);
        Assert.Equal(Jobs, hostile.UpdateAttempts);
    }

    /// <summary>
    /// 起動時復旧と実行を同じエンジンに並行して呼ぶと、走り出した Job が
    /// もう一方の復旧に「前回の残骸」とみなされて Failed で閉じられる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>これは実装の不具合を再現するテストであり、直っていないので Skip してある。</b>
    /// <see cref="JobExecutionEngine"/> の <c>_recovered</c> は排他で守られておらず、
    /// 「重なっても条件付き更新があるから壊れない」という注記が付いている。
    /// その注記が守っているのは復旧どうしの重なりだけで、復旧とこのプロセス自身の実行が
    /// 重なる場合は守られない。復旧は Running / Cancelling をすべて残骸とみなすので、
    /// 直前に自分が Running にした Job まで Failed にしてしまう。
    /// </para>
    /// <para>
    /// 直し方には <c>SemaphoreSlim</c> で復旧を直列化して二重に走らせない案があるが、
    /// 「守らない」という判断が注記として明示されているので、勝手に覆さずに報告する。
    /// </para>
    /// </remarks>
    [Fact(Skip = "未修正の不具合の再現。_recovered が排他で守られていないため、"
        + "復旧と実行を並行させると走行中の Job が Failed で閉じられる。")]
    public async Task 復旧と実行を並行させると走行中のJobがFailedにされる()
    {
        ControllableJobHandler handler = new(HandledJobType);
        await AddQueuedAsync("job-1");

        GatedJobStore gated = new(_store, JobStatus.Running);
        JobExecutionEngine engine = CreateEngine(gated, handler);

        // 1 本目の復旧を Running の読み出しの手前で止める。
        Task recovering = engine.EnsureRecoveredAsync(CancellationToken.None);
        await gated.Entered;

        // 同じエンジンに実行を頼む。_recovered はまだ立っていないので復旧がもう一度走り、
        // 素通しで終わったあと job-1 を Running にして実行を始める。
        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;
        Assert.Equal(JobStatus.Running, (await FindAsync("job-1")).Status);

        // 止めていた 1 本目を進ませる。いま Running なのは自分のプロセスが動かしている Job。
        gated.Release();
        await recovering.WaitAsync(HangGuard);

        // 走っている最中の Job が終端で閉じられている。
        Assert.Equal(JobStatus.Running, (await FindAsync("job-1")).Status);

        handler.Release();
        await running.WaitAsync(HangGuard);

        // ハンドラは完走したのに、記録は完了になっていない。
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);
        Assert.Single(handler.Executions);
    }

    private JobExecutionEngine CreateEngine(IJobStore store, params IJobHandler[] handlers) =>
        new(
            store,
            new JobHandlerRegistry(handlers),
            new RunningJobRegistry(),
            _timeProvider,
            NullLogger<JobExecutionEngine>.Instance);

    private async Task AddQueuedAsync(
        string id,
        string parameters = "",
        DateTimeOffset? createdAt = null)
    {
        Job job = Job.Create(JobId.From(id), $"Job {id}", HandledJobType, parameters, createdAt ?? Now);
        await _store.AddAsync(job, CancellationToken.None);
    }

    private async Task<Job> FindAsync(string id) =>
        await _store.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");
}
