namespace Netsoft.Jobs.Domain.Tests;

/// <summary>
/// 状態機械が <see cref="JobTransitionTable"/> の仕様と一致することを、
/// 状態 × 契機の全組み合わせで確かめる。
/// </summary>
/// <remarks>
/// 行ごとのテストは立てない。表に無い組み合わせを含む全域を 1 本で走査するので、
/// 行を足したときに検査の側を足し忘れることが起きない。個々の行の意図は
/// <see cref="JobTransitionTable"/> に書いてある。
/// </remarks>
public sealed class JobStateMachineTests
{
    public static TheoryData<JobStatus, JobTrigger, JobStatus> 遷移表 { get; } = BuildTable();

    public static TheoryData<JobStatus, JobTrigger> 全組み合わせ { get; } = BuildAllCombinations();

    [Theory]
    [MemberData(nameof(全組み合わせ))]
    public void 全組み合わせが遷移表と一致し終端はいかなる契機でも動かない(JobStatus current, JobTrigger trigger)
    {
        bool 表にある = JobTransitionTable.Allowed.TryGetValue((current, trigger), out JobStatus expected);

        JobTransitionResult result = JobStateMachine.Evaluate(current, trigger);

        Assert.Equal(表にある, result.IsAllowed);

        if (表にある)
        {
            Assert.Equal(expected, result.Status);
            Assert.Null(result.Rejection);
            return;
        }

        // 拒否は現在の状態を動かさず、必ず理由が付く。呼び出し側はこの理由で応答を分ける。
        Assert.Equal(current, result.Status);
        Assert.NotNull(result.Rejection);

        // 終端は一方通行の行き止まり。表に行が無いことに加えて、理由が
        // 「もう終わっている」で揃うことまで固定する（409 に写す判断がこれに乗る）。
        if (current.IsTerminal())
        {
            Assert.Equal(JobTransitionRejection.JobAlreadyFinished, result.Rejection);
        }
    }

    /// <summary>
    /// 終端でない拒否の理由が 2 つに分かれること。「もう効いている」は利用者にとって
    /// 成功（ボタンを 2 回押しただけ）に写り、「今の状態では無理」は 409 に写る。
    /// </summary>
    [Theory]
    [InlineData(JobStatus.Cancelling, JobTrigger.RequestCancel, JobTransitionRejection.AlreadyInEffect)]
    [InlineData(JobStatus.Running, JobTrigger.Start, JobTransitionRejection.AlreadyInEffect)]
    [InlineData(JobStatus.Queued, JobTrigger.Complete, JobTransitionRejection.InvalidForCurrentStatus)]
    [InlineData(JobStatus.Running, JobTrigger.ConfirmCancelled, JobTransitionRejection.InvalidForCurrentStatus)]
    public void 終端以外の拒否は既に効いているかどうかで理由が分かれる(
        JobStatus current, JobTrigger trigger, JobTransitionRejection expected)
    {
        Assert.Equal(expected, JobStateMachine.Evaluate(current, trigger).Rejection);
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
