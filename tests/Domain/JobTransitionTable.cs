namespace Netsoft.Jobs.Domain.Tests;

/// <summary>
/// 仕様としての遷移表。テストはこの表を正とし、実装がこの表と一致することを確かめる。
/// </summary>
/// <remarks>
/// 実装とは独立に、表そのものを 1 か所に書く。
/// 実装のロジックをテスト側で書き直すと、同じ間違いを二度書くだけになるため。
/// </remarks>
internal static class JobTransitionTable
{
    /// <summary>許可される遷移のすべて。ここに無い組み合わせは拒否されなければならない。</summary>
    public static readonly IReadOnlyDictionary<(JobStatus Current, JobTrigger Trigger), JobStatus> Allowed =
        new Dictionary<(JobStatus, JobTrigger), JobStatus>
        {
            [(JobStatus.Queued, JobTrigger.Start)] = JobStatus.Running,
            [(JobStatus.Queued, JobTrigger.RequestCancel)] = JobStatus.Cancelled,
            [(JobStatus.Running, JobTrigger.Complete)] = JobStatus.Completed,
            [(JobStatus.Running, JobTrigger.Fail)] = JobStatus.Failed,
            [(JobStatus.Running, JobTrigger.RequestCancel)] = JobStatus.Cancelling,
            [(JobStatus.Cancelling, JobTrigger.ConfirmCancelled)] = JobStatus.Cancelled,
            [(JobStatus.Cancelling, JobTrigger.Complete)] = JobStatus.Completed,
            [(JobStatus.Cancelling, JobTrigger.Fail)] = JobStatus.Failed,
            [(JobStatus.Running, JobTrigger.RecoverAfterCrash)] = JobStatus.Failed,
            [(JobStatus.Cancelling, JobTrigger.RecoverAfterCrash)] = JobStatus.Failed,
        };

    public static IEnumerable<JobStatus> AllStatuses => Enum.GetValues<JobStatus>();

    public static IEnumerable<JobTrigger> AllTriggers => Enum.GetValues<JobTrigger>();
}
