namespace Netsoft.Jobs.Domain;

/// <summary>
/// Job を構成するサブタスク。親の <see cref="Job"/> と連番で識別され、自分の状態だけを持つ。
/// </summary>
/// <remarks>
/// <para>
/// 状態を変える経路は <see cref="Apply"/> ただ一つ。遷移表は
/// Pending → Running → Completed と、Pending / Running → Cancelled の 4 行だけで、
/// 終端からはどこへも進まない（<see cref="Job"/> と同じ一方通行）。
/// </para>
/// <para>
/// 時刻を持たない。サブタスクの粒度は「1 秒 × m 回」の進捗であって、
/// いつ始まりいつ終わったかは親 Job の StartedAt / FinishedAt が代表する。
/// 列を足すのは、サブタスク単位の時刻に使い道ができてからでよい。
/// </para>
/// </remarks>
public sealed class SubTask
{
    private SubTask(JobId jobId, int index, SubTaskStatus status)
    {
        JobId = jobId;
        Index = index;
        Status = status;
    }

    /// <summary>親 Job の識別子。</summary>
    public JobId JobId { get; }

    /// <summary>親 Job の中での連番（0 始まり）。実行もこの順で行われる。</summary>
    public int Index { get; }

    /// <summary>現在の状態。外から代入する経路は無い。</summary>
    public SubTaskStatus Status { get; private set; }

    /// <summary>
    /// 新しいサブタスクを作る。状態は必ず <see cref="SubTaskStatus.Pending"/> から始まる。
    /// </summary>
    public static SubTask Create(JobId jobId, int index)
    {
        if (jobId.IsEmpty)
        {
            throw new ArgumentException("JobId が指定されていません。", nameof(jobId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return new SubTask(jobId, index, SubTaskStatus.Pending);
    }

    /// <summary>
    /// 永続化された値から組み立て直す。リポジトリ実装のためのもの。
    /// </summary>
    /// <remarks>
    /// 遷移を検証しない唯一の経路。呼んでよいのは <see cref="ISubTaskStore"/> の実装だけ
    /// （理由は <see cref="Job.Rehydrate"/> と同じ）。
    /// </remarks>
    public static SubTask Rehydrate(JobId jobId, int index, SubTaskStatus status) =>
        new(jobId, index, status);

    /// <summary>
    /// 契機を適用する。許可されれば状態を進め、拒否されれば何も変更しない。
    /// </summary>
    /// <returns>
    /// 適用の結果。<see cref="SubTaskTransition.Previous"/> は遷移前の状態で、
    /// <see cref="ISubTaskStore.UpdateAsync"/> の期待値にそのまま渡す
    /// （<see cref="JobTransitionResult.Previous"/> と同じ役割）。
    /// </returns>
    public SubTaskTransition Apply(SubTaskTrigger trigger)
    {
        SubTaskStatus previous = Status;

        // 遷移表はこの switch が唯一の定義。行が少ないので Job のような別クラスにはしない。
        SubTaskStatus? next = (Status, trigger) switch
        {
            (SubTaskStatus.Pending, SubTaskTrigger.Start) => SubTaskStatus.Running,
            (SubTaskStatus.Running, SubTaskTrigger.Complete) => SubTaskStatus.Completed,

            // 畳むのは着手前でも実行中でもよい。終端に達したものは畳めない
            //（完了の事実をキャンセルで上書きしない。Job の終端保護と同じ判断）。
            (SubTaskStatus.Pending, SubTaskTrigger.Cancel) => SubTaskStatus.Cancelled,
            (SubTaskStatus.Running, SubTaskTrigger.Cancel) => SubTaskStatus.Cancelled,

            _ => null,
        };

        if (next is not { } allowed)
        {
            return new SubTaskTransition(IsAllowed: false, previous);
        }

        Status = allowed;
        return new SubTaskTransition(IsAllowed: true, previous);
    }
}

/// <summary>
/// <see cref="SubTask.Apply"/> の結果。
/// </summary>
/// <param name="IsAllowed">遷移が許可されたか。false なら何も変わっていない。</param>
/// <param name="Previous">遷移前の状態。条件付き更新の期待値に使う。</param>
public readonly record struct SubTaskTransition(bool IsAllowed, SubTaskStatus Previous);
