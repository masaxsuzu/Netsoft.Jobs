using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Ui;

/// <summary>
/// パラメータ編集 API の応答。登録（400 の項目別エラー）とキャンセル（404 / 409）の
/// 両方の性質を持つので、その 2 つの応答型を合わせた形になっている。
/// </summary>
public sealed class EditJobResponse
{
    private static readonly IDictionary<string, string[]> NoErrors =
        new Dictionary<string, string[]>();

    private EditJobResponse(JobDto? job, bool isSuccess, IDictionary<string, string[]> errors)
    {
        Job = job;
        IsSuccess = isSuccess;
        Errors = errors;
    }

    /// <summary>要求の後の Job。404 と 400 では null。拒否（409）では現在の Job。</summary>
    public JobDto? Job { get; }

    /// <summary>要求として成功したか（API が 200 を返したか）。</summary>
    public bool IsSuccess { get; }

    /// <summary>項目名をキーにした検証エラー（400）。それ以外は空。</summary>
    public IDictionary<string, string[]> Errors { get; }

    /// <summary>対象が見つからなかった応答を作る。</summary>
    public static EditJobResponse NotFound() => new(null, false, NoErrors);

    /// <summary>入力が不正だった応答を作る。</summary>
    public static EditJobResponse Invalid(IDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return new EditJobResponse(null, false, errors);
    }

    /// <summary>状態が合わず拒否された応答を作る。</summary>
    public static EditJobResponse Rejected(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new EditJobResponse(job, false, NoErrors);
    }

    /// <summary>編集が受け付けられた応答を作る。</summary>
    public static EditJobResponse Accepted(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new EditJobResponse(job, true, NoErrors);
    }
}
