using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Ui;

/// <summary>
/// 画面が持つ状態と、画面からの操作。Razor（<c>Home.razor</c>）の外に置く。
/// </summary>
/// <remarks>
/// <para>
/// <b>Razor の外に出す理由は 2 つある。</b>1 つは測れるようにするため。カバレッジは
/// <c>**/*.razor</c> を除外しており（別プロセスの E2E でしか動かない部分があるため）、
/// <c>@code</c> に置いたロジックは分岐の床が一切かからない。実際、除外したまま
/// 「一時停止ボタンが再開を叩く」ように壊しても全テストが通る状態だった。
/// もう 1 つは、回路（WebSocket）を立てずに単体で叩けるようにするため。
/// </para>
/// <para>
/// <b>API 呼び出しの例外はすべてここで握る。</b>握らないと Blazor Server の回路が落ち、
/// <b>画面全体が二度と操作できなくなる</b>（リロードするまで復帰しない）。
/// <see cref="JobsApiClient"/> は契約どおりの失敗（400 / 404 / 409）を結果型で返し、
/// それ以外（5xx・接続不能）を例外にする約束なので、その例外の受け手がここになる。
/// 利用者に見せるのは「できなかった」という事実だけで、例外の型は区別しない
/// （繋がらない・5xx・壊れた応答のいずれも打つ手は同じ）。
/// </para>
/// </remarks>
public sealed class JobBoard
{
    private readonly JobsApiClient _api;

    // 利用者が打った値だけを持ち、読み込みでの種まきをしない。種まき方式だと、
    // 一覧の差し替えと種まきの間に描画が走った瞬間に取りこぼす（描画は await のたびに割り込める）。
    private readonly Dictionary<string, string> _edits = [];

    public JobBoard(JobsApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
    }

    /// <summary>一覧に出す Job。</summary>
    public IReadOnlyList<JobListItemDto> Jobs { get; private set; } = [];

    /// <summary>登録で選べる種類。</summary>
    public IReadOnlyList<JobTypeDto> JobTypes { get; private set; } = [];

    /// <summary>一覧の再読み込みと登録の結果として利用者に見せる知らせ。無ければ null。</summary>
    public string? Notice { get; private set; }

    /// <summary>
    /// 種類を取れなかったことの知らせ。<see cref="Notice"/> と分けてあるのは、
    /// これが「登録できない理由」であり、一覧の操作の結果で消えてはいけないため。
    /// </summary>
    public string? JobTypesNotice { get; private set; }

    /// <summary>キャンセル・編集・一時停止・再開が失敗または拒否されたこと。無ければ null。</summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="Notice"/> に相乗りさせない。</b>相乗りしていた頃は
    /// <see cref="ReloadAsync"/> が成功で <see cref="Notice"/> を消すため、背景の変更通知
    /// （SSE）で走る取り直しが、出したばかりの「できませんでした」を読む間もなく消していた。
    /// 一覧が古いという知らせと、押した操作が通らなかったという知らせは、出る条件も
    /// 消える条件も違う。<b>取り直しはこれに触らない。</b>
    /// </para>
    /// <para>
    /// <b>そのかわり永続化しない。</b>消えるのは次の操作を始めたときと、利用者が
    /// <see cref="CloseOperationDialog"/> で閉じたときだけ。時間でも再描画でも消えない。
    /// 押した本人が読み終わる前に消えるのを避けつつ、画面の状態として残り続けないための
    /// 線引きがここ。次の操作の結果に古い失敗が混ざることはない。
    /// </para>
    /// <para>
    /// <b>成功は入れない。</b>通った操作の結果は行そのもの（状態・パラメータ）に出るので、
    /// ダイアログに出すと読む必要のない知らせを毎回閉じさせることになる。失敗と拒否は
    /// 行の見た目が変わらないまま何も起きないので、出さなければ「押せていない」と
    /// 区別が付かない。<b>出すのは、行を見ても分からないことだけ。</b>
    /// </para>
    /// <para>
    /// 拒否されたときも一覧は取り直す。拒否の理由は「ボタンを出した後に状態が進んだ」なので、
    /// 取り直した行こそが利用者の見たい現在の状態になる。かつては
    /// <see cref="Notice"/> が消えるのを避けるために失敗時は取り直さない細工を置いていたが、
    /// 消える理由が無くなったので外した。
    /// </para>
    /// </remarks>
    public string? OperationError { get; private set; }

