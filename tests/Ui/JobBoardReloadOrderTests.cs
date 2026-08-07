using System.Net;
using System.Text;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// 一覧の取り直しが重なったときの順序。
/// </summary>
/// <remarks>
/// <para>
/// E2E の「キャンセルすると中止済みになる」が CI でだけ落ちていた件の回帰。画面が
/// 中止要求中のまま止まり、同時に API から見た状態は Cancelled になっている、という形で出る。
/// </para>
/// <para>
/// <b>実ブラウザでは狙って起こせない。</b>要るのは「先に投げた読み出しが後から返る」という
/// HTTP の応答順の入れ替わりで、起きるかどうかは運になる。ここでは応答を握るハンドラで
/// その入れ替わりを常に起こし、重なり自体が生じないことを固定する。
/// </para>
/// </remarks>
public sealed class JobBoardReloadOrderTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task 取り直しが重なっても新しい一覧を古い一覧で上書きしない()
    {
        OverlapDetectingHandler handler = new();
        JobBoard board = new(new JobsApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://api.test") }));

        // 1 本目はキャンセル要求の直後に押した本人が走らせる取り直し（まだ Cancelling が見える）。
        // 2 本目はエンジンが Cancelled を書いたことによる変更通知の取り直し。
        // await せずに 2 本続けて始めるのは Home.razor の変更通知と同じ形
        //（InvokeAsync に投げっぱなしで、await に達した時点で次が走り出せる）。
        Task first = board.ReloadAsync(None);
        Task second = board.ReloadAsync(None);

        await Task.WhenAll(first, second);

        // 重なれば、どちらが先に返るかは HTTP 次第になる。重ならないことが、
        // 読み出しの順序と一覧への書き込みの順序が同じであることの根拠。
        Assert.False(handler.Overlapped, "取り直しが重なった");

        // 後から始めた方（新しい状態を読んだ方）が最後に残る。
        Assert.Equal("Cancelled", board.Jobs[0].Status);
    }

    /// <summary>
    /// 応答を少し遅らせ、その間に別の要求が飛んできたかを記録する。
    /// </summary>
    /// <remarks>
    /// 遅延の長さは検証に効かない。重ねられる作りなら 2 本目はこの窓に必ず入り、
    /// 直列化されていれば窓の長さによらず入れない。長さが決めるのは所要時間だけ。
    /// </remarks>
    private sealed class OverlapDetectingHandler : HttpMessageHandler
    {
        private int _inFlight;
        private int _served;

        public bool Overlapped { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _inFlight) > 1)
            {
                Overlapped = true;
            }

            await Task.Delay(50, cancellationToken);

            Interlocked.Decrement(ref _inFlight);

            // 到着が遅い方ほど新しい状態を読んでいる、という実物の並びを写す。
            string status = Interlocked.Increment(ref _served) == 1 ? "Cancelling" : "Cancelled";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    [{"id":"job-1","name":"n","jobType":"subtasks","parameters":"1 60","status":"{{status}}",
                      "createdAt":"2026-08-07T00:00:00+00:00","startedAt":null,"finishedAt":null,
                      "failureMessage":null,"completedSubTasks":0,"totalSubTasks":1,
                      "canCancel":false,"canRequestPause":false,"canRequestResume":false,"canEdit":false}]
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
