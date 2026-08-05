namespace Netsoft.Jobs.Domain.Tests;

/// <summary>
/// <see cref="Job"/> が状態機械に判断を委ね、その結果として<b>時刻と理由を記録する</b>こと。
/// </summary>
/// <remarks>
/// 遷移が許されるかどうかの網羅は <see cref="JobStateMachineTests"/> が持つ。
/// ここで見るのは Job だけが持つ関心 ── 開始／終了時刻がいつ入るか、失敗理由の扱い、
/// 生成と復元の契約 ── に絞ってある。
/// </remarks>
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

        // パラメータはハンドラだけが解釈する不透明な文字列。空でも中身が何でも通す。
        Assert.Equal(string.Empty, Job.Create(JobId.From("job-2"), "名前", "Demo", string.Empty, CreatedAt).Parameters);
    }

    [Theory]
    [InlineData(false, "名前", "Demo")]
    [InlineData(true, " ", "Demo")]
    [InlineData(true, "名前", " ")]
    public void 識別子と名前と種類が欠けていると作成できない(bool validId, string name, string jobType)
    {
        Assert.Throws<ArgumentException>(() =>
            Job.Create(validId ? JobId.From("job-1") : default, name, jobType, "{}", CreatedAt));
    }

    [Theory]
    [MemberData(nameof(遷移表))]
    public void 遷移表どおりに状態が変わる(JobStatus current, JobTrigger trigger, JobStatus expected)
    {
        Job job = JobAt(current);

        JobTransitionResult result = job.Apply(trigger, FinishedAt, "失敗理由");

        Assert.True(result.IsAllowed);
        Assert.Equal(expected, job.Status);

        // 遷移前の状態を結果が持つ。呼び出し側が条件付き更新の期待値に使うので、
        // 自分で控えておく必要が無い。
        Assert.Equal(current, result.Previous);
    }

    /// <summary>
    /// 開始時刻は Running に入るときだけ、終了時刻は終端に達したときだけ入る。
    /// 待機中のキャンセルは Running を経ないので、開始時刻を持たないまま終端に達する。
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Queued, JobTrigger.Start, false, false)]
    [InlineData(JobStatus.Queued, JobTrigger.RequestCancel, false, true)]
    [InlineData(JobStatus.Running, JobTrigger.RequestCancel, true, false)]
    [InlineData(JobStatus.Running, JobTrigger.Complete, true, true)]
    [InlineData(JobStatus.Running, JobTrigger.RecoverAfterCrash, true, true)]
    [InlineData(JobStatus.Cancelling, JobTrigger.ConfirmCancelled, true, true)]
    public void 開始時刻はRunningに入るときだけ終了時刻は終端でだけ入る(
        JobStatus current, JobTrigger trigger, bool hasStarted, bool terminal)
    {
        Job job = JobAt(current);

        Assert.True(job.Apply(trigger, current == JobStatus.Queued && trigger == JobTrigger.Start
            ? StartedAt
            : FinishedAt, "失敗理由").IsAllowed);

        Assert.Equal(terminal, job.Status.IsTerminal());
        Assert.Equal(hasStarted || trigger == JobTrigger.Start ? StartedAt : null, job.StartedAt);
        Assert.Equal(terminal ? FinishedAt : null, job.FinishedAt);
    }

    /// <summary>
    /// 拒否されたときは状態も時刻も理由も一切変わらない。理由の分類自体は
    /// 状態機械の領分なので、ここでは素通しされることだけを見る。
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Running, JobTrigger.ConfirmCancelled, JobTransitionRejection.InvalidForCurrentStatus)]
    [InlineData(JobStatus.Cancelling, JobTrigger.RequestCancel, JobTransitionRejection.AlreadyInEffect)]
    [InlineData(JobStatus.Completed, JobTrigger.RequestCancel, JobTransitionRejection.JobAlreadyFinished)]
    public void 拒否されたときは何も変わらず理由がそのまま返る(
        JobStatus current, JobTrigger trigger, JobTransitionRejection expected)
    {
        Job job = JobAt(current);
        DateTimeOffset? startedBefore = job.StartedAt;
        DateTimeOffset? finishedBefore = job.FinishedAt;

        JobTransitionResult result = job.Apply(trigger, FinishedAt.AddMinutes(1), "失敗理由");

        Assert.False(result.IsAllowed);
        Assert.Equal(expected, result.Rejection);
        Assert.Equal(current, job.Status);
        Assert.Equal(startedBefore, job.StartedAt);
        Assert.Equal(finishedBefore, job.FinishedAt);
        Assert.Null(job.FailureMessage);
    }

    [Fact]
    public void Failedへ進むときだけ理由が要る()
    {
        Job job = JobAt(JobStatus.Running);

        // 理由の無い Failed は「なぜ落ちたか分からない Job」を作るので、書く前に弾く。
        Assert.Throws<ArgumentException>(() => job.Apply(JobTrigger.Fail, FinishedAt));
        Assert.Throws<ArgumentException>(() => job.Apply(JobTrigger.Fail, FinishedAt, "   "));
        Assert.Equal(JobStatus.Running, job.Status);

        job.Apply(JobTrigger.Fail, FinishedAt, "ハンドラが例外で終了しました");
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Equal("ハンドラが例外で終了しました", job.FailureMessage);

        // 例外になるのは Failed へ「遷移する」ときだけ。拒否される契機は想定内の分岐で、
        // 理由が無くても結果で返る。
        Job queued = CreateJob();
        Assert.False(queued.Apply(JobTrigger.Fail, FinishedAt).IsAllowed);
        Assert.Equal(JobStatus.Queued, queued.Status);
    }

    /// <summary>
    /// 一時停止と再開の時刻の規則。停止中も「開始した」事実は保ち、
    /// 待ち行列へ戻る瞬間だけ消す（Queued は StartedAt を持たないという不変条件を守る）。
    /// </summary>
    [Fact]
    public void 再開でQueuedへ戻るとStartedAtが消え次のStartで入り直す()
    {
        Job job = JobAt(JobStatus.Running);

        Assert.True(job.Apply(JobTrigger.RequestPause, FinishedAt).IsAllowed);
        Assert.Equal(StartedAt, job.StartedAt);

        Assert.True(job.Apply(JobTrigger.ConfirmPaused, FinishedAt).IsAllowed);
        Assert.Equal(JobStatus.Paused, job.Status);
        Assert.Equal(StartedAt, job.StartedAt);
        Assert.Null(job.FinishedAt);

        Assert.True(job.Apply(JobTrigger.Resume, FinishedAt).IsAllowed);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Null(job.StartedAt);

        DateTimeOffset restartedAt = FinishedAt.AddMinutes(1);
        Assert.True(job.Apply(JobTrigger.Start, restartedAt).IsAllowed);
        Assert.Equal(restartedAt, job.StartedAt);
    }

    /// <summary>
    /// 受理前の揺り戻し（Pausing → Resume → Running）は実行が途切れていないので、
    /// 開始時刻を触らない。Start でだけ書くという規則の縁。
    /// </summary>
    [Fact]
    public void 受理前の再開はRunningへ戻り開始時刻を触らない()
    {
        Job job = JobAt(JobStatus.Pausing);

        Assert.True(job.Apply(JobTrigger.Resume, FinishedAt).IsAllowed);

        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(StartedAt, job.StartedAt);
    }

    /// <summary>
    /// パラメータの編集は状態遷移ではない。編集できる状態の定義（終端と Cancelling は不可）は
    /// CanEditParameters が 1 か所で持ち、エンティティはそれを守る。
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Queued, true)]
    [InlineData(JobStatus.Running, true)]
    [InlineData(JobStatus.Pausing, true)]
    [InlineData(JobStatus.Paused, true)]
    [InlineData(JobStatus.Cancelling, false)]
    [InlineData(JobStatus.Completed, false)]
    [InlineData(JobStatus.Failed, false)]
    [InlineData(JobStatus.Cancelled, false)]
    public void パラメータを編集できるのは終端とCancelling以外(JobStatus status, bool editable)
    {
        Job job = JobAt(status);

        Assert.Equal(editable, status.CanEditParameters());

        if (editable)
        {
            job.ChangeParameters("5 2");
            Assert.Equal("5 2", job.Parameters);
            Assert.Equal(status, job.Status);
        }
        else
        {
            // 呼び出し側の誤り（編集ハンドラは先に確かめて拒否を結果で返す）。
            Assert.Throws<InvalidOperationException>(() => job.ChangeParameters("5 2"));
            Assert.Equal("{}", job.Parameters);
        }
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
