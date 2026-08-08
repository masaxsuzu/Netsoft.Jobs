using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Audit;

/// <summary>
/// コマンドの結果と、そこで起きた実施の監査ログ。
/// </summary>
/// <param name="Value">コマンドが返す本来の結果。</param>
/// <param name="Logs">
/// 起きた実施の記録。<b>先頭は必ず利用者の要求</b>で、後ろに続くのは
/// そのコマンドの中でシステムが確定させた分。
/// </param>
/// <typeparam name="T">コマンドの結果の型。</typeparam>
/// <remarks>
/// <para>
/// <b>コマンドが監査ログを自分で書かない理由。</b>書く形にすると、書き忘れも二重書きも
/// 型では防げない。新しいコマンドを足したときに黙って監査が欠け、しかも<b>監査が欠けたことは
/// 監査ログからは分からない</b>（無い記録は探せない）。
/// </para>
/// <para>
/// ここでは<b>材料を返させ、書くのは <see cref="AuditRecorder"/> だけ</b>にしてある。
/// 戻り値の型がこれなので、材料を作らないコマンドはコンパイルが通らない。
/// </para>
/// <para>
/// <b>1 件とは限らない理由。</b>「利用者の操作 1 つに 1 件」は守るが、コマンドによっては
/// その中でシステムの確定まで済ませる（待ち行列の Job のキャンセル、停止中の Job の再開）。
/// 要求と確定は別の出来事なので分けて記録する ── 畳むと「誰が押したか」と
/// 「いつ実際に決着したか」のどちらも読めなくなる。同じ時刻に並ぶことになるが、
/// 保存先は書いた順を並びの第 2 キーに持つので、要求 → 確定の順で読める。
/// </para>
/// <para>
/// <b>実施は状態遷移ではない。</b>停止中からの再開は Job 行を 2 回書く（Resuming と Resumed）が、
/// 実施は「利用者が要求した」と「システムが確定させた」の 2 つで、書き込み回数と一致するのは
/// たまたま（<see cref="AuditLog"/> の注記）。
/// </para>
/// </remarks>
public sealed record Audited<T>(T Value, IReadOnlyList<AuditLog> Logs)
{
    /// <summary>確定を伴わない実施（要求だけ）の結果を作る。</summary>
    public Audited(T value, AuditLog log)
        : this(value, [log])
    {
    }
}
