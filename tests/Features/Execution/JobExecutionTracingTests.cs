using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// job.execute スパンのテスト。属性・Error の付け方・登録トレースへの Link と、
/// 購読者がいないときに何も起きない（trace context の読み出しすら無い）ことを固定する。
/// </summary>
/// <remarks>
/// 購読は名前ではなく、このテストが作った ActivitySource の参照一致で絞る。
/// 名前で絞ると、並行して走る他のテストクラスの同名 ActivitySource まで購読してしまい、
/// 「購読者がいない」検証がテストの実行順に左右される。
/// </remarks>
public sealed class JobExecutionTracingTests : IDisposable
{
    private const string HandledJobType = "test-job";

    private static readonly DateTimeOffset Now = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly RunningJobRegistry _runningJobs = new();
    private readonly JobQueueSignal _signal = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly RecordingJobTraceContextStore _traceContexts = new();
    private readonly JobExecutionInstrumentation _instrumentation;

    public JobExecutionTracingTests() =>
        _instrumentation = new JobExecutionInstrumentation(_meterFactory, _store, _timeProvider, _traceContexts, NullLogger<JobExecutionInstrumentation>.Instance);

    public void Dispose()
    {
        _instrumentation.Dispose();
        _meterFactory.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task 完走したスパンにはJobの属性が付きErrorにならない()
    {
        using RecordedActivities activities = new(_instrumentation.ActivitySource);
        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        await AddRegisteredAsync("job-1");

        Assert.True(await (await CreateEngineAsync(handler)).RunOnceAsync(CancellationToken.None));

        Activity span = Assert.Single(activities.Stopped);
        Assert.Equal("job.execute", span.OperationName);
        Assert.Equal("job-1", span.GetTagItem("job.id"));
        Assert.Equal(HandledJobType, span.GetTagItem("job.type"));
        Assert.Equal(nameof(JobStatus.Completed), span.GetTagItem("job.status"));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
    }

    [Fact]
    public async Task ハンドラが失敗したスパンはErrorになる()
    {
        using RecordedActivities activities = new(_instrumentation.ActivitySource);
        ControllableJobHandler handler = new(HandledJobType);
        handler.Throw(new InvalidOperationException("集計元のファイルがありません。"));
        await AddRegisteredAsync("job-1");

        Assert.True(await (await CreateEngineAsync(handler)).RunOnceAsync(CancellationToken.None));

        Activity span = Assert.Single(activities.Stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Contains("集計元のファイルがありません。", span.StatusDescription);
        Assert.Equal(nameof(JobStatus.Failed), span.GetTagItem("job.status"));
    }

    [Fact]
    public async Task キャンセルされたスパンはErrorにならない()
    {
        // キャンセルは利用者が意図した結末で、失敗ではない。Error にすると誤検知になる。
        using RecordedActivities activities = new(_instrumentation.ActivitySource);
        ControllableJobHandler handler = new(HandledJobType);
        await AddRegisteredAsync("job-1");
        JobExecutionEngine engine = await CreateEngineAsync(handler);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;

        Job job = await FindAsync("job-1");
        Assert.True(job.Apply(JobTrigger.RequestCancel, Now).IsAllowed);
        Assert.True(await _store.UpdateAsync(job, CancellationToken.None));
        Assert.True(_runningJobs.TryRequestCancel(JobId.From("job-1")));
        Assert.True(await running);

        Activity span = Assert.Single(activities.Stopped);
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.Equal(nameof(JobStatus.Cancelled), span.GetTagItem("job.status"));
    }

    [Fact]
    public async Task 保存されたTraceContextへのLinkがスパンに付く()
    {
        using RecordedActivities activities = new(_instrumentation.ActivitySource);

        // 登録側が保存する traceparent を演じる。Link の照合は trace id で行う。
        ActivityTraceId registeredTrace = ActivityTraceId.CreateRandom();
        string traceParent = $"00-{registeredTrace}-{ActivitySpanId.CreateRandom()}-01";
        await _traceContexts.SaveAsync(JobId.From("job-1"), traceParent, CancellationToken.None);

        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        await AddRegisteredAsync("job-1");

        Assert.True(await (await CreateEngineAsync(handler)).RunOnceAsync(CancellationToken.None));

        Activity span = Assert.Single(activities.Stopped);
        ActivityLink link = Assert.Single(span.Links);
        Assert.Equal(registeredTrace, link.Context.TraceId);

        // 実行スパン自身は登録トレースの子ではない。繋がりは Link だけで表す。
        Assert.NotEqual(registeredTrace, span.TraceId);
        Assert.Equal(1, _traceContexts.FindCalls);
    }

    [Fact]
    public async Task TraceContextが保存されていないスパンにはLinkが付かない()
    {
        using RecordedActivities activities = new(_instrumentation.ActivitySource);
        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        await AddRegisteredAsync("job-1");

        Assert.True(await (await CreateEngineAsync(handler)).RunOnceAsync(CancellationToken.None));

        Activity span = Assert.Single(activities.Stopped);
        Assert.Empty(span.Links);
    }

    [Fact]
    public async Task 解釈できないTraceContextはLinkなしで実行される()
    {
        using RecordedActivities activities = new(_instrumentation.ActivitySource);
        await _traceContexts.SaveAsync(JobId.From("job-1"), "これは traceparent ではない", CancellationToken.None);

        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        await AddRegisteredAsync("job-1");

        Assert.True(await (await CreateEngineAsync(handler)).RunOnceAsync(CancellationToken.None));

        Activity span = Assert.Single(activities.Stopped);
        Assert.Empty(span.Links);
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);
    }

    [Fact]
    public async Task TraceContextの読み出しが失敗してもJobは実行される()
    {
        // 観測は本体の妨げにならない。読めなければ Link が欠けるだけで、実行はそのまま進む。
        using RecordedActivities activities = new(_instrumentation.ActivitySource);
        _traceContexts.FindFailure = new IOException("観測の置き場が壊れています。");

        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        await AddRegisteredAsync("job-1");

        Assert.True(await (await CreateEngineAsync(handler)).RunOnceAsync(CancellationToken.None));

        Activity span = Assert.Single(activities.Stopped);
        Assert.Empty(span.Links);
        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);
    }

