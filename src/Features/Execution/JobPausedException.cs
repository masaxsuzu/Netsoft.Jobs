using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// ハンドラが一時停止要求を安全な区切り（サブタスクの境界）で受理して抜けたことを表す。
/// </summary>
/// <remarks>
/// <see cref="OperationCanceledException"/> → Cancelled の鏡。ハンドラがこれを投げると、
/// エンジンが捕まえて <see cref="JobTrigger.ConfirmPaused"/> を書く。
/// 失敗ではないので、エンジン以外がこれを握って Failed に変えてはいけない。
/// </remarks>
public sealed class JobPausedException : Exception
{
    /// <summary>受理した Job の識別子で生成する。</summary>
    public JobPausedException(JobId jobId)
        : base($"Job {jobId.Value} は一時停止要求を受理しました。") => JobId = jobId;

    /// <summary>受理した Job の識別子。</summary>
    public JobId JobId { get; }
}
