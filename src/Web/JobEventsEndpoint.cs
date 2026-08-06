using System.Threading.Channels;

using Netsoft.Jobs.Features;

namespace Netsoft.Jobs.Web;

/// <summary>
/// Job の変更通知を SSE (text/event-stream) として外へ出す入口。
/// 別プロセスの UI が <see cref="JobChangeFeed"/> の合図を HTTP 越しに購読するための口。
/// </summary>
/// <remarks>
/// <para>
/// 流すのは合図（<c>data: changed</c>）の 1 種類だけで、変更内容は運ばない。
/// <see cref="JobChangeFeed"/> の注記と同じ思想で、クライアントは受けたら一覧を
/// 取り直すだけだから差分に使い道が無い。内容を運ぶと順序と欠損ゼロの配送保証が
/// 正しさの前提になるが、合図だけなら取りこぼしても次の読み直しで自己修復する。
/// </para>
/// <para>
/// .NET の <c>TypedResults.ServerSentEvents</c> / <c>SseItem&lt;T&gt;</c> は使わず、
/// 応答へ直接書く。keep-alive は「クライアントの処理を起こさないがバッファは流す」
/// ために SSE のコメント行（<c>: keep-alive</c>）で送りたいが、SseItem はイベントしか
/// 表現できずコメント行を書く口が無い。合図とコメントの 2 行を手で書くだけなので、
/// フォーマッタに払う複雑さも無い。
/// </para>
/// </remarks>
public static class JobEventsEndpoint
{
    /// <summary>
    /// 静かなときにコメント行を流す間隔。死んだ TCP の検出と、途中のプロキシが
    /// 応答を抱え込まないようにするため。短いほど検出は早いが無駄も増える。
    /// </summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <c>GET /api/jobs/events</c> を登録する。Web 側はこれを呼ぶだけでよい。
    /// </summary>
    public static IEndpointRouteBuilder MapJobEvents(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(JobApiRoutes.JobEvents, StreamAsync)
            .WithName("JobEvents");

        return endpoints;
    }

    /// <summary>
    /// 切断されるまで合図と keep-alive を書き続ける。切断で正常に終わる。
    /// </summary>
    /// <remarks>
    /// public なのはテストが接続を模して直接呼ぶため。切断（RequestAborted）後に
    /// 購読が外れることを、実時間を待たずにタスクの完了として観測できる。
    /// </remarks>
    public static async Task StreamAsync(HttpContext context, JobChangeFeed feed, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(timeProvider);

        CancellationToken disconnected = context.RequestAborted;

        // 接続ごとに容量 1 の箱を置き、読み手が追いつくまでの重複した合図は DropWrite で落とす。
        // 合図はペイロードを持たないので「変更があった」以上の情報が無く、
        // 2 つ溜めても 1 つと同じ意味にしかならない。落としても情報は失われない。
        Channel<byte> signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

        // TryWrite は満杯でも待たずに false を返すだけ。発火元は store の書き込みスレッドなので、
        // 遅いクライアントの読み取りを書き込みが待つ形にしてはいけない。
        Action signal = () => signals.Writer.TryWrite(0);

        // 購読は最初の await より前に済ませる。応答が始まった時点で購読済みであることが
        // 保証されるので、クライアントは「接続できたのに直後の変更を取りこぼす」を心配しなくてよい。
        feed.Changed += signal;
        try
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";

            // ヘッダを先に送り出す。最初の合図まで応答が始まらないと、
            // クライアントは接続が確立したのかどうかも分からない。
            await context.Response.Body.FlushAsync(disconnected);

            while (true)
            {
                // keep-alive の間隔は TimeProvider の時計で計る。実時間に縛るとテストが待つしかない。
                using CancellationTokenSource quiet = new(KeepAliveInterval, timeProvider);
                using CancellationTokenSource waiting =
                    CancellationTokenSource.CreateLinkedTokenSource(disconnected, quiet.Token);

                try
                {
                    _ = await signals.Reader.ReadAsync(waiting.Token);
                }
                catch (OperationCanceledException) when (!disconnected.IsCancellationRequested)
                {
                    // 切断されていないのに待ちが切れた＝静かなまま間隔が過ぎた。
                    // コメント行は SSE の仕様上イベントとして配られないので、
                    // クライアントに何もさせずに TCP が生きていることだけ確かめられる。
                    await context.Response.WriteAsync(": keep-alive\n\n", disconnected);
                    await context.Response.Body.FlushAsync(disconnected);
                    continue;
                }

                await context.Response.WriteAsync("data: changed\n\n", disconnected);
                await context.Response.Body.FlushAsync(disconnected);
            }
        }
        catch (OperationCanceledException)
        {
            // 切断は SSE の正常な終わり方。呼び出し側へ伝えるべき失敗ではない。
        }
        finally
        {
            // 必ず外す。外し忘れると接続の数だけデリゲートが feed に溜まり続け、
            // 切れた接続へ向けた合図（チャネルへの書き込み）が永久に走り続ける。
            feed.Changed -= signal;
        }
    }
}
