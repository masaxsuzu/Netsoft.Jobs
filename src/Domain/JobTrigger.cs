namespace Netsoft.Jobs.Domain;

/// <summary>
/// 状態遷移の契機。「誰が何をしたか」ではなく「何が起きたか」を表す。
/// </summary>
/// <remarks>
/// <see cref="JobStatus"/> と同じく、数値に意味を持たせない。
/// </remarks>
public enum JobTrigger
{
    /// <summary>実行スロットが割り当てられ、ハンドラを起動する。</summary>
    Start,

    /// <summary>ハンドラが正常終了した。</summary>
    Complete,

    /// <summary>ハンドラが例外で終了した。</summary>
    Fail,

    /// <summary>利用者がキャンセルを要求した。</summary>
    RequestCancel,

    /// <summary>ハンドラがキャンセルを受理して終了した。</summary>
    ConfirmCancelled,

    /// <summary>起動時の復旧走査。前回プロセスが異常終了していた。</summary>
    RecoverAfterCrash,
}
