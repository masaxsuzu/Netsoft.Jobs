using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.QueryJobs;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.QueryJobs;

public sealed class QueryJobsHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = new(2026, 7, 29, 9, 1, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Finished = new(2026, 7, 29, 9, 2, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly QueryJobsHandler _handler;

    public QueryJobsHandlerTests() => _handler = new QueryJobsHandler(_store);

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task 一覧は作成日時の新しい順で返る()
    {
        await AddAsync(Queued("job-old", Created));
        await AddAsync(Queued("job-new", Created.AddHours(2)));
        await AddAsync(Queued("job-middle", Created.AddHours(1)));

        IReadOnlyList<JobDto> jobs = await _handler.ListAsync(CancellationToken.None);

        Assert.Equal(["job-new", "job-middle", "job-old"], jobs.Select(job => job.Id));
    }

    [Fact]
    public async Task 一件も無いときは空の一覧が返る()
    {
        // 0 件は異常ではないので、例外にも null にもしない。
        IReadOnlyList<JobDto> jobs = await _handler.ListAsync(CancellationToken.None);

        Assert.Empty(jobs);
    }

    [Fact]
    public async Task 詳細は指定した識別子のJobを返す()
    {
        await AddAsync(Queued("job-1", Created));
        await AddAsync(Queued("job-2", Created.AddHours(1)));

        JobDto? job = await _handler.FindAsync("job-2", CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal("job-2", job.Id);
    }

    [Fact]
    public async Task 存在しない識別子ならnullが返る()
    {
        // 「該当が無い」を null で表す。エンドポイントはこれを見て 404 を返す。
        await AddAsync(Queued("job-1", Created));

        Assert.Null(await _handler.FindAsync("job-2", CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task 識別子の形にならない値も無いものとして扱う(string? id)
    {
        // 何も指し示していない以上、答えは「無い」でよい。呼び出し側に別の分岐を増やさない。
        await AddAsync(Queued("job-1", Created));

        Assert.Null(await _handler.FindAsync(id!, CancellationToken.None));
    }

    [Fact]
    public async Task 一覧を読んでもJobの状態は変わらない()
    {
        await AddAsync(Queued("job-1", Created));
        await AddAsync(Running("job-2", Created));
        await AddAsync(Failed("job-3", Created, "接続できませんでした。"));

        IReadOnlyList<string> before = await SnapshotAsync();

        await _handler.ListAsync(CancellationToken.None);

        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 詳細を読んでもJobの状態は変わらない()
    {
        await AddAsync(Queued("job-1", Created));
        await AddAsync(Running("job-2", Created));

        IReadOnlyList<string> before = await SnapshotAsync();

        await _handler.FindAsync("job-2", CancellationToken.None);

        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 待機中のJobは開始時刻も終了時刻も持たない()
    {
        await AddAsync(Queued("job-1", Created));

        JobDto job = await FindRequiredAsync("job-1");

        Assert.Equal(nameof(JobStatus.Queued), job.Status);
        Assert.Equal(Created, job.CreatedAt);
        Assert.Null(job.StartedAt);
        Assert.Null(job.FinishedAt);
        Assert.Null(job.FailureMessage);
    }

    [Fact]
    public async Task 実行中のJobは開始時刻を持ち終了時刻を持たない()
    {
        await AddAsync(Running("job-1", Created));

        JobDto job = await FindRequiredAsync("job-1");

        Assert.Equal(nameof(JobStatus.Running), job.Status);
        Assert.Equal(Started, job.StartedAt);
        Assert.Null(job.FinishedAt);
        Assert.Null(job.FailureMessage);
    }

    [Fact]
    public async Task キャンセル要求中のJobは実行中と同じ時刻を保つ()
    {
        await AddAsync(Cancelling("job-1", Created));

        JobDto job = await FindRequiredAsync("job-1");

        Assert.Equal(nameof(JobStatus.Cancelling), job.Status);
        Assert.Equal(Started, job.StartedAt);

        // 受理を待っている最中でまだ終わっていない。
        Assert.Null(job.FinishedAt);
    }

    [Fact]
    public async Task 完了したJobは開始時刻と終了時刻を持つ()
    {
        await AddAsync(Completed("job-1", Created));

        JobDto job = await FindRequiredAsync("job-1");

        Assert.Equal(nameof(JobStatus.Completed), job.Status);
        Assert.Equal(Started, job.StartedAt);
        Assert.Equal(Finished, job.FinishedAt);
        Assert.Null(job.FailureMessage);
    }

    [Fact]
    public async Task 失敗したJobは理由が落ちずに返る()
    {
        await AddAsync(Failed("job-1", Created, "接続できませんでした。"));

        JobDto job = await FindRequiredAsync("job-1");

        Assert.Equal(nameof(JobStatus.Failed), job.Status);
        Assert.Equal(Started, job.StartedAt);
        Assert.Equal(Finished, job.FinishedAt);
        Assert.Equal("接続できませんでした。", job.FailureMessage);
    }

    [Fact]
    public async Task 中止したJobは開始時刻と終了時刻を持ち理由を持たない()
    {
        await AddAsync(Cancelled("job-1", Created));

        JobDto job = await FindRequiredAsync("job-1");

        Assert.Equal(nameof(JobStatus.Cancelled), job.Status);
        Assert.Equal(Started, job.StartedAt);
        Assert.Equal(Finished, job.FinishedAt);
        Assert.Null(job.FailureMessage);
    }

    [Fact]
    public async Task 一覧でも各状態の時刻と理由が落ちない()
    {
        // 詳細と一覧で写し方が食い違わないことを見る。
        await AddAsync(Failed("job-1", Created, "接続できませんでした。"));
        await AddAsync(Completed("job-2", Created.AddMinutes(1)));

        IReadOnlyList<JobDto> jobs = await _handler.ListAsync(CancellationToken.None);

        JobDto failed = jobs.Single(job => job.Id == "job-1");
        Assert.Equal(nameof(JobStatus.Failed), failed.Status);
        Assert.Equal("接続できませんでした。", failed.FailureMessage);
        Assert.Equal(Finished, failed.FinishedAt);

        JobDto completed = jobs.Single(job => job.Id == "job-2");
        Assert.Equal(nameof(JobStatus.Completed), completed.Status);
        Assert.Null(completed.FailureMessage);
    }

    [Fact]
    public async Task 名前と種類とパラメータもそのまま返る()
    {
        Job job = Job.Create(JobId.From("job-1"), "毎晩の集計", "Demo", "これは JSON ではない", Created);
        await AddAsync(job);

        JobDto dto = await FindRequiredAsync("job-1");

        Assert.Equal("毎晩の集計", dto.Name);
        Assert.Equal("Demo", dto.JobType);
        Assert.Equal("これは JSON ではない", dto.Parameters);
    }

    private async Task<JobDto> FindRequiredAsync(string id)
    {
        JobDto? job = await _handler.FindAsync(id, CancellationToken.None);

        Assert.NotNull(job);
        return job;
    }

    private Task AddAsync(Job job) => _store.AddAsync(job, CancellationToken.None);

    /// <summary>
    /// store の中身を文字列に写して、読み出しの前後で変わっていないことを比べられるようにする。
    /// </summary>
    private async Task<IReadOnlyList<string>> SnapshotAsync() =>
    [
        .. (await _store.ListAsync(CancellationToken.None)).Select(job =>
            $"{job.Id}|{job.Status}|{job.CreatedAt:O}|{job.StartedAt:O}|{job.FinishedAt:O}|{job.FailureMessage}")
    ];

    private static Job Queued(string id, DateTimeOffset createdAt) =>
        Job.Create(JobId.From(id), $"集計 {id}", "Demo", "{}", createdAt);

    private static Job Running(string id, DateTimeOffset createdAt)
    {
        Job job = Queued(id, createdAt);
        job.Apply(JobTrigger.Start, Started);
        return job;
    }

    private static Job Cancelling(string id, DateTimeOffset createdAt)
    {
        Job job = Running(id, createdAt);
        job.Apply(JobTrigger.RequestCancel, Finished);
        return job;
    }

    private static Job Completed(string id, DateTimeOffset createdAt)
    {
        Job job = Running(id, createdAt);
        job.Apply(JobTrigger.Complete, Finished);
        return job;
    }

    private static Job Failed(string id, DateTimeOffset createdAt, string failureMessage)
    {
        Job job = Running(id, createdAt);
        job.Apply(JobTrigger.Fail, Finished, failureMessage);
        return job;
    }

    private static Job Cancelled(string id, DateTimeOffset createdAt)
    {
        Job job = Cancelling(id, createdAt);
        job.Apply(JobTrigger.ConfirmCancelled, Finished);
        return job;
    }
}
