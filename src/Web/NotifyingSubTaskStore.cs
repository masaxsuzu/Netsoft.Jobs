using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Web;

/// <summary>
/// 書き込みが成功したら <see cref="JobChangeFeed"/> へ知らせる <see cref="ISubTaskStore"/> のデコレータ。
/// </summary>
/// <remarks>
/// 理由と作りは <see cref="NotifyingJobStore"/> と同じ。サブタスクの書き込みは
/// ハンドラ（実行エンジンの中）からしか起きないが、画面は進捗（k/N と各行の状態）を
/// 見せるので、行が動いたら合図が要る。合図の意味は「何かが変わったかもしれない」の
/// ままで、Job の変更とサブタスクの変更を区別しない（画面はどちらでも読み直すだけ）。
/// </remarks>
public sealed class NotifyingSubTaskStore : ISubTaskStore
{
    private readonly ISubTaskStore _inner;
    private readonly JobChangeFeed _feed;

    public NotifyingSubTaskStore(ISubTaskStore inner, JobChangeFeed feed)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(feed);

        _inner = inner;
        _feed = feed;
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(IReadOnlyList<SubTask> subTasks, CancellationToken cancellationToken)
    {
        await _inner.AddRangeAsync(subTasks, cancellationToken);

        if (subTasks.Count > 0)
        {
            // 空の保存は何も書いていないので知らせない。
            _feed.Publish();
        }
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(SubTask subTask, SubTaskStatus expectedStatus, CancellationToken cancellationToken)
    {
        bool updated = await _inner.UpdateAsync(subTask, expectedStatus, cancellationToken);
        if (updated)
        {
            // false は何も書かなかった（理由は NotifyingJobStore と同じ）。
            _feed.Publish();
        }

        return updated;
    }

    /// <inheritdoc />
    public async Task RemovePendingFromAsync(JobId jobId, int firstIndex, CancellationToken cancellationToken)
    {
        await _inner.RemovePendingFromAsync(jobId, firstIndex, cancellationToken);

        // 消えた行数は返ってこないので、0 行でも知らせる。余分な合図は無害（読み直すだけ）。
        _feed.Publish();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubTask>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) =>
        _inner.ListByJobAsync(jobId, cancellationToken);
}