    [Fact]
    public async Task 購読者がいなければTraceContextを読まない()
    {
        // Listener を作らない。スパンが立たない構成では、Link のための I/O も一切足さない。
        ControllableJobHandler handler = Released(new ControllableJobHandler(HandledJobType));
        await AddRegisteredAsync("job-1");

        Assert.True(await (await CreateEngineAsync(handler)).RunOnceAsync(CancellationToken.None));

        Assert.Equal(JobStatus.Completed, (await FindAsync("job-1")).Status);
        Assert.Equal(0, _traceContexts.FindCalls);
    }

    private Task<JobExecutionEngine> CreateEngineAsync(params IJobHandler[] handlers) =>
        JobExecutionEngine.StartAsync(
            _store,
            new JobHandlerRegistry(handlers),
            _runningJobs,
            _signal,
            _timeProvider,
            _instrumentation,
            NullLogger<JobExecutionEngine>.Instance,
            CancellationToken.None);

    private static ControllableJobHandler Released(ControllableJobHandler handler)
    {
        // 止める必要が無いテストでは、先に解放しておけば実行は即座に終わる。
        handler.Release();
        return handler;
    }

    private async Task<Job> AddRegisteredAsync(string id)
    {
        Job job = Job.Create(JobId.From(id), $"Job {id}", HandledJobType, string.Empty, Now);
        await _store.AddAsync(job, CancellationToken.None);
        return job;
    }

    private async Task<Job> FindAsync(string id) =>
        await _store.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");

    /// <summary>
    /// 指定した ActivitySource（参照一致）のスパンを、止まった順に集める購読者。
    /// </summary>
    /// <remarks>
    /// 集めるのは止まったスパンだけ。job.status のような結末の属性はスパンの終わり際に
    /// 付くので、開始時点を集めても検証にならない。Dispose で購読を必ず外し、
    /// 他のテストへ購読を漏らさない。
    /// </remarks>
    private sealed class RecordedActivities : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _stopped = [];

        public RecordedActivities(ActivitySource source)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = candidate => ReferenceEquals(candidate, source),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                    ActivitySamplingResult.AllData,
                ActivityStopped = _stopped.Add,
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Stopped => _stopped;

        public void Dispose() => _listener.Dispose();
    }
}
