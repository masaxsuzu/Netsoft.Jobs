namespace Netsoft.Jobs.Domain;

/// <summary>
/// 遷移判定の結果。許可されたか、遷移後の状態、拒否された理由を持つ。
/// </summary>
/// <remarks>
/// 拒否は例外にしない。「その状態ではその操作ができない」は利用者の操作から
/// 日常的に起きる分岐であって、プログラムの誤りではないため。
/// </remarks>
public readonly record struct JobTransitionResult
{
    private JobTransitionResult(bool isAllowed, JobStatus status, JobTransitionRejection? rejection)
    {
        IsAllowed = isAllowed;
        Status = status;
        Rejection = rejection;
    }

    /// <summary>遷移が許可されたか。</summary>
    public bool IsAllowed { get; }

    /// <summary>
    /// 許可された場合は遷移後の状態。拒否された場合は現在の状態のまま。
    /// </summary>
    public JobStatus Status { get; }

    /// <summary>拒否された場合の理由。許可された場合は null。</summary>
    public JobTransitionRejection? Rejection { get; }

    /// <summary>許可された結果を作る。</summary>
    public static JobTransitionResult Allowed(JobStatus next) => new(true, next, null);

    /// <summary>拒否された結果を作る。状態は変わらないので現在の状態をそのまま返す。</summary>
    public static JobTransitionResult Rejected(JobStatus current, JobTransitionRejection rejection) =>
        new(false, current, rejection);
}
