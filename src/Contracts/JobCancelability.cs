using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Contracts;

/// <summary>
/// 画面がキャンセルボタンを押せる状態にするかの判定。判断は Domain の状態機械に委ねる。
/// </summary>
/// <remarks>
/// 画面側に「Queued か Running なら押せる」と書かない。状態が増えたときに
/// 状態機械とここの 2 か所を直すことになり、片方を忘れると
/// 「押せるのに拒否されるボタン」ができる。
/// </remarks>
public static class JobCancelability
{
    /// <summary>
    /// この Job にキャンセルを要求できるか。
    /// </summary>
    public static bool CanRequestCancel(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        // DTO の状態は JobStatusText の文字列表現。読み戻せない値は未知の状態で、
        // 何が起きるか分からない操作を許すより、押させない側に倒す。
        return JobStatusText.TryFromText(job.Status, out JobStatus status)
            && JobStateMachine.Evaluate(status, JobTrigger.RequestCancel).IsAllowed;
    }
}
