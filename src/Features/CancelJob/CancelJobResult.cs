using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.CancelJob;

/// <summary>
/// キャンセル要求の結果。受け付けたか、対象が無かったか、状態が合わず拒否されたかを表す。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Result{T}"/> を使わない。あれは「入力のどの項目が不正か」を返す型で、
/// 失敗は <see cref="ValidationError.Field"/> を持つ前提になっている。キャンセルの失敗は
/// 入力の誤りではなく Job の現在の状態の問題なので、書ける項目名が無い。
/// 無理に載せると、呼び出し側が失敗の中身（文言）を見て 404 と 409 を選び分けることになる。
/// </para>
/// <para>
/// <see cref="QueryJobs.QueryJobsHandler"/> のように <c>JobDto?</c> の null だけで表すのも足りない。
/// キャンセルは「無い」以外に「既に効いている」「もう終わっている」を区別する必要があり、
/// それぞれ 200 と 409 に分かれるため。
/// </para>
/// <para>
/// 拒否の理由は <see cref="JobTransitionRejection"/> をそのまま持つ。Domain が既に
/// 上位層が応答を作り分けられる粒度で理由を決めているので、ここで数え直すと
/// 状態機械が理由を増やしたときに二重に直すことになる。
/// この型が足しているのは Domain が知らない「Job が見つからない」だけで、
/// それは <see cref="Job"/> が null かどうかで表す。
/// </para>
/// </remarks>
public sealed class CancelJobResult
{
    private CancelJobResult(JobDto? job, JobTransitionRejection? rejection)
    {
        Job = job;
        Rejection = rejection;
    }

    /// <summary>
    /// 要求の後の Job。見つからなかった場合だけ null。
    /// </summary>
    /// <remarks>
    /// 拒否された場合も現在の Job を返す。呼び出し側が「では今どうなっているのか」を
    /// もう一度読み直さずに済ませるため。
    /// </remarks>
    public JobDto? Job { get; }

    /// <summary>
    /// 状態機械が拒否した理由。遷移できた場合と、対象が無い場合は null。
    /// </summary>
    public JobTransitionRejection? Rejection { get; }

    /// <summary>
    /// 要求として成功したか。
    /// </summary>
    /// <remarks>
    /// <see cref="JobTransitionRejection.AlreadyInEffect"/> を成功に含める。既にキャンセル中なら
    /// 要求の意図は満たされていて、利用者にとっては 2 回押しただけ。冪等に成功として返す。
    /// この判断をここに置くのは、HTTP と画面の両方が同じ答えを出す必要があるため。
    /// </remarks>
    public bool IsSuccess =>
        Job is not null && Rejection is null or JobTransitionRejection.AlreadyInEffect;

    /// <summary>対象が見つからなかった結果を作る。</summary>
    public static CancelJobResult NotFound() => new(null, null);

    /// <summary>キャンセルを受け付けた結果を作る。</summary>
    public static CancelJobResult Accepted(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new CancelJobResult(job, null);
    }

    /// <summary>状態機械に拒否された結果を作る。</summary>
    public static CancelJobResult Rejected(JobDto job, JobTransitionRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new CancelJobResult(job, rejection);
    }
}
