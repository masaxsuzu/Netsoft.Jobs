namespace Netsoft.Jobs.E2E.Tests;

/// <summary>
/// Blazor の回路が繋がった（= 操作を受け付ける）状態のページを開くためのヘルパー。
/// </summary>
internal static class InteractivePageExtensions
{
    /// <summary>
    /// この長さ以上の受信フレームを最初の描画バッチとみなす。
    /// </summary>
    /// <remarks>
    /// 回路の確立時に届く他のメッセージ（SignalR のハンドシェイク応答、StartCircuit の
    /// 完了通知など）は数十〜数百バイトの制御メッセージで、ページ全体のマークアップを
    /// 含む描画バッチだけが桁違いに大きい。境界の 512 はその間を取った値。
    /// </remarks>
    private const int RenderBatchMinimumLength = 512;

    /// <summary>
    /// ページを開き、Blazor Server の回路が繋がるまで待ってから返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// プリレンダリング直後の画面は素の HTML で、この段階の入力やクリックは
    /// サーバに届かず失われる（回路の最初の描画で入力値も空に巻き戻る）。
    /// フォーム操作の前に必ずここを通すこと。怠ると「たまに入力が消える」flaky になる。
    /// </para>
    /// <para>
    /// 回路は WebSocket 上の SignalR で繋がる。「WebSocket が開いた」だけでは
    /// まだハンドラが配線されていないため、次の順のイベントを証拠として待つ。
    /// <list type="number">
    /// <item>WebSocket が作られる（接続の開始）。</item>
    /// <item>サーバから大きなフレームが届く（最初の描画バッチ。
    /// <see cref="RenderBatchMinimumLength"/> を参照）。</item>
    /// <item>その後にクライアントがフレームを送る。Blazor はバッチを DOM へ適用し
    /// 終えてから完了通知（OnRenderCompleted）を返すので、この送信を観測した時点で
    /// プリレンダリングの巻き戻りは済んでおり、以後の入力は失われない。</item>
    /// </list>
    /// </para>
    /// <para>
    /// goto の前から購読を始めるのは、接続がナビゲーション直後に確立するため。
    /// 後から待ち始めると確立の瞬間を取りこぼして永久に待つ。また
    /// <c>WaitForWebSocketAsync</c> ではなくイベント購読で書いているのは、待ちの解決と
    /// フレーム購読の間にすき間を作らないため。Playwright はイベントを到着順に配送する
    /// ので、WebSocket 生成イベントの中で購読すればフレームを取りこぼさない。
    /// </para>
    /// </remarks>
    public static async Task<IPage> OpenInteractiveAsync(this IBrowser browser, string url)
    {
        IPage page = await browser.NewPageAsync();

        TaskCompletionSource circuitReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnWebSocketCreated(object? sender, IWebSocket webSocket)
        {
            bool renderBatchReceived = false;

            webSocket.FrameReceived += (_, frame) =>
            {
                // Blazor の既定プロトコル（blazorpack）はバイナリだが、テキストに
                // 切り替わっても判定が成り立つよう、届いた方の長さを見る。
                int length = frame.Binary?.Length ?? frame.Text?.Length ?? 0;
                if (length >= RenderBatchMinimumLength)
                {
                    renderBatchReceived = true;
                }
            };

            webSocket.FrameSent += (_, _) =>
            {
                if (renderBatchReceived)
                {
                    circuitReady.TrySetResult();
                }
            };
        }

        page.WebSocket += OnWebSocketCreated;
        try
        {
            await page.GotoAsync(url);
            await circuitReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            page.WebSocket -= OnWebSocketCreated;
        }

        return page;
    }
}
