using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Audit;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// 実行エンジンと起動時復旧が残す監査ログ。実施者はすべてシステム。
/// </summary>
/// <remarks>
/// <para>
/// 利用者の要求と対になる確定（一時停止・キャンセル）がここに出る。<b>要求はこの層を
/// 通っていない</b> ── 別のプロセス（API）が別の時刻に書いたものなので、対で見えるのは
/// HTTP まで通す <c>tests/Web</c> の側になる。ここで見るのはシステム側の 1 件ずつ。
/// </para>
/// <para>
/// 起動時復旧だけは「1 アクション 1 ログ」から外れて Job ごとに 1 件。走査は 1 回でも、
/// 閉じられた Job の側から見れば「自分に何が起きたか」は 1 件で読めないと意味が無い。
/// </para>
/// </remarks>
public sealed class JobExecutionAuditTests : IDisposable
{
    private const string HandledJobType = "test-job";

    private static readonly DateTimeOffset Now = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly JobQueueSignal _signal = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly RecordingAuditLogStore _logs = new();
    private readonly JobExecutionInstrumentation _instrumentation;

    public JobExecutionAuditTests()
    {
        _instrumentation = new JobExecutionInstrumentation(
            _meterFactory,
            _store,
            _timeProvider,
            new NullJobTraceContextStore(),
            NullLogger<JobExecutionInstrumentation>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        _meterFactory.Dispose();
    }

    [Fact]
    public async Task 完走した実行は開始と完了の2件をシステム名義で残す()
    {
        await AddAsync("job-1");
        JobExecutionEngine engine = await CreateEngineAsync(Released(new ControllableJobHandler(HandledJobType)));

        Assert.True(await engine.RunOnceAsync(CancellationToken.None));

        Assert.Equal(
            [(AuditActor.System, "実行を開始した（パラメータ=3 1）"), (AuditActor.System, "実行を完了した")],
            _logs.Logs.Select(log => (log.Actor, log.Content)));

        Assert.All(_logs.Logs, log => Assert.Equal(JobId.From("job-1"), log.JobId));
        Assert.All(_logs.Logs, log => Assert.Null(log.Error));
    }

    /// <summary>
    /// 失敗した実行はエラーを載せる。実施内容は「何をしたか」なので結末によらず
    /// 過去形の 1 文で、通らなかった理由は <c>Error</c> 側にある。
    /// </summary>
    [Fact]
    public async Task 失敗した実行はエラー付きで残る()
    {
        await AddAsync("job-1");
        JobExecutionEngine engine = await CreateEngineAsync(new ThrowingJobHandler(HandledJobType));

        Assert.True(await engine.RunOnceAsync(CancellationToken.None));

        AuditLog finished = _logs.Logs[^1];

        Assert.Equal("実行に失敗した", finished.Content);
        Assert.Contains("壊れた", finished.Error);
    }

    /// <summary>
    /// <b>起動時復旧は復旧した Job ごとに 1 件。</b>走査は 1 回だが、閉じられた Job から
    /// 見れば「自分がなぜ失敗になったのか」が読めないと意味が無い。
    /// </summary>
    [Fact]
    public async Task 起動時復旧は復旧したJobごとに1件残す()
    {
        await AddLeftoverAsync("job-1", JobStatus.InProgress);
        await AddLeftoverAsync("job-2", JobStatus.Cancelling);

        // 静止している Job は復旧の対象外。記録も出ない。
        await AddAsync("job-3");

        await CreateEngineAsync();

        Assert.Equal(
            ["前回の異常終了を検出し、InProgress だった Job を Failed で閉じた",
             "前回の異常終了を検出し、Cancelling だった Job を Cancelled で閉じた"],
            _logs.Logs.Select(log => log.Content));

        Assert.Equal([JobId.From("job-1"), JobId.From("job-2")], _logs.Logs.Select(log => log.JobId));
        Assert.All(_logs.Logs, log => Assert.Equal(AuditActor.System, log.Actor));

        // 失敗として閉じたものにだけ理由が載る。中止として閉じたものは失敗ではない。
        Assert.NotNull(_logs.Logs[0].Error);
        Assert.Null(_logs.Logs[1].Error);
    }

    private Task<JobExecutionEngine> CreateEngineAsync(params IJobHandler[] handlers) =>
        JobExecutionEngine.StartAsync(
            _store,
            new JobHandlerRegistry(handlers),
            _signal,
            _timeProvider,
            _instrumentation,
            new AuditRecorder(_logs, NullLogger<AuditRecorder>.Instance),
            NullLogger<JobExecutionEngine>.Instance,
            CancellationToken.None);

    private static ControllableJobHandler Released(ControllableJobHandler handler)
    {
        handler.Release();

        return handler;
    }

    private Task AddAsync(string id) =>
        _store.AddAsync(Job.Create(JobId.From(id), "監査される Job", HandledJobType, "3 1", Now), CancellationToken.None);

    /// <summary>前回プロセスの残骸を置く。状態機械を通さずに組み立てる。</summary>
    private Task AddLeftoverAsync(string id, JobStatus status) =>
        _store.AddAsync(
            Job.Rehydrate(JobId.From(id), "残骸", HandledJobType, "3 1", status, Now, Now, null, null),
            CancellationToken.None);

    /// <summary>必ず例外で終わるハンドラ。失敗の経路を決定的に作る。</summary>
    private sealed class ThrowingJobHandler(string jobType) : IJobHandler
    {
        public string JobType { get; } = jobType;

        public Task ExecuteAsync(JobId jobId, string parameters) =>
            throw new InvalidOperationException("壊れた");
    }
}
