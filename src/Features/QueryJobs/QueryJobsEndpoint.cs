using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Features.QueryJobs;

/// <summary>
/// 読み出しの HTTP 入口。
/// </summary>
public static class QueryJobsEndpoint
{
    /// <summary>
    /// <c>GET /api/jobs</c> と <c>GET /api/jobs/{id}</c> を登録する。Web 側はこれを呼ぶだけでよい。
    /// </summary>
    public static IEndpointRouteBuilder MapQueryJobs(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // URL は登録の Location ヘッダとキャンセルの生え先が一致していなければならないので、
        // 機能ごとに定数を持たず Features 直下の JobApiRoutes を共有する。
        endpoints.MapGet(JobApiRoutes.Jobs, async Task<Ok<IReadOnlyList<JobListItemDto>>> (
            QueryJobsHandler handler,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<JobListItemDto> jobs = await handler.ListAsync(cancellationToken);

            // 1 件も無いのは異常ではないので、空の配列をそのまま 200 で返す。
            return TypedResults.Ok(jobs);
        })
        .WithName("ListJobs");

        endpoints.MapGet($"{JobApiRoutes.Jobs}/{{id}}", async Task<Results<Ok<JobDto>, NotFound>> (
            string id,
            QueryJobsHandler handler,
            CancellationToken cancellationToken) =>
        {
            JobDto? job = await handler.FindAsync(id, cancellationToken);

            return job is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(job);
        })
        .WithName("GetJob");

        return endpoints;
    }
}
