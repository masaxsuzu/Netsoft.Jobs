namespace Netsoft.Jobs.Domain;

/// <summary>
/// Job の永続化の口。実装は Infrastructure 側に置く。
/// </summary>
/// <remarks>
/// <see cref="CancellationToken"/> を受け取るのは I/O を中断するためであって、
/// Domain が時間やスレッドを扱うという意味ではない。Domain 側は中断可能性を宣言するだけ。
/// </remarks>
public interface IJobStore
{
    /// <summary>新しい Job を保存する。</summary>
    Task AddAsync(Job job, CancellationToken cancellationToken);

    /// <summary>既存の Job の状態を書き戻す。</summary>
    Task UpdateAsync(Job job, CancellationToken cancellationToken);

    /// <summary>識別子で 1 件取得する。見つからなければ null。</summary>
    Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken);

    /// <summary>全件を作成日時の新しい順で取得する。</summary>
    Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 最も古い Queued の Job を 1 件取得する。実行エンジンが次に動かすものを選ぶのに使う。
    /// </summary>
    Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 指定した状態の Job を取得する。起動時復旧で Running / Cancelling を拾うのに使う。
    /// </summary>
    Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken);
}
