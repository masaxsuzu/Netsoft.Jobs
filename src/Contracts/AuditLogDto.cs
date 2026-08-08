namespace Netsoft.Jobs.Contracts;

/// <summary>
/// 監査ログ 1 件。プロセスをまたぐ形。
/// </summary>
/// <param name="Actor">実施者。<c>User</c> か <c>System</c>。</param>
/// <param name="At">実施した時刻。</param>
/// <param name="Content">何をしたか。過去形で、パラメータを含む 1 文。</param>
/// <param name="JobId">どの Job についての実施か。紐づかない実施なら null。</param>
/// <param name="Error">通らなかった理由。通ったなら null。</param>
/// <remarks>
/// <para>
/// <paramref name="Actor" /> は enum ではなく文字列。契約は Domain を参照しないので
/// （<c>Contracts</c> は依存ゼロ）、状態と同じく名前で運ぶ。画面側の文言への写しは
/// 画面が持つ（<c>JobStatusLabel</c> と同じ形）。
/// </para>
/// <para>
/// 連番を持たないので、<b>並びは応答の順序そのものが意味を持つ</b>。Job 単位は古い順、
/// 全件は新しい順（<c>IAuditLogStore</c> の注記）。受け取った側で並べ直さないこと。
/// </para>
/// </remarks>
public sealed record AuditLogDto(
    string Actor,
    DateTimeOffset At,
    string Content,
    string? JobId,
    string? Error);
