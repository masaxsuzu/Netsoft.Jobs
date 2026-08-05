namespace Netsoft.Jobs.Domain;

/// <summary>
/// サブタスクの永続化の口。実装は Infrastructure 側に置く。
/// </summary>
/// <remarks>
/// 書くのはサブタスクを実行しているハンドラだけ（読み手は API と画面）。
/// それでも <see cref="UpdateAsync"/> を条件付きにしてあるのは <see cref="IJobStore"/> と
/// 同じ理由で、無条件の書き込み口を用意すると読み出しからの間に起きたことを
/// 黙って上書きできてしまうから。書き手が 1 人という現在の事情を契約の前提にしない。
/// </remarks>
public interface ISubTaskStore
{
    /// <summary>
    /// 1 つの Job のサブタスクをまとめて保存する。全件入るか 1 件も入らないかのどちらか。
    /// </summary>
    /// <remarks>
    /// 途中まで入ると「N 個のはずが k 個しか無い」状態が読み手に見えてしまう。
    /// N は parameters から導かれる約束で、行数がそれとずれた瞬間に進捗表示が嘘になる。
    /// </remarks>
    Task AddRangeAsync(IReadOnlyList<SubTask> subTasks, CancellationToken cancellationToken);

    /// <summary>
    /// 読み出した時点の状態が変わっていない場合にだけ、サブタスクを書き戻す。
    /// </summary>
    /// <param name="subTask">遷移を適用した後のサブタスク。</param>
    /// <param name="expectedStatus">
    /// 遷移前の状態（<see cref="SubTaskTransition.Previous"/> をそのまま渡す）。
    /// </param>
    /// <param name="cancellationToken">I/O の中断に使う。</param>
    /// <returns>書き戻せたなら true。他から状態が進められていたなら false。</returns>
    /// <exception cref="SubTaskNotFoundException">
    /// その行が保存されていない場合。状態の食い違い（false）と区別する理由は
    /// <see cref="IJobStore.UpdateAsync"/> の契約と同じ。
    /// </exception>
    Task<bool> UpdateAsync(SubTask subTask, SubTaskStatus expectedStatus, CancellationToken cancellationToken);

    /// <summary>指定した Job のサブタスクを連番順で取得する。無ければ空。</summary>
    Task<IReadOnlyList<SubTask>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken);

    /// <summary>
    /// 行を持つすべての Job の進捗を、まとめて 1 回で数える。行の無い Job は含まれない。
    /// </summary>
    /// <remarks>
    /// 一覧の表示のための口。Job ごとに <see cref="ListByJobAsync"/> を呼ぶと問い合わせが
    /// Job 数に比例して増えるうえ、行の中身は数えるためだけに読み捨てになる。
    /// 数だけが要るなら数だけを運ぶ。
    /// </remarks>
    Task<IReadOnlyDictionary<JobId, SubTaskProgress>> CountByJobAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 連番が <paramref name="firstIndex"/> 以降の<b>未着手（Pending）の</b>行を消す。
    /// </summary>
    /// <remarks>
    /// 編集で N を減らしたときの畳み方。着手済み（Pending でない）行は条件に合わず残るので、
    /// この口からは事実を消せない。「編集は定義の変更であって履歴の書き換えではない」を
    /// 契約として持つのはこの条件で、呼び出し側の行儀に頼らない。
    /// </remarks>
    Task RemovePendingFromAsync(JobId jobId, int firstIndex, CancellationToken cancellationToken);
}
