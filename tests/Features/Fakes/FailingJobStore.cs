using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 指定した操作で例外を投げる <see cref="IJobStore"/>。
/// </summary>
/// <remarks>
/// <para>
/// 競合（書き戻しが false）は <see cref="InterferingJobStore"/> と
/// <see cref="RelentlessJobStore"/> が扱う。こちらは<b>store そのものが応えない</b>場合
/// ── ディスクが一杯、ファイルが消えた、権限が変わった、といった I/O の障害 ── を演じる。
/// 状態が矛盾するのではなく、書けたかどうかが分からないまま制御が戻ってくるところが違う。
/// </para>
/// <para>
/// 投げるのは <see cref="Failures"/> 回だけで、それを過ぎたら素通しになる。
/// 永久に投げると「復旧できるか」を確かめられない。
/// </para>
/// </remarks>
internal sealed class FailingJobStore : IJobStore
{
    /// <summary>障害として投げる例外。SQLite の I/O 失敗の代役。</summary>
    public const string FailureMessage = "disk I/O error";

    private readonly IJobStore _inner;

    private int _updateFailures;
    private int _findOldestQueuedFailures;

    public FailingJobStore(IJobStore inner) => _inner = inner;

    /// <summary>これから <see cref="UpdateAsync"/> を失敗させる回数。</summary>
    public int UpdateFailures
    {
        get => Volatile.Read(ref _updateFailures);
        set => Volatile.Write(ref _updateFailures, value);
    }

    /// <summary>これから <see cref="FindOldestQueuedAsync"/> を失敗させる回数。</summary>
    public int FindOldestQueuedFailures
    {
        get => Volatile.Read(ref _findOldestQueuedFailures);
        set => Volatile.Write(ref _findOldestQueuedFailures, value);
    }

    public Task AddAsync(Job job, CancellationToken cancellationToken) =>
        _inner.AddAsync(job, cancellationToken);

    public Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken) =>
        Consume(ref _updateFailures)
            ? throw new IOException(FailureMessage)
            : _inner.UpdateAsync(job, cancellationToken);

    public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken) =>
        _inner.FindAsync(id, cancellationToken);

    public Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken) =>
        Consume(ref _findOldestQueuedFailures)
            ? throw new IOException(FailureMessage)
            : _inner.FindOldestQueuedAsync(cancellationToken);

    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken) =>
        _inner.ListAsync(cancellationToken);

    public Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken) =>
        _inner.ListByStatusAsync(status, cancellationToken);

    private static bool Consume(ref int remaining)
    {
        // 減らしてから判定する。0 を下回らせないために CompareExchange で取り合う。
        while (true)
        {
            int current = Volatile.Read(ref remaining);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref remaining, current - 1, current) == current)
            {
                return true;
            }
        }
    }
}
