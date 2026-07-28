namespace Netsoft.Jobs.Domain;

/// <summary>
/// 遷移が拒否された理由。上位層が応答を作り分けられるように区別する。
/// </summary>
public enum JobTransitionRejection
{
    /// <summary>
    /// 同じ要求が既に効いている（Cancelling への RequestCancel、Running への Start など）。
    /// 要求の意図は既に満たされているので、API 層は 200 で冪等に返す想定。
    /// </summary>
    AlreadyInEffect,

    /// <summary>終端状態に対する操作。もう何も起こせない。</summary>
    JobAlreadyFinished,

    /// <summary>それ以外の不正。現在の状態ではその契機がありえない。</summary>
    InvalidForCurrentStatus,
}
