using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// 画面の状態と操作。実 API ホストへ in-process で繋いで、操作の結末が
/// 表示（一覧・知らせ・入力エラー）へどう写るかを固定する。
/// </summary>
/// <remarks>
/// <para>
/// ここが厚いのは、この型が Razor の外に居る理由そのものだから。<c>@code</c> に
/// 置いていた頃はカバレッジの除外に隠れ、「一時停止ボタンが再開を叩く」ように壊しても
/// 全テストが通る状態だった。
/// </para>
/// <para>
/// <b>例外の経路を必ず通す。</b>API 呼び出しの例外を握り損ねると Blazor の回路が落ち、
/// 画面全体が二度と操作できなくなる（リロードするまで復帰しない）。
/// 実際に「API を落としてボタンを 1 回押すと画面が無言で凍る」状態だった。
/// </para>
/// </remarks>
public sealed class JobBoardTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly UiHostFactory _factory = new();
    private readonly JobsApiClient _api;
    private readonly JobBoard _board;

    public JobBoardTests()
    {
        _api = _factory.Services.GetRequiredService<JobsApiClient>();
        _board = new JobBoard(_api);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task 入口で種類と一覧を読み込む()
    {
        await RegisterViaApiAsync("読み込み対象");

        await _board.InitializeAsync(None);

        Assert.Equal([new JobTypeDto("subtasks")], _board.JobTypes);
        Assert.Null(_board.JobTypesNotice);
        Assert.True(_board.CanRegister);
        Assert.Contains(_board.Jobs, job => job.Name == "読み込み対象");
        Assert.Null(_board.Notice);
    }

    /// <summary>
    /// API へ繋がらなくても例外を通さない。通すとプリレンダリングが落ちて本文の無い 500 になり、
    /// 回路が立たないので API が戻っても自力で復帰しない。
    /// </summary>
    [Fact]
    public async Task API不在でも入口は例外を投げず知らせに変える()
    {
        JobBoard board = BrokenBoard();

        await board.InitializeAsync(None);

        Assert.Empty(board.JobTypes);
        Assert.False(board.CanRegister);
        Assert.Contains("種類を取得できませんでした", board.JobTypesNotice);
        Assert.Contains("一覧の更新に失敗しました", board.Notice);
        Assert.Empty(board.Jobs);
    }

    [Fact]
    public async Task 一覧の取り直しが成功すると失敗の知らせが消える()
    {
        JobBoard broken = BrokenBoard();
        await broken.ReloadAsync(None);
        Assert.NotNull(broken.Notice);

        // 復旧した側で取り直せば消える（行は最新なのに赤字だけ残る、を防ぐ）。
        await _board.ReloadAsync(None);
        Assert.Null(_board.Notice);
    }

    [Fact]
    public async Task 登録が通ると入力欄が空になり一覧に出る()
    {
        await _board.InitializeAsync(None);
        _board.Name = "登録される Job";
        _board.JobType = "subtasks";
        _board.Parameters = "2 1";

        await _board.RegisterAsync(None);

        Assert.Empty(_board.Name);
        Assert.Empty(_board.JobType);
        Assert.Empty(_board.Parameters);
        Assert.Empty(_board.ErrorsFor("name"));
        Assert.Contains(_board.Jobs, job => job.Name == "登録される Job");
    }

    [Fact]
    public async Task 不正な登録は項目別のエラーになり入力が残る()
    {
        await _board.InitializeAsync(None);
        _board.Name = string.Empty;
        _board.JobType = string.Empty;

        await _board.RegisterAsync(None);

        Assert.NotEmpty(_board.ErrorsFor("name"));
        Assert.NotEmpty(_board.ErrorsFor("jobType"));
        Assert.Empty(_board.ErrorsFor("存在しない項目"));
    }

    [Fact]
    public async Task 登録が例外になっても知らせに変える()
    {
        JobBoard board = BrokenBoard();

        await board.RegisterAsync(None);

        Assert.Contains("登録できませんでした", board.Notice);
    }

    [Fact]
    public async Task 実行中のJobは一時停止でき受理前なら再開できる()
    {
        string id = await RegisterViaApiAsync("止めたい Job");
        await StartAsync(id);
        await _board.InitializeAsync(None);

        await _board.PauseAsync(id, None);

        Assert.Null(_board.Notice);
        Assert.Equal("Pausing", _board.Jobs.Single(job => job.Id == id).Status);

        await _board.ResumeAsync(id, None);

        Assert.Null(_board.Notice);
        Assert.Equal("Running", _board.Jobs.Single(job => job.Id == id).Status);
    }

    [Fact]
    public async Task 状態が合わない一時停止は現在の状態つきの知らせになる()
    {
        string id = await RegisterViaApiAsync("まだの Job");
        await _board.InitializeAsync(None);

        await _board.PauseAsync(id, None);

        Assert.Equal("一時停止できませんでした。現在の状態: Queued", _board.Notice);
    }

    [Fact]
    public async Task 対象が無い操作は見つからない知らせになる()
    {
        await _board.InitializeAsync(None);

        await _board.PauseAsync("does-not-exist", None);
        Assert.Equal("対象の Job が見つかりませんでした。", _board.Notice);

        await _board.ResumeAsync("does-not-exist", None);
        Assert.Equal("対象の Job が見つかりませんでした。", _board.Notice);

        await _board.CancelAsync("does-not-exist", None);
        Assert.Equal("対象の Job が見つかりませんでした。", _board.Notice);
    }

    [Fact]
    public async Task 操作が例外になっても知らせに変える()
    {
        JobBoard board = BrokenBoard();

        await board.PauseAsync("job-1", None);
        Assert.Contains("一時停止できませんでした", board.Notice);

        await board.ResumeAsync("job-1", None);
        Assert.Contains("再開できませんでした", board.Notice);

        await board.CancelAsync("job-1", None);
        Assert.Contains("キャンセルできませんでした", board.Notice);

        await board.EditAsync("job-1", None);
        Assert.Contains("編集できませんでした", board.Notice);
    }

    /// <summary>
    /// 例外がメッセージを持たないときは型名を出す。空の「できませんでした: 」だけを
    /// 見せると、利用者にも読む側にも何も伝わらない。
    /// </summary>
    [Fact]
    public async Task メッセージの無い例外は型名で知らせる()
    {
        JobBoard board = BrokenBoard(new InvalidOperationException(string.Empty));

        await board.CancelAsync("job-1", None);

        Assert.Equal("キャンセルできませんでした: InvalidOperationException", board.Notice);
    }

    [Fact]
    public async Task 待機中のJobはキャンセルできる()
    {
        string id = await RegisterViaApiAsync("消す Job");
        await _board.InitializeAsync(None);

        await _board.CancelAsync(id, None);

        Assert.Null(_board.Notice);
        Assert.Equal("Cancelled", _board.Jobs.Single(job => job.Id == id).Status);

        // 2 回目は終端なので拒否され、現在の状態が知らせに載る。
        await _board.CancelAsync(id, None);

        Assert.Equal("キャンセルできませんでした。現在の状態: Cancelled", _board.Notice);
    }

    [Fact]
    public async Task 打った値が保存され受理されると欄はサーバ値へ戻る()
    {
        string id = await RegisterViaApiAsync("編集する Job");
        await _board.InitializeAsync(None);
        JobListItemDto job = _board.Jobs.Single(item => item.Id == id);

        // 打つ前はサーバ値、打ったらその値。
        Assert.Equal("3 1", _board.EditValueFor(job));
        _board.SetEdit(id, "5 2");
        Assert.Equal("5 2", _board.EditValueFor(job));

        await _board.EditAsync(id, None);

        Assert.Null(_board.Notice);
        JobListItemDto saved = _board.Jobs.Single(item => item.Id == id);
        Assert.Equal("5 2", saved.Parameters);
        Assert.Equal("5 2", _board.EditValueFor(saved));
    }

    [Fact]
    public async Task 読めない値の保存は項目のメッセージが知らせに載る()
    {
        string id = await RegisterViaApiAsync("壊れた編集");
        await _board.InitializeAsync(None);

        _board.SetEdit(id, "junk");
        await _board.EditAsync(id, None);

        Assert.StartsWith("編集できませんでした: ", _board.Notice);
        Assert.Contains("個数 秒数", _board.Notice);

        // 空（null）を打っても同じ経路。SetEdit は null を空文字として控える。
        _board.SetEdit(id, null);
        await _board.EditAsync(id, None);
        Assert.StartsWith("編集できませんでした: ", _board.Notice);
    }

    [Fact]
    public async Task 終端のJobの編集は現在の状態つきの知らせになる()
    {
        string id = await RegisterViaApiAsync("終わった Job");
        await CancelViaApiAsync(id);
        await _board.InitializeAsync(None);

        _board.SetEdit(id, "5 2");
        await _board.EditAsync(id, None);

        Assert.Equal("編集できませんでした。現在の状態: Cancelled", _board.Notice);
    }

    [Fact]
    public async Task 一覧にも入力にも無いJobの編集は空文字を送って弾かれる()
    {
        await _board.InitializeAsync(None);

        // 打った値も一覧の行も無いので、送るのは空文字になる。
        // 検証は対象の探索より先に走るので、返るのは 404 ではなく入力エラー。
        await _board.EditAsync("does-not-exist", None);

        Assert.StartsWith("編集できませんでした: ", _board.Notice);
    }

    [Fact]
    public async Task 読める値でも対象が無ければ見つからない知らせになる()
    {
        await _board.InitializeAsync(None);

        _board.SetEdit("does-not-exist", "5 2");
        await _board.EditAsync("does-not-exist", None);

        Assert.Equal("対象の Job が見つかりませんでした。", _board.Notice);
    }

    /// <summary>
    /// 二重送信を受け付けない。ボタンの disabled だけに頼ると、素早い二度押しが
    /// 再描画より先に 2 つ目のイベントを送れる（実際にダブルクリックで Job が 2 つ登録された）。
    /// </summary>
    [Fact]
    public async Task 操作中の二度目の操作は受け付けない()
    {
        GateHandler gate = new();
        JobBoard board = new(new JobsApiClient(new HttpClient(gate) { BaseAddress = new Uri("http://api.test") }));

        Task first = board.CancelAsync("job-1", None);
        Assert.True(board.IsBusy);

        // 走っている間の 2 度目は素通りする（知らせも変わらない）。
        await board.PauseAsync("job-1", None);
        await board.ResumeAsync("job-1", None);
        await board.EditAsync("job-1", None);
        await board.RegisterAsync(None);
        Assert.Null(board.Notice);

        gate.Release();
        await first;

        Assert.False(board.IsBusy);
        Assert.Equal("対象の Job が見つかりませんでした。", board.Notice);
    }

    [Fact]
    public void 進捗は行が無ければハイフンで数があれば分数()
    {
        Assert.Equal("-", JobBoard.ProgressFor(Row(completed: 0, total: 0)));
        Assert.Equal("0/3", JobBoard.ProgressFor(Row(completed: 0, total: 3)));
        Assert.Equal("2/3", JobBoard.ProgressFor(Row(completed: 2, total: 3)));
    }

    [Fact]
    public void 時刻は無ければハイフン()
    {
        Assert.Equal("-", JobBoard.Format(null));

        DateTimeOffset at = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), JobBoard.Format(at));
    }

    [Fact]
    public async Task 種類が空なら登録させない()
    {
        // 種類の一覧が空になる状況をハンドラで作る（サーバ側には必ず 1 つあるため）。
        JobBoard board = new(new JobsApiClient(
            new HttpClient(new EmptyListHandler()) { BaseAddress = new Uri("http://api.test") }));

        await board.InitializeAsync(None);

        Assert.False(board.CanRegister);
        Assert.Equal("登録できる Job の種類がありません。", board.JobTypesNotice);
    }

    private static JobListItemDto Row(int completed, int total) =>
        new("job-1", "行", "subtasks", "3 1", "Queued", DateTimeOffset.UtcNow, null, null, null, completed, total);

    private static JobBoard BrokenBoard(Exception? failure = null) =>
        new(new JobsApiClient(new HttpClient(new ThrowingHandler(failure))
        {
            BaseAddress = new Uri("http://api.test"),
        }));

    private async Task<string> RegisterViaApiAsync(string name)
    {
        RegisterJobResponse response = await _api.RegisterJobAsync(name, "subtasks", "3 1", None);
        Assert.True(response.IsSuccess);
        Assert.NotNull(response.Job);
        return response.Job.Id;
    }

    private async Task CancelViaApiAsync(string id) =>
        Assert.True((await _api.CancelJobAsync(id, None)).IsSuccess);

    /// <summary>store を直接進めて実行中にする。実行エンジンは止まっているのでこれが唯一の道。</summary>
    private async Task StartAsync(string id)
    {
        IJobStore store = _factory.Api.Services.GetRequiredService<IJobStore>();
        Job job = await store.FindAsync(JobId.From(id), None)
            ?? throw new InvalidOperationException($"Job {id} が保存されていません。");

        Assert.True(job.Apply(JobTrigger.Start, DateTimeOffset.UtcNow).IsAllowed);
        Assert.True(await store.UpdateAsync(job, None));
    }

    /// <summary>API へ繋がらない状況を作る。</summary>
    private sealed class ThrowingHandler(Exception? failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw failure ?? new HttpRequestException("接続できませんでした。");
    }

    /// <summary>応答を握って離さない。操作が走っている最中を決定的に作る。</summary>
    private sealed class GateHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new();

        public void Release() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _gate.Task;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>何を訊かれても空の配列を返す。</summary>
    private sealed class EmptyListHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