    /// <summary>操作の結果を出すダイアログが開いているか。</summary>
    /// <remarks>
    /// 開いているかの判断を Razor の <c>@code</c> ではなくここに置くのは、カバレッジが
    /// <c>**/*.razor</c> を除外していて、あちらに書いた条件には分岐の床が一切かからないため
    /// （この型が Razor の外に居る理由そのもの。型の注記を参照）。
    /// </remarks>
    public bool IsOperationDialogOpen => OperationError is not null;

    /// <summary>登録の入力エラー。項目名 → メッセージ。</summary>
    public IDictionary<string, string[]> RegistrationErrors { get; private set; } =
        new Dictionary<string, string[]>();

    /// <summary>登録フォームの入力（画面が双方向に束ねる）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <inheritdoc cref="Name" />
    public string JobType { get; set; } = string.Empty;

    /// <inheritdoc cref="Name" />
    public string Parameters { get; set; } = string.Empty;

    /// <summary>
    /// 操作が走っている最中か。画面はこの間ボタンを押させない。
    /// </summary>
    /// <remarks>
    /// 二重送信の防止。ボタンの disabled だけに頼らないのは、素早い二度押しが
    /// 再描画より先に 2 つ目のイベントを送れるため（実際にダブルクリックで
    /// 同じ Job が 2 つ登録された）。押させない見た目と、受け付けない動作の両方を置く。
    /// </remarks>
    public bool IsBusy { get; private set; }

    /// <summary>選べる種類が 1 つも無い状態で登録させない。</summary>
    public bool CanRegister => JobTypes.Count > 0;

