using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.QueryJobs;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.QueryJobs;

/// <summary>
/// 読み出しの写し方（Job → JobDto）を固定する。
/// </summary>
/// <remarks>
/// 状態ごとの時刻と理由の有無は 1 つの Theory に束ねてある。写し方は状態で場合分け
/// されるものではなく「あるものをそのまま写す」の 1 本なので、行を分けても
/// 守れるものは増えない。HTTP への写し（200/404）は tests/Web の JobApiTests が持つ。
/// </remarks>
public sealed class QueryJobsHandlerTests : IDisposable
{
    private const string FailureText = "接続できませんでした。";

    private static readonly DateTimeOffset Created = new(2026, 7, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = new(2026, 7, 29, 9, 1, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Finished = new(2026, 7, 29, 9, 2, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _store = new();
    private readonly TemporarySubTaskStore _subTasks = new();
    private readonly QueryJobsHandler _handler;

    public QueryJobsHandlerTests() => _handler = new QueryJobsHandler(_store, _subTasks);

    public void Dispose()
    {
        _subTasks.Dispose();
        _store.Dispose();
    }

    [Fact]
    public async Task 一覧は作成日時の新しい順で返る()
    {
        await AddAsync(Registered("job-old", Created));
        await AddAsync(Registered("job-new", Created.AddHours(2)));
        await AddAsync(Registered("job-middle", Created.AddHours(1)));

        IReadOnlyList<JobListItemDto> jobs = await _handler.ListAsync(CancellationToken.None);

        Assert.Equal(["job-new", "job-middle", "job-old"], jobs.Select(job => job.Id));
    }

    [Fact]
    public async Task 詳細は指定した識別子のJobを返す()
    {
        await AddAsync(Registered("job-1", Created));
        await AddAsync(Registered("job-2", Created.AddHours(1)));

        JobDto? job = await _handler.FindAsync("job-2", CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal("job-2", job.Id);
    }

    /// <summary>
    /// 存在しない・識別子の形にならない、はどちらも「無い」。null で表し、
    /// エンドポイントはこれを見て 404 を返す。呼び出し側に別の分岐を増やさない。
    /// </summary>
    [Theory]
    [InlineData("job-9")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task 対象が無ければnullが返る(string? id)
    {
        await AddAsync(Registered("job-1", Created));

        Assert.Null(await _handler.FindAsync(id!, CancellationToken.None));
    }

    /// <summary>
    /// どの状態でも、持っている時刻と理由がそのまま写ること。
    /// 期待値は状態機械の定義から導ける（開始前は StartedAt が無く、終端だけが
    /// FinishedAt を持ち、理由は Failed だけが持つ）ので、行ごとに書き並べない。
    /// </summary>
    [Theory]
    [InlineData(nameof(JobStatus.Registered))]
    [InlineData(nameof(JobStatus.InProgress))]
    [InlineData(nameof(JobStatus.Cancelling))]
    [InlineData(nameof(JobStatus.Completed))]
    [InlineData(nameof(JobStatus.Failed))]
    [InlineData(nameof(JobStatus.Cancelled))]
    public async Task 状態ごとの時刻と理由が落ちずに写る(string status)
    {
        await AddAsync(InState("job-1", status));

        JobDto? job = await _handler.FindAsync("job-1", CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal(status, job.Status);
        Assert.Equal(status == nameof(JobStatus.Registered) ? null : Started, job.StartedAt);

        bool terminal = status
            is nameof(JobStatus.Completed)
            or nameof(JobStatus.Failed)
            or nameof(JobStatus.Cancelled);
        Assert.Equal(terminal ? Finished : (DateTimeOffset?)null, job.FinishedAt);
        Assert.Equal(status == nameof(JobStatus.Failed) ? FailureText : null, job.FailureMessage);
    }

    [Fact]
    public async Task 一覧でも各状態の時刻と理由が落ちない()
    {
        // 詳細と一覧で写し方が食い違わないことを見る。
        await AddAsync(Failed("job-1", Created));
        await AddAsync(Completed("job-2", Created.AddMinutes(1)));

        IReadOnlyList<JobListItemDto> jobs = await _handler.ListAsync(CancellationToken.None);

        JobListItemDto failed = jobs.Single(job => job.Id == "job-1");
        Assert.Equal(nameof(JobStatus.Failed), failed.Status);
        Assert.Equal(FailureText, failed.FailureMessage);
        Assert.Equal(Finished, failed.FinishedAt);

        JobListItemDto completed = jobs.Single(job => job.Id == "job-2");
        Assert.Equal(nameof(JobStatus.Completed), completed.Status);
        Assert.Null(completed.FailureMessage);
    }

    /// <summary>
    /// 一覧が進捗を運ぶこと。画面が行ごとにサブタスクを取りに行かずに済む唯一の根拠。
    /// </summary>
    [Fact]
    public async Task 一覧は各Jobのサブタスクの進捗を添えて返る()
    {
        await AddAsync(Registered("job-1", Created));
        await AddAsync(Registered("job-2", Created.AddMinutes(1)));

        // job-1 は 3 行で 1 つ完了。job-2 は行を作らない（登録しただけの Job）。
        SubTask first = SubTask.Create(JobId.From("job-1"), 0);
        await _subTasks.AddRangeAsync(
            [first, SubTask.Create(JobId.From("job-1"), 1), SubTask.Create(JobId.From("job-1"), 2)],
            CancellationToken.None);
        await ApplyAsync(first, SubTaskTrigger.Start);
        await ApplyAsync(first, SubTaskTrigger.Complete);

        IReadOnlyList<JobListItemDto> jobs = await _handler.ListAsync(CancellationToken.None);

        JobListItemDto withRows = jobs.Single(job => job.Id == "job-1");
        Assert.Equal(1, withRows.CompletedSubTasks);
        Assert.Equal(3, withRows.TotalSubTasks);

        // 行がまだ無い Job は 0/0。集計に現れないので、既定値が当たることを固定する。
        JobListItemDto withoutRows = jobs.Single(job => job.Id == "job-2");
        Assert.Equal(0, withoutRows.CompletedSubTasks);
        Assert.Equal(0, withoutRows.TotalSubTasks);
    }

    [Fact]
    public async Task 名前と種類とパラメータもそのまま返る()
    {
        Job job = Job.Create(JobId.From("job-1"), "毎晩の集計", "Demo", "これは JSON ではない", Created);
        await AddAsync(job);

        JobDto? dto = await _handler.FindAsync("job-1", CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal("毎晩の集計", dto.Name);
        Assert.Equal("Demo", dto.JobType);
        Assert.Equal("これは JSON ではない", dto.Parameters);
    }

    private Task AddAsync(Job job) => _store.AddAsync(job, CancellationToken.None);

    private async Task ApplyAsync(SubTask subTask, SubTaskTrigger trigger)
    {
        SubTaskTransition transition = subTask.Apply(trigger);
        Assert.True(await _subTasks.UpdateAsync(subTask, transition.Previous, CancellationToken.None));
    }

    private static Job Registered(string id, DateTimeOffset createdAt) =>
        Job.Create(JobId.From(id), $"集計 {id}", "Demo", "{}", createdAt);

    private static Job InProgress(string id, DateTimeOffset createdAt)
    {
        Job job = Registered(id, createdAt);
        job.Apply(JobTrigger.Start, Started);
        return job;
    }

    private static Job Completed(string id, DateTimeOffset createdAt)
    {
        Job job = InProgress(id, createdAt);
        job.Apply(JobTrigger.Complete, Finished);
        return job;
    }

    private static Job Failed(string id, DateTimeOffset createdAt)
    {
        Job job = InProgress(id, createdAt);
        job.Apply(JobTrigger.Fail, Finished, FailureText);
        return job;
    }

    private static Job InState(string id, string status)
    {
        Job job = Registered(id, Created);

        switch (status)
        {
            case nameof(JobStatus.Registered):
                break;
            case nameof(JobStatus.InProgress):
                job.Apply(JobTrigger.Start, Started);
                break;
            case nameof(JobStatus.Cancelling):
                job.Apply(JobTrigger.Start, Started);
                job.Apply(JobTrigger.RequestCancel, Finished);
                break;
            case nameof(JobStatus.Completed):
                job.Apply(JobTrigger.Start, Started);
                job.Apply(JobTrigger.Complete, Finished);
                break;
            case nameof(JobStatus.Failed):
                job.Apply(JobTrigger.Start, Started);
                job.Apply(JobTrigger.Fail, Finished, FailureText);
                break;
            case nameof(JobStatus.Cancelled):
                job.Apply(JobTrigger.Start, Started);
                job.Apply(JobTrigger.RequestCancel, Finished);
                job.Apply(JobTrigger.ConfirmCancelled, Finished);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "未知の状態です。");
        }

        return job;
    }
}
