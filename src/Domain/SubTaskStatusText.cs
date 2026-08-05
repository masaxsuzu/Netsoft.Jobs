namespace Netsoft.Jobs.Domain;

/// <summary>
/// <see cref="SubTaskStatus"/> と文字列表現の変換。永続化（DB の列）と DTO が同じ表現を使う。
/// </summary>
/// <remarks>
/// 数値ではなく enum の名前を使う理由と、全員がここを通す理由は
/// <see cref="JobStatusText"/> と同じ。
/// </remarks>
public static class SubTaskStatusText
{
    /// <summary>文字列表現にする。</summary>
    public static string ToText(SubTaskStatus status) => status.ToString();

    /// <summary>
    /// 文字列表現から読み戻す。読み戻せなければ例外。
    /// </summary>
    /// <remarks>自分が <see cref="ToText"/> で書いた値を読む側（永続化の読み戻し）用。</remarks>
    public static SubTaskStatus FromText(string text) => Enum.Parse<SubTaskStatus>(text);
}
