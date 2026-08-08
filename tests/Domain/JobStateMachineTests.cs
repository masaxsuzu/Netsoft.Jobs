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
    [InlineData(JobStatus.InProgress, JobTrigger.Start, JobTransitionRejection.AlreadyInEffect)]
    [InlineData(JobStatus.Pausing, JobTrigger.Start, JobTransitionRejection.AlreadyInEffect)]
    [InlineData(JobStatus.Pausing, JobTrigger.RequestPause, JobTransitionRejection.AlreadyInEffect)]
    [InlineData(JobStatus.Paused, JobTrigger.RequestPause, JobTransitionRejection.AlreadyInEffect)]
    [InlineData(JobStatus.Registered, JobTrigger.Resume, JobTransitionRejection.AlreadyInEffect)]
    [InlineData(JobStatus.InProgress, JobTrigger.Resume, JobTransitionRejection.AlreadyInEffect)]
    [InlineData(JobStatus.Registered, JobTrigger.Complete, JobTransitionRejection.InvalidForCurrentStatus)]
    [InlineData(JobStatus.InProgress, JobTrigger.ConfirmCancelled, JobTransitionRejection.InvalidForCurrentStatus)]
    [InlineData(JobStatus.Cancelling, JobTrigger.Resume, JobTransitionRejection.InvalidForCurrentStatus)]
    public void 終端以外の拒否は既に効いているかどうかで理由が分かれる(
        JobStatus current, JobTrigger trigger, JobTransitionRejection expected)
    {
        Assert.Equal(expected, JobStateMachine.Evaluate(current, trigger).Rejection);
    }

    /// <summary>
    /// <b>一時停止が掛かるのは走っているものだけ。</b>待ち行列に居る Job は
    /// まだ始まっていないので、止める対象が無い。
    /// </summary>
    /// <remarks>
    /// かつては待ち行列（Registered / Resumed / Resuming）からも掛かり、待ち行列から
    /// 降ろす操作を兼ねていた。降ろす手段をキャンセルに寄せたので入口が 1 つになっている。
    /// 総当たりにしてあるのは、状態を足したときに「そこからも止められる」を
    /// 黙って通さないため。
    /// </remarks>
    [Fact]
    public void 一時停止できるのは実行中だけ()
    {
        foreach (JobStatus status in JobTransitionTable.AllStatuses)
        {
            JobTransitionResult result = JobStateMachine.Evaluate(status, JobTrigger.RequestPause);

            Assert.Equal(status == JobStatus.InProgress, result.IsAllowed);
        }
    }

    /// <summary>
    /// 走っている Job を止めて再開すると、待ち行列には <see cref="JobStatus.Resumed"/> として戻る。
    /// </summary>
    /// <remarks>
    /// 入口が InProgress だけになったので、この往復に非対称は無くなった。かつては
    /// Registered からも Pausing へ入れたため、そこから再開すると走っていない Job が
    /// InProgress へ出てしまい、戻り先が Paused 1 つしか無いことと合わせて
    /// 「Registered だけ往復で戻らない」歪みを抱えていた。
    /// </remarks>
    [Fact]
    public void 停止して再開すると待ち行列にはResumedとして戻る()
    {
        JobTransitionResult pausing = JobStateMachine.Evaluate(JobStatus.InProgress, JobTrigger.RequestPause);
        Assert.Equal(JobStatus.Pausing, pausing.Status);

        JobTransitionResult paused = JobStateMachine.Evaluate(pausing.Status, JobTrigger.ConfirmPaused);
        Assert.Equal(JobStatus.Paused, paused.Status);

        JobTransitionResult resuming = JobStateMachine.Evaluate(paused.Status, JobTrigger.Resume);
        Assert.Equal(JobStatus.Resuming, resuming.Status);

        JobTransitionResult back = JobStateMachine.Evaluate(resuming.Status, JobTrigger.ConfirmResumed);
        Assert.Equal(JobStatus.Resumed, back.Status);
    }

    [Fact]
    public void ハンドラ稼働中と判定されるのはInProgressとCancellingとPausingだけ()
    {
        // 起動時復旧の対象を決める判定なので、対象がずれないことを明示的に固定する。
        foreach (JobStatus status in JobTransitionTable.AllStatuses)
        {
            bool expected = status is JobStatus.InProgress or JobStatus.Cancelling or JobStatus.Pausing;

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
