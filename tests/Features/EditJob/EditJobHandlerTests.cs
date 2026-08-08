using Microsoft.Extensions.Logging.Abstractions;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.EditJob;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.EditJob;

/// <summary>
/// 編集要求 1 回の結末を、シナリオ単位で固定する。
/// </summary>
/// <remarks>
/// 編集できる状態の定義は Domain（CanEditParameters）が持ち、行の突き合わせは
/// 実行側の境界が持つ。ここで見るのは要求の写り方 ── 受理 / 400（入力）/
/// 409（状態）/ 404 ── と、「書くのは Job 行の parameters だけ」という分担。
/// </remarks>
public sealed class EditJobHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly TemporaryJobStore _jobs = new();
    private readonly TemporarySubTaskStore _subTasks = new();
    private readonly EditJobHandler _handler;

    public EditJobHandlerTests() =>
        _handler = new EditJobHandler(_jobs, _subTasks, TimeProvider.System, NullLogger<EditJobHandler>.Instance);

    public void Dispose()
    {
        _subTasks.Dispose();
        _jobs.Dispose();
    }

    [Theory]
    [InlineData(nameof(JobStatus.Registered))]
    [InlineData(nameof(JobStatus.InProgress))]
    [InlineData(nameof(JobStatus.Pausing))]
    [InlineData(nameof(JobStatus.Paused))]
    public async Task 編集できる状態ならparametersだけが書き換わる(string status)
    {
        await AddJobAsync(status, "2 1");

        EditJobResult result = (await _handler.HandleAsync("job-1", "5 3", CancellationToken.None)).Value;

        Assert.True(result.IsSuccess);
        Assert.Equal("5 3", result.Job?.Parameters);

        // 状態は動かない。編集は遷移ではない。
        Job saved = await SavedAsync();
        Assert.Equal(status, saved.Status.ToString());
        Assert.Equal("5 3", saved.Parameters);

        // 行はここでは触らない（突き合わせは実行側の境界の仕事）。
        Assert.Empty(await _subTasks.ListByJobAsync(JobId.From("job-1"), CancellationToken.None));
    }

    [Theory]
    [InlineData(nameof(JobStatus.Cancelling), JobTransitionRejection.InvalidForCurrentStatus)]
    [InlineData(nameof(JobStatus.Completed), JobTransitionRejection.JobAlreadyFinished)]
    [InlineData(nameof(JobStatus.Cancelled), JobTransitionRejection.JobAlreadyFinished)]
    public async Task 編集できない状態は理由付きで拒否され何も変わらない(string status, JobTransitionRejection expected)
    {
        await AddJobAsync(status, "2 1");

        EditJobResult result = (await _handler.HandleAsync("job-1", "5 3", CancellationToken.None)).Value;

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Rejection);
        Assert.Equal("2 1", (await SavedAsync()).Parameters);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("a 3")]
    [InlineData("0 3")]
    public async Task 読めない指定は項目付きの入力エラーになり保存されない(string parameters)
    {
        await AddJobAsync(nameof(JobStatus.InProgress), "2 1");

        EditJobResult result = (await _handler.HandleAsync("job-1", parameters, CancellationToken.None)).Value;

        Assert.False(result.IsSuccess);
        Assert.Equal("parameters", Assert.Single(result.Errors).Field);
        Assert.Equal("2 1", (await SavedAsync()).Parameters);
    }

    [Fact]
    public async Task 着手済みより小さいNは入力エラーになる()
    {
        await AddJobAsync(nameof(JobStatus.InProgress), "3 1");

        // 2 個が着手済み（完了 1 + 実行中 1）、1 個が未着手。
        SubTask first = SubTask.Create(JobId.From("job-1"), 0);
        SubTask second = SubTask.Create(JobId.From("job-1"), 1);
        SubTask third = SubTask.Create(JobId.From("job-1"), 2);
        await _subTasks.AddRangeAsync([first, second, third], CancellationToken.None);
        await ApplyAsync(first, SubTaskTrigger.Start);
        await ApplyAsync(first, SubTaskTrigger.Complete);
        await ApplyAsync(second, SubTaskTrigger.Start);

        EditJobResult result = (await _handler.HandleAsync("job-1", "1 1", CancellationToken.None)).Value;

        Assert.False(result.IsSuccess);
        Assert.Contains("2", Assert.Single(result.Errors).Message);
        Assert.Equal("3 1", (await SavedAsync()).Parameters);

        // 着手済みちょうど（N = 2）なら通る。未着手を全部畳む編集。
        Assert.True(((await _handler.HandleAsync("job-1", "2 1", CancellationToken.None)).Value).IsSuccess);
    }

    [Theory]
    [InlineData("job-9")]
    [InlineData("")]
    [InlineData(null)]
    public async Task 対象が無ければ対象なしとして返る(string? id)
    {
        await AddJobAsync(nameof(JobStatus.InProgress), "2 1");

        EditJobResult result = (await _handler.HandleAsync(id!, "5 3", CancellationToken.None)).Value;

        Assert.False(result.IsSuccess);
        Assert.Null(result.Job);
        Assert.Empty(result.Errors);
    }

    /// <summary>
    /// 読み出しと保存の間に他所が書いていたら、書き戻さずに読み直してやり直す。
    /// </summary>
    /// <remarks>
    /// この再試行を外しても結末は「受理」で返る（API は 200、画面は保存されたと表示する）。
    /// 実際には保存されていない。**変異検査でこの穴が素通りしていた**ので固定する。
    /// </remarks>
    [Fact]
    public async Task 読み出しと保存の間に書かれたら読み直してやり直す()
    {
        await AddJobAsync(nameof(JobStatus.InProgress), "2 1");

        InterferingJobStore interfering = new(_jobs);
        EditJobHandler handler = new(interfering, _subTasks, TimeProvider.System, NullLogger<EditJobHandler>.Instance);

        // 書き戻す直前に、状態を動かさない書き込みを差し込む（版だけが進む）。
        interfering.BeforeNextUpdate = async () =>
        {
            Job other = await SavedAsync();
            other.ChangeParameters("7 7");
            Assert.True(await _jobs.UpdateAsync(other, CancellationToken.None));
        };

        EditJobResult result = (await handler.HandleAsync("job-1", "5 3", CancellationToken.None)).Value;

        Assert.True(result.IsSuccess);

        // 受理を返しただけでなく、実際に保存まで届いていること。
        Assert.Equal("5 3", (await SavedAsync()).Parameters);
        Assert.Equal(2, interfering.UpdateAttempts);
    }

    private async Task AddJobAsync(string status, string parameters)
    {
        JobStatus target = Enum.Parse<JobStatus>(status);
        Job job = Job.Rehydrate(
            JobId.From("job-1"),
            "編集対象",
            "subtasks",
            parameters,
            target,
            Created,
            target == JobStatus.Registered ? null : Created.AddMinutes(1),
            target.IsTerminal() ? Created.AddMinutes(2) : null,
            null);

        await _jobs.AddAsync(job, CancellationToken.None);
    }

    private async Task ApplyAsync(SubTask subTask, SubTaskTrigger trigger)
    {
        SubTaskTransition transition = subTask.Apply(trigger);
        Assert.True(await _subTasks.UpdateAsync(subTask, transition.Previous, CancellationToken.None));
    }

    private async Task<Job> SavedAsync() =>
        await _jobs.FindAsync(JobId.From("job-1"), CancellationToken.None)
        ?? throw new InvalidOperationException("Job が保存されていません。");
}
