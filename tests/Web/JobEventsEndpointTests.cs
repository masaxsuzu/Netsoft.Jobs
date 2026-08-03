using System.IO.Pipelines;
using System.Reflection;

using Microsoft.AspNetCore.Http;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// SSE ハンドラを接続を模して直接呼び、切断と keep-alive の振る舞いを固定する。
/// </summary>
/// <remarks>
/// HTTP を通さないのは決定性のため。切断（RequestAborted）を自分のトークンで起こせば、
/// 購読解除の完了をハンドラのタスクの完了として await でき、実時間の待ちが要らない。
/// keep-alive も <see cref="ManualTimeProvider"/> の針を進めるだけで起こせる。
/// 実際のホストとの結線（ルート・ヘッダ・配信）は JobEventsApiTests が HTTP 越しに見る。
/// </remarks>
public sealed class JobEventsEndpointTests
{
    /// <summary>
    /// 切断すると購読が外れる。外れないと接続の数だけデリゲートが溜まり続ける。
    /// </summary>
    [Fact]
    public async Task 切断すると購読が解除される()
    {
        JobChangeFeed feed = new();
        using CancellationTokenSource disconnect = new();
        (DefaultHttpContext context, _) = CreateConnection(disconnect.Token);

        Task streaming = JobEventsEndpoint.StreamAsync(context, feed, TimeProvider.System);

        // 購読は最初の await より前に行われるので、呼び出しが戻った時点で 1 件のはず。
        Assert.Equal(1, CountSubscribers(feed));

        disconnect.Cancel();
        await streaming;

        Assert.Equal(0, CountSubscribers(feed));
    }

    /// <summary>
    /// 変更が無いまま間隔が過ぎると、イベントではなくコメント行が流れる。
    /// 死んだ TCP の検出とプロキシのバッファリング対策が目的で、クライアントには何もさせない。
    /// </summary>
    [Fact]
    public async Task 静かなまま間隔が過ぎるとkeepaliveコメントが流れる()
    {
        JobChangeFeed feed = new();
        ManualTimeProvider time = new();
        using CancellationTokenSource disconnect = new();
        (DefaultHttpContext context, StreamReader reader) = CreateConnection(disconnect.Token);

        Task streaming = JobEventsEndpoint.StreamAsync(context, feed, time);

        // StreamAsync は最初の合図待ちまで同期で進んでから戻るので、この時点で
        // keep-alive の期限は必ず張られている。針を進めれば決定的に期限を越えさせられる。
        time.Advance(JobEventsEndpoint.KeepAliveInterval);

        Assert.Equal(": keep-alive", await reader.ReadLineAsync());

        disconnect.Cancel();
        await streaming;
    }

    /// <summary>
    /// keep-alive の後も合図は届く。タイムアウトの経路がループを終わらせないこと。
    /// </summary>
    /// <remarks>
    /// 2 回目の keep-alive を針で起こすのは決定的に書けない（次の期限が張られるのは
    /// コメント行が読めた後の非同期のどこかで、針を進める側と競争になる）。
    /// 代わりに合図で確かめる。合図はチャネルに残るので、ハンドラがどこまで進んでいても届く。
    /// </remarks>
    [Fact]
    public async Task keepaliveの後でも合図は届く()
    {
        JobChangeFeed feed = new();
        ManualTimeProvider time = new();
        using CancellationTokenSource disconnect = new();
        (DefaultHttpContext context, StreamReader reader) = CreateConnection(disconnect.Token);

        Task streaming = JobEventsEndpoint.StreamAsync(context, feed, time);

        time.Advance(JobEventsEndpoint.KeepAliveInterval);
        Assert.Equal(": keep-alive", await reader.ReadLineAsync());
        Assert.Equal("", await reader.ReadLineAsync());

        feed.Publish();

        Assert.Equal("data: changed", await reader.ReadLineAsync());

        disconnect.Cancel();
        await streaming;
    }

    /// <summary>
    /// 接続を模した HttpContext と、ハンドラが書いた応答を読む口を作る。
    /// </summary>
    /// <remarks>
    /// 応答の body を Pipe にするのは、読み手が「書かれるまで待つ」ため。
    /// MemoryStream だと読みがすぐ空で返り、待ち合わせをテスト側で組み立てることになる。
    /// </remarks>
    private static (DefaultHttpContext Context, StreamReader Reader) CreateConnection(CancellationToken aborted)
    {
        Pipe pipe = new();
        DefaultHttpContext context = new() { RequestAborted = aborted };
        context.Response.Body = pipe.Writer.AsStream();
        return (context, new StreamReader(pipe.Reader.AsStream()));
    }

    /// <summary>
    /// <see cref="JobChangeFeed.Changed"/> の購読者数を数える。
    /// </summary>
    /// <remarks>
    /// event は外から購読者を数えられない。テスト専用の口を本体へ足す代わりに、
    /// コンパイラが event と同名で生成する裏フィールドの呼び出しリストを検査する。
    /// </remarks>
    private static int CountSubscribers(JobChangeFeed feed)
    {
        FieldInfo field = typeof(JobChangeFeed)
            .GetField(nameof(JobChangeFeed.Changed), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("event の裏フィールドが見つからない。");

        return field.GetValue(feed) is Delegate subscribers ? subscribers.GetInvocationList().Length : 0;
    }
}
