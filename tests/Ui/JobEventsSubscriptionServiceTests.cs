using System.IO.Pipelines;
using System.Net;
using System.Text;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// 変更通知の購読サービス。SSE を模したハンドラと針を進める時計で、
/// 「合図がいつ発火するか」を実時間に依存せず固定する。
/// </summary>
/// <remarks>
/// push（SSE）は最適化で、正しさは「再接続直後の合図」と「フォールバックポーリング」の
/// 2 つの保険が支える設計（サービス本体の注記を参照）。ここではその保険が
/// 確かに働くことを固定する。実ホストとの結線は JobEventsIntegrationTests の領分。
/// </remarks>
public sealed class JobEventsSubscriptionServiceTests : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private readonly SseServerStub _server = new();
    private readonly JobChangeFeed _feed = new();
    private readonly SemaphoreSlim _signals = new(0);
    private readonly ManualTimeProvider _time = new();
    private readonly UiOptions _options = new()
    {
        PollingInterval = TimeSpan.FromSeconds(30),
        ReconnectInterval = TimeSpan.FromSeconds(5),
    };

    private readonly JobEventsSubscriptionService _service;

    public JobEventsSubscriptionServiceTests()
    {
        _feed.Changed += () => _signals.Release();
        _service = new JobEventsSubscriptionService(
            new StubHttpClientFactory(_server), _feed, _options, _time);
    }

    public void Dispose()
    {
        _service.Dispose();
        _signals.Dispose();
    }

    /// <summary>
    /// 接続できた直後に 1 回発火する。繋がっていなかった間の変更を取りこぼしている
    /// 可能性があるためで、画面は一覧を丸ごと取り直すから合図 1 回で追いつける。
    /// </summary>
    [Fact]
    public async Task 接続に成功すると合図が1回発火する()
    {
        await _service.StartAsync(CancellationToken.None);

        Assert.True(await _signals.WaitAsync(WaitLimit), "接続直後の合図が届かない。");
        Assert.Equal(1, _server.Attempts);

        await StopAsync();
    }

    [Fact]
    public async Task changedイベントが届くたびに合図が発火する()
    {
        await _service.StartAsync(CancellationToken.None);
        Assert.True(await _signals.WaitAsync(WaitLimit), "接続直後の合図が届かない。");

        await _server.Connection(0).WriteAsync("data: changed\n\n");
        Assert.True(await _signals.WaitAsync(WaitLimit), "1 回目の変更の合図が届かない。");

        await _server.Connection(0).WriteAsync("data: changed\n\n");
        Assert.True(await _signals.WaitAsync(WaitLimit), "2 回目の変更の合図が届かない。");

        await StopAsync();
    }

    /// <summary>
    /// keep-alive のコメント行は TCP の生存確認であって、画面に何もさせない契約
    /// （JobEventsEndpoint 参照）。合図に変えてしまうと無駄な読み直しが 15 秒ごとに走る。
    /// </summary>
    [Fact]
    public async Task keepaliveコメントでは合図を発火しない()
    {
        await _service.StartAsync(CancellationToken.None);
        Assert.True(await _signals.WaitAsync(WaitLimit), "接続直後の合図が届かない。");

        // コメント行 → 合図の順で流す。合図が届いた時点でコメント行は処理済み
        //（読み取りは 1 本のループで直列）なので、「発火しなかった」を時間で待たずに言える。
        await _server.Connection(0).WriteAsync(": keep-alive\n\n");
        await _server.Connection(0).WriteAsync("data: changed\n\n");

        Assert.True(await _signals.WaitAsync(WaitLimit), "変更の合図が届かない。");
        Assert.Equal(0, _signals.CurrentCount);

        await StopAsync();
    }

    /// <summary>
    /// 切断されたら間隔を置いて繋ぎ直し、繋がった直後に必ず合図を発火する。
    /// 切断中の変更は SSE では届いていないので、この合図が取りこぼしを引き受ける。
    /// </summary>
    [Fact]
    public async Task 切断後に針を進めると再接続して合図が発火する()
    {
        await _service.StartAsync(CancellationToken.None);
        Assert.True(await _signals.WaitAsync(WaitLimit), "接続直後の合図が届かない。");

        _server.Connection(0).Close();

        // タイマー 1 本目はポーリング（StartAsync が戻る前に張られる）、
        // 2 本目が切断後の再接続待ち。張られてから針を進めないと空振りする。
        await _time.WaitForTimersAsync(2).WaitAsync(WaitLimit);
        _time.Advance(_options.ReconnectInterval);

        Assert.True(await _signals.WaitAsync(WaitLimit), "再接続直後の合図が届かない。");
        Assert.Equal(2, _server.Attempts);

        await StopAsync();
    }

    /// <summary>
    /// SSE が死んでいても、ポーリングが一定間隔で合図を発火する。push は最適化で
    /// あって、これが画面を最新へ追いつかせる唯一の保険になる。
    /// </summary>
    [Fact]
    public async Task SSEに繋がらなくてもポーリング間隔で合図が発火し続ける()
    {
        _server.RefuseConnections = true;

        await _service.StartAsync(CancellationToken.None);

        // ポーリングの期限（1 本目）は StartAsync が戻る前に張られるが、SSE 側の
        // 再接続待ち（2 本目）は HttpClient が最初の送信前に譲る分だけ遅れて張られる。
        // 両方が張られてから進めないと、進めた後に張られた期限が永久に来ない。
        await _time.WaitForTimersAsync(2).WaitAsync(WaitLimit);

        _time.Advance(_options.PollingInterval);
        Assert.True(await _signals.WaitAsync(WaitLimit), "1 回目のポーリングの合図が届かない。");

        // 次の周回の期限（と、失敗し続ける SSE の再接続待ち）が張り直されるのを待ってから進める。
        await _time.WaitForTimersAsync(4).WaitAsync(WaitLimit);
        _time.Advance(_options.PollingInterval);
        Assert.True(await _signals.WaitAsync(WaitLimit), "2 回目のポーリングの合図が届かない。");

        // 合図はポーリングの分だけ。接続に失敗した SSE 側が発火していないこと。
        Assert.Equal(0, _signals.CurrentCount);

        await StopAsync();
    }

    /// <summary>
    /// 停止要求で両方のループが終わる。終わらないとホストの Shutdown が
    /// StopAsync のタイムアウトまで待たされる。
    /// </summary>
    [Fact]
    public async Task 停止すると購読もポーリングも終わる()
    {
        await _service.StartAsync(CancellationToken.None);
        Assert.True(await _signals.WaitAsync(WaitLimit), "接続直後の合図が届かない。");

        await StopAsync();

        Assert.True(_service.ExecuteTask?.IsCompleted, "停止後もループが動いている。");
    }

    /// <summary>
    /// StopAsync に期限付きのトークンを渡す。ループが停止要求を無視する退行が
    /// あったとき、テストが永久に待つのではなく失敗として観測するため。
    /// </summary>
    private async Task StopAsync()
    {
        using CancellationTokenSource limit = new(WaitLimit);
        await _service.StopAsync(limit.Token);
    }

    /// <summary>常に同じハンドラでクライアントを作る工場。DI の代わり。</summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            // サービスは接続ごとにクライアントを使い捨てるので、ハンドラは破棄させない。
            new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://api.example"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }

    /// <summary>
    /// SSE サーバの代役。接続ごとに Pipe を作り、テストが好きなタイミングで
    /// 行を流したり切断したりできる。
    /// </summary>
    private sealed class SseServerStub : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<SseConnection> _connections = [];

        /// <summary>true の間、接続の試みを失敗させる。</summary>
        public bool RefuseConnections { get; set; }

        /// <summary>接続を試みられた回数。再接続の検証に使う。</summary>
        public int Attempts
        {
            get
            {
                lock (_gate)
                {
                    return _attempts;
                }
            }
        }

        private int _attempts;

        public SseConnection Connection(int index)
        {
            lock (_gate)
            {
                return _connections[index];
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _attempts++;
                if (RefuseConnections)
                {
                    throw new HttpRequestException("接続を拒否するテスト設定");
                }

                SseConnection connection = new();
                _connections.Add(connection);

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(connection.ResponseStream),
                });
            }
        }
    }

    /// <summary>1 本の SSE 接続。書き込みが即座に読み手へ届くよう Pipe で繋ぐ。</summary>
    private sealed class SseConnection
    {
        private readonly Pipe _pipe = new();

        public Stream ResponseStream => _pipe.Reader.AsStream();

        public async Task WriteAsync(string text) =>
            await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));

        /// <summary>サーバ側からの切断。読み手は EOF を見る。</summary>
        public void Close() => _pipe.Writer.Complete();
    }
}
