using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Ui;

/// <summary>
/// キャンセル API の応答。受け付けたか、対象が無かったか、状態が合わず拒否されたかを表す。
/// </summary>
/// <remarks>
/// Features の CancelJobResult と同じ区別を HTTP のステータスから復元した形。
/// 拒否の理由（JobTransitionRejection）までは運ばない。API が 409 の本文に現在の
/// Job を載せる契約なので、画面は「今どうなっているのか」を見せられれば足りる。
/// </remarks>
public sealed class CancelJobResponse
{
    private CancelJobResponse(JobDto? job, bool isSuccess)
    {
        Job = job;
        IsSuccess = isSuccess;
    }

    /// <summary>
    /// 要求の後の Job。見つからなかった（404）場合だけ null。
    /// 拒否（409）でも現在の Job が入る。
    /// </summary>
    public JobDto? Job { get; }

    /// <summary>要求として成功したか（API が 200 を返したか）。</summary>
    public bool IsSuccess { get; }

    /// <summary>対象が見つからなかった応答を作る。</summary>
    public static CancelJobResponse NotFound() => new(null, false);

    /// <summary>キャンセルが受け付けられた応答を作る。</summary>
    public static CancelJobResponse Accepted(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new CancelJobResponse(job, true);
    }

    /// <summary>状態が合わず拒否された応答を作る。</summary>
    public static CancelJobResponse Rejected(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new CancelJobResponse(job, false);
    }
}
