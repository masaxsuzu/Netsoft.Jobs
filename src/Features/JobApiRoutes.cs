namespace Netsoft.Jobs.Features;

/// <summary>
/// HTTP API の URL。機能をまたいで一致していなければならないものだけを置く。
/// </summary>
/// <remarks>
/// <para>
/// 機能ごとに定数を持つのが本来だが、この URL は 3 つの機能が同じ値であることを
/// 前提にしている。登録が返す Location ヘッダは詳細の URL を指し、
/// キャンセルは詳細の下（<c>/api/jobs/{id}/cancel</c>）に生える。
/// 値がずれても誰もコンパイルで気づけず、実行して 404 を見るまで分からない。
/// そのため「機能の内部の決めごと」ではなく「機能が共有する API の形」として外に出す。
/// </para>
/// <para>
/// 共有する先を機能同士にしない（たとえばキャンセルが登録の定数を参照する）のは、
/// URL を合わせたいだけなのに機能間の依存ができてしまうため。
/// 全機能が等しく依存してよい Features 直下に置けば、どの機能も他の機能を知らずに済む。
/// </para>
/// </remarks>
internal static class JobApiRoutes
{
    /// <summary>Job のコレクション。詳細・キャンセルはこの下に生える。</summary>
    internal const string Jobs = "/api/jobs";
}
