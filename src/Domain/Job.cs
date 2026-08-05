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
    private Job(
        JobId id,
        string name,
        string jobType,
        string parameters,
        JobStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        string? failureMessage)
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
    }

    /// <summary>識別子。</summary>
    public JobId Id { get; }

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
    public string Parameters { get; }

    /// <summary>現在の状態。外から代入する経路は無い。</summary>
    public JobStatus Status { get; private set; }

    /// <summary>登録された時刻。</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>ハンドラを起動した時刻。まだ起動していなければ null。</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>終端に達した時刻。まだ終わっていなければ null。</summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>失敗の理由。<see cref="JobStatus.Failed"/> 以外では null。</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>
    /// 新しい Job を作る。状態は必ず <see cref="JobStatus.Queued"/> から始まる。
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

        return new Job(id, name, jobType, parameters, JobStatus.Queued, createdAt, null, null, null);
    }

    /// <summary>
    /// 永続化された値から Job を組み立て直す。リポジトリ実装のためのもの。
    /// </summary>
    /// <remarks>
    /// 状態機械を通さない唯一の経路。既に起きたことを読み戻すだけなので遷移の検証はしない。
    /// アプリケーションのロジックからこれを呼ぶと、状態機械を迂回して任意の状態を作れてしまう。
    /// 呼んでよいのは <see cref="IJobStore"/> の実装だけ。
    /// </remarks>
    public static Job Rehydrate(
        JobId id,
        string name,
        string jobType,
        string parameters,
        JobStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        string? failureMessage) =>
        new(id, name, jobType, parameters, status, createdAt, startedAt, finishedAt, failureMessage);

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

        // Running へ入る契機は Start（初回・再開後とも）と Resume（Pausing の揺り戻し）の
        // 2 つある。開始時刻を書くのは Start だけ。揺り戻しは実行が途切れていないので、
        // 時刻を触ると「走り続けているのに開始し直した」という嘘になる。
        if (trigger == JobTrigger.Start)
        {
            StartedAt = at;
        }

        // 再開で待ち行列へ戻るとき、前回の実行の開始時刻を消す。Queued は
        // 「まだ開始していない」状態で、時刻が残ると不変条件（Queued に StartedAt は無い）が
        // 破れる。次の Start が新しい時刻を書く。
        if (next == JobStatus.Queued)
        {
            StartedAt = null;
        }

        if (next.IsTerminal())
        {
            FinishedAt = at;
        }

        Status = next;
        return result;
    }
}
