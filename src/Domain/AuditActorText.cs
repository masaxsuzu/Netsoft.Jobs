namespace Netsoft.Jobs.Domain;

/// <summary>
/// <see cref="AuditActor"/> と文字列の相互変換。保存と契約に使う。
/// </summary>
/// <remarks>
/// 変換を <see cref="JobStatusText"/> と同じ形で 1 か所に置く。
/// 保存するのは数値ではなく名前 ── 値を足し引きしたときに、保存済みの行の意味が
/// 黙って変わらないようにするため。
/// </remarks>
public static class AuditActorText
{
    /// <summary>文字列表現にする。</summary>
    public static string ToText(AuditActor actor) => actor.ToString();

    /// <summary>文字列表現から戻す。</summary>
    public static AuditActor FromText(string text) => Enum.Parse<AuditActor>(text);
}
