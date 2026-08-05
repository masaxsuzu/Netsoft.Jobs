using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.EditJob;

/// <summary>
/// 編集要求の結果。受け付けたか、対象が無かったか、状態が合わず拒否されたか、
/// 入力として不正だったかを表す。
/// </summary>
/// <remarks>
/// キャンセル系の 3 値（<see cref="CancelJob.CancelJobResult"/>）に加えて
/// <see cref="Errors"/> がある。編集だけは「状態の問題」（409）と「入力の問題」（400）の
/// 両方を持つため。項目別のエラーは <see cref="ValidationError"/> をそのまま使い、
/// 画面が登録フォームと同じ形で表示できるようにする。
/// </remarks>
public sealed class EditJobResult
{
    private static readonly IReadOnlyList<ValidationError> NoErrors = [];

    private EditJobResult(JobDto? job, JobTransitionRejection? rejection, IReadOnlyList<ValidationError> errors)
    {
        Job = job;
        Rejection = rejection;
        Errors = errors;
    }

    /// <summary>要求の後の Job。見つからなかった場合と入力が不正な場合は null。</summary>
    public JobDto? Job { get; }

    /// <summary>状態の問題で拒否された理由。それ以外は null。</summary>
    public JobTransitionRejection? Rejection { get; }

    /// <summary>入力の問題。無ければ空。</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>要求として成功したか。</summary>
    public bool IsSuccess => Job is not null && Rejection is null && Errors.Count == 0;

    /// <summary>対象が見つからなかった結果を作る。</summary>
    public static EditJobResult NotFound() => new(null, null, NoErrors);

    /// <summary>入力が不正だった結果を作る。</summary>
    public static EditJobResult Invalid(IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return errors.Count > 0
            ? new EditJobResult(null, null, errors)
            : throw new ArgumentException("入力の問題には理由が必要です。", nameof(errors));
    }

    /// <summary>状態の問題で拒否された結果を作る。</summary>
    public static EditJobResult Rejected(JobDto job, JobTransitionRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new EditJobResult(job, rejection, NoErrors);
    }

    /// <summary>編集を受け付けた結果を作る。</summary>
    public static EditJobResult Accepted(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new EditJobResult(job, null, NoErrors);
    }
}
