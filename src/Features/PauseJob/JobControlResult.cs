using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.PauseJob;

/// <summary>
/// 一時停止・再開の要求の結果。受け付けたか、対象が無かったか、状態が合わず拒否されたかを表す。
/// </summary>
/// <remarks>
/// 形は <see cref="CancelJob.CancelJobResult"/> と同じ（設計判断もそちらの注記を参照）。
/// 停止と再開は 1 つの機能の両側なので、結果の型は共有する。
/// </remarks>
public sealed class JobControlResult
{
    private JobControlResult(JobDto? job, JobTransitionRejection? rejection)
    {
        Job = job;
        Rejection = rejection;
    }

    /// <summary>
    /// 要求の後の Job。見つからなかった場合だけ null。
    /// 拒否された場合も現在の Job を返す（呼び出し側が読み直さずに済ませるため）。
    /// </summary>
    public JobDto? Job { get; }

    /// <summary>状態機械が拒否した理由。遷移できた場合と、対象が無い場合は null。</summary>
    public JobTransitionRejection? Rejection { get; }

    /// <summary>
    /// 要求として成功したか。
    /// </summary>
    /// <remarks>
    /// <see cref="JobTransitionRejection.AlreadyInEffect"/> を成功に含める。
    /// 既に停止（再開）へ向かっているなら要求の意図は満たされていて、
    /// 利用者にとってはボタンを 2 回押しただけ。キャンセルと同じ判断。
    /// </remarks>
    public bool IsSuccess =>
        Job is not null && Rejection is null or JobTransitionRejection.AlreadyInEffect;

    /// <summary>対象が見つからなかった結果を作る。</summary>
    public static JobControlResult NotFound() => new(null, null);

    /// <summary>要求を受け付けた結果を作る。</summary>
    public static JobControlResult Accepted(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobControlResult(job, null);
    }

    /// <summary>状態機械に拒否された結果を作る。</summary>
    public static JobControlResult Rejected(JobDto job, JobTransitionRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobControlResult(job, rejection);
    }
}
