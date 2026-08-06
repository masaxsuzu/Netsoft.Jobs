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
/// 将来 store を迂回して DB へ直接書く経路を作ると、その Job は待ち行列に居るまま
/// 誰にも気づかれずに止まる（エラーはどこにも出ない）。安全網のポーリングは
/// 意図的に置いていない（利用者の決定。合図 + 起動時スキャンのみ）。
/// </para>
/// </remarks>
public sealed class JobExecutionHostedService : BackgroundService, IHostedLifecycleService
{
    private readonly JobExecutionEngineFactory _engines;
    private readonly JobQueueSignal _signal;
    private readonly JobChangeFeed _feed;

    /// <remarks>
    /// <see cref="StartingAsync"/> で起こしたエンジンを <see cref="ExecuteAsync"/> へ渡すためだけの場。
    /// 「まだ作っていない」状態がここに 1 つ戻るが、消費するのはこのクラスだけで、
    /// 外から見えるところ（DI や <see cref="JobExecutionEngineFactory"/>）には漏れない。
    /// </remarks>
    private JobExecutionEngine? _engine;

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

    /// <summary>
    /// 購読を繋ぎ、起動時復旧を済ませたエンジンを起こす。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ここが <see cref="ExecuteAsync"/> ではないことが、復旧の正しさそのものである。</b>
    /// ホストは「全員の StartingAsync → 全員の StartAsync → 全員の StartedAsync」の順で
    /// 立ち上がる。Kestrel が口を開けるのは自分の StartAsync なので、ここに置いた処理は
    /// <b>HTTP の受付が始まる前</b>に必ず終わっている。
    /// </para>
    /// <para>
    /// 復旧は「ハンドラが 1 つも走っていない」ことを前提に、非終端で取り残された Job を
    /// Failed へ畳む。この前提は単一プロセスなら常に真だが、<b>API は別の書き手</b>である。
    /// 受付が開いた後に復旧が走ると、利用者の編集（Running のまま版だけ進む）と衝突して
    /// 書き戻しが弾かれ、その Job は Running のまま誰にも閉じられなくなる
    /// ── エンジンは待ち行列しか拾わず、キャンセルは Cancelling、一時停止は Pausing で
    /// 固着し、復旧はこのプロセスで二度と走らない。実測で再現した回帰である。
    /// かつては <see cref="ExecuteAsync"/> で <c>Task.Yield</c> してから復旧しており、
    /// この窓が開いていた（<c>Task.Yield</c> を外しても閉じない。Kestrel の
    /// HostedService はこのクラスより先に登録されるので、順序が変わらない）。
    /// </para>
    /// <para>
    /// 待たせる時間は 1 行の UPDATE 分しかない。Running を書くのはエンジンだけで、
    /// エンジンは 1 件を待ってから次を取りに行くので、異常終了時に非終端で残る Job は
    /// 高々 1 件（Pausing / Cancelling も Running からしか来ない）。
    /// 溜まっていた Job の実行は今までどおり <see cref="ExecuteAsync"/> 側なので、
    /// 起動が実行に引きずられることも無い。
    /// </para>
    /// <para>
    /// 購読をここで繋ぐのは、復旧の完了から実行ループの初回スキャンまでの間に
    /// 受け付けた登録を取りこぼさないため。合図は溜め込まずに 1 つだけ保つので、
    /// 早く繋いで損はない。
    /// </para>
    /// </remarks>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        _feed.Changed += _signal.Set;
        _engine = await _engines.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService の StartAsync は ExecuteAsync が最初に譲るまで同期実行される。
        // 溜まっていた Job の実行でホストの起動を待たせないよう、先にスレッドを返す。
        await Task.Yield();

        try
        {
            // StartingAsync が先に走ることはホストが保証する。null なら結線の事故。
            JobExecutionEngine engine = _engine
                ?? throw new InvalidOperationException("起動時復旧が済んでいません。");

            await engine.RunAsync(stoppingToken);
        }
        finally
        {
            // 外し忘れると、エンジンが止まった後も発火のたびに合図だけが溜まり続ける。
            _feed.Changed -= _signal.Set;
        }
    }
}
