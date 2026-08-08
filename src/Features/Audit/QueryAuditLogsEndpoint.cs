using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Audit;

/// <summary>
/// 監査ログの読み出し口。
/// </summary>
/// <remarks>
/// <para>
/// <b>並びは応答の順序が意味を持つ。</b>Job 単位は古い順（1 つの Job の物語として読む）、
/// 全件は新しい順（直近に何が起きたかを見る）。並べ直す材料（連番）は載せていないので、
/// 受け取った側は順序をそのまま使う。
/// </para>
/// <para>
/// Job 単位に 404 を返さない。Job が無ければ空の配列でよい ── 監査ログは Job が消えても
/// 残りうる種類の記録で、「Job が無い」と「その Job について何も起きていない」を
/// 呼び出し側が区別する必要が無い（画面は実在する行の分しか叩かない）。
/// </para>
/// </remarks>
public static class QueryAuditLogsEndpoint
{
    /// <summary>監査ログの GET を登録する。</summary>
    public static IEndpointRouteBuilder MapQueryAuditLogs(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet($"{JobApiRoutes.Jobs}/{{id}}/audit-logs",
            async Task<Ok<IReadOnlyList<AuditLogDto>>> (
                string id,
                IAuditLogStore store,
                CancellationToken cancellationToken) =>
            {
                // 識別子の形にならない値は何も指し示していない。空で返す。
                if (!JobId.TryFrom(id, out JobId jobId))
                {
                    return TypedResults.Ok<IReadOnlyList<AuditLogDto>>([]);
                }

                IReadOnlyList<AuditLog> logs = await store.ListByJobAsync(jobId, cancellationToken);

                return TypedResults.Ok<IReadOnlyList<AuditLogDto>>([.. logs.Select(ToDto)]);
            })
            .WithName("QueryJobAuditLogs");

        endpoints.MapGet(JobApiRoutes.AuditLogs,
            async Task<Ok<IReadOnlyList<AuditLogDto>>> (
                IAuditLogStore store,
                CancellationToken cancellationToken) =>
            {
                IReadOnlyList<AuditLog> logs = await store.ListAsync(cancellationToken);

                return TypedResults.Ok<IReadOnlyList<AuditLogDto>>([.. logs.Select(ToDto)]);
            })
            .WithName("QueryAuditLogs");

        return endpoints;
    }

    /// <remarks>
    /// 実施者は enum の名前のまま出す。契約は Domain を参照しないので、
    /// 状態（<see cref="JobStatusText"/>）と同じ扱いにしてある。
    /// </remarks>
    private static AuditLogDto ToDto(AuditLog log) =>
        new(
            AuditActorText.ToText(log.Actor),
            log.At,
            log.Content,
            log.JobId?.Value,
            log.Error);
}
