namespace Netsoft.Jobs.Domain.Tests;

public sealed class JobStateMachineTests
{
    public static TheoryData<JobStatus, JobTrigger, JobStatus> 遷移表 { get; } = BuildTable();

    public static TheoryData<JobStatus, JobTrigger> 全組み合わせ { get; } = BuildAllCombinations();

    [Theory]
    [MemberData(nameof(遷移表))]
    public void 遷移表にある組み合わせは許可され_表どおりの状態になる(JobStatus current, JobTrigger trigger, JobStatus expected)
    {
        JobTransitionResult result = JobStateMachine.Evaluate(current, trigger);

        Assert.True(result.IsAllowed);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Rejection);
    }

    [Theory]
    [MemberData(nameof(全組み合わせ))]
    public void 遷移表に無い組み合わせはすべて拒否される(JobStatus current, JobTrigger trigger)
    {
        bool 表にある = JobTransitionTable.Allowed.ContainsKey((current, trigger));

        JobTransitionResult result = JobStateMachine.Evaluate(current, trigger);

        Assert.Equal(表にある, result.IsAllowed);
    }

    [Theory]
    [MemberData(nameof(全組み合わせ))]
    public void 拒否されたときは現在の状態がそのまま返り_理由が付く(JobStatus current, JobTrigger trigger)
    {
        JobTransitionResult result = JobStateMachine.Evaluate(current, trigger);

        if (result.IsAllowed)
        {
            return;
        }

        Assert.Equal(current, result.Status);
        Assert.NotNull(result.Rejection);
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void 終端状態はいかなる契機でも遷移しない(JobStatus terminal)
    {
        Assert.True(terminal.IsTerminal());

        foreach (JobTrigger trigger in JobTransitionTable.AllTriggers)
        {
            JobTransitionResult result = JobStateMachine.Evaluate(terminal, trigger);

            Assert.False(result.IsAllowed);
            Assert.Equal(JobTransitionRejection.JobAlreadyFinished, result.Rejection);
            Assert.Equal(terminal, result.Status);
        }
    }

    [Fact]
    public void キャンセル要求後にハンドラが完走したらCompletedになる()
    {
        // 実際に起きたのは「完走」なので、要求されたキャンセルではなく完走を記録する。
        JobTransitionResult result = JobStateMachine.Evaluate(JobStatus.Cancelling, JobTrigger.Complete);

        Assert.True(result.IsAllowed);
        Assert.Equal(JobStatus.Completed, result.Status);
    }

    [Fact]
    public void 待機中へのキャンセル要求は即座にCancelledになる()
    {
        // ハンドラが起動していないので、受理を待つ相手がいない。
        JobTransitionResult result = JobStateMachine.Evaluate(JobStatus.Queued, JobTrigger.RequestCancel);

        Assert.True(result.IsAllowed);
        Assert.Equal(JobStatus.Cancelled, result.Status);
    }

    [Fact]
    public void 待機中は起動時復旧の対象外()
    {
        JobTransitionResult result = JobStateMachine.Evaluate(JobStatus.Queued, JobTrigger.RecoverAfterCrash);

        Assert.False(result.IsAllowed);
        Assert.Equal(JobStatus.Queued, result.Status);
        Assert.False(JobStatus.Queued.IsHandlerActive());
    }

    [Fact]
    public void 既に効いている要求はAlreadyInEffectとして区別される()
    {
        Assert.Equal(
            JobTransitionRejection.AlreadyInEffect,
            JobStateMachine.Evaluate(JobStatus.Cancelling, JobTrigger.RequestCancel).Rejection);

        Assert.Equal(
            JobTransitionRejection.AlreadyInEffect,
            JobStateMachine.Evaluate(JobStatus.Running, JobTrigger.Start).Rejection);
    }

    [Fact]
    public void それ以外の不正はInvalidForCurrentStatusとして区別される()
    {
        Assert.Equal(
            JobTransitionRejection.InvalidForCurrentStatus,
            JobStateMachine.Evaluate(JobStatus.Queued, JobTrigger.Complete).Rejection);

        Assert.Equal(
            JobTransitionRejection.InvalidForCurrentStatus,
            JobStateMachine.Evaluate(JobStatus.Running, JobTrigger.ConfirmCancelled).Rejection);
    }

    [Fact]
    public void ハンドラ稼働中と判定されるのはRunningとCancellingだけ()
    {
        // 起動時復旧の対象を決める判定なので、対象がずれないことを明示的に固定する。
        foreach (JobStatus status in JobTransitionTable.AllStatuses)
        {
            bool expected = status is JobStatus.Running or JobStatus.Cancelling;

            Assert.Equal(expected, status.IsHandlerActive());
        }
    }

    private static TheoryData<JobStatus, JobTrigger, JobStatus> BuildTable()
    {
        TheoryData<JobStatus, JobTrigger, JobStatus> data = [];

        foreach (KeyValuePair<(JobStatus Current, JobTrigger Trigger), JobStatus> entry in JobTransitionTable.Allowed)
        {
            data.Add(entry.Key.Current, entry.Key.Trigger, entry.Value);
        }

        return data;
    }

    private static TheoryData<JobStatus, JobTrigger> BuildAllCombinations()
    {
        TheoryData<JobStatus, JobTrigger> data = [];

        foreach (JobStatus status in JobTransitionTable.AllStatuses)
        {
            foreach (JobTrigger trigger in JobTransitionTable.AllTriggers)
            {
                data.Add(status, trigger);
            }
        }

        return data;
    }
}
