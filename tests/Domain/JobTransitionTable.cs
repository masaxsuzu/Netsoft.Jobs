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

            // 保留も同じ理由で 2 段を踏まず直接 Paused へ。エンジンは Queued しか claim
            // しないので走り出さなくなる。Paused → Queued があるので往復できる。
            [(JobStatus.Queued, JobTrigger.RequestPause)] = JobStatus.Paused,

            [(JobStatus.Running, JobTrigger.Complete)] = JobStatus.Completed,
            [(JobStatus.Running, JobTrigger.Fail)] = JobStatus.Failed,
            [(JobStatus.Running, JobTrigger.RequestCancel)] = JobStatus.Cancelling,
            [(JobStatus.Cancelling, JobTrigger.ConfirmCancelled)] = JobStatus.Cancelled,

            // 要求した後にハンドラが完走／失敗することはある。実際に起きたのはそちらなので、
            // 要求されたキャンセルではなく起きた結末を記録する。別プロセスで走っている Job の
            // 決着もこの 2 行が引き受ける（キャンセルはプロセスをまたいで届かないため）。
            [(JobStatus.Cancelling, JobTrigger.Complete)] = JobStatus.Completed,
            [(JobStatus.Cancelling, JobTrigger.Fail)] = JobStatus.Failed,

            // 起動時復旧の対象はハンドラが動いていたはずの状態だけ。Queued に行が無いのは
            // 副作用が無くそのまま実行すればよいから。Paused に行が無いのは、受理済みの
            // 停止はハンドラが抜けた後で、プロセスが落ちても失われた実行が無いから。
            [(JobStatus.Running, JobTrigger.RecoverAfterCrash)] = JobStatus.Failed,
            [(JobStatus.Cancelling, JobTrigger.RecoverAfterCrash)] = JobStatus.Failed,
            [(JobStatus.Pausing, JobTrigger.RecoverAfterCrash)] = JobStatus.Failed,

            // 一時停止は要求と受理の 2 段（キャンセルと同型）。効き目はサブタスクの境界。
            [(JobStatus.Running, JobTrigger.RequestPause)] = JobStatus.Pausing,
            [(JobStatus.Pausing, JobTrigger.ConfirmPaused)] = JobStatus.Paused,

            // 受理前に結末が来たら結末が勝つ（Cancelling の 2 行と同じ判断）。
            [(JobStatus.Pausing, JobTrigger.Complete)] = JobStatus.Completed,
            [(JobStatus.Pausing, JobTrigger.Fail)] = JobStatus.Failed,

            // 中止は停止より強い意図（捨てる）。要求中でも受理後でも上書きできる。
            // 受理後は誰も走っていないので、Queued と同じく即終端。
            [(JobStatus.Pausing, JobTrigger.RequestCancel)] = JobStatus.Cancelling,
            [(JobStatus.Paused, JobTrigger.RequestCancel)] = JobStatus.Cancelled,

            // 再開。受理前ならハンドラが走り続けているので Running へ揺り戻すだけ。
            // 受理後は Queued へ戻して既存のディスパッチに乗せる（専用の再実行経路は無い）。
            [(JobStatus.Pausing, JobTrigger.Resume)] = JobStatus.Running,
            [(JobStatus.Paused, JobTrigger.Resume)] = JobStatus.Queued,
        };

    public static IEnumerable<JobStatus> AllStatuses => Enum.GetValues<JobStatus>();

    public static IEnumerable<JobTrigger> AllTriggers => Enum.GetValues<JobTrigger>();
}
