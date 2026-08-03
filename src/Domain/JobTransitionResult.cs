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
    private JobTransitionResult(JobStatus previous, JobStatus status, JobTransitionRejection? rejection)
    {
        Previous = previous;
        Status = status;
        Rejection = rejection;
    }

    /// <summary>
    /// 判定を行った時点の状態。<see cref="IJobStore.UpdateAsync"/> の期待状態に渡す。
    /// </summary>
    /// <remarks>
    /// これを結果に載せるのは、遷移前の状態を知っているのが判定そのものだけだから。
    /// 呼び出し側に控えさせると、<see cref="Job.Apply"/> が Job を破壊的に変える前に
    /// 控えるという順序の約束が生まれ、破っても何も言われない。破ると期待状態が遷移「後」の
    /// 状態になり、条件付き更新が永久に一致せず、読み直しのループが回り続ける。
    /// </remarks>
    public JobStatus Previous { get; }

    /// <summary>
    /// 許可された場合は遷移後の状態。拒否された場合は現在の状態のまま。
    /// </summary>
    public JobStatus Status { get; }

    /// <summary>拒否された場合の理由。許可された場合は null。</summary>
    public JobTransitionRejection? Rejection { get; }

    /// <summary>遷移が許可されたか。</summary>
    /// <remarks>
    /// 理由の有無から導く。別に持つと「許可されたのに理由がある」ような、
    /// 生成側が間違えないと作れないはずの組み合わせを表現できてしまう。
    /// </remarks>
    public bool IsAllowed => Rejection is null;

    /// <summary>許可された結果を作る。</summary>
    public static JobTransitionResult Allowed(JobStatus current, JobStatus next) => new(current, next, null);

    /// <summary>拒否された結果を作る。状態は変わらないので現在の状態をそのまま返す。</summary>
    public static JobTransitionResult Rejected(JobStatus current, JobTransitionRejection rejection) =>
        new(current, current, rejection);
}
