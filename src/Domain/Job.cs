namespace Netsoft.Jobs.Domain;

/// <summary>
/// Job のエンティティ。状態を変える経路は <see cref="Apply"/> ただ一つで、
/// 内部で必ず <see cref="JobStateMachine"/> を通す。
/// </summary>
/// <remarks>
/// 時刻は自分で取らずに引数で受け取る。テストが時計に依存しなくなるのと、
/// 「いつ起きたことにするか」を決めるのは実行エンジン側の責務であるため。
/// </remarks>
public sealed class Job
{
    /// <summary>まだ一度も書き戻されていない Job の版。</summary>
    private const long InitialVersion = 1;

    private Job(
        JobId id,
        string name,
        string jobType,
        string parameters,
        JobStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        string? failureMessage,
        long version)
    {
        Id = id;
        Name = name;
        JobType = jobType;
        Parameters = parameters;
        Status = status;
        CreatedAt = createdAt;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        FailureMessage = failureMessage;
        Version = version;
    }

    /// <summary>識別子。</summary>
    public JobId Id { get; }

    /// <summary>
    /// このインスタンスを読み出した時点の版。書き戻しの期待値になる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 状態ではなく版で守るのは、<b>状態を変えない書き込みがあるから</b>。編集
    /// （<see cref="ChangeParameters"/>）は遷移ではないので状態が動かず、状態を期待値にすると
    /// 素通りする。書き戻しは全列を書くので、素通りした先で編集が黙って巻き戻る。
    /// 版はどの書き込みでも進むので、この穴が原理的に無い。
    /// </para>
    /// <para>
    /// <see cref="Apply"/> でも <see cref="ChangeParameters"/> でも版は動かない。版を進めるのは
    /// 保存が成功したときだけで、それを知っているのは store だけだから
    /// （<see cref="IJobStore.UpdateAsync"/> の契約を参照）。
    /// </para>
    /// </remarks>
    public long Version { get; }

    /// <summary>利用者が付けた名前。</summary>
    public string Name { get; }

    /// <summary>どの種類の Job か。実行エンジンがハンドラの解決に使う。</summary>
    public string JobType { get; }

    /// <summary>
    /// 不透明なペイロード。Domain は中身を一切解釈しない。
    /// </summary>
    /// <remarks>
    /// JSON なのか別の形式なのかも Domain は知らない。
    /// 解釈するのは <see cref="JobType"/> に対応するハンドラだけで、
    /// ここで形式を仮定すると Job の種類を増やすたびに Domain が変わってしまう。
    /// </remarks>
    public string Parameters { get; private set; }

    /// <summary>現在の状態。外から代入する経路は無い。</summary>
    public JobStatus Status { get; private set; }

    /// <summary>
    /// パラメータを書き換える。編集は状態遷移ではないので <see cref="Apply"/> を通らない。
    /// </summary>
    /// <remarks>
    /// どの状態で編集できるかの定義は <see cref="JobStatusExtensions.CanEditParameters"/> が
    /// 1 か所で持つ。呼び出し側（編集のハンドラ）は先に確かめて拒否を結果で返し、
    /// ここまで来て違反していたら呼び出し側の誤りとして例外にする（拒否とは性質が違う）。
    /// </remarks>
    /// <exception cref="InvalidOperationException">編集できない状態の場合。</exception>
    public void ChangeParameters(string parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (!Status.CanEditParameters())
        {
            throw new InvalidOperationException($"状態 {Status} の Job のパラメータは編集できません。");
        }

        Parameters = parameters;
    }

    /// <summary>登録された時刻。</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// ハンドラを<b>最初に</b>起動した時刻。一度も起動していなければ null。
    /// </summary>
    /// <remarks>
    /// 一度立ったら二度と動かない（停止して待ち行列へ戻っても消えないし、再開後の
    /// 起動でも書き直さない）。したがって「値がある ⟺ 一度でも走った」が成り立つ。
    /// これは状態からは導けない事実で、<see cref="JobStatus.Registered"/> が必ず持たないこと
    /// （<c>Create</c> でしか作られないため）以外に言えることは無い ──
    /// <see cref="JobStatus.Resumed"/> にも走る前に保留した Job が居る
    /// （理由は <see cref="Apply"/> の注記に）。
    /// </remarks>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>終端に達した時刻。まだ終わっていなければ null。</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>失敗の理由。<see cref="JobStatus.Failed"/> 以外では null。</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>
    /// 新しい Job を作る。状態は必ず <see cref="JobStatus.Registered"/> から始まる。
    /// </summary>
    public static Job Create(JobId id, string name, string jobType, string parameters, DateTimeOffset createdAt)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("JobId が指定されていません。", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);

