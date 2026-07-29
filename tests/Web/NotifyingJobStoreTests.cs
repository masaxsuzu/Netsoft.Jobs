using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// 変更通知デコレータ。画面の push 更新はここが全書き込みを漏れなく拾うことに
/// 依存しているので、「書き込みだけが通知される」「失敗は通知されない」を固定する。
/// 保存そのものの正しさは Infrastructure のテストの領分なので、内側は偽物でよい。
/// </summary>
public sealed class NotifyingJobStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly RecordingInnerStore _inner = new();
    private readonly JobChangeFeed _feed = new();
    private readonly NotifyingJobStore _store;
    private int _published;

    public NotifyingJobStoreTests()
    {
        _store = new NotifyingJobStore(_inner, _feed);
        _feed.Changed += () => _published++;
    }

    [Fact]
    public async Task 追加が成功すると通知される()
    {
        await _store.AddAsync(CreateJob(), CancellationToken.None);

        Assert.Equal(1, _published);
        Assert.Equal(["Add"], _inner.Calls);
    }

    [Fact]
    public async Task 更新が成功すると通知される()
    {
        Assert.True(await _store.UpdateAsync(CreateJob(), JobStatus.Queued, CancellationToken.None));

        Assert.Equal(1, _published);
        Assert.Equal(["Update"], _inner.Calls);
    }

    /// <summary>
    /// 条件付き更新の false は「前提が崩れたので何も書かなかった」。
    /// DB が変わっていない以上、通知しても画面が同じ一覧を描き直すだけで無駄になる。
    /// </summary>
    [Fact]
    public async Task 更新が書き戻されなかったら通知されない()
    {
        _inner.UpdateResult = false;

        Assert.False(await _store.UpdateAsync(CreateJob(), JobStatus.Queued, CancellationToken.None));

        Assert.Equal(0, _published);
        Assert.Equal(["Update"], _inner.Calls);
    }

    /// <summary>
    /// 書き込みが失敗したのに通知すると、画面が「変わったはず」と再取得して
    /// 変わっていない一覧を描き直すだけになる。失敗は通知しない。
    /// </summary>
    [Fact]
    public async Task 追加が失敗すると通知されない()
    {
        _inner.FailWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.AddAsync(CreateJob(), CancellationToken.None));

        Assert.Equal(0, _published);
    }

    [Fact]
    public async Task 更新が失敗すると通知されない()
    {
        _inner.FailWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.UpdateAsync(CreateJob(), JobStatus.Queued, CancellationToken.None));

        Assert.Equal(0, _published);
    }

    [Fact]
    public async Task 読み取りは通知されず内側へ素通しされる()
    {
        _ = await _store.FindAsync(JobId.From("job-1"), CancellationToken.None);
        _ = await _store.ListAsync(CancellationToken.None);
        _ = await _store.FindOldestQueuedAsync(CancellationToken.None);
        _ = await _store.ListByStatusAsync(JobStatus.Queued, CancellationToken.None);

        Assert.Equal(0, _published);
        Assert.Equal(["Find", "List", "FindOldestQueued", "ListByStatus"], _inner.Calls);
    }

    private static Job CreateJob()
    {
        JobId id = JobId.From("job-1");
        return Job.Create(id, "テスト", "demo", "", T0);
    }

    /// <summary>呼ばれたことだけを記録する内側。デコレータの検証に保存の実体は要らない。</summary>
    private sealed class RecordingInnerStore : IJobStore
    {
        public List<string> Calls { get; } = [];

        public bool FailWrites { get; set; }

        /// <summary>条件付き更新が書き戻せたかどうか。既定は「書き戻せた」。</summary>
        public bool UpdateResult { get; set; } = true;

        public Task AddAsync(Job job, CancellationToken cancellationToken)
        {
            ThrowIfWriteShouldFail();
            Calls.Add("Add");
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(Job job, JobStatus expectedStatus, CancellationToken cancellationToken)
        {
            ThrowIfWriteShouldFail();
            Calls.Add("Update");
            return Task.FromResult(UpdateResult);
        }

        public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken)
        {
            Calls.Add("Find");
            return Task.FromResult<Job?>(null);
        }

        public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken)
        {
            Calls.Add("List");
            return Task.FromResult<IReadOnlyList<Job>>([]);
        }

        public Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken)
        {
            Calls.Add("FindOldestQueued");
            return Task.FromResult<Job?>(null);
        }

        public Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken)
        {
            Calls.Add("ListByStatus");
            return Task.FromResult<IReadOnlyList<Job>>([]);
        }

        private void ThrowIfWriteShouldFail()
        {
            if (FailWrites)
            {
                throw new InvalidOperationException("書き込みを失敗させるテスト設定");
            }
        }
    }
}
