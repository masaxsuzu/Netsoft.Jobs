using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Contracts;

/// <summary>
/// 画面がキャンセルボタンを押せる状態にするかの判定。判断は Domain の状態機械に委ねる。
/// </summary>
/// <remarks>
/// <para>
/// 画面側に「Registered か Running なら押せる」と書かない。状態が増えたときに
/// 状態機械とここの 2 か所を直すことになり、片方を忘れると
/// 「押せるのに拒否されるボタン」ができる。
/// </para>
/// <para>
/// 受け取るのは DTO ではなく状態の文字列。読むのが Status だけなので、そう書けば
/// 一覧の <see cref="JobListItemDto"/> と単体の <see cref="JobDto"/> のどちらからでも
/// 同じ判定を通せる。判定の中身は変わらないのに、型ごとに入口を生やす理由が無い。
/// </para>
/// </remarks>
public static class JobCancelability
{
    /// <summary>
    /// この状態の Job にキャンセルを要求できるか。
    /// </summary>
    public static bool CanRequestCancel(string status) =>
        // 状態は JobStatusText の文字列表現。読み戻せない値は未知の状態で、
        // 何が起きるか分からない操作を許すより、押させない側に倒す。
        JobStatusText.TryFromText(status, out JobStatus parsed)
            && JobStateMachine.Evaluate(parsed, JobTrigger.RequestCancel).IsAllowed;
}