        // Parameters は空でもよい（引数を取らない Job がある）が、null は許さない。
        ArgumentNullException.ThrowIfNull(parameters);

        return new Job(
            id, name, jobType, parameters, JobStatus.Registered, createdAt, null, null, null, InitialVersion);
    }

    /// <summary>
    /// 永続化された値から Job を組み立て直す。リポジトリ実装のためのもの。
    /// </summary>
    /// <remarks>
    /// 状態機械を通さない唯一の経路。既に起きたことを読み戻すだけなので遷移の検証はしない。
    /// アプリケーションのロジックからこれを呼ぶと、状態機械を迂回して任意の状態を作れてしまう。
    /// 呼んでよいのは <see cref="IJobStore"/> の実装だけ。
    /// </remarks>
    /// <param name="version">
    /// 保存されている版。既定は新規と同じ初期値で、版を持たない古い呼び出し
    /// （テストの組み立てなど）がそのまま書ける。実際の store は必ず読んだ値を渡す。
    /// </param>
    public static Job Rehydrate(
        JobId id,
        string name,
        string jobType,
        string parameters,
        JobStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        string? failureMessage,
        long version = InitialVersion) =>
        new(id, name, jobType, parameters, status, createdAt, startedAt, finishedAt, failureMessage, version);

    /// <summary>
    /// 契機を適用する。許可されれば状態と時刻を更新し、拒否されれば何も変更しない。
    /// </summary>
    /// <param name="trigger">起きたこと。</param>
    /// <param name="at">それが起きた時刻。</param>
    /// <param name="failureMessage"><see cref="JobStatus.Failed"/> へ遷移する場合の理由。</param>
    /// <exception cref="ArgumentException">
    /// Failed へ遷移するのに理由が無い場合。理由の無い失敗は利用者に何も説明できないため、
    /// これは呼び出し側の誤りとして例外にする（拒否とは性質が違う）。
    /// </exception>
    public JobTransitionResult Apply(JobTrigger trigger, DateTimeOffset at, string? failureMessage = null)
    {
        JobTransitionResult result = JobStateMachine.Evaluate(Status, trigger);
        if (!result.IsAllowed)
        {
            // 拒否されたときは集約を一切変更しない。時刻すら触らない。
            return result;
        }

        JobStatus next = result.Status;

        // 検証は代入より先。ここで投げれば集約は一切変更されていない。
        if (next == JobStatus.Failed)
        {
            if (string.IsNullOrWhiteSpace(failureMessage))
            {
                throw new ArgumentException("Failed へ遷移するには失敗理由が必要です。", nameof(failureMessage));
            }

            FailureMessage = failureMessage;
        }

        // 開始時刻は「最初にハンドラが起動した時刻」で、一度立ったら二度と動かない。
        //
        // Running へ入る契機は Start（初回・再開後とも）と Resume（Pausing の揺り戻し）の
        // 2 つあるが、どちらでも書き直さない。揺り戻しは実行が途切れていないので触れば
        // 「走り続けているのに開始し直した」という嘘になるし、再開後の Start で書き直すと
        // 停止をまたいだ Job の「いつから走っているか」が失われる。
        //
        // かつては待ち行列へ戻るときに消していた（待ち行列は開始時刻を持たない、という
        // 不変条件のため）。やめたのは、消すと「実際に走ったのに走った記録が無い」行が
        // 作れてしまうから ── 停止して再開待ちのまま中止すると、サブタスクが進んでいるのに
        // 開始時刻が空の終端が残る。いまの不変条件は
        // 「StartedAt がある ⟺ ハンドラが一度でも起動した」で、単調（倒れない）。
        if (trigger == JobTrigger.Start && StartedAt is null)
        {
            StartedAt = at;
        }

        if (next.IsTerminal())
        {
            FinishedAt = at;
        }

        Status = next;
        return result;
    }
}
