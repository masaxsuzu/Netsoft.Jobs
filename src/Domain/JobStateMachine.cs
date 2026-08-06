namespace Netsoft.Jobs.Domain;

/// <summary>
/// Job の状態遷移の唯一の定義。純関数のみで、状態も時間も持たない。
/// </summary>
/// <remarks>
/// <para>
/// ここに書かれていない組み合わせはすべて拒否する。
/// 遷移の可否を各層で判定し始めると仕様が分裂するので、判断は必ずここに集める。
/// </para>
/// <para>
/// 表は形を持っている。状態を「静止（ed）」「確定待ち（ing）」「実行中（InProgress）」に分け、
/// 要求は必ず ing を経由し、ing は対応する ed へ落ちる。形そのものは
/// tests/Domain の <c>JobTransitionShapeTests</c> が総当たりで固定しているので、
/// ここに規則を破る行を足すとそちらが落ちる。<b>例外を増やすなら向こうにも名指しで書くこと。</b>
/// </para>
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
            // ── 実行の開始。待ち行列の 2 状態からだけで、契機を引くのはエンジン。
            // API から届く契機がここに混ざってはいけない（利用者は実行の順番を決めない）。
            (JobStatus.Registered or JobStatus.Resumed, JobTrigger.Start) =>
                JobTransitionResult.Allowed(current, JobStatus.InProgress),

            // ── 要求（API）。行き先は必ず ing で、ed へ直行させない。
            //
            // handler が居ない相手（Registered / Resumed / Paused / Resuming）への要求も
            // ここを通る。直行にすると「押した」ことが状態に残らず、確定が一瞬で済む相手か
            // どうかで画面の見え方が変わってしまう。確定は要求を受けたコマンドが続けて書く
            // （Features の PauseJobHandler / CancelJobHandler / ResumeJobHandler）。
            (JobStatus.Registered or JobStatus.Resumed or JobStatus.Paused
                or JobStatus.InProgress or JobStatus.Pausing or JobStatus.Resuming,
                JobTrigger.RequestCancel) =>
                JobTransitionResult.Allowed(current, JobStatus.Cancelling),

            (JobStatus.Registered or JobStatus.Resumed or JobStatus.InProgress or JobStatus.Resuming,
                JobTrigger.RequestPause) =>
                JobTransitionResult.Allowed(current, JobStatus.Pausing),

            (JobStatus.Paused, JobTrigger.Resume) => JobTransitionResult.Allowed(current, JobStatus.Resuming),

            // ── 確定。ing から対応する ed へ落とす。
            (JobStatus.Cancelling, JobTrigger.ConfirmCancelled) =>
                JobTransitionResult.Allowed(current, JobStatus.Cancelled),
            (JobStatus.Pausing, JobTrigger.ConfirmPaused) => JobTransitionResult.Allowed(current, JobStatus.Paused),
            (JobStatus.Resuming, JobTrigger.ConfirmResumed) => JobTransitionResult.Allowed(current, JobStatus.Resumed),

            // ── 実行の結末。InProgress の ed が 2 つあることが、この状態だけ特別な理由。
            //
            // Cancelling / Pausing からも受ける。要求が効く前に handler が終わったケースで、
            // 記録するのは「要求されたこと」ではなく「実際に起きたこと」。
            // ここを Cancelled / Paused にすると、成果物が出来ているのに止めたという嘘が残る。
            (JobStatus.InProgress or JobStatus.Cancelling or JobStatus.Pausing, JobTrigger.Complete) =>
                JobTransitionResult.Allowed(current, JobStatus.Completed),
            (JobStatus.InProgress or JobStatus.Cancelling or JobStatus.Pausing, JobTrigger.Fail) =>
                JobTransitionResult.Allowed(current, JobStatus.Failed),

            // ── 前回プロセスの異常終了。
            //
            // ing は対応する ed へ落とす（確定の担い手がプロセスと一緒に消えただけなので、
            // 要求どおりの行き先で閉じる）。InProgress だけ Failed ── 結果が分からない以上、
            // 完了とも中断とも言えない。
            //
            // Pausing を Failed にしていたころの理由は「結果が分からない」だったが、
            // それは要求を無視して勝手に終端へ落とす側の判断だった。利用者は止めろと言っており、
            // Paused なら再開できる（進捗は handler が永続化した記録が持つ。docs/operating.md）。
            // 待ち行列はハンドラを起動していないため対象外で、次のプロセスがそのまま実行する。
            (JobStatus.InProgress, JobTrigger.RecoverAfterCrash) =>
                JobTransitionResult.Allowed(current, JobStatus.Failed),
            (JobStatus.Cancelling, JobTrigger.RecoverAfterCrash) =>
                JobTransitionResult.Allowed(current, JobStatus.Cancelled),
            (JobStatus.Pausing, JobTrigger.RecoverAfterCrash) =>
                JobTransitionResult.Allowed(current, JobStatus.Paused),
            (JobStatus.Resuming, JobTrigger.RecoverAfterCrash) =>
                JobTransitionResult.Allowed(current, JobStatus.Resumed),

            // ── 唯一の例外。受理前の取り消しで、handler はまだ走っている。
            //
            // 規則どおり Resuming へ流すと、確定で待ち行列（Resumed）へ戻り、
            // 走っている handler を残したままエンジンが二重に claim する。
            // ing → ing のまま残すのはそのため。handler は境界で Pausing を見たときだけ
            // 抜けるので、この揺り戻しに気づく必要も無い。
            (JobStatus.Pausing, JobTrigger.Resume) => JobTransitionResult.Allowed(current, JobStatus.InProgress),

            _ => JobTransitionResult.Rejected(current, Classify(current, trigger)),
        };
    }

    /// <summary>
    /// 拒否の理由を選ぶ。上位層が「冪等に成功として返す」か「誤りとして返す」かを分けられるようにする。
    /// </summary>
    private static JobTransitionRejection Classify(JobStatus current, JobTrigger trigger) => (current, trigger) switch
    {
        // ハンドラは既に起動済み。起動要求としての意図は満たされている。
        (JobStatus.InProgress or JobStatus.Cancelling or JobStatus.Pausing, JobTrigger.Start) =>
            JobTransitionRejection.AlreadyInEffect,

        // キャンセルは既に要求済みで、確定を待っている最中。
        (JobStatus.Cancelling, JobTrigger.RequestCancel) => JobTransitionRejection.AlreadyInEffect,

        // 一時停止は既に要求済み（または受理済み）。
        (JobStatus.Pausing or JobStatus.Paused, JobTrigger.RequestPause) => JobTransitionRejection.AlreadyInEffect,

        // 動いている（これから動く）ものへの再開要求。意図は満たされている。
        (JobStatus.Registered or JobStatus.Resumed or JobStatus.InProgress or JobStatus.Resuming, JobTrigger.Resume) =>
            JobTransitionRejection.AlreadyInEffect,

        _ => JobTransitionRejection.InvalidForCurrentStatus,
    };
}
