using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.CancelJob;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// 実行エンジンのログのテスト。JobId で絞れば Job の一生がタイムラインとして読めることを保証する。
/// </summary>
/// <remarks>
/// 実行の正しさは <see cref="JobExecutionEngineTests"/> が見ている。こちらはログだけを見るので、
/// エンジンの組み立てには割り込み用のデコレータを挟まず、素の store を渡す。
/// </remarks>
public sealed class JobExecutionLoggingTests : IDisposable
{
    private const string HandledJobType = "test-job";

    private static readonly DateTimeOffset Now = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly JobQueueSignal _signal = new();
    private readonly RecordingLogger<JobExecutionEngine> _logger = new();

    private readonly TestMeterFactory _meterFactory = new();
    private readonly JobExecutionInstrumentation _instrumentation;

    public JobExecutionLoggingTests() =>
        _instrumentation = new JobExecutionInstrumentation(_meterFactory, _store, _timeProvider, new NullJobTraceContextStore(), NullLogger<JobExecutionInstrumentation>.Instance);

    public void Dispose()
    {
        _instrumentation.Dispose();
        _meterFactory.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task 実行開始のログにJobIdとJobTypeが残る()
    {
        JobExecutionEngine engine = await CreateEngineAsync(Released(new ControllableJobHandler(HandledJobType)));
        await AddRegisteredAsync("job-1");

        await engine.RunOnceAsync(CancellationToken.None);

        RecordedLog started = Assert.Single(_logger.Entries, entry => entry.Message.Contains("開始"));
        Assert.Equal(LogLevel.Information, started.Level);
        Assert.Equal("job-1", started.State["JobId"]);
        Assert.Equal(HandledJobType, started.State["JobType"]);
    }

    [Fact]
    public async Task 結末のログにJobIdと確定した状態が残る()
    {
        JobExecutionEngine engine = await CreateEngineAsync(Released(new ControllableJobHandler(HandledJobType)));
        await AddRegisteredAsync("job-1");

        await engine.RunOnceAsync(CancellationToken.None);

        RecordedLog finished = Assert.Single(_logger.Entries, entry => entry.Message.Contains("終了"));
        Assert.Equal(LogLevel.Information, finished.Level);
        Assert.Equal("job-1", finished.State["JobId"]);
        Assert.Equal(JobStatus.Completed, finished.State["Status"]);
        Assert.Equal(JobTrigger.Complete, finished.State["Trigger"]);
    }

    [Fact]
    public async Task 失敗の結末もJobId付きで残る()
    {
        ControllableJobHandler handler = new(HandledJobType);
        handler.Throw(new InvalidOperationException("集計元のファイルがありません。"));
        JobExecutionEngine engine = await CreateEngineAsync(handler);
        await AddRegisteredAsync("job-1");

        await engine.RunOnceAsync(CancellationToken.None);

        RecordedLog finished = Assert.Single(_logger.Entries, entry => entry.Message.Contains("終了"));
        Assert.Equal("job-1", finished.State["JobId"]);
        Assert.Equal(JobStatus.Failed, finished.State["Status"]);
        Assert.Equal(JobTrigger.Fail, finished.State["Trigger"]);
    }

    [Fact]
    public async Task 実行中のログにはスコープでJobIdとJobTypeが積まれる()
    {
        // ハンドラは parameters しか受け取らず、自分がどの Job かを知らない。
        // それでもハンドラや await の継続が書くログ行に JobId が付くのは、
        // RunHandlerAsync のスコープに積んであるから。エンジン自身がスコープの中で書く
        // 開始・結末の行で、スコープの中身を確かめる。
        JobExecutionEngine engine = await CreateEngineAsync(Released(new ControllableJobHandler(HandledJobType)));
        await AddRegisteredAsync("job-1");

        await engine.RunOnceAsync(CancellationToken.None);

        RecordedLog started = Assert.Single(_logger.Entries, entry => entry.Message.Contains("開始"));
        RecordedLog finished = Assert.Single(_logger.Entries, entry => entry.Message.Contains("終了"));

        foreach (RecordedLog entry in new[] { started, finished })
        {
            Assert.Equal("job-1", entry.Scope["JobId"]);
            Assert.Equal(HandledJobType, entry.Scope["JobType"]);
        }
    }

    /// <summary>
    /// キャンセル要求（Cancelling）より完走が勝った状況。Job 行は Completed になり、
    /// キャンセル要求の痕跡は行から消える。「要求はあった」（CancelJobHandler のログ）と
    /// 「完走が勝った」（エンジンの結末ログ）の組だけがタイムラインを再構成する材料になる。
    /// </summary>
    [Fact]
    public async Task キャンセル要求より完走が勝ったとき要求と結末の両方のログが残る()
    {
        ControllableJobHandler handler = new(HandledJobType);
        JobExecutionEngine engine = await CreateEngineAsync(handler);
        await AddRegisteredAsync("job-1");

        // 走っているハンドラは中断を観測しない作り（store を渡していない）なので、
        // Cancelling が書かれても最後まで走り切る。「完走が勝つ」はこの形でしか作れない。
        RecordingLogger<CancelJobHandler> cancelLogger = new();
        CancelJobHandler cancelHandler = new(_store, _timeProvider, cancelLogger);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;

        CancelJobResult requested = (await cancelHandler.HandleAsync("job-1", CancellationToken.None)).Value;
        Assert.True(requested.IsSuccess);

        handler.Release();
        Assert.True(await running);

        // Job 行からは要求の痕跡が消えている。
        Job? job = await _store.FindAsync(JobId.From("job-1"), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, job?.Status);

        // 要求があった事実と時刻はキャンセル側のログが持つ。
        RecordedLog request = Assert.Single(cancelLogger.Entries);
        Assert.Equal("job-1", request.State["JobId"]);
        Assert.Equal(JobStatus.Cancelling, request.State["Status"]);

        // 完走が勝ったことは結末のログの文言から読み取れる。
        RecordedLog finished = Assert.Single(_logger.Entries, entry => entry.Message.Contains("完走が勝ち"));
        Assert.Equal("job-1", finished.State["JobId"]);
        Assert.Equal(JobStatus.Completed, finished.State["Status"]);
        Assert.Equal(JobTrigger.Complete, finished.State["Trigger"]);
    }

    private Task<JobExecutionEngine> CreateEngineAsync(params IJobHandler[] handlers) =>
        JobExecutionEngine.StartAsync(
            _store,
            new JobHandlerRegistry(handlers),
            _signal,
            _timeProvider,
            _instrumentation,
            _logger,
            CancellationToken.None);

    private static ControllableJobHandler Released(ControllableJobHandler handler)
    {
        // 止める必要が無いテストでは、先に解放しておけば実行は即座に終わる。
        handler.Release();
        return handler;
    }

    private async Task AddRegisteredAsync(string id)
    {
        Job job = Job.Create(JobId.From(id), $"Job {id}", HandledJobType, "", Now);
        await _store.AddAsync(job, CancellationToken.None);
    }
}
