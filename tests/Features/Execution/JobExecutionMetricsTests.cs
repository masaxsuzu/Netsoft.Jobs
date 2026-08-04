using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// 実行基盤のメトリクスのテスト。値は <see cref="FixedTimeProvider"/> で固定して検証する。
/// </summary>
/// <remarks>
/// 実行の正しさは <see cref="JobExecutionEngineTests"/> が見ている。こちらは計器に何が
/// 記録されるかだけを見る。タグに JobId が無い（閉じた集合だけ）ことも、ここで固定する。
/// </remarks>
public sealed class JobExecutionMetricsTests : IDisposable
{
    private const string HandledJobType = "test-job";

    private static readonly DateTimeOffset Now = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly FixedTimeProvider _timeProvider = new(Now);
    private readonly RunningJobRegistry _runningJobs = new();
    private readonly JobQueueSignal _signal = new();
    private readonly TestMeterFactory _meterFactory = new();
    private readonly JobExecutionInstrumentation _instrumentation;

    public JobExecutionMetricsTests() =>
        _instrumentation = new JobExecutionInstrumentation(_meterFactory, _store, _timeProvider);

    public void Dispose()
    {
        _instrumentation.Dispose();
        _meterFactory.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task 完走すると待ち時間と所要時間と完了数が記録される()
    {
        using MetricCollector<double> queueWait = CreateCollector<double>("netsoft.jobs.queue_wait");
        using MetricCollector<double> duration = CreateCollector<double>("netsoft.jobs.execution_duration");
        using MetricCollector<long> finished = CreateCollector<long>("netsoft.jobs.finished");

        ControllableJobHandler handler = new(HandledJobType);

        // 登録は 45 秒前、実行は 180 秒かかったことにする。どの時間差がどの計器に
        // 入るのかを、値そのもので区別できるようにする。
        await AddQueuedAsync("job-1", createdAt: Now.AddSeconds(-45));
        JobExecutionEngine engine = CreateEngine(handler);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;
        _timeProvider.UtcNow = Now.AddSeconds(180);
        handler.Release();
        Assert.True(await running);

        CollectedMeasurement<double> wait = Assert.Single(queueWait.GetMeasurementSnapshot());
        Assert.Equal(45, wait.Value);

        // タグは閉じた集合だけ。個数まで見るのは、JobId のような高カーディナリティの
        // タグが紛れ込むと Job の数だけ系列が増えるから。
        CollectedMeasurement<double> executed = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal(180, executed.Value);
        Assert.Equal(2, executed.Tags.Count);
        Assert.Equal(HandledJobType, executed.Tags["job.type"]);
        Assert.Equal(nameof(JobStatus.Completed), executed.Tags["job.status"]);

        CollectedMeasurement<long> count = Assert.Single(finished.GetMeasurementSnapshot());
        Assert.Equal(1, count.Value);
        KeyValuePair<string, object?> tag = Assert.Single(count.Tags);
        Assert.Equal("job.status", tag.Key);
        Assert.Equal(nameof(JobStatus.Completed), tag.Value);
    }

    [Fact]
    public async Task 失敗した終端はFailedのタグで数えられる()
    {
        using MetricCollector<long> finished = CreateCollector<long>("netsoft.jobs.finished");

        ControllableJobHandler handler = new(HandledJobType);
        handler.Throw(new InvalidOperationException("集計元のファイルがありません。"));
        await AddQueuedAsync("job-1");

        Assert.True(await CreateEngine(handler).RunOnceAsync(CancellationToken.None));

        CollectedMeasurement<long> count = Assert.Single(finished.GetMeasurementSnapshot());
        Assert.Equal(1, count.Value);
        Assert.Equal(nameof(JobStatus.Failed), count.Tags["job.status"]);
    }

    [Fact]
    public async Task キャンセルされた終端はCancelledのタグで数えられる()
    {
        using MetricCollector<long> finished = CreateCollector<long>("netsoft.jobs.finished");

        ControllableJobHandler handler = new(HandledJobType);
        await AddQueuedAsync("job-1");
        JobExecutionEngine engine = CreateEngine(handler);

        Task<bool> running = engine.RunOnceAsync(CancellationToken.None);
        await handler.Entered;

        // キャンセル機能（コマンド側）と同じ順序。状態を進めてから実行中のハンドラへ伝える。
        Job job = await FindAsync("job-1");
        Assert.True(job.Apply(JobTrigger.RequestCancel, Now).IsAllowed);
        Assert.True(await _store.UpdateAsync(job, JobStatus.Running, CancellationToken.None));
        Assert.True(_runningJobs.TryRequestCancel(JobId.From("job-1")));
        Assert.True(await running);

        CollectedMeasurement<long> count = Assert.Single(finished.GetMeasurementSnapshot());
        Assert.Equal(1, count.Value);
        Assert.Equal(nameof(JobStatus.Cancelled), count.Tags["job.status"]);
    }

    [Fact]
    public async Task 最古の待機中Jobの滞留時間が観測できる()
    {
        using MetricCollector<double> age = CreateCollector<double>("netsoft.jobs.oldest_queued_age");

        // 最古の 1 件だけが観測される。新しい方の 30 秒が出たら選び方が間違っている。
        await AddQueuedAsync("job-1", createdAt: Now.AddSeconds(-120));
        await AddQueuedAsync("job-2", createdAt: Now.AddSeconds(-30));

        age.RecordObservableInstruments();

        Assert.Equal(120, Assert.Single(age.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public void 待機中のJobが無ければ滞留時間は0になる()
    {
        using MetricCollector<double> age = CreateCollector<double>("netsoft.jobs.oldest_queued_age");

        age.RecordObservableInstruments();

        Assert.Equal(0, Assert.Single(age.GetMeasurementSnapshot()).Value);
    }

    private MetricCollector<T> CreateCollector<T>(string instrumentName)
        where T : struct =>
        new(_meterFactory, JobExecutionInstrumentation.Name, instrumentName);

    private JobExecutionEngine CreateEngine(params IJobHandler[] handlers) =>
        new(
            _store,
            new JobHandlerRegistry(handlers),
            _runningJobs,
            _signal,
            _timeProvider,
            _instrumentation,
            new NullJobTraceContextStore(),
            NullLogger<JobExecutionEngine>.Instance);

    private async Task<Job> AddQueuedAsync(string id, DateTimeOffset? createdAt = null)
    {
        Job job = Job.Create(JobId.From(id), $"Job {id}", HandledJobType, string.Empty, createdAt ?? Now);
        await _store.AddAsync(job, CancellationToken.None);
        return job;
    }

    private async Task<Job> FindAsync(string id) =>
        await _store.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");
}
