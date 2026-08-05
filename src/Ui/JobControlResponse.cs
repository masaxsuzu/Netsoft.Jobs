using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Ui;

/// <summary>
/// 一時停止・再開 API の応答。形と理由は <see cref="CancelJobResponse"/> と同じ
/// （2 つの操作は 1 つの機能の両側なので応答の型は共有する）。
/// </summary>
public sealed class JobControlResponse
{
    private JobControlResponse(JobDto? job, bool isSuccess)
    {
        Job = job;
        IsSuccess = isSuccess;
    }

    /// <summary>要求の後の Job。見つからなかった（404）場合だけ null。拒否（409）でも入る。</summary>
    public JobDto? Job { get; }

    /// <summary>要求として成功したか（API が 200 を返したか）。</summary>
    public bool IsSuccess { get; }

    /// <summary>対象が見つからなかった応答を作る。</summary>
    public static JobControlResponse NotFound() => new(null, false);

    /// <summary>要求が受け付けられた応答を作る。</summary>
    public static JobControlResponse Accepted(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobControlResponse(job, true);
    }

    /// <summary>状態が合わず拒否された応答を作る。</summary>
    public static JobControlResponse Rejected(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new JobControlResponse(job, false);
    }
}
