namespace Netsoft.Jobs.Ui;

/// <summary>
/// API ホストの変更通知（SSE）を購読し、<see cref="JobChangeFeed"/> の合図に変える常駐サービス。
/// </summary>
/// <remarks>
/// <para>
/// push（SSE）は最適化であって、正しさを push に依存させない。そのための保険が 2 つある。
/// 切断からの<b>再接続</b>では、繋がった直後に必ず 1 回合図を発火する。切断中の変更を
/// 取りこぼしているためで、画面は一覧を取り直すだけだから合図 1 回で全部追いつける。
/// さらに SSE と独立した<b>フォールバックポーリング</b>が一定間隔で合図を発火する。
/// SSE が生きていれば無駄打ち（画面が同じ一覧を描き直すだけ）だが、SSE が死んで
/// いるときに画面を最新へ追いつかせる唯一の保険になる。
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

        // 繋がった直後に必ず 1 回発火する。繋がっていなかった間の変更を取りこぼして
        // いる可能性があり、画面は一覧を丸ごと取り直すので合図 1 回で追いつける。
        // 初回接続も区別しない。区別して得るものが無く、余分な合図は無害だから。
        _feed.Publish();

        using StreamReader reader = new(await response.Content.ReadAsStreamAsync(stoppingToken));

        // サーバは data 行（合図）とコメント行（keep-alive）しか流さない。
        // コメント行は TCP が生きていることの確認であって、画面に何もさせない。
        while (await reader.ReadLineAsync(stoppingToken) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                _feed.Publish();
            }
        }

        // ここに来るのはサーバが応答を閉じたとき。呼び出し元が繋ぎ直す。
    }

    /// <summary>
    /// SSE と独立に、一定間隔で合図を発火し続ける。
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

            _feed.Publish();
        }
    }
}
