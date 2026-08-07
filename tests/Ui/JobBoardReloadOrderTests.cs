using System.Net;
using System.Text;

using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// 取り直しが重なったときに、どの行が残るか。
/// </summary>
/// <remarks>
/// <para>
/// E2E の「実行中の Job をキャンセルすると中止済みになる」が CI でだけ落ちていた件の回帰。
/// 画面が中止要求中のまま止まり、同時に API から見た状態は Cancelled になっている、
/// という形で出る（#83 で実測）。
/// </para>
/// <para>
/// <b>実ブラウザでは狙って起こせない。</b>要るのは「先に投げた読み出しが後から返る」という
/// HTTP の応答順の入れ替わりで、起きるかどうかは運になる。ここでは応答を握るハンドラで
/// その入れ替わりを常に起こす。
/// </para>
/// </remarks>
public sealed class JobBoardReloadOrderTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>
    /// 版が新しい行は、古い応答が後から返っても戻らない。
    /// </summary>
    [Fact]
    public async Task 古い応答が後から返っても新しい版の行を上書きしない()
    {
        HeldListHandler handler = new();
        JobBoard board = Board(handler);

        // 1 本目はキャンセル要求の直後に押した本人が走らせる取り直し（まだ Cancelling が見える）。
        // 2 本目はエンジンが Cancelled を書いたことによる変更通知の取り直し。
        // await せずに続けて始めるのは Home.razor の変更通知と同じ形
        //（InvokeAsync に投げっぱなしで、await に達した時点で次が走り出せる）。
        Task first = board.ReloadAsync(None);
        await handler.WaitForRequestsAsync(1);

        Task second = board.ReloadAsync(None);
        await handler.WaitForRequestsAsync(2);

        // 後から始めた方が先に返る。
        handler.Respond(index: 1, status: "Cancelled", version: 4);
        await second;

        handler.Respond(index: 0, status: "Cancelling", version: 3);
        await first;

        Assert.Equal("Cancelled", Assert.Single(board.Jobs).Status);
    }

    /// <summary>
    /// 版が同じなら取り直した方を採る。そこで違うのは進捗だけで、採らないと止まって見える。
    /// </summary>
    [Fact]
    public async Task 版が同じなら取り直した方の進捗を採る()
    {
        HeldListHandler handler = new();
        JobBoard board = Board(handler);

        Task first = board.ReloadAsync(None);
        await handler.WaitForRequestsAsync(1);
        handler.Respond(index: 0, status: "InProgress", version: 2, completed: 1);
        await first;

        Task second = board.ReloadAsync(None);
        await handler.WaitForRequestsAsync(2);
        handler.Respond(index: 1, status: "InProgress", version: 2, completed: 2);
        await second;

        Assert.Equal(2, Assert.Single(board.Jobs).CompletedSubTasks);
    }

    /// <summary>
    /// 古い応答に入っていない行は落とさない。落とすと、登録した本人の画面から
    /// 行が一度消えて次の通知で戻る。
    /// </summary>
    [Fact]
    public async Task 取り直した一覧に無い行は残す()
    {
        HeldListHandler handler = new();
        JobBoard board = Board(handler);

        Task first = board.ReloadAsync(None);
        await handler.WaitForRequestsAsync(1);
        handler.Respond(index: 0, status: "InProgress", version: 2);
        await first;

        // 2 本目は自分より前に読まれた応答で、この Job をまだ知らない。
        Task second = board.ReloadAsync(None);
        await handler.WaitForRequestsAsync(2);
        handler.RespondEmpty(index: 1);
        await second;

        Assert.Equal("job-1", Assert.Single(board.Jobs).Id);
    }

    private static JobBoard Board(HttpMessageHandler handler) =>
        new(new JobsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://api.test") }));

    /// <summary>
    /// 到着順に応答を握り、指名した順で返す。重なった取り直しの順序を決定的に作る。
    /// </summary>
    private sealed class HeldListHandler : HttpMessageHandler
    {
        private readonly List<TaskCompletionSource<string>> _pending = [];
        private readonly Lock _gate = new();

        public void Respond(int index, string status, long version, int completed = 0) =>
            Pending(index).SetResult(Body(
                new JobListItemDto(
                    "job-1", "n", "subtasks", "1 60", status,
                    new DateTimeOffset(2026, 8, 7, 0, 0, 0, TimeSpan.Zero), null, null, null,
                    completed, 1,
                    CanCancel: false, CanRequestPause: false, CanRequestResume: false, CanEdit: false,
                    Version: version)));

        public void RespondEmpty(int index) => Pending(index).SetResult("[]");

        /// <summary>要求が届くまで待つ。届いた数で「何本飛んでいるか」を決定的に見る。</summary>
        public async Task WaitForRequestsAsync(int count)
        {
            while (Count() < count)
            {
                await Task.Delay(1);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            TaskCompletionSource<string> pending = new();
            lock (_gate)
            {
                _pending.Add(pending);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(await pending.Task, Encoding.UTF8, "application/json"),
            };
        }

        private static string Body(JobListItemDto job) =>
            $$"""
            [{"id":"{{job.Id}}","name":"{{job.Name}}","jobType":"{{job.JobType}}",
              "parameters":"{{job.Parameters}}","status":"{{job.Status}}",
              "createdAt":"{{job.CreatedAt:O}}","startedAt":null,"finishedAt":null,
              "failureMessage":null,"completedSubTasks":{{job.CompletedSubTasks}},
              "totalSubTasks":{{job.TotalSubTasks}},"canCancel":false,"canRequestPause":false,
              "canRequestResume":false,"canEdit":false,"version":{{job.Version}}}]
            """;

        private int Count()
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }

        private TaskCompletionSource<string> Pending(int index)
        {
            lock (_gate)
            {
                return _pending[index];
            }
        }
    }
}
