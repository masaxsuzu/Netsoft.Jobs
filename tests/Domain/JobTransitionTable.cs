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
            // 実行の開始。待ち行列は 2 状態あるが、ディスパッチから見れば同じなので同じ行き先。
            // 引くのはエンジンで、API から届く契機はここに無い（利用者は実行の順番を決めない）。
            [(JobStatus.Registered, JobTrigger.Start)] = JobStatus.InProgress,
            [(JobStatus.Resumed, JobTrigger.Start)] = JobStatus.InProgress,

            // 中止の要求。行き先は状態によらず Cancelling で、静止状態へ直行する行は無い。
            // ハンドラが居ない相手（待ち行列 / Paused / Resuming）でも一度は要求を状態に残す
            // ── 押した事実が見えないと、利用者は同じボタンをもう一度押す。
            [(JobStatus.Registered, JobTrigger.RequestCancel)] = JobStatus.Cancelling,
            [(JobStatus.Resumed, JobTrigger.RequestCancel)] = JobStatus.Cancelling,
            [(JobStatus.Paused, JobTrigger.RequestCancel)] = JobStatus.Cancelling,
            [(JobStatus.InProgress, JobTrigger.RequestCancel)] = JobStatus.Cancelling,
            [(JobStatus.Resuming, JobTrigger.RequestCancel)] = JobStatus.Cancelling,

            // 中止は停止より強い意図（捨てる）ので、停止の要求中でも上書きできる。
            [(JobStatus.Pausing, JobTrigger.RequestCancel)] = JobStatus.Cancelling,

            // 保留の要求。走っているものにしか掛からないので、行はこの 1 本だけ。
            // 待ち行列（Registered / Resumed / Resuming）はまだ始まっていないので止める対象が無く、
            // 待ち行列から降ろす手段はキャンセルに寄せてある。Paused は既に効いている。
            [(JobStatus.InProgress, JobTrigger.RequestPause)] = JobStatus.Pausing,

            // 再開の要求。行き先が Resuming なのは、Paused → Resumed が 1 手で足りるからではなく、
            // 要求を受け付けた事実を他の 2 つと同じ形で残すため。
            [(JobStatus.Paused, JobTrigger.Resume)] = JobStatus.Resuming,

            // 確定。要求を受けた ing を、対応する ed へ落とす。
            [(JobStatus.Cancelling, JobTrigger.ConfirmCancelled)] = JobStatus.Cancelled,
            [(JobStatus.Pausing, JobTrigger.ConfirmPaused)] = JobStatus.Paused,
            [(JobStatus.Resuming, JobTrigger.ConfirmResumed)] = JobStatus.Resumed,

            // 実行の結末。落ち先が 2 つあることが、InProgress だけ他の ing と違う点。
            // Cancelling / Pausing からも受けるのは、要求が効く前にハンドラが終わることがあるため。
            // 実際に起きたのはそちらなので、要求ではなく起きた結末を記録する。
            // 別プロセスで走っている Job の決着もこの 4 行が引き受ける
            // （キャンセルはプロセスをまたいで届かないため）。
            [(JobStatus.InProgress, JobTrigger.Complete)] = JobStatus.Completed,
            [(JobStatus.InProgress, JobTrigger.Fail)] = JobStatus.Failed,
            [(JobStatus.Cancelling, JobTrigger.Complete)] = JobStatus.Completed,
            [(JobStatus.Cancelling, JobTrigger.Fail)] = JobStatus.Failed,
            [(JobStatus.Pausing, JobTrigger.Complete)] = JobStatus.Completed,
            [(JobStatus.Pausing, JobTrigger.Fail)] = JobStatus.Failed,

            // 起動時復旧。ing は確定の担い手がプロセスと一緒に消えただけなので、要求どおりの
            // 行き先で閉じる。InProgress だけ Failed ── 結果が分からない以上、完了とも中断とも
            // 言えない。待ち行列と Paused に行が無いのは、どちらも静止状態で落とす先が無いから。
            [(JobStatus.InProgress, JobTrigger.RecoverAfterCrash)] = JobStatus.Failed,
            [(JobStatus.Cancelling, JobTrigger.RecoverAfterCrash)] = JobStatus.Cancelled,
            [(JobStatus.Pausing, JobTrigger.RecoverAfterCrash)] = JobStatus.Paused,
            [(JobStatus.Resuming, JobTrigger.RecoverAfterCrash)] = JobStatus.Resumed,

            // 唯一 ing → ing のまま残る行。受理前の取り消しで、ハンドラはまだ走っている。
            // 規則どおり Resuming へ流すと待ち行列へ戻り、エンジンが二重に claim する。
            [(JobStatus.Pausing, JobTrigger.Resume)] = JobStatus.InProgress,
        };

    public static IEnumerable<JobStatus> AllStatuses => Enum.GetValues<JobStatus>();

    public static IEnumerable<JobTrigger> AllTriggers => Enum.GetValues<JobTrigger>();
}
