using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 実行基盤のメトリクスとトレースの計装。
/// </summary>
/// <remarks>
/// <para>
/// BCL（System.Diagnostics.Metrics / System.Diagnostics）だけで計装する。
/// エクスポータやバックエンドの設定は持たない。購読者（MeterListener / ActivityListener）が
/// いなければ記録は捨てられ、実質ゼロコストで挙動も変わらない。
/// </para>
/// <para>
/// Meter は <see cref="IMeterFactory"/> 経由で作る。テストが MetricCollector で
/// 観測対象を factory 単位に絞れるし、Meter の寿命も factory が面倒を見てくれる。
/// </para>
/// <para>
/// メトリクスのタグは閉じた集合（job.type / job.status）だけにし、<b>JobId をタグに付けない</b>。
/// Job の数だけ系列が増えて（cardinality 爆発）メトリクスの置き場を食い潰す。
/// 個々の Job を追うのはスパン（属性は高カーディナリティでよい）の仕事。
/// </para>
/// </remarks>
public sealed class JobExecutionInstrumentation : IDisposable
{
    /// <summary>Meter と ActivitySource の名前。購読側はこの名前で購読する。</summary>
    public const string Name = "Netsoft.Jobs";

    private readonly IJobStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly IJobTraceContextStore _traceContexts;
    private readonly ILogger<JobExecutionInstrumentation> _logger;
    private readonly Histogram<double> _queueWait;
    private readonly Histogram<double> _executionDuration;
    private readonly Counter<long> _finished;

    public JobExecutionInstrumentation(
        IMeterFactory meterFactory,
        IJobStore store,
        TimeProvider timeProvider,
        IJobTraceContextStore traceContexts,
        ILogger<JobExecutionInstrumentation> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(traceContexts);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _timeProvider = timeProvider;
        _traceContexts = traceContexts;
        _logger = logger;

        Meter meter = meterFactory.Create(Name);

        _queueWait = meter.CreateHistogram<double>(
            "netsoft.jobs.queue_wait",
            unit: "s",
            description: "登録から実行開始までの待ち時間。");

        _executionDuration = meter.CreateHistogram<double>(
            "netsoft.jobs.execution_duration",
            unit: "s",
            description: "実行開始から終端までの所要時間。");

        _finished = meter.CreateCounter<long>(
            "netsoft.jobs.finished",
            unit: "{job}",
            description: "終端に達した Job の数。");

        // この計器は「Job の全書き込みは NotifyingJobStore を通り、その合図がエンジンを起こす」
        // という不変条件が破れて Job が黙って止まる故障の唯一の警報。安全網ポーリングを
        // 廃止したときに引き受けたリスクで、結線が切れてもエラーはどこにも出ず
        // 待ち行列が滞留するだけなので、滞留時間そのものを外へ出して監視で気づけるようにする。
        meter.CreateObservableGauge(
            "netsoft.jobs.oldest_waiting_age",
            ObserveOldestWaitingAge,
            unit: "s",
            description: "最も古い待機中 Job の滞留時間。待機中が無ければ 0。");

        ActivitySource = new ActivitySource(Name);
    }

    /// <summary>
    /// job.execute スパンの出どころ。
    /// </summary>
    /// <remarks>
    /// static にせずインスタンスで持つのは、テストの隔離のため。static だと並行して走る
    /// テストクラスが同じ ActivitySource を共有し、あるテストの ActivityListener が
    /// 別のテストの「購読者がいない」検証を壊す。インスタンスなら参照一致で購読を絞れる。
    /// </remarks>
    public ActivitySource ActivitySource { get; }

