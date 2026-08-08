namespace Netsoft.Jobs.Domain;

/// <summary>
/// 監査ログの置き場。
/// </summary>
/// <remarks>
/// <para>
/// <b>書くだけで、書き換える口も消す口も無い。</b>監査ログは後から読むためだけに在るので、
/// 直せてしまうと記録としての値が消える。<see cref="IJobStore"/> が条件付き更新を持つのと
/// 対照的に、ここには <c>Update</c> も <c>Delete</c> も置かない。
/// </para>
/// <para>
/// 読み出しが Job 単位のものだけなのは、画面がそこしか見ないため。Job に紐づかない実施
/// （登録の入力エラー、存在しない Id への操作）も記録はするが、画面には出さないと決めてある。
/// 全件を読む口は <see cref="ListAsync"/> にあるので、そちらから辿れる。
/// </para>
/// </remarks>
public interface IAuditLogStore
{
    /// <summary>1 件書く。</summary>
    /// <remarks>
    /// <b>失敗しても呼び出し側の操作は止めない</b>という約束は、この実装ではなく
    /// 呼び出し側（Features の記録係）が持つ。ここは素直に例外を投げてよい。
    /// </remarks>
    Task WriteAsync(AuditLog log, CancellationToken cancellationToken);

    /// <summary>指定した Job の監査ログを、古い順で取得する。</summary>
    /// <remarks>
    /// 古い順なのは、1 つの Job の記録が「登録した → 実行を開始した → …」という
    /// 物語として読まれるため。<b>並びの第 1 キーは実施した時刻</b>で、書いた順ではない
    /// ── 利用者の要求はコマンドの終わりに書かれるのに対し、その書き込みが呼び起こした
    /// 実行エンジンは先に自分の分を書けるため、書いた順では逆転する。
    /// 時刻だけでは同一ミリ秒の並びが決まらないので、書いた順を第 2 キーに持つこと。
    /// </remarks>
    Task<IReadOnlyList<AuditLog>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken);

    /// <summary>全件を新しい順で取得する。</summary>
    /// <remarks>
    /// Job 単位と逆に新しい順。こちらは物語ではなく「直近に何が起きたか」を見るためで、
    /// Job の一覧（<see cref="IJobStore.ListAsync"/>）と同じ向きに揃えてある。
    /// </remarks>
    Task<IReadOnlyList<AuditLog>> ListAsync(CancellationToken cancellationToken);
}
