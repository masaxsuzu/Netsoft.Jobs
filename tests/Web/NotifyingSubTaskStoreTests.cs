using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// サブタスク側の変更通知デコレータ。画面の進捗（k/N）の push 更新はここが
/// 書き込みを漏れなく拾うことに依存する。理由の本体は <see cref="NotifyingJobStoreTests"/>。
/// </summary>
public sealed class NotifyingSubTaskStoreTests
{
    private readonly RecordingInnerSubTaskStore _inner = new();
    private readonly JobChangeFeed _feed = new();
    private readonly NotifyingSubTaskStore _store;
    private int _published;

    public NotifyingSubTaskStoreTests()
    {
        _store = new NotifyingSubTaskStore(_inner, _feed);
        _feed.Changed += () => _published++;
    }

    [Fact]
    public async Task まとめた追加が成功すると1回だけ通知される()
    {
        await _store.AddRangeAsync(
            [SubTask.Create(JobId.From("job-1"), 0), SubTask.Create(JobId.From("job-1"), 1)],
            CancellationToken.None);

        // N 行で N 回鳴らさない。合図は「変わったかもしれない」以上を運ばない。
        Assert.Equal(1, _published);
    }

    [Fact]
    public async Task 空の追加は何も書いていないので通知されない()
    {
        await _store.AddRangeAsync([], CancellationToken.None);

        Assert.Equal(0, _published);
    }

    [Fact]
    public async Task 更新は成功したときだけ通知される()
    {
        Assert.True(await _store.UpdateAsync(
            SubTask.Create(JobId.From("job-1"), 0), SubTaskStatus.Pending, CancellationToken.None));
        Assert.Equal(1, _published);

        _inner.UpdateResult = false;
        Assert.False(await _store.UpdateAsync(
            SubTask.Create(JobId.From("job-1"), 0), SubTaskStatus.Pending, CancellationToken.None));
        Assert.Equal(1, _published);
    }

    [Fact]
    public async Task 未着手の削除は通知され読み出しは通知されない()
    {
        await _store.RemovePendingFromAsync(JobId.From("job-1"), 1, CancellationToken.None);
        Assert.Equal(1, _published);

        _ = await _store.ListByJobAsync(JobId.From("job-1"), CancellationToken.None);
        Assert.Equal(1, _published);
    }

    private sealed class RecordingInnerSubTaskStore : ISubTaskStore
    {
        public bool UpdateResult { get; set; } = true;

        public Task AddRangeAsync(IReadOnlyList<SubTask> subTasks, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> UpdateAsync(SubTask subTask, SubTaskStatus expectedStatus, CancellationToken cancellationToken) =>
            Task.FromResult(UpdateResult);

        public Task<IReadOnlyList<SubTask>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SubTask>>([]);

        public Task RemovePendingFromAsync(JobId jobId, int firstIndex, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyDictionary<JobId, SubTaskProgress>> CountByJobAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<JobId, SubTaskProgress>>(
                new Dictionary<JobId, SubTaskProgress>());
    }
}
