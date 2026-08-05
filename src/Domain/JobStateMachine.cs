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
            (JobStatus.Queued, JobTrigger.Start) => JobTransitionResult.Allowed(current, JobStatus.Running),

            // ハンドラをまだ起動していないので、受理を待つ相手がいない。即座に終端へ落とす。
            (JobStatus.Queued, JobTrigger.RequestCancel) => JobTransitionResult.Allowed(current, JobStatus.Cancelled),

            (JobStatus.Running, JobTrigger.Complete) => JobTransitionResult.Allowed(current, JobStatus.Completed),
            (JobStatus.Running, JobTrigger.Fail) => JobTransitionResult.Allowed(current, JobStatus.Failed),
            (JobStatus.Running, JobTrigger.RequestCancel) => JobTransitionResult.Allowed(current, JobStatus.Cancelling),

            (JobStatus.Cancelling, JobTrigger.ConfirmCancelled) => JobTransitionResult.Allowed(current, JobStatus.Cancelled),

            // キャンセルが効く前にハンドラが完走したケース。
            // 記録するのは「要求されたこと」ではなく「実際に起きたこと」なので Completed にする。
            // ここを Cancelled にすると、成果物が出来ているのに中止したという嘘の記録が残る。
            (JobStatus.Cancelling, JobTrigger.Complete) => JobTransitionResult.Allowed(current, JobStatus.Completed),
            (JobStatus.Cancelling, JobTrigger.Fail) => JobTransitionResult.Allowed(current, JobStatus.Failed),

            // 前回プロセスの異常終了。ハンドラが動いていたはずの状態は、
            // 結果が分からない以上 Failed として閉じるしかない。
            // Queued はハンドラを起動していないため対象外で、次のプロセスがそのまま実行する。
            (JobStatus.Running, JobTrigger.RecoverAfterCrash) => JobTransitionResult.Allowed(current, JobStatus.Failed),
            (JobStatus.Cancelling, JobTrigger.RecoverAfterCrash) => JobTransitionResult.Allowed(current, JobStatus.Failed),
            (JobStatus.Pausing, JobTrigger.RecoverAfterCrash) => JobTransitionResult.Allowed(current, JobStatus.Failed),

            // 一時停止。効き目はサブタスクの境界なので、要求と受理の 2 段になる（キャンセルと同型）。
            (JobStatus.Running, JobTrigger.RequestPause) => JobTransitionResult.Allowed(current, JobStatus.Pausing),
            (JobStatus.Pausing, JobTrigger.ConfirmPaused) => JobTransitionResult.Allowed(current, JobStatus.Paused),

            // 受理される前に最後のサブタスクが終わった（落ちた）ケース。キャンセルと同じく
            // 「要求されたこと」ではなく「実際に起きたこと」を記録する。
            (JobStatus.Pausing, JobTrigger.Complete) => JobTransitionResult.Allowed(current, JobStatus.Completed),
            (JobStatus.Pausing, JobTrigger.Fail) => JobTransitionResult.Allowed(current, JobStatus.Failed),

            // 停止要求中でも中止はできる。キャンセルの方が強い意図（捨てる）なので上書きを許す。
            (JobStatus.Pausing, JobTrigger.RequestCancel) => JobTransitionResult.Allowed(current, JobStatus.Cancelling),

            // 受理前の取り消し。ハンドラは走り続けているので、Running に戻すだけで済む。
            // ハンドラは境界で Pausing を見たときだけ抜けるから、この揺り戻しに気づく必要も無い。
            (JobStatus.Pausing, JobTrigger.Resume) => JobTransitionResult.Allowed(current, JobStatus.Running),

            // 再開は Queued へ戻す。専用の再実行経路は作らず、既存のディスパッチに乗せる。
            // 進捗はサブタスクの行が持っているので、ハンドラは続きから走れる。
            (JobStatus.Paused, JobTrigger.Resume) => JobTransitionResult.Allowed(current, JobStatus.Queued),

            // 停止中は誰も走っていない。Queued への要求と同じく、受理を待つ相手がいないので即終端。
            (JobStatus.Paused, JobTrigger.RequestCancel) => JobTransitionResult.Allowed(current, JobStatus.Cancelled),

            _ => JobTransitionResult.Rejected(current, Classify(current, trigger)),
        };
    }

    /// <summary>
    /// 拒否の理由を選ぶ。上位層が「冪等に成功として返す」か「誤りとして返す」かを分けられるようにする。
    /// </summary>
    private static JobTransitionRejection Classify(JobStatus current, JobTrigger trigger) => (current, trigger) switch
    {
        // ハンドラは既に起動済み。起動要求としての意図は満たされている。
        (JobStatus.Running or JobStatus.Cancelling or JobStatus.Pausing, JobTrigger.Start) => JobTransitionRejection.AlreadyInEffect,

        // キャンセルは既に要求済みで、ハンドラの受理を待っている最中。
        (JobStatus.Cancelling, JobTrigger.RequestCancel) => JobTransitionRejection.AlreadyInEffect,

        // 一時停止は既に要求済み（または受理済み）。
        (JobStatus.Pausing or JobStatus.Paused, JobTrigger.RequestPause) => JobTransitionRejection.AlreadyInEffect,

        // 動いている（これから動く）ものへの再開要求。意図は満たされている。
        (JobStatus.Queued or JobStatus.Running, JobTrigger.Resume) => JobTransitionRejection.AlreadyInEffect,

        _ => JobTransitionRejection.InvalidForCurrentStatus,
    };
}
