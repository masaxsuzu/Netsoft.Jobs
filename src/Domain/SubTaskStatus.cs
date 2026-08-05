namespace Netsoft.Jobs.Domain;

/// <summary>
/// サブタスクの状態。<see cref="JobStatus"/> より 1 段簡素で、Cancelling を持たない。
/// </summary>
/// <remarks>
/// Job に Cancelling（受理待ち）があるのは、要求する側（画面）と受理する側（ハンドラ）が
/// 別の実行の流れだから。サブタスクは進めるのも畳むのも同じハンドラなので、
/// 「要求したが受理されていない」という中間が構造的に存在しない。
/// </remarks>
public enum SubTaskStatus
{
    /// <summary>まだ着手していない。</summary>
    Pending,

    /// <summary>実行中。</summary>
    Running,

    /// <summary>最後まで走り終えた。終端。</summary>
    Completed,

    /// <summary>着手前または実行中に畳まれた。終端。</summary>
    Cancelled,
}

/// <summary>
/// <see cref="SubTaskStatus"/> の判定。
/// </summary>
public static class SubTaskStatusExtensions
{
    /// <summary>
    /// もう変わらない状態か。true になった行がその後に変わることは無い。
    /// </summary>
    /// <remarks>
    /// Job が終端に達しているのにサブタスクがここで false（Pending / Running のまま）なら、
    /// それは異常終了の中断点の記録。上書きして揃えず、どこで止まったかの情報として残す。
    /// </remarks>
    public static bool IsTerminal(this SubTaskStatus status) =>
        status is SubTaskStatus.Completed or SubTaskStatus.Cancelled;
}