    /// <summary>種類と一覧をまとめて読み込む。画面の入口で 1 回だけ呼ぶ。</summary>
    /// <remarks>
    /// どちらの失敗も画面ごと落とさない。ここで例外を通すとプリレンダリングが落ち、
    /// 本文の無い 500 になる。回路が立たないので、API が戻っても自力では復帰しない。
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await LoadJobTypesAsync(cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    /// <summary>一覧を取り直す。成功したら失敗の知らせを消す。</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Jobs = await _api.ListJobsAsync(cancellationToken);

            // 復旧したのに赤字が残り続けないようにする。行は最新なのに
            // 「更新に失敗しました」だけが居座るのは、事実と食い違う。
            Notice = null;
        }
        catch (Exception exception)
        {
            Notice = $"一覧の更新に失敗しました: {Describe(exception)}";
        }
    }

    /// <summary>Job を登録する。成功したら入力欄を空にする。</summary>
    public async Task RegisterAsync(CancellationToken cancellationToken)
    {
        if (!TryBeginOperation())
        {
            return;
        }

        try
        {
            RegisterJobResponse result = await _api.RegisterJobAsync(
                Name, JobType, Parameters, cancellationToken);

            if (!result.IsSuccess)
            {
                // API の 400（ValidationProblem）の errors がそのまま項目ごとの表示に使える形。
                RegistrationErrors = result.Errors;
                return;
            }

            RegistrationErrors = new Dictionary<string, string[]>();
            Name = string.Empty;
            JobType = string.Empty;
            Parameters = string.Empty;

            // 一覧は変更通知でも更新されるが、自分の操作の結果は通知を待たずに反映する。
            await ReloadAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            Notice = $"登録できませんでした: {Describe(exception)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>一時停止を要求する。</summary>
    public Task PauseAsync(string id, CancellationToken cancellationToken) =>
        ControlAsync("一時停止", () => _api.PauseJobAsync(id, cancellationToken), cancellationToken);

    /// <summary>再開を要求する。</summary>
    public Task ResumeAsync(string id, CancellationToken cancellationToken) =>
        ControlAsync("再開", () => _api.ResumeJobAsync(id, cancellationToken), cancellationToken);

    /// <summary>キャンセルを要求する。</summary>
    /// <remarks>
    /// 拒否は誤りではなく「ボタンを出した後に状態が進んだ」競合。現在の状態を見せて説明する。
    /// </remarks>
    public async Task CancelAsync(string id, CancellationToken cancellationToken)
    {
        if (!TryBeginOperation())
        {
            return;
        }

        try
        {
            CancelJobResponse result = await _api.CancelJobAsync(id, cancellationToken);

            OperationError = result switch
            {
                { Job: null } => "対象の Job が見つかりませんでした。",
                { IsSuccess: false } => $"キャンセルできませんでした。現在の状態: {result.Job.Status}",
                _ => null,
            };

            await ReloadAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            OperationError = $"キャンセルできませんでした: {Describe(exception)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>編集中のパラメータを保存する。</summary>
    public async Task EditAsync(string id, CancellationToken cancellationToken)
    {
        if (!TryBeginOperation())
        {
            return;
        }

        try
        {
            // 打っていなければ欄にはサーバ値が出ているので、それをそのまま送る（実質の無変更）。
            string parameters = _edits.TryGetValue(id, out string? edited)
                ? edited
                : Jobs.FirstOrDefault(job => job.Id == id)?.Parameters ?? string.Empty;

            EditJobResponse result = await _api.EditJobParametersAsync(id, parameters, cancellationToken);

            OperationError = result switch
            {
                { IsSuccess: true } => null,
                { Errors.Count: > 0 } => $"編集できませんでした: {result.Errors.Values.SelectMany(m => m).FirstOrDefault()}",
                { Job: null } => "対象の Job が見つかりませんでした。",
                _ => $"編集できませんでした。現在の状態: {result.Job.Status}",
            };

            if (result.IsSuccess)
            {
                // 入力中の値を破棄して、欄をサーバの現在値（受理された形）へ戻す。
                _edits.Remove(id);
            }

            await ReloadAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            OperationError = $"編集できませんでした: {Describe(exception)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>編集欄に出す値。入力中の値があればそれを、無ければサーバの現在値を出す。</summary>
    public string EditValueFor(JobListItemDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return _edits.TryGetValue(job.Id, out string? edited) ? edited : job.Parameters;
    }

    /// <summary>編集欄に打たれた値を控える。</summary>
    public void SetEdit(string id, string? value) => _edits[id] = value ?? string.Empty;

    /// <summary>操作の結果のダイアログを閉じる。</summary>
    /// <remarks>
    /// 閉じる手段を画面側の <c>@code</c> のフラグにしない。フラグを別に持つと
    /// 「閉じたが <see cref="OperationError"/> は残っている」状態ができ、次の失敗が
    /// 同じ文言だったときに開き直せない（属性は変わらないので DOM も動かない）。
    /// 閉じることと知らせを捨てることを同じ 1 つの状態にしておく。
    /// </remarks>
    public void CloseOperationDialog() => OperationError = null;

    /// <summary>登録の入力エラーのうち、指定した項目のもの。</summary>
    public IEnumerable<string> ErrorsFor(string field) =>
        RegistrationErrors.TryGetValue(field, out string[]? messages) ? messages : [];

    /// <summary>
    /// サブタスクの進捗（完了 / 総数）。行がまだ無い（登録直後で分割されていない）のは
    /// 進捗ゼロとは違うので、0/0 とは出さない。
    /// </summary>
    public static string ProgressFor(JobListItemDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return job.TotalSubTasks == 0 ? "-" : $"{job.CompletedSubTasks}/{job.TotalSubTasks}";
    }

    /// <summary>時刻の表示。持っていなければ「-」。</summary>
    public static string Format(DateTimeOffset? value) =>
        value is { } present ? present.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-";

    private async Task LoadJobTypesAsync(CancellationToken cancellationToken)
    {
        try
        {
            JobTypes = await _api.ListJobTypesAsync(cancellationToken);
            JobTypesNotice = JobTypes.Count == 0
                ? "登録できる Job の種類がありません。"
                : null;
        }
        catch (Exception exception)
        {
            JobTypes = [];
            JobTypesNotice = $"Job の種類を取得できませんでした: {Describe(exception)}";
        }
    }

    /// <summary>一時停止と再開は結果の形が同じなので、writing だけ差し替えて共有する。</summary>
    private async Task ControlAsync(
        string operation, Func<Task<JobControlResponse>> request, CancellationToken cancellationToken)
    {
        if (!TryBeginOperation())
        {
            return;
        }

        try
        {
            JobControlResponse result = await request();

            OperationError = result switch
            {
                { Job: null } => "対象の Job が見つかりませんでした。",
                { IsSuccess: false } => $"{operation}できませんでした。現在の状態: {result.Job.Status}",
                _ => null,
            };

            await ReloadAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            OperationError = $"{operation}できませんでした: {Describe(exception)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>操作を始められるなら始める。走っている最中なら false を返す。</summary>
    /// <remarks>
    /// 前の <see cref="OperationError"/> を消すのをここ 1 か所にまとめてある。操作ごとに
    /// 書くと、足した操作で消し忘れて古い失敗が次の結果に混ざる（消える条件は
    /// <see cref="OperationError"/> の注記のとおり「次の操作を始めたとき」だけなので、
    /// 消し忘れは画面に残り続ける形で出る）。
    /// </remarks>
    private bool TryBeginOperation()
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        OperationError = null;
        return true;
    }

    // 例外の型は利用者に見せない。打つ手が変わらないので、伝えるのは中身だけでよい。
    private static string Describe(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
}
