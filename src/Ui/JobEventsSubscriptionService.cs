namespace Netsoft.Jobs.Ui;

/// <summary>
/// API ホストの変更通知（SSE）を購読し、<see cref="JobChangeFeed"/> の合図に変える常駐サービス。
/// </summary>
/// <remarks>
/// <para>
/// push（SSE）は最適化であって、正しさを push に依存させない。そのための保険が 2 つある。
/// 切断からの<b>再接続</b>では、繋がった直後に必ず 1 回合図を発火する。切断中の変更を
/// 取りこぼしているためで、画面は一覧を取り直すだけだから合図 1 回で全部追いつける。
/// さらに SSE と独立した<b>フォールバックポーリング</b>があり、SSE から
/// <see cref="UiOptions.PollingInterval"/> より長く何も届かないときだけ合図を発火する。
/// サーバは静かな間も keep-alive のコメント行をポーリングより短い間隔で流すので、
/// 受信の途絶は接続の死（TCP の無言死を含む）を意味する。接続が健在な間は発火せず、
/// SSE が死んでいるときに画面を最新へ追いつかせる唯一の保険、という性質は変わらない。
/// </para>
/// <para>
/// 間隔はすべて <see cref="TimeProvider"/> で計る。実時間に縛るとテストが待つしかない。
/// </para>
/// </remarks>
public sealed class JobEventsSubscriptionService : BackgroundService
{
    /// <summary>
    /// DI（AddHttpClient）に登録する名前。API 呼び出し用のクライアントと分けるのは、
    /// SSE の応答は切断まで終わらないので Timeout を無限にする必要があり、
    /// その設定を通常の API 呼び出しに波及させないため。
    /// </summary>
    public const string HttpClientName = "JobEvents";

    private readonly IHttpClientFactory _clientFactory;
    private readonly JobChangeFeed _feed;
    private readonly UiOptions _options;
    private readonly TimeProvider _timeProvider;

    // SSE から最後に何かを受信した時刻（UtcNow の ticks）。受信ループとポーリングの
    // 別タスクから触るので、DateTimeOffset ではなくアトミックに読み書きできる long で持つ。
    // 初期値 0（受信なし）は「途絶している」と評価され、最初の期限からポーリングが働く。
    private long _lastReceivedTicks;

    public JobEventsSubscriptionService(
        IHttpClientFactory clientFactory,
        JobChangeFeed feed,
        UiOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _clientFactory = clientFactory;
        _feed = feed;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // ポーリングを先に呼ぶ。最初の await（Task.Delay）まで同期で進むので、
        // StartAsync が戻った時点でポーリングの期限が張られていることが保証され、
        // テストは針を進めるだけで決定的に発火を起こせる。
        Task.WhenAll(PollAsync(stoppingToken), SubscribeAsync(stoppingToken));

    /// <summary>
    /// SSE へ接続し続ける。切れたら（繋がらなかったら）間隔を置いて繋ぎ直す。
    /// </summary>
    private async Task SubscribeAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveEventsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // 接続の失敗・切断・応答の異常は全部同じ扱いでよい。何が起きたにせよ
                // できることは「間隔を置いて繋ぎ直す」だけで、取りこぼしの心配は
                // 再接続直後の合図とポーリングが引き受けている。
            }

            try
            {
                await Task.Delay(_options.ReconnectInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 1 本の SSE 接続を張り、切れるまで合図を発火し続ける。
    /// </summary>
    private async Task ReceiveEventsAsync(CancellationToken stoppingToken)
    {
        using HttpClient client = _clientFactory.CreateClient(HttpClientName);
        using HttpRequestMessage request = new(HttpMethod.Get, JobApiRoutes.JobEvents);

        // ResponseHeadersRead でヘッダだけ待つ。既定は body の終わりを待つが、
        // SSE の body は切断まで終わらない。
        using HttpResponseMessage response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
        response.EnsureSuccessStatusCode();

        // 接続の成立も受信のうち。直後の合図で画面は追いつくので、ポーリングを急がせる理由が無い。
        RecordReceived();

        // 繋がった直後に必ず 1 回発火する。繋がっていなかった間の変更を取りこぼして
        // いる可能性があり、画面は一覧を丸ごと取り直すので合図 1 回で追いつける。
        // 初回接続も区別しない。区別して得るものが無く、余分な合図は無害だから。
        _feed.Publish();

        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(stoppingToken));

        // サーバは data 行（合図）とコメント行（keep-alive）しか流さない。
        // コメント行は TCP が生きていることの確認であって、画面に何もさせないが、
        // 「接続が健在」の証拠としてはどちらも等価なので、行の種類を見る前に記録する。
        // 記録を合図より先にするのは、合図を観測した側から見て記録済みであることを
        // 保証するため（テストが実時間を待たずに書ける）。
        while (await reader.ReadLineAsync(stoppingToken) is { } line)
        {
            RecordReceived();

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                _feed.Publish();
            }
        }

        // ここに来るのはサーバが応答を閉じたとき。呼び出し元が繋ぎ直す。
    }

    /// <summary>
    /// SSE と独立に間隔を計り、受信が途絶えているときだけ合図を発火し続ける。
    /// </summary>
    private async Task PollAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PollingInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 間隔以内に SSE から何か（data 行でも keep-alive でも）届いていれば、接続は
            // 健在で取りこぼしの疑いも無いので撃たない。サーバの keep-alive（15 秒）は
            // この間隔（既定 30 秒）より短いから、接続が生きている限りここは発火しない。
            // 受信が途絶えたら（TCP の無言死を含む）従来どおり発火する。SSE が死んで
            // いるときの保険という性質は変えていない。
            long elapsed = _timeProvider.GetUtcNow().UtcTicks - Volatile.Read(ref _lastReceivedTicks);
            if (elapsed < _options.PollingInterval.Ticks)
            {
                continue;
            }

            _feed.Publish();
        }
    }

    private void RecordReceived() =>
        Volatile.Write(ref _lastReceivedTicks, _timeProvider.GetUtcNow().UtcTicks);
}
