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
    private readonly TestMeterFactory _meterFactory = new();
    private readonly JobExecutionInstrumentation _instrumentation;

    public JobExecutionEngineConcurrencyTests() =>
        _instrumentation = new JobExecutionInstrumentation(_meterFactory, _store, _timeProvider, new NullJobTraceContextStore(), NullLogger<JobExecutionInstrumentation>.Instance);

    public void Dispose()
    {
        _instrumentation.Dispose();
        _meterFactory.Dispose();
        _store.Dispose();
    }

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

        // 立ち上げの時点で各エンジンの復旧は済んでいる。Job を入れるのはその後なので、
        // 復旧が実行中の Job を見ることはない。
        JobExecutionEngine[] engines = await Task.WhenAll(
            Enumerable.Range(0, Engines).Select(_ => CreateEngineAsync(_store, handler)));

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
        JobExecutionEngine engine = await CreateEngineAsync(hostile, handler);

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
        JobExecutionEngine engine = await CreateEngineAsync(hostile, handler);

        // どの候補も書き戻す直前に奪われるので、実行できるものは 1 件も無い。
        Assert.False(await engine.RunOnceAsync(CancellationToken.None).WaitAsync(HangGuard));

        Assert.Empty(handler.Executions);
        Assert.Equal(Jobs, hostile.UpdateAttempts);
    }

    /// <summary>
    /// エンジンが立った後は何度実行しても起動時復旧が走らないこと。走行中の Job が
    /// 「前回の残骸」とみなされて閉じられる余地が無いことを、呼ばれていない事実で示す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// かつてここには二重復旧の再現テストがあり、Skip してあった（何が壊れていたかは
    /// <see cref="JobExecutionEngine.StartAsync"/> の注記）。いまはその重なりを
    /// 書けないので<b>再現テストは消してある</b>。代わりに、消えた経路が本当に
    /// 消えていることをここで押さえる。
    /// </para>
    /// <para>
    /// <see cref="GatedJobStore.ListByStatusCalls"/> を数えるのは、状態で絞った読み出しを
    /// するのが復旧だけだから。実行をどれだけ回してもこの数が動かないことが、
    /// 実行の入口から復旧へ辿れないことの証拠になる。
    /// </para>
    /// <para>
    /// 実行は逐次に回す。1 インスタンスの同時実行数は 1 という契約で、
    /// 並行して呼ぶと <see cref="RunningJobRegistry"/> が二重登録で例外を投げる。
    /// ここで見たいのは復旧が呼ばれないことなので、契約の内側で足りる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 起動後は何度実行しても起動時復旧は走らない()
    {
        CountingJobHandler handler = new(HandledJobType);
        GatedJobStore gated = new(_store, JobStatus.Running);

        // 復旧はここで済む。gate は最初の 1 回だけ止めるが、誰も待っていないので素通しでよい。
        gated.Release();
        JobExecutionEngine engine = await CreateEngineAsync(gated, handler);

        int afterStartup = gated.ListByStatusCalls;
        Assert.NotEqual(0, afterStartup);

        // エンジンが立った後に現れた Running。誰かが今まさに動かしている、という想定。
        await AddRunningAsync("job-1");
        await AddQueuedAsync("job-2", createdAt: Now.AddSeconds(1));

        // 同じエンジンに何度も実行を頼む。以前は実行の入口が毎回復旧を通っていたので、
        // ここが復旧を呼び直す経路になっていた。
        for (int i = 0; i < 4; i++)
        {
            await engine.RunOnceAsync(CancellationToken.None).WaitAsync(HangGuard);
        }

        // 復旧は 1 度も追加で走っていない。
        Assert.Equal(afterStartup, gated.ListByStatusCalls);

        // 走行中とみなした Job は触られず、待機中の Job だけが実行された。
        Assert.Equal(JobStatus.Running, (await FindAsync("job-1")).Status);
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-2")).Status);
    }

    private Task<JobExecutionEngine> CreateEngineAsync(IJobStore store, params IJobHandler[] handlers) =>
        JobExecutionEngine.StartAsync(
            store,
            new JobHandlerRegistry(handlers),
            new RunningJobRegistry(),
            new JobQueueSignal(),
            _timeProvider,
            _instrumentation,
            NullLogger<JobExecutionEngine>.Instance,
            CancellationToken.None);

    /// <summary>
    /// 実行中に見える Job を直に置く。エンジンが立った後に他所が動かし始めた状況を作る。
    /// </summary>
    private async Task AddRunningAsync(string id)
    {
        Job job = Job.Create(JobId.From(id), $"Job {id}", HandledJobType, string.Empty, Now);
        await _store.AddAsync(job, CancellationToken.None);

        Assert.True(job.Apply(JobTrigger.Start, Now).IsAllowed);
        Assert.True(await _store.UpdateAsync(job, CancellationToken.None));
    }

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
