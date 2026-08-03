using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Tests.Concurrency;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 条件付き更新が成立した書き込みを <see cref="JobObservationLog"/> へ控える
/// <see cref="IJobStore"/> のデコレータ。
/// </summary>
/// <remarks>
/// システム全体の書き込みはこの store を必ず通るので、ここで控えれば
/// 「ある Job のある状態から書き戻せたのは高々 1 回か」を漏れなく検査できる。
/// 状態を後から読むだけでは、上書きされて消えた更新は見えない。
/// </remarks>
internal sealed class RecordingJobStore : IJobStore
{
    private readonly IJobStore _inner;
    private readonly JobObservationLog _log;

    public RecordingJobStore(IJobStore inner, JobObservationLog log)
    {
        _inner = inner;
        _log = log;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(Job job, JobStatus expectedStatus, CancellationToken cancellationToken)
    {
        // Job は書き戻しの前に Apply 済みなので、ここで読む Status が遷移後の状態になる。
        JobStatus next = job.Status;

        bool updated = await _inner.UpdateAsync(job, expectedStatus, cancellationToken);
        if (updated)
        {
            _log.RecordAcceptedUpdate(job.Id, expectedStatus, next);
        }

        return updated;
    }

    /// <inheritdoc />
    public Task AddAsync(Job job, CancellationToken cancellationToken) =>
        _inner.AddAsync(job, cancellationToken);

    /// <inheritdoc />
    public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken) =>
        _inner.FindAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken) =>
        _inner.ListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken) =>
        _inner.FindOldestQueuedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken) =>
        _inner.ListByStatusAsync(status, cancellationToken);
}
