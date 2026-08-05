using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Contracts;

/// <summary>
/// 画面がパラメータの編集を許すかの判定。定義は Domain の
/// <see cref="JobStatusExtensions.CanEditParameters"/> が 1 か所で持つ
/// （状態の文字列を受け取る理由は <see cref="JobCancelability"/> と同じ）。
/// </summary>
public static class JobEditability
{
    /// <summary>この状態の Job のパラメータを編集できるか。</summary>
    public static bool CanEdit(string status) =>
        JobStatusText.TryFromText(status, out JobStatus parsed) && parsed.CanEditParameters();
}
