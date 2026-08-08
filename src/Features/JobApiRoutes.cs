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
/// <para>
/// public なのは、変更通知（SSE）の入口を Web が同じ URL 空間（<c>/api/jobs</c> の下）に
/// 生やすため。通知は画面へどう伝えるかというホストの関心なので実装は Web にあるが、
/// URL の形はここが決める。internal のまま Web に別の定数を持たせると、
/// この注記が警告している「値がずれても実行するまで気づけない」状態に戻ってしまう。
/// </para>
/// </remarks>
public static class JobApiRoutes
{
    /// <summary>Job のコレクション。詳細・キャンセルはこの下に生える。</summary>
    public const string Jobs = "/api/jobs";

    /// <summary>
    /// Job の変更通知（SSE）。実装は Web 側（JobEventsEndpoint）にある。
    /// </summary>
    /// <remarks>
    /// <c>{Jobs}/{{id}}</c> と同じ位置に生えるが、ASP.NET Core のルーティングは
    /// リテラル（events）をパラメータ（{id}）より優先するので衝突しない。
    /// 代わりに "events" という id の Job は取得できなくなるが、id は GUID v7 で
    /// 採番される（GuidV7JobIdFactory）ので実際にはぶつからない。
    /// </remarks>
    public const string JobEvents = $"{Jobs}/events";

    /// <summary>
    /// 登録済みの Job の種類の一覧（GET）。
    /// </summary>
    /// <remarks>
    /// <c>{Jobs}/{{id}}</c> と同じ位置に生えるが、<see cref="JobEvents"/> と同じ理由で
    /// 衝突しない（リテラルがパラメータより優先される。id は GUID v7 なので
    /// "types" という id も現れない）。
    /// </remarks>
    public const string JobTypes = $"{Jobs}/types";

    /// <summary>監査ログの全件。Job 単位は <see cref="Jobs"/> の下（<c>/api/jobs/{id}/audit-logs</c>）。</summary>
    public const string AuditLogs = "/api/audit-logs";
}
