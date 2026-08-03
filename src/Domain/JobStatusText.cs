namespace Netsoft.Jobs.Domain;

/// <summary>
/// <see cref="JobStatus"/> と文字列表現の変換。永続化（DB の列）と DTO が同じ表現を使う。
/// </summary>
/// <remarks>
/// <para>
/// 数値ではなく enum の名前を使う。理由は 2 つある。
/// 1 つは、数値だと DB や API 応答を直接覗いたときに "3" が何を指すのか読めないこと。
/// もう 1 つは、enum のメンバーを並べ替えたり途中に足したりした瞬間に
/// 既存データの意味が変わってしまうこと。名前なら並び順から独立していられる。
/// </para>
/// <para>
/// 書く側（Infrastructure の列、Features の DTO）と読む側（Infrastructure の読み戻し、
/// Web の判定）が別の場所にあるので、全員がここを通ることで表現の一致を保つ。
/// 各所が ToString / Enum.Parse を直接書くと、表現を変える判断をしたときに
/// 片方だけ直っても全部コンパイルが通ってしまう。
/// </para>
/// </remarks>
public static class JobStatusText
{
    /// <summary>文字列表現にする。</summary>
    public static string ToText(JobStatus status) => status.ToString();

    /// <summary>
    /// 文字列表現から読み戻す。読み戻せなければ例外。
    /// </summary>
    /// <remarks>自分が <see cref="ToText"/> で書いた値を読む側（永続化の読み戻し）用。</remarks>
    public static JobStatus FromText(string text) => Enum.Parse<JobStatus>(text);

    /// <summary>
    /// 例外を投げずに読み戻す。外から来た値（DTO など）の検証に使う。
    /// </summary>
    public static bool TryFromText(string? text, out JobStatus status) => Enum.TryParse(text, out status);
}
