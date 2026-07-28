namespace Netsoft.Jobs.Domain;

/// <summary>
/// Job の状態遷移の唯一の定義。純関数のみで、状態も時間も持たない。
/// </summary>
/// <remarks>
/// ここに書かれていない組み合わせはすべて拒否する。
/// 遷移の可否を各層で判定し始めると仕様が分裂するので、判断は必ずここに集める。
/// </remarks>
public static class JobStateMachine
{
    /// <summary>
    /// 現在の状態に契機を与えたとき、遷移できるかを判定する。
    /// </summary>
    public static JobTransitionResult Evaluate(JobStatus current, JobTrigger trigger)
    {
        // 終端は最初に落とす。終端に対する操作はすべて「もう終わっている」で説明できるので、
        // 契機ごとの細かい理由分けをしない。
        if (current.IsTerminal())
        {
            return JobTransitionResult.Rejected(current, JobTransitionRejection.JobAlreadyFinished);
        }

        return (current, trigger) switch
        {
            (JobStatus.Queued, JobTrigger.Start) => JobTransitionResult.Allowed(JobStatus.Running),

            // ハンドラをまだ起動していないので、受理を待つ相手がいない。即座に終端へ落とす。
            (JobStatus.Queued, JobTrigger.RequestCancel) => JobTransitionResult.Allowed(JobStatus.Cancelled),

            (JobStatus.Running, JobTrigger.Complete) => JobTransitionResult.Allowed(JobStatus.Completed),
            (JobStatus.Running, JobTrigger.Fail) => JobTransitionResult.Allowed(JobStatus.Failed),
            (JobStatus.Running, JobTrigger.RequestCancel) => JobTransitionResult.Allowed(JobStatus.Cancelling),

            (JobStatus.Cancelling, JobTrigger.ConfirmCancelled) => JobTransitionResult.Allowed(JobStatus.Cancelled),

            // キャンセルが効く前にハンドラが完走したケース。
            // 記録するのは「要求されたこと」ではなく「実際に起きたこと」なので Completed にする。
            // ここを Cancelled にすると、成果物が出来ているのに中止したという嘘の記録が残る。
            (JobStatus.Cancelling, JobTrigger.Complete) => JobTransitionResult.Allowed(JobStatus.Completed),
            (JobStatus.Cancelling, JobTrigger.Fail) => JobTransitionResult.Allowed(JobStatus.Failed),

            // 前回プロセスの異常終了。ハンドラが動いていたはずの状態は、
            // 結果が分からない以上 Failed として閉じるしかない。
            // Queued はハンドラを起動していないため対象外で、次のプロセスがそのまま実行する。
            (JobStatus.Running, JobTrigger.RecoverAfterCrash) => JobTransitionResult.Allowed(JobStatus.Failed),
            (JobStatus.Cancelling, JobTrigger.RecoverAfterCrash) => JobTransitionResult.Allowed(JobStatus.Failed),

            _ => JobTransitionResult.Rejected(current, Classify(current, trigger)),
        };
    }

    /// <summary>
    /// 拒否の理由を選ぶ。上位層が「冪等に成功として返す」か「誤りとして返す」かを分けられるようにする。
    /// </summary>
    private static JobTransitionRejection Classify(JobStatus current, JobTrigger trigger) => (current, trigger) switch
    {
        // ハンドラは既に起動済み。起動要求としての意図は満たされている。
        (JobStatus.Running or JobStatus.Cancelling, JobTrigger.Start) => JobTransitionRejection.AlreadyInEffect,

        // キャンセルは既に要求済みで、ハンドラの受理を待っている最中。
        (JobStatus.Cancelling, JobTrigger.RequestCancel) => JobTransitionRejection.AlreadyInEffect,

        _ => JobTransitionRejection.InvalidForCurrentStatus,
    };
}
