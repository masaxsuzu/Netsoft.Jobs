namespace Netsoft.Jobs.Domain.Tests;

/// <summary>
/// 仕様としての遷移表。テストはこの表を正とし、実装がこの表と一致することを確かめる。
/// </summary>
/// <remarks>
/// 実装とは独立に、表そのものを 1 か所に書く。
/// 実装のロジックをテスト側で書き直すと、同じ間違いを二度書くだけになるため。
/// 個々の行の意図もここに書く。行ごとにテストを立てると、表と同じことを
/// 二度書いた上で、片方だけ直される余地ができる。
/// </remarks>
internal static class JobTransitionTable
{
    /// <summary>許可される遷移のすべて。ここに無い組み合わせは拒否されなければならない。</summary>
    public static readonly IReadOnlyDictionary<(JobStatus Current, JobTrigger Trigger), JobStatus> Allowed =
        new Dictionary<(JobStatus, JobTrigger), JobStatus>
        {
            [(JobStatus.Queued, JobTrigger.Start)] = JobStatus.Running,

            // 待機中はハンドラを起動していないので、受理を待つ相手がいない。即座に終端へ。
            [(JobStatus.Queued, JobTrigger.RequestCancel)] = JobStatus.Cancelled,

            [(JobStatus.Running, JobTrigger.Complete)] = JobStatus.Completed,
            [(JobStatus.Running, JobTrigger.Fail)] = JobStatus.Failed,
            [(JobStatus.Running, JobTrigger.RequestCancel)] = JobStatus.Cancelling,
            [(JobStatus.Cancelling, JobTrigger.ConfirmCancelled)] = JobStatus.Cancelled,

            // 要求した後にハンドラが完走／失敗することはある。実際に起きたのはそちらなので、
            // 要求されたキャンセルではなく起きた結末を記録する。別プロセスで走っている Job の
            // 決着もこの 2 行が引き受ける（キャンセルはプロセスをまたいで届かないため）。
            [(JobStatus.Cancelling, JobTrigger.Complete)] = JobStatus.Completed,
            [(JobStatus.Cancelling, JobTrigger.Fail)] = JobStatus.Failed,

            // 起動時復旧の対象はハンドラが動いていたはずの状態だけ。Queued に行が無いのは、
            // 副作用が無く、そのまま実行すればよいから。
            [(JobStatus.Running, JobTrigger.RecoverAfterCrash)] = JobStatus.Failed,
            [(JobStatus.Cancelling, JobTrigger.RecoverAfterCrash)] = JobStatus.Failed,
        };

    public static IEnumerable<JobStatus> AllStatuses => Enum.GetValues<JobStatus>();

    public static IEnumerable<JobTrigger> AllTriggers => Enum.GetValues<JobTrigger>();
}
