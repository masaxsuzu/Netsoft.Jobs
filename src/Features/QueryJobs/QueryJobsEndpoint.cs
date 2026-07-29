using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Netsoft.Jobs.Features.QueryJobs;

/// <summary>
/// 読み出しの HTTP 入口。
/// </summary>
/// <remarks>
/// ここに置くのはハンドラを呼んで結果を HTTP へ写す処理だけ。判断を書かない。
/// 画面（Blazor）は HTTP を通らずハンドラを直接呼ぶので、ここにロジックがあると画面から使えない。
/// </remarks>
public static class QueryJobsEndpoint
{
    /// <summary>
    /// この機能が扱う URL。
    /// </summary>
    /// <remarks>
    /// 登録が返す Location ヘッダ（<c>/api/jobs/{id}</c>）が指す先がこの詳細エンドポイントなので、
    /// <see cref="RegisterJob.RegisterJobEndpoint"/> の URL と一致していなければならない。
    /// </remarks>
    private const string JobsPath = "/api/jobs";

    /// <summary>
    /// <c>GET /api/jobs</c> と <c>GET /api/jobs/{id}</c> を登録する。Web 側はこれを呼ぶだけでよい。
    /// </summary>
    public static IEndpointRouteBuilder MapQueryJobs(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(JobsPath, async Task<Ok<IReadOnlyList<JobDto>>> (
            QueryJobsHandler handler,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<JobDto> jobs = await handler.ListAsync(cancellationToken);

            // 1 件も無いのは異常ではないので、空の配列をそのまま 200 で返す。
            return TypedResults.Ok(jobs);
        })
        .WithName("ListJobs");

        endpoints.MapGet($"{JobsPath}/{{id}}", async Task<Results<Ok<JobDto>, NotFound>> (
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
