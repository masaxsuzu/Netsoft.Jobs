namespace Netsoft.Jobs.Domain.Tests;

public sealed class JobTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(9));
    private static readonly DateTimeOffset StartedAt = CreatedAt.AddMinutes(1);
    private static readonly DateTimeOffset FinishedAt = CreatedAt.AddMinutes(2);

    public static TheoryData<JobStatus, JobTrigger, JobStatus> 遷移表 => JobStateMachineTests.遷移表;

    [Fact]
    public void 作成直後はQueuedで開始時刻も終了時刻も無い()
    {
        Job job = CreateJob();

        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(CreatedAt, job.CreatedAt);
        Assert.Null(job.StartedAt);
        Assert.Null(job.FinishedAt);
        Assert.Null(job.FailureMessage);
    }

    [Theory]
    [MemberData(nameof(遷移表))]
    public void 遷移表どおりに状態が変わる(JobStatus current, JobTrigger trigger, JobStatus expected)
    {
        Job job = JobAt(current);

        JobTransitionResult result = job.Apply(trigger, FinishedAt, "失敗理由");

        Assert.True(result.IsAllowed);
        Assert.Equal(expected, job.Status);
    }

    [Fact]
    public void Startで開始時刻が入る()
    {
        Job job = CreateJob();

        job.Apply(JobTrigger.Start, StartedAt);

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(StartedAt, job.StartedAt);
        Assert.Null(job.FinishedAt);
    }

    [Theory]
    [InlineData(JobStatus.Running, JobTrigger.Complete)]
    [InlineData(JobStatus.Cancelling, JobTrigger.ConfirmCancelled)]
    [InlineData(JobStatus.Running, JobTrigger.RecoverAfterCrash)]
    public void 終端に達すると終了時刻が入る(JobStatus current, JobTrigger trigger)
    {
        Job job = JobAt(current);

        job.Apply(trigger, FinishedAt, "失敗理由");

        Assert.True(job.Status.IsTerminal());
        Assert.Equal(FinishedAt, job.FinishedAt);
        Assert.Equal(StartedAt, job.StartedAt);
    }

    [Fact]
    public void 待機中のキャンセルは開始時刻を持たないまま終了時刻だけが入る()
    {
        Job job = CreateJob();

        JobTransitionResult result = job.Apply(JobTrigger.RequestCancel, FinishedAt);

        Assert.True(result.IsAllowed);
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Null(job.StartedAt);
        Assert.Equal(FinishedAt, job.FinishedAt);
    }

    [Fact]
    public void キャンセル要求後に完走したらCompletedとして記録される()
    {
        Job job = JobAt(JobStatus.Cancelling);

        JobTransitionResult result = job.Apply(JobTrigger.Complete, FinishedAt);

        Assert.True(result.IsAllowed);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Null(job.FailureMessage);
    }

    [Fact]
    public void 拒否されたときは状態も時刻も一切変わらない()
    {
        Job job = JobAt(JobStatus.Running);

        JobTransitionResult result = job.Apply(JobTrigger.ConfirmCancelled, FinishedAt, "失敗理由");

        Assert.False(result.IsAllowed);
        Assert.Equal(JobTransitionRejection.InvalidForCurrentStatus, result.Rejection);
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(StartedAt, job.StartedAt);
        Assert.Null(job.FinishedAt);
        Assert.Null(job.FailureMessage);
    }

    [Fact]
    public void 終端に達した後の操作はJobAlreadyFinishedとして拒否される()
    {
        Job job = JobAt(JobStatus.Completed);

        JobTransitionResult result = job.Apply(JobTrigger.RequestCancel, FinishedAt.AddMinutes(1));

        Assert.False(result.IsAllowed);
        Assert.Equal(JobTransitionRejection.JobAlreadyFinished, result.Rejection);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(FinishedAt, job.FinishedAt);
    }

    [Fact]
    public void キャンセル要求済みへの再要求はAlreadyInEffectとして拒否される()
    {
        Job job = JobAt(JobStatus.Cancelling);

        JobTransitionResult result = job.Apply(JobTrigger.RequestCancel, FinishedAt);

        Assert.False(result.IsAllowed);
        Assert.Equal(JobTransitionRejection.AlreadyInEffect, result.Rejection);
        Assert.Equal(JobStatus.Cancelling, job.Status);
    }

    [Fact]
    public void 理由なしにFailedへ遷移させようとすると例外になる()
    {
        Job job = JobAt(JobStatus.Running);

        Assert.Throws<ArgumentException>(() => job.Apply(JobTrigger.Fail, FinishedAt));
        Assert.Throws<ArgumentException>(() => job.Apply(JobTrigger.Fail, FinishedAt, "   "));
        Assert.Equal(JobStatus.Running, job.Status);
    }

    [Fact]
    public void Failedには理由が残る()
    {
        Job job = JobAt(JobStatus.Running);

        job.Apply(JobTrigger.Fail, FinishedAt, "ハンドラが例外で終了しました");

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("ハンドラが例外で終了しました", job.FailureMessage);
    }

    [Fact]
    public void 拒否される契機なら理由が無くても例外にならない()
    {
        // 例外は「Failed へ遷移するのに理由が無い」ときだけ。拒否は想定内の分岐なので結果で返す。
        Job job = CreateJob();

        JobTransitionResult result = job.Apply(JobTrigger.Fail, FinishedAt);

        Assert.False(result.IsAllowed);
        Assert.Equal(JobStatus.Queued, job.Status);
    }

    [Fact]
    public void 空の識別子や空の名前では作成できない()
    {
        Assert.Throws<ArgumentException>(() =>
            Job.Create(default, "名前", "Demo", "{}", CreatedAt));

        Assert.Throws<ArgumentException>(() =>
            Job.Create(JobId.From("job-1"), " ", "Demo", "{}", CreatedAt));

        Assert.Throws<ArgumentException>(() =>
            Job.Create(JobId.From("job-1"), "名前", " ", "{}", CreatedAt));
    }

    [Fact]
    public void パラメータは空文字でもよいが_中身は解釈されない()
    {
        Job job = Job.Create(JobId.From("job-1"), "名前", "Demo", string.Empty, CreatedAt);

        Assert.Equal(string.Empty, job.Parameters);

        Job another = Job.Create(JobId.From("job-2"), "名前", "Demo", "これは JSON ではない", CreatedAt);

        Assert.Equal("これは JSON ではない", another.Parameters);
    }

    [Fact]
    public void 復元は状態機械を通さずに保存された状態をそのまま再現する()
    {
        Job job = Job.Rehydrate(
            JobId.From("job-1"),
            "名前",
            "Demo",
            "{}",
            JobStatus.Failed,
            CreatedAt,
            StartedAt,
            FinishedAt,
            "落ちた");

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal(StartedAt, job.StartedAt);
        Assert.Equal(FinishedAt, job.FinishedAt);
        Assert.Equal("落ちた", job.FailureMessage);
    }

    private static Job CreateJob() =>
        Job.Create(JobId.From("job-1"), "毎晩の集計", "Demo", "{}", CreatedAt);

    /// <summary>
    /// 指定した状態の Job を用意する。復元経路を使うのは、テストのために
    /// 状態機械へ回り道の遷移を足したくないため。
    /// </summary>
    private static Job JobAt(JobStatus status) => Job.Rehydrate(
        JobId.From("job-1"),
        "毎晩の集計",
        "Demo",
        "{}",
        status,
        CreatedAt,
        status == JobStatus.Queued ? null : StartedAt,
        status.IsTerminal() ? FinishedAt : null,
        null);
}
