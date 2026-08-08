using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Audit;

/// <summary>
/// コマンドの結果と、その実施の監査ログ。
/// </summary>
/// <param name="Value">コマンドが返す本来の結果。</param>
/// <param name="Log">その実施 1 件の記録。</param>
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
/// 「利用者の操作 1 つに対して監査ログ 1 つ」が、書き手の注意ではなく型で決まる。
/// </para>
/// <para>
/// <b>実施は状態遷移ではない。</b>1 回の再開は Job 行を 2 回書くが、
/// <see cref="Log"/> は 1 件（<see cref="AuditLog"/> の注記）。
/// </para>
/// </remarks>
public sealed record Audited<T>(T Value, AuditLog Log);
