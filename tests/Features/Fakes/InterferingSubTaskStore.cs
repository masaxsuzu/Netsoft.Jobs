using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 書き戻しの直後に、テストが指定した処理を 1 度だけ割り込ませる <see cref="ISubTaskStore"/>。
/// </summary>
/// <remarks>
/// サブタスク側の <see cref="InterferingJobStore"/>。ハンドラの「最後の完了を書いてから、
/// 次の周回で境界を見るまで」の窓に割り込むために使う。この窓は本物のスレッドでは
/// 狙って当てられないので、書き込みそのものを合図にして決定的に起こす。
/// </remarks>
internal sealed class InterferingSubTaskStore : ISubTaskStore
{
    private readonly ISubTaskStore _inner;

    public InterferingSubTaskStore(ISubTaskStore inner) => _inner = inner;

    /// <summary>
    /// 次の書き戻しが成立した<b>直後</b>に 1 度だけ実行する処理。発火したら自分で外れる。
    /// </summary>
    public Action? AfterNextUpdate { get; set; }

    public Task AddRangeAsync(IReadOnlyList<SubTask> subTasks, CancellationToken cancellationToken) =>
        _inner.AddRangeAsync(subTasks, cancellationToken);

    public async Task<bool> UpdateAsync(
        SubTask subTask,
        SubTaskStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        bool updated = await _inner.UpdateAsync(subTask, expectedStatus, cancellationToken);

        if (updated && AfterNextUpdate is { } interference)
        {
            AfterNextUpdate = null;
            interference();
        }

        return updated;
    }

    public Task<IReadOnlyList<SubTask>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) =>
        _inner.ListByJobAsync(jobId, cancellationToken);

    public Task<IReadOnlyDictionary<JobId, SubTaskProgress>> CountByJobAsync(CancellationToken cancellationToken) =>
        _inner.CountByJobAsync(cancellationToken);

    public Task RemovePendingFromAsync(JobId jobId, int firstIndex, CancellationToken cancellationToken) =>
        _inner.RemovePendingFromAsync(jobId, firstIndex, cancellationToken);
}