    /// <summary>
    /// job.execute スパンを開始する。購読者がいなければ何もせず null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// スパンの開始（context の読み出し・Link と属性の組み立て）は観測の機構であって
    /// 実行の判断を含まないので、エンジンではなくここに置く。エンジンに残るのは
    /// スパンの終わり方（結末に応じた SetStatus / SetTag）だけ。
    /// </para>
    /// <para>
    /// 登録時に保存された trace context への Link は <see cref="ActivitySource.StartActivity(string, ActivityKind, ActivityContext, IEnumerable{KeyValuePair{string, object?}}?, IEnumerable{ActivityLink}?, DateTimeOffset)"/>
    /// の引数で渡す。開始後の Activity には Link を足せないため、読み出しは開始より前に行う。
    /// </para>
    /// <para>
    /// <see cref="ActivitySource.HasListeners"/> を先に見るのは、購読者がいないとき
    /// （StartActivity が null を返す構成のとき）に <see cref="IJobTraceContextStore.FindAsync"/> の
    /// I/O まで足さないため。観測がゼロなら追加コストもゼロにする。
    /// </para>
    /// </remarks>
    public async Task<Activity?> StartExecuteActivityAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!ActivitySource.HasListeners())
        {
            return null;
        }

        IEnumerable<ActivityLink>? links = null;
        try
        {
            // 登録トレースは別プロセス（別リクエスト）のものなので isRemote。
            // 解釈できない・無い場合は Link なしで開始する。
            string? traceParent = await _traceContexts.FindAsync(job.Id, cancellationToken);
            if (traceParent is not null
                && ActivityContext.TryParse(traceParent, traceState: null, isRemote: true, out ActivityContext registered))
            {
                links = [new ActivityLink(registered)];
            }
        }
        catch (Exception exception)
        {
            // 観測の失敗で実行を妨げない。欠けるのは登録トレースへの Link だけ。
            _logger.LogWarning(
                exception,
                "Job {JobId} の登録時 trace context を読めませんでした。Link なしで実行します。",
                job.Id.Value);
        }

        // スパン属性は系列を作らないので、メトリクスと違い JobId のような
        // 高カーディナリティの値を持たせてよい。個々の実行を追うのはここの仕事。
        return ActivitySource.StartActivity(
            "job.execute",
            ActivityKind.Internal,
            parentContext: default,
            tags: [new("job.id", job.Id.Value), new("job.type", job.JobType)],
            links: links);
    }

    /// <summary>
    /// Running が確定した（開始の書き戻しに成功した）Job の待ち時間を記録する。
    /// </summary>
    public void RecordStarted(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        _queueWait.Record((job.StartedAt!.Value - job.CreatedAt).TotalSeconds);
    }

    /// <summary>
    /// 結末が確定した（終端の書き戻しに成功した）Job の所要時間と数を記録する。
    /// </summary>
    public void RecordFinished(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        KeyValuePair<string, object?> type = new("job.type", job.JobType);
        KeyValuePair<string, object?> status = new("job.status", JobStatusText.ToText(job.Status));

        _executionDuration.Record((job.FinishedAt!.Value - job.StartedAt!.Value).TotalSeconds, type, status);
        _finished.Add(1, status);
    }

    public void Dispose() => ActivitySource.Dispose();

    /// <summary>
    /// 最も古い待機中 Job の滞留時間（秒）。待機中が無ければ 0。
    /// </summary>
    /// <remarks>
    /// <para>
    /// コールバックは同期で <see cref="IJobStore"/> は非同期なので、観測のたびに
    /// <see cref="IJobStore.FindOldestWaitingAsync"/> を同期待ちで読む。エンジンに値を
    /// キャッシュさせる案は採らなかった。この計器は「エンジン（や合図の結線）が壊れて
    /// Job が黙って止まる」ことの警報であり、警報の値を疑っている当のエンジンに作らせると、
    /// エンジンが止まった瞬間に警報も一緒に止まる。真実の置き場（store）を直接読むから、
    /// どこが壊れていても待ち行列の滞留が見える。
    /// </para>
    /// <para>
    /// 同期待ちのコストは SQLite の 1 行読みで実測 30 µs 程度、走るのも購読者が
    /// スクレイプしたときだけなので、実行経路には影響しない。
    /// </para>
    /// </remarks>
    private double ObserveOldestWaitingAge()
    {
        Job? oldest = _store.FindOldestWaitingAsync(CancellationToken.None).GetAwaiter().GetResult();

        return oldest is null ? 0 : (_timeProvider.GetUtcNow() - oldest.CreatedAt).TotalSeconds;
    }
}
