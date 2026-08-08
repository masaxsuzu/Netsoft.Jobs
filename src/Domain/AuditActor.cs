namespace Netsoft.Jobs.Domain;

/// <summary>
/// 監査ログの実施者。
/// </summary>
/// <remarks>
/// <para>
/// 個人は識別しない。この基盤に認証が無く、誰が押したかを知る手段が存在しないため。
/// 「利用者の誰か」と「システム自身」を分けることには意味がある ── 起きたことが
/// 外から要求されたものか、基盤が自分で決めたものかは、後から読むときに必ず要る。
/// </para>
/// <para>
/// <see cref="JobStatus"/> と同じく、数値に意味を持たせない。
/// </para>
/// </remarks>
public enum AuditActor
{
    /// <summary>利用者が API を叩いた。</summary>
    User,

    /// <summary>基盤が自分で動いた（実行エンジン・起動時復旧）。</summary>
    System,
}
