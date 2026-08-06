namespace Netsoft.Jobs.Domain.Tests;

/// <summary>
/// 遷移表が持つ「形」を固定する。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JobStateMachineTests"/> は行を 1 本ずつ突き合わせるので、行き先を間違えた行は
/// 捕まえられるが、<b>規則を破る行が増えたこと</b>は捕まえられない ── 表とテストの両方に
/// 同じ行を足せば通ってしまう。ここは行を数えず、表全体を述語で走査する。
/// </para>
/// <para>
/// 規則は 4 つ。要求（API）は必ず確定待ち（ing）へ行く。実行の開始は待ち行列からだけで、
/// API からは引けない。ing は対応する ed へ落ちる。終端へ入れるのは実行の結末と復旧だけ。
/// 例外は <see cref="ResumeFromPausing"/> の 1 行しか無く、名指しで許してある。
/// </para>
/// </remarks>
public sealed class JobTransitionShapeTests
{
    /// <summary>利用者の要求として API から引かれる契機。</summary>
    private static readonly JobTrigger[] RequestTriggers =
        [JobTrigger.RequestCancel, JobTrigger.RequestPause, JobTrigger.Resume];

    /// <summary>
    /// 唯一の例外。受理前の一時停止を取り消すと、走り続けているハンドラのところへ戻る。
    /// </summary>
    /// <remarks>
    /// 規則どおり Resuming へ流すと確定で待ち行列へ戻り、ハンドラが走ったままエンジンが
    /// 二重に claim する。<b>ここを増やすときは、増やす理由をこの注記に足すこと。</b>
    /// </remarks>
    private static readonly (JobStatus Current, JobTrigger Trigger) ResumeFromPausing =
        (JobStatus.Pausing, JobTrigger.Resume);

    [Fact]
    public void 要求は必ず確定待ちへ行く()
    {
        foreach (((JobStatus current, JobTrigger trigger), JobStatus next) in JobTransitionTable.Allowed)
        {
            if (!RequestTriggers.Contains(trigger) || (current, trigger) == ResumeFromPausing)
            {
                continue;
            }

            Assert.True(
                next.IsSettling(),
                $"({current}, {trigger}) の行き先が {next} です。要求は静止状態へ直行してはいけません。");
        }
    }

    [Fact]
    public void 確定待ちへ入れるのは要求だけ()
    {
        foreach (((JobStatus current, JobTrigger trigger), JobStatus next) in JobTransitionTable.Allowed)
        {
            if (!next.IsSettling() || next == current)
            {
                continue;
            }

            Assert.True(
                RequestTriggers.Contains(trigger),
                $"({current}, {trigger}) が {next} へ入っています。確定待ちは要求でしか作れません。");
        }
    }

    [Fact]
    public void 実行の開始は待ち行列からだけで契機はStartだけ()
    {
        foreach (((JobStatus current, JobTrigger trigger), JobStatus next) in JobTransitionTable.Allowed)
        {
            if (next != JobStatus.InProgress || (current, trigger) == ResumeFromPausing)
            {
                continue;
            }

            Assert.Equal(JobTrigger.Start, trigger);
            Assert.True(current.IsWaiting(), $"{current} から実行が始まっています。待ち行列からだけのはずです。");
        }

        // 逆向き。待ち行列から Start を引けば必ず実行中になる。
        foreach (JobStatus waiting in JobTransitionTable.AllStatuses.Where(status => status.IsWaiting()))
        {
            Assert.Equal(JobStatus.InProgress, JobStateMachine.Evaluate(waiting, JobTrigger.Start).Status);
        }
    }

    /// <summary>
    /// どの確定待ちにも、対応する静止状態へ落とす契機がちょうど 1 つある。
    /// </summary>
    /// <remarks>
    /// 落ちない ing があると、要求した Job がそこで滞留する。エラーはどこにも出ず、
    /// 画面には「要求中」が並び続けるだけなので、外から気づく口が無い。
    /// </remarks>
    [Fact]
    public void 確定待ちは必ず静止状態へ落ちる()
    {
        foreach (JobStatus settling in JobTransitionTable.AllStatuses.Where(status => status.IsSettling()))
        {
            JobTrigger confirm = Assert.NotNull(settling.SettlementTrigger());

            JobStatus settled = JobStateMachine.Evaluate(settling, confirm).Status;
            Assert.False(settled.IsSettling(), $"{settling} の確定先が {settled} です。落ちきっていません。");

            // 起動時復旧も同じ行き先へ落とす。担い手がプロセスと一緒に消えただけなので、
            // 要求とは違う結末を書く理由が無い。
            Assert.Equal(settled, JobStateMachine.Evaluate(settling, JobTrigger.RecoverAfterCrash).Status);
        }
    }

    [Fact]
    public void 終端へ入れるのは実行の結末と復旧だけ()
    {
        foreach (((JobStatus current, JobTrigger trigger), JobStatus next) in JobTransitionTable.Allowed)
        {
            if (!next.IsTerminal())
            {
                continue;
            }

            Assert.True(
                trigger is JobTrigger.Complete or JobTrigger.Fail
                    or JobTrigger.ConfirmCancelled or JobTrigger.RecoverAfterCrash,
                $"({current}, {trigger}) が終端 {next} へ入っています。");
        }
    }

    /// <summary>
    /// Completed と Failed は <see cref="JobStatus.InProgress"/> の落ち先で、そこからしか来ない。
    /// </summary>
    /// <remarks>
    /// この 2 つがあることが、InProgress を他の確定待ちと分けている唯一の理由
    /// （落ち先が 1 つに決まらない）。Cancelling / Pausing からも来るのは、要求が効く前に
    /// ハンドラが終わったケースで、そのときも走っていたのは InProgress の続きである。
    /// </remarks>
    [Fact]
    public void 完了と失敗は実行していたものからしか来ない()
    {
        foreach (((JobStatus current, JobTrigger trigger), JobStatus next) in JobTransitionTable.Allowed)
        {
            if (next is not (JobStatus.Completed or JobStatus.Failed))
            {
                continue;
            }

            Assert.True(
                current.IsHandlerActive(),
                $"({current}, {trigger}) が {next} へ入っています。実行していない Job は完了も失敗もしません。");
        }
    }
}
