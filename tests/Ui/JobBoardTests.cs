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

        Assert.Null(_board.OperationError);
        Assert.Equal("Pausing", _board.Jobs.Single(job => job.Id == id).Status);

        await _board.ResumeAsync(id, None);

        Assert.Null(_board.OperationError);
        Assert.Equal("InProgress", _board.Jobs.Single(job => job.Id == id).Status);
    }

    [Fact]
    public async Task 状態が合わない一時停止は現在の状態つきの知らせになる()
    {
        // 待機中と実行中は保留できるので、拒否されるのは中止の受理待ち（Cancelling）。
        string id = await RegisterViaApiAsync("中止中の Job");
        await StartAsync(id);
        await CancelViaApiAsync(id);
        await _board.InitializeAsync(None);

        await _board.PauseAsync(id, None);

        Assert.Equal("一時停止できませんでした。現在の状態: Cancelling", _board.OperationError);
    }

    [Fact]
    public async Task 対象が無い操作は見つからない知らせになる()
    {
        await _board.InitializeAsync(None);

        await _board.PauseAsync("does-not-exist", None);
        Assert.Equal("対象の Job が見つかりませんでした。", _board.OperationError);

        await _board.ResumeAsync("does-not-exist", None);
        Assert.Equal("対象の Job が見つかりませんでした。", _board.OperationError);

        await _board.CancelAsync("does-not-exist", None);
        Assert.Equal("対象の Job が見つかりませんでした。", _board.OperationError);
    }

    [Fact]
    public async Task 操作が例外になっても知らせに変える()
    {
        JobBoard board = BrokenBoard();

        await board.PauseAsync("job-1", None);
        Assert.Contains("一時停止できませんでした", board.OperationError);

        await board.ResumeAsync("job-1", None);
        Assert.Contains("再開できませんでした", board.OperationError);

        await board.CancelAsync("job-1", None);
        Assert.Contains("キャンセルできませんでした", board.OperationError);

        await board.EditAsync("job-1", None);
        Assert.Contains("編集できませんでした", board.OperationError);
    }

    /// <summary>
    /// 操作の失敗はダイアログに出し、一覧の知らせ（<see cref="JobBoard.Notice"/>）には出さない。
    /// 画面はこの 2 つを別の要素で描くので、混ざっていないことが表示の前提になる。
    /// </summary>
    [Fact]
    public async Task 操作の失敗はダイアログに出て一覧の知らせには出ない()
    {
        string id = await RegisterViaApiAsync("拒否される Job");
        await CancelViaApiAsync(id);
        await _board.InitializeAsync(None);
        Assert.False(_board.IsOperationDialogOpen);

        await _board.CancelAsync(id, None);

        Assert.True(_board.IsOperationDialogOpen);
        Assert.Equal("キャンセルできませんでした。現在の状態: Cancelled", _board.OperationError);
        Assert.Null(_board.Notice);
    }

    /// <summary>
    /// 背景の変更通知（SSE）は <see cref="JobBoard.ReloadAsync"/> を呼ぶだけなので、
    /// ダイアログは開いたまま、裏の一覧だけが新しくなる。相乗りしていた頃は、
    /// 通知が来た瞬間に「できませんでした」が読む間もなく消えていた。
    /// </summary>
    [Fact]
    public async Task 背景の再読み込みはダイアログを閉じず裏の一覧だけを新しくする()
    {
        await _board.InitializeAsync(None);
        await _board.CancelAsync("does-not-exist", None);

        // 通知の中身に当たるもの（他の画面や実行エンジンによる変更）を作ってから取り直す。
        await RegisterViaApiAsync("通知で現れる Job");
        await _board.ReloadAsync(None);

        Assert.True(_board.IsOperationDialogOpen);
        Assert.Equal("対象の Job が見つかりませんでした。", _board.OperationError);
        Assert.Contains(_board.Jobs, job => job.Name == "通知で現れる Job");
    }

    /// <summary>
    /// 画面の状態として残り続けない。閉じるのは次の操作を始めたときで、
    /// その操作が通ったかどうかには依らない。
    /// </summary>
    [Fact]
    public async Task ダイアログは次の操作を始めると閉じる()
    {
        string id = await RegisterViaApiAsync("消す Job");
        await _board.InitializeAsync(None);
        await _board.PauseAsync("does-not-exist", None);
        Assert.True(_board.IsOperationDialogOpen);

        await _board.CancelAsync(id, None);
        Assert.False(_board.IsOperationDialogOpen);
        Assert.Null(_board.OperationError);

        // 通らなかった操作でも、前の失敗ではなく今の失敗だけが残る。
        await _board.PauseAsync("does-not-exist", None);
        await _board.CancelAsync(id, None);
        Assert.Equal("キャンセルできませんでした。現在の状態: Cancelled", _board.OperationError);

        // 登録も操作のうち。別の欄（Notice / RegistrationErrors）へ出るものでも閉じる。
        _board.Name = "登録し直す Job";
        _board.JobType = "subtasks";
        _board.Parameters = "2 1";
        await _board.RegisterAsync(None);
        Assert.False(_board.IsOperationDialogOpen);
    }

    /// <summary>
    /// 閉じる手段が要る。非モーダルの dialog は Esc で閉じないので、
    /// これが唯一の「読み終わったから消す」道になる。
    /// </summary>
    [Fact]
    public async Task ダイアログは閉じるボタンで閉じられる()
    {
        await _board.InitializeAsync(None);
        await _board.CancelAsync("does-not-exist", None);
        Assert.True(_board.IsOperationDialogOpen);

        _board.CloseOperationDialog();

        Assert.False(_board.IsOperationDialogOpen);
        Assert.Null(_board.OperationError);
    }

    /// <summary>
    /// 拒否されても一覧は取り直す。拒否の理由は「ボタンを出した後に状態が進んだ」なので、
    /// 取り直した行こそが見たい現在の状態になる。かつては知らせが消えるのを避けるために
    /// 失敗時は取り直さない細工が入っていた。
    /// </summary>
    [Fact]
    public async Task 拒否された操作でも一覧は取り直す()
    {
        string id = await RegisterViaApiAsync("外から進む Job");
        await _board.InitializeAsync(None);
        Assert.Equal("Registered", _board.Jobs.Single(job => job.Id == id).Status);

        // 画面の知らないところで終端まで進める。ボタンはまだ押せる見た目のまま。
        await CancelViaApiAsync(id);

        await _board.CancelAsync(id, None);

        Assert.Equal("キャンセルできませんでした。現在の状態: Cancelled", _board.OperationError);
        Assert.Equal("Cancelled", _board.Jobs.Single(job => job.Id == id).Status);
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

        Assert.Equal("キャンセルできませんでした: InvalidOperationException", board.OperationError);
    }

    [Fact]
    public async Task 待機中のJobはキャンセルできる()
    {
        string id = await RegisterViaApiAsync("消す Job");
        await _board.InitializeAsync(None);

        await _board.CancelAsync(id, None);

        Assert.Null(_board.OperationError);
        Assert.Equal("Cancelled", _board.Jobs.Single(job => job.Id == id).Status);

        // 2 回目は終端なので拒否され、現在の状態が知らせに載る。
        await _board.CancelAsync(id, None);

        Assert.Equal("キャンセルできませんでした。現在の状態: Cancelled", _board.OperationError);
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

        Assert.Null(_board.OperationError);
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

        Assert.StartsWith("編集できませんでした: ", _board.OperationError);
        Assert.Contains("個数 秒数", _board.OperationError);

        // 空（null）を打っても同じ経路。SetEdit は null を空文字として控える。
        _board.SetEdit(id, null);
        await _board.EditAsync(id, None);
        Assert.StartsWith("編集できませんでした: ", _board.OperationError);
    }

    [Fact]
    public async Task 終端のJobの編集は現在の状態つきの知らせになる()
    {
        string id = await RegisterViaApiAsync("終わった Job");
        await CancelViaApiAsync(id);
        await _board.InitializeAsync(None);

        _board.SetEdit(id, "5 2");
        await _board.EditAsync(id, None);

        Assert.Equal("編集できませんでした。現在の状態: Cancelled", _board.OperationError);
    }

    [Fact]
    public async Task 一覧にも入力にも無いJobの編集は空文字を送って弾かれる()
    {
        await _board.InitializeAsync(None);

        // 打った値も一覧の行も無いので、送るのは空文字になる。
        // 検証は対象の探索より先に走るので、返るのは 404 ではなく入力エラー。
        await _board.EditAsync("does-not-exist", None);

        Assert.StartsWith("編集できませんでした: ", _board.OperationError);
    }

    [Fact]
    public async Task 読める値でも対象が無ければ見つからない知らせになる()
    {
        await _board.InitializeAsync(None);

        _board.SetEdit("does-not-exist", "5 2");
        await _board.EditAsync("does-not-exist", None);

        Assert.Equal("対象の Job が見つかりませんでした。", _board.OperationError);
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
        Assert.Null(board.OperationError);

        gate.Release();
        await first;

        Assert.False(board.IsBusy);
        Assert.Equal("対象の Job が見つかりませんでした。", board.OperationError);
    }

    [Fact]
    public void 進捗は行が無ければハイフンで数があれば分数()
    {
        Assert.Equal("-", JobBoard.ProgressFor(Row(completed: 0, total: 0)));
        Assert.Equal("0/3", JobBoard.ProgressFor(Row(completed: 0, total: 3)));
        Assert.Equal("2/3", JobBoard.ProgressFor(Row(completed: 2, total: 3)));
    }

    /// <summary>
    /// 一覧の時刻は年を落とす。3 列を 1 行に収めるためで、年まで要る人のために
    /// 完全な値を別に返す（画面は列の title に載せている）。
    /// </summary>
    [Fact]
    public void 時刻は年を落として出し完全な値は別に返す()
    {
        DateTimeOffset at = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

        Assert.Equal(at.ToLocalTime().ToString("MM-dd HH:mm:ss"), JobBoard.Format(at));
        Assert.Equal(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), JobBoard.FormatFull(at));
    }

    /// <summary>
    /// 持っていない時刻は、一覧では「-」。title は空にする ── 空の吹き出しは出ない。
    /// </summary>
    [Fact]
    public void 時刻は無ければハイフンでtitleは空()
    {
        Assert.Equal("-", JobBoard.Format(null));
        Assert.Equal(string.Empty, JobBoard.FormatFull(null));
    }

    /// <summary>
    /// 帯の幅。行がまだ無い（登録直後で分割されていない）のは 0% で、
    /// 画面はそもそも帯を出さない（0% の帯と「まだ分割されていない」は別のこと）。
    /// </summary>
    [Fact]
    public void 進捗の帯は完了の割合で行が無ければゼロ()
    {
        Assert.Equal(0, JobBoard.ProgressPercent(Row(completed: 0, total: 0)));
        Assert.Equal(0, JobBoard.ProgressPercent(Row(completed: 0, total: 4)));
        Assert.Equal(50, JobBoard.ProgressPercent(Row(completed: 2, total: 4)));
        Assert.Equal(100, JobBoard.ProgressPercent(Row(completed: 4, total: 4)));

        // 割り切れないときは切り捨て。帯が満杯に見えるのは本当に終わったときだけにする。
        Assert.Equal(66, JobBoard.ProgressPercent(Row(completed: 2, total: 3)));
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

    /// <summary>
    /// 押した瞬間に開く。中身が来るまでは空のまま出る。
    /// </summary>
    /// <remarks>
    /// 取得を待ってから開くと、応答が遅い間だけ画面が無反応に見える。押した事実は
    /// すぐ画面に出て、中身は後から埋まる、が正しい順序。
    /// </remarks>
    [Fact]
    public async Task 監査ログは押すと対象の名前つきで開き閉じられる()
    {
        string id = await RegisterViaApiAsync("記録される Job");
        await _board.InitializeAsync(None);

        Assert.False(_board.IsAuditLogDialogOpen);

        await _board.ShowAuditLogsAsync(_board.Jobs.Single(job => job.Id == id), None);

        Assert.True(_board.IsAuditLogDialogOpen);
        Assert.Equal("記録される Job", _board.AuditLogJobName);
        Assert.Null(_board.AuditLogNotice);

        // 登録の 1 件が既に載っている。
        Assert.NotNull(_board.AuditLogs);
        Assert.Contains(_board.AuditLogs, log => log.Content.StartsWith("Job を登録した", StringComparison.Ordinal));

        _board.CloseAuditLogDialog();

        Assert.False(_board.IsAuditLogDialogOpen);
        Assert.Null(_board.AuditLogs);

        // 名前も一緒に消す。残ると、次に開いたときの見出しが前の Job のままになる瞬間ができる。
        Assert.Null(_board.AuditLogJobName);
    }

    /// <summary>
    /// 取得に失敗しても例外を通さない。通すと回路が落ちて画面全体が凍る。
    /// </summary>
    /// <remarks>
    /// 空の一覧と取得の失敗を同じ見た目にしない。何も起きていない Job と、
    /// 起きたことを読めなかった Job は、読み手にとって別のこと。
    /// </remarks>
    [Fact]
    public async Task 監査ログの取得が例外になっても知らせに変える()
    {
        JobBoard board = BrokenBoard();

        await board.ShowAuditLogsAsync(FailedRow("読めない値です。"), None);

        Assert.True(board.IsAuditLogDialogOpen);
        Assert.Null(board.AuditLogs);
        Assert.Contains("監査ログを取得できませんでした", board.AuditLogNotice);
    }

    /// <summary>
    /// 別の行を押したら、そちらへ差し替わる。前の行の分が残ると、
    /// 押した行と開いている中身が食い違う。
    /// </summary>
    [Fact]
    public async Task 続けて別の行を押すと開く中身が差し替わる()
    {
        string first = await RegisterViaApiAsync("1 つ目の Job");
        string second = await RegisterViaApiAsync("2 つ目の Job");
        await _board.InitializeAsync(None);

        await _board.ShowAuditLogsAsync(_board.Jobs.Single(job => job.Id == first), None);
        await _board.ShowAuditLogsAsync(_board.Jobs.Single(job => job.Id == second), None);

        Assert.Equal("2 つ目の Job", _board.AuditLogJobName);
        Assert.NotNull(_board.AuditLogs);
        Assert.All(_board.AuditLogs, log => Assert.Equal(second, log.JobId));
    }

    /// <summary>
    /// 監査ログは行の中身であって操作の結果ではない。開いている間は、
    /// 次の操作を始めても閉じない（<c>OperationError</c> はそこで消える。
    /// 消える条件が違うので別に持っている）。
    /// </summary>
    [Fact]
    public async Task 開いた監査ログは次の操作を始めても閉じない()
    {
        string id = await RegisterViaApiAsync("記録される Job");
        await _board.InitializeAsync(None);
        await _board.ShowAuditLogsAsync(_board.Jobs.Single(job => job.Id == id), None);

        await _board.CancelAsync("does-not-exist", None);

        Assert.True(_board.IsAuditLogDialogOpen);
        Assert.NotNull(_board.AuditLogs);
    }

    /// <summary>
    /// 実施者は画面の文言へ写す。知らない値はそのまま出す ── 「不明」に畳むと、
    /// 値が増えたときに何が起きているのか画面から分からなくなる。
    /// </summary>
    [Fact]
    public void 実施者は日本語に写り知らない値はそのまま出る()
    {
        Assert.Equal("利用者", JobBoard.ActorLabel("User"));
        Assert.Equal("システム", JobBoard.ActorLabel("System"));
        Assert.Equal("Robot", JobBoard.ActorLabel("Robot"));
    }

    // 進捗の表示だけを見る行。可否は Registered の Job が持つ値
    // （どれを入れても表示は変わらないが、ありえない組み合わせを置かない）。
    private static JobListItemDto Row(int completed, int total) =>
        new("job-1", "行", "subtasks", "3 1", "Registered", DateTimeOffset.UtcNow, null, null, null, completed, total,
            CanCancel: true, CanRequestPause: true, CanRequestResume: false, CanEdit: true, Version: 1);

    // 失敗して終わった行。終端なので操作はどれも押せない。
    private static JobListItemDto FailedRow(string? failureMessage) =>
        new("job-2", "失敗した Job", "subtasks", "3 1", "Failed", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, failureMessage, 1, 3,
            CanCancel: false, CanRequestPause: false, CanRequestResume: false, CanEdit: false, Version: 3);

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
