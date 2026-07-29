using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.CancelJob;

public sealed class CancelJobHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = new(2026, 7, 29, 9, 1, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Finished = new(2026, 7, 29, 9, 2, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Requested = new(2026, 7, 29, 9, 3, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _jobs = new();
    private readonly CancelJobCallLog _log = new();
    private readonly RecordingJobStore _store;
    private readonly RecordingRunningJobRegistry _runningJobs;
    private readonly FixedTimeProvider _timeProvider = new(Requested);
    private readonly CancelJobHandler _handler;

    public CancelJobHandlerTests()
    {
        _store = new RecordingJobStore(_jobs, _log);
        _runningJobs = new RecordingRunningJobRegistry(_log);
        _handler = new CancelJobHandler(_store, _runningJobs, _timeProvider);
    }

    public void Dispose() => _jobs.Dispose();

    [Fact]
    public async Task 待機中のJobをキャンセルすると即座に中止済みになる()
    {
        // ハンドラを起動していないので、受理を待つ相手がいない。
        await AddAsync(Queued("job-1"));

        CancelJobResult result = await CancelAsync("job-1");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Rejection);
        Assert.NotNull(result.Job);
        Assert.Equal(nameof(JobStatus.Cancelled), result.Job.Status);
    }

    [Fact]
    public async Task 待機中のJobをキャンセルするとその状態が保存される()
    {
        await AddAsync(Queued("job-1"));

        await CancelAsync("job-1");

        Job saved = await SavedAsync("job-1");
        Assert.Equal(JobStatus.Cancelled, saved.Status);

        // 終端に達したので終了時刻が入る。時刻は要求した時点のもの。
        Assert.Equal(Requested, saved.FinishedAt);
        Assert.Equal(["update:job-1:Cancelled", "cancel:job-1"], _log.Entries);
    }

    [Fact]
    public async Task 実行中のJobをキャンセルするとキャンセル中になり保存される()
    {
        // ハンドラが動いているので、受理されるまでは終端に進めない。
        await AddAsync(Running("job-1"));

        CancelJobResult result = await CancelAsync("job-1");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Job);
        Assert.Equal(nameof(JobStatus.Cancelling), result.Job.Status);

        Job saved = await SavedAsync("job-1");
        Assert.Equal(JobStatus.Cancelling, saved.Status);
        Assert.Null(saved.FinishedAt);
    }

    [Fact]
    public async Task 実行中のJobはハンドラへキャンセルが伝えられる()
    {
        await AddAsync(Running("job-1"));

        await CancelAsync("job-1");

        Assert.Equal([JobId.From("job-1")], _runningJobs.RequestedIds);
    }

    [Fact]
    public async Task キャンセルは保存してから伝えられる()
    {
        // 逆順にすると、こちらが Cancelling を書く前にハンドラが終端を書き込みうる。
        // その後に届くこの更新が終端を Cancelling で上書きして、
        // 終わっているのに終わっていない Job ができる。
        await AddAsync(Running("job-1"));

        await CancelAsync("job-1");

        Assert.Equal(["update:job-1:Cancelling", "cancel:job-1"], _log.Entries);
    }

    [Fact]
    public async Task 実行中でないと伝えられなくても状態遷移は巻き戻らない()
    {
        // TryRequestCancel の false は失敗ではない（別プロセスが実行している、など）。
        _runningJobs.Result = false;
        await AddAsync(Running("job-1"));

        CancelJobResult result = await CancelAsync("job-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(nameof(JobStatus.Cancelling), result.Job?.Status);
        Assert.Equal(JobStatus.Cancelling, (await SavedAsync("job-1")).Status);
    }

    [Fact]
    public async Task 待機中のJobでも伝達の戻り値を成否の判断に使わない()
    {
        // 待機中はそもそも実行中ではないので false が返る。それが正しい。
        _runningJobs.Result = false;
        await AddAsync(Queued("job-1"));

        CancelJobResult result = await CancelAsync("job-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(JobStatus.Cancelled, (await SavedAsync("job-1")).Status);
    }

    [Fact]
    public async Task キャンセル中に再度要求しても成功として扱う()
    {
        // 利用者から見ればボタンを 2 回押しただけ。要求の意図は既に満たされている。
        await AddAsync(Cancelling("job-1"));

        CancelJobResult result = await CancelAsync("job-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(JobTransitionRejection.AlreadyInEffect, result.Rejection);
        Assert.Equal(nameof(JobStatus.Cancelling), result.Job?.Status);
    }

    [Fact]
    public async Task キャンセル中への再要求では保存も伝達もしない()
    {
        // 状態は既に Cancelling で、トークンは最初の要求で発火済み。やり直すことが無い。
        await AddAsync(Cancelling("job-1"));

        await CancelAsync("job-1");

        Assert.Empty(_log.Entries);
    }

    [Theory]
    [InlineData(nameof(JobStatus.Completed))]
    [InlineData(nameof(JobStatus.Failed))]
    [InlineData(nameof(JobStatus.Cancelled))]
    public async Task 終端のJobは既に終わっているとして拒否される(string status)
    {
        await AddAsync(Terminal("job-1", status));

        CancelJobResult result = await CancelAsync("job-1");

        // エンドポイントはこれを 409 に写す。
        Assert.False(result.IsSuccess);
        Assert.Equal(JobTransitionRejection.JobAlreadyFinished, result.Rejection);

        // 現在の状態は返す。呼び出し側が読み直さずに表示を決められるようにするため。
        Assert.Equal(status, result.Job?.Status);
    }

    [Fact]
    public async Task 存在しない識別子は対象なしとして返る()
    {
        // エンドポイントはこれを 404 に写す。
        await AddAsync(Queued("job-1"));

        CancelJobResult result = await CancelAsync("job-2");

        Assert.Null(result.Job);
        Assert.Null(result.Rejection);
        Assert.False(result.IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task 識別子の形にならない値も対象なしとして扱う(string? id)
    {
        // 何も指し示していない以上、答えは「無い」でよい。読み出しと同じ扱いにそろえる。
        await AddAsync(Queued("job-1"));

        CancelJobResult result = await _handler.HandleAsync(id!, CancellationToken.None);

        Assert.Null(result.Job);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task 対象が無いときは伝達もしない()
    {
        CancelJobResult result = await CancelAsync("job-1");

        Assert.Null(result.Job);
        Assert.Empty(_runningJobs.RequestedIds);
    }

    [Fact]
    public async Task 拒否されたときstoreの内容は変わらない()
    {
        await AddAsync(Terminal("job-1", nameof(JobStatus.Completed)));
        await AddAsync(Terminal("job-2", nameof(JobStatus.Failed)));
        await AddAsync(Cancelling("job-3"));

        IReadOnlyList<string> before = await SnapshotAsync();

        await CancelAsync("job-1");
        await CancelAsync("job-2");
        await CancelAsync("job-3");
        await CancelAsync("job-4");

        Assert.Equal(before, await SnapshotAsync());
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task キャンセルは指定したJobだけに効く()
    {
        await AddAsync(Queued("job-1"));
        await AddAsync(Running("job-2"));

        await CancelAsync("job-2");

        Assert.Equal(JobStatus.Queued, (await SavedAsync("job-1")).Status);
        Assert.Equal(JobStatus.Cancelling, (await SavedAsync("job-2")).Status);
    }

    private Task<CancelJobResult> CancelAsync(string id) => _handler.HandleAsync(id, CancellationToken.None);

    private Task AddAsync(Job job) => _jobs.AddAsync(job, CancellationToken.None);

    private async Task<Job> SavedAsync(string id) =>
        await _jobs.FindAsync(JobId.From(id), CancellationToken.None)
        ?? throw new InvalidOperationException($"Job {id} が保存されていません。");

    /// <summary>
    /// store の中身を文字列に写して、要求の前後で変わっていないことを比べられるようにする。
    /// </summary>
    private async Task<IReadOnlyList<string>> SnapshotAsync() =>
    [
        .. (await _jobs.ListAsync(CancellationToken.None)).Select(job =>
            $"{job.Id}|{job.Status}|{job.CreatedAt:O}|{job.StartedAt:O}|{job.FinishedAt:O}|{job.FailureMessage}")
    ];

    private static Job Queued(string id) =>
        Job.Create(JobId.From(id), $"集計 {id}", "Demo", "{}", Created);

    private static Job Running(string id)
    {
        Job job = Queued(id);
        job.Apply(JobTrigger.Start, Started);
        return job;
    }

    private static Job Cancelling(string id)
    {
        Job job = Running(id);
        job.Apply(JobTrigger.RequestCancel, Finished);
        return job;
    }

    private static Job Terminal(string id, string status)
    {
        Job job = Running(id);

        switch (status)
        {
            case nameof(JobStatus.Completed):
                job.Apply(JobTrigger.Complete, Finished);
                break;
            case nameof(JobStatus.Failed):
                job.Apply(JobTrigger.Fail, Finished, "接続できませんでした。");
                break;
            case nameof(JobStatus.Cancelled):
                job.Apply(JobTrigger.RequestCancel, Finished);
                job.Apply(JobTrigger.ConfirmCancelled, Finished);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "終端状態ではありません。");
        }

        return job;
    }
}
