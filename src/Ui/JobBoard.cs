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

    // 取り直しは重なるので、一覧は読んで畳んで差し替えるまでを不可分にする
    //（理由は ReloadAsync と Merge の注記）。差し替えの単位が参照 1 本で済むよう、
    // 一覧は常に作り直して入れ替える（要素を足し引きしない）。
    private IReadOnlyList<JobListItemDto> _jobs = [];

    public JobBoard(JobsApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);

        _api = api;
    }

    /// <summary>一覧に出す Job。</summary>
    public IReadOnlyList<JobListItemDto> Jobs => Volatile.Read(ref _jobs);

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
    /// <remarks>
    /// <para>
    /// 取り直しは重なる。<c>Home.razor</c> の変更通知は <c>InvokeAsync</c> に投げっぱなしで、
    /// await に達した時点で次の通知が走り出せる。キャンセル 1 回で API 側の書き込みは 3 回
    /// （Cancelling・サブタスクの畳み込み・Cancelled）起き、押した本人の取り直しもそこへ重なる。
    /// <b>重なること自体は止めない。</b>新旧は行が載せている版で決まるので、到着順は問わない
    /// （<see cref="Merge"/>）。
    /// </para>
    /// <para>
    /// <b>取り直しを直列化する形は一度入れて外した</b>（#83 → #84）。直列化でも順序は直るが、
    /// 直るのは「読み出しの順序と書き込みの順序が同じになるから」で、
    /// <b>新しさの根拠を時間に置いたまま</b>だった。通知が続く間その数だけ順番に GET が走り、
    /// API が遅い回はそこで詰まる。版なら根拠がデータ自身にあるので、
    /// 何本同時に飛ばしても、どの順で返っても結果が変わらない。
    /// </para>
    /// </remarks>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<JobListItemDto> fetched = await _api.ListJobsAsync(cancellationToken);

            // 読んでから差し替えるまでに他の取り直しが差し替えていたら、その結果を踏んで
            // 畳み直す。負けた側が捨てられるのではなく、勝った側の上に載り直すので、
            // どちらの応答も落ちない。周回できるのは他の取り直しが着地した回数まで。
            while (true)
            {
                IReadOnlyList<JobListItemDto> held = Volatile.Read(ref _jobs);
                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _jobs, Merge(held, fetched), held), held))
                {
                    break;
                }
            }

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

    /// <summary>開いている失敗理由の全文。開いていなければ null。</summary>
    /// <remarks>
    /// <para>
    /// 一覧の失敗理由の列は絵柄 1 つしか置かない（例外の <c>Message</c> は長さに上限が無く、
    /// 表の中に本文として置ける形をしていない）。中身を読む手段がこれ。
    /// </para>
    /// <para>
    /// <b>Job の参照ではなく、押した瞬間の文字列を控える。</b>参照を持つと、背景の
    /// 取り直しが行を差し替えた（<see cref="Merge"/> は行ごと作り直す）あとも古い
    /// インスタンスを掴んだままになる。失敗理由は終端で確定してもう動かないので、
    /// 文字列を控えても古くならない。<see cref="OperationError"/> と別に持つのは、
    /// これが操作の結果ではなく行の中身であり、次の操作で消えてはいけないため。
    /// </para>
    /// </remarks>
    public string? FailureDetail { get; private set; }

    /// <summary>開いている失敗理由が、どの Job のものか。開いていなければ null。</summary>
    public string? FailureDetailName { get; private set; }

    /// <summary>失敗理由のダイアログが開いているか。</summary>
    public bool IsFailureDialogOpen => FailureDetail is not null;

    /// <summary>失敗理由の全文を開く。</summary>
    public void ShowFailure(JobListItemDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        FailureDetailName = job.Name;
        FailureDetail = job.FailureMessage;
    }

    /// <summary>失敗理由のダイアログを閉じる。</summary>
    /// <remarks>
    /// 2 つまとめて消す。<see cref="FailureDetailName"/> だけ残ると、次に開いたときの
    /// 見出しが前の Job の名前のまま出る瞬間ができる。
    /// 閉じることと中身を捨てることを 1 つの状態にしておく理由は
    /// <see cref="CloseOperationDialog"/> と同じ。
    /// </remarks>
    public void CloseFailureDialog()
    {
        FailureDetail = null;
        FailureDetailName = null;
    }

    /// <summary>失敗理由を持っているか（＝絵柄を出すか）。</summary>
    /// <remarks>
    /// 空白だけの理由も「無い」と見なす。押せる絵柄を出して空のダイアログが開くより、
    /// 何も出ないほうが読み手を騙さない。判定を <c>.razor</c> に書かないのは
    /// <see cref="JobStatusLabel.ClassFor"/> と同じ理由。
    /// </remarks>
    public static bool HasFailure(JobListItemDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return !string.IsNullOrWhiteSpace(job.FailureMessage);
    }

    /// <summary>登録の入力エラーのうち、指定した項目のもの。</summary>
    public IEnumerable<string> ErrorsFor(string field) =>
        RegistrationErrors.TryGetValue(field, out string[]? messages) ? messages : [];

    /// <summary>
    /// サブタスクの進捗（完了 / 総数）。行がまだ無い（登録直後で分割されていない）のは
    /// 進捗ゼロとは違うので、0/0 とは出さない。
    /// </summary>
    /// <remarks>
    /// <b>一覧の文字としては出さない。</b>帯の隣に置いていたが、10 列を 1 画面に
    /// 収める余地が無くなり、操作の列が画面外へ出ていた。数字そのものは
    /// 帯の <c>title</c> と読み上げ名に載せてあるので、消えてはいない。
    /// </remarks>
    public static string ProgressFor(JobListItemDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return job.TotalSubTasks == 0 ? "-" : $"{job.CompletedSubTasks}/{job.TotalSubTasks}";
    }

    /// <summary>進捗の帯の幅（%）。行がまだ無ければ 0。</summary>
    /// <remarks>
    /// <para>
    /// 帯だけだと 3/4 と 30/40 が同じに見える。それでも一覧に出すのは帯だけにしてある ──
    /// 一覧を上から眺めるときに要るのは進み具合の比較で、そこは帯のほうが速い。
    /// 分母が要る場面のために、数（<see cref="ProgressFor"/>）は帯の <c>title</c> と
    /// 読み上げ名に載せている。
    /// </para>
    /// <para>
    /// 計算を <c>.razor</c> に書かないのは <see cref="JobStatusLabel.ClassFor"/> と同じ理由。
    /// </para>
    /// </remarks>
    public static int ProgressPercent(JobListItemDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return job.TotalSubTasks == 0 ? 0 : job.CompletedSubTasks * 100 / job.TotalSubTasks;
    }

    /// <summary>一覧に出す時刻。持っていなければ「-」。</summary>
    /// <remarks>
    /// 年を落としてある。作成・開始・終了の 3 列を 1 行に収めるためで、落とさないと
    /// 列が折り返して 1 行が 2 段になる（それが元の画面で起きていた）。
    /// 年まで要る場面のために、完全な値は <see cref="FormatFull"/> が返し、
    /// 画面は列の <c>title</c> に載せている。
    /// </remarks>
    public static string Format(DateTimeOffset? value) =>
        value is { } present ? present.ToLocalTime().ToString("MM-dd HH:mm:ss") : "-";

    /// <summary>年を含む完全な時刻。持っていなければ空。</summary>
    public static string FormatFull(DateTimeOffset? value) =>
        value is { } present ? present.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : string.Empty;

    /// <summary>
    /// 手元の一覧に取り直した一覧を重ね、行ごとに版の新しい方を残す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>古い応答が新しい行を上書きしないことが、この関数の目的そのもの。</b>
    /// 上書きが起きると画面はそこで止まる ── 終端まで進んだ Job にはもう書き込みが無いので
    /// 変更通知は二度と来ず、次の取り直しの契機が無い。フォールバックポーリングも救わない
    /// （サーバの keep-alive がポーリングの間隔より短いので、接続が健在な限り発火しない）。
    /// この経路には順序の正しさ以外の安全網が無い。
    /// </para>
    /// <para>
    /// <b>手元にしか無い行は残す。</b>古い応答には、その後に登録された Job が入っていない。
    /// 落とすと、登録した本人の画面から行が一度消えて次の通知で戻る。Job は消えない
    /// （削除の口が無い）ので、残して困ることはない。
    /// </para>
    /// <para>
    /// <b>版が同じなら取り直した方を採る。</b>そこで違うのは進捗だけで
    /// （サブタスクの書き込みは Job 行を書かないので版が動かない）、どちらが新しいかは
    /// 決められない。採らない形にすると、版が動かない間ずっと進捗が止まって見える。
    /// 進捗が動いている間は次の書き込みと通知が必ず続くので、逆転しても次で直る。
    /// </para>
    /// <para>
    /// 並べ直すのは、行の集合が取り直した一覧そのままではなくなるから。
    /// 並びはサーバの <c>ORDER BY CreatedAt DESC, Id DESC</c> と同じにする。
    /// どちらも Job の一生を通じて変わらない値なので、突き合わせで並びが揺れない。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<JobListItemDto> Merge(
        IReadOnlyList<JobListItemDto> held, IReadOnlyList<JobListItemDto> fetched)
    {
        Dictionary<string, JobListItemDto> merged = held.ToDictionary(job => job.Id, StringComparer.Ordinal);

        foreach (JobListItemDto job in fetched)
        {
            if (!merged.TryGetValue(job.Id, out JobListItemDto? mine) || job.Version >= mine.Version)
            {
                merged[job.Id] = job;
            }
        }

        return
        [
            .. merged.Values
                .OrderByDescending(job => job.CreatedAt)
                .ThenByDescending(job => job.Id, StringComparer.Ordinal)
        ];
    }

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
