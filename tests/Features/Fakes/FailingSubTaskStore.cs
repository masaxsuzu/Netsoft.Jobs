using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 指定した回数だけ書き戻しで例外を投げる <see cref="ISubTaskStore"/>。
/// </summary>
/// <remarks>
/// サブタスク側の <see cref="FailingJobStore"/>。ハンドラの途中で I/O が落ちたときに、
/// Job とサブタスクの行がどう残るかを見るために使う。
/// </remarks>
internal sealed class FailingSubTaskStore : ISubTaskStore
{
    private readonly ISubTaskStore _inner;

    private int _updateFailures;

    public FailingSubTaskStore(ISubTaskStore inner) => _inner = inner;

    /// <summary>これから <see cref="UpdateAsync"/> を失敗させる回数。</summary>
    public int UpdateFailures
    {
        get => Volatile.Read(ref _updateFailures);
        set => Volatile.Write(ref _updateFailures, value);
    }

    public Task AddRangeAsync(IReadOnlyList<SubTask> subTasks, CancellationToken cancellationToken) =>
        _inner.AddRangeAsync(subTasks, cancellationToken);

    public Task<bool> UpdateAsync(SubTask subTask, SubTaskStatus expectedStatus, CancellationToken cancellationToken)
    {
        while (true)
        {
            int current = UpdateFailures;
            if (current <= 0)
            {
                return _inner.UpdateAsync(subTask, expectedStatus, cancellationToken);
            }

            if (Interlocked.CompareExchange(ref _updateFailures, current - 1, current) == current)
            {
                throw new IOException(FailingJobStore.FailureMessage);
            }
        }
    }

    public Task<IReadOnlyList<SubTask>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) =>
        _inner.ListByJobAsync(jobId, cancellationToken);

    public Task<IReadOnlyDictionary<JobId, SubTaskProgress>> CountByJobAsync(CancellationToken cancellationToken) =>
        _inner.CountByJobAsync(cancellationToken);

    public Task RemovePendingFromAsync(JobId jobId, int firstIndex, CancellationToken cancellationToken) =>
        _inner.RemovePendingFromAsync(jobId, firstIndex, cancellationToken);
}
