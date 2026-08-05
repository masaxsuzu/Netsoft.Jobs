using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Contracts;

/// <summary>
/// 画面がパラメータの編集を許すかの判定。定義は Domain の
/// <see cref="JobStatusExtensions.CanEditParameters"/> が 1 か所で持つ。
/// </summary>
public static class JobEditability
{
    /// <summary>この Job のパラメータを編集できるか。</summary>
    public static bool CanEdit(JobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);

        return JobStatusText.TryFromText(job.Status, out JobStatus status)
            && status.CanEditParameters();
    }
}
