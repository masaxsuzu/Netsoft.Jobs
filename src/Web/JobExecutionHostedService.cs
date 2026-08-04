using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Web;

/// <summary>
/// 実行エンジンを常駐させる殻。変更通知（<see cref="JobChangeFeed"/>）を
/// エンジンの合図（<see cref="JobQueueSignal"/>）へ結線し、<see cref="JobExecutionEngine.RunAsync"/> を呼ぶ。
/// </summary>
/// <remarks>
/// <para>
/// エンジンはホスティングに依存しない作りになっている（エンジン側の注記を参照）。
/// ここが持つのは結線と起動だけで、ループも判断も持たない。
/// </para>
/// <para>
/// <b>この結線は「Job の全書き込みが IJobStore として登録された NotifyingJobStore を通る」
/// ことに依存する。</b>登録・キャンセル・エンジン自身の遷移はすべてそこを通って
/// <see cref="JobChangeFeed"/> を発火するので、合図はここ経由で必ずエンジンに届く。
/// 将来 store を迂回して DB へ直接書く経路を作ると、その Job は Queued のまま
/// 誰にも気づかれずに止まる（エラーはどこにも出ない）。安全網のポーリングは
/// 意図的に置いていない（利用者の決定。合図 + 起動時スキャンのみ）。
/// </para>
/// </remarks>
public sealed class JobExecutionHostedService : BackgroundService
{
    private readonly JobExecutionEngineFactory _engines;
    private readonly JobQueueSignal _signal;
    private readonly JobChangeFeed _feed;

    /// <remarks>
    /// エンジンではなくファクトリを受け取る。エンジンは起動時復旧を済ませてからでないと
    /// 手に入らず、生成に await が要るので DI から直接は解決できない
    /// （<see cref="JobExecutionEngineFactory"/> の注記を参照）。
    /// </remarks>
    public JobExecutionHostedService(
        JobExecutionEngineFactory engines,
        JobQueueSignal signal,
        JobChangeFeed feed)
    {
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(feed);

        _engines = engines;
        _signal = signal;
        _feed = feed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService の StartAsync は ExecuteAsync が最初に譲るまで同期実行される。
        // 起動時復旧や溜まっていた Job の実行でホストの起動（HTTP の受付開始）を
        // 待たせないよう、先にスレッドを返す。
        await Task.Yield();

        // 購読はエンジンを走らせる前に済ませる。逆順だと、起動時スキャンが null を見てから
        // 購読が繋がるまでの書き込みを誰も合図にできず、その Job は次の書き込みまで止まる。
        // 復旧より前に繋ぐのは、復旧の最中に届いた書き込みも取りこぼさないため。
        // 合図は溜め込まずに 1 つだけ保つので、早く繋いで損はない。
        _feed.Changed += _signal.Set;
        try
        {
            JobExecutionEngine engine = await _engines.StartAsync(stoppingToken);
            await engine.RunAsync(stoppingToken);
        }
        finally
        {
            // 外し忘れると、エンジンが止まった後も発火のたびに合図だけが溜まり続ける。
            _feed.Changed -= _signal.Set;
        }
    }
}
