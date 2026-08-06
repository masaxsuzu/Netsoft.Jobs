using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Features.QueryJobTypes;

/// <summary>
/// Job の種類の読み出しの HTTP 入口。
/// </summary>
public static class QueryJobTypesEndpoint
{
    /// <summary>
    /// <c>GET /api/jobs/types</c> を登録する。Web 側はこれを呼ぶだけでよい。
    /// </summary>
    public static IEndpointRouteBuilder MapQueryJobTypes(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // URL は詳細（/api/jobs/{id}）と同じ位置に生えるので、値は Features 直下の
        // JobApiRoutes で共有する（衝突しない理由もそこに書いてある）。
        endpoints.MapGet(JobApiRoutes.JobTypes, Ok<IReadOnlyList<JobTypeDto>> (
            QueryJobTypesHandler handler) =>
        {
            // ハンドラが 1 つも登録されていない構成は異常だが、この口が決めることではない。
            // 空の配列をそのまま 200 で返し、どう見せるかは呼び出し側に任せる。
            return TypedResults.Ok(handler.List());
        })
        .WithName("ListJobTypes");

        return endpoints;
    }
}
