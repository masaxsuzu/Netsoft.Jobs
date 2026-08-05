using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Features.QueryJobs;

/// <summary>
/// サブタスク読み出しの HTTP 入口。
/// </summary>
public static class QuerySubTasksEndpoint
{
    /// <summary><c>GET /api/jobs/{id}/subtasks</c> を登録する。</summary>
    public static IEndpointRouteBuilder MapQuerySubTasks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet($"{JobApiRoutes.Jobs}/{{id}}/subtasks",
            async Task<Results<Ok<IReadOnlyList<SubTaskDto>>, NotFound>> (
                string id,
                QuerySubTasksHandler handler,
                CancellationToken cancellationToken) =>
        {
            IReadOnlyList<SubTaskDto>? subTasks = await handler.ListAsync(id, cancellationToken);

            return subTasks is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(subTasks);
        })
        .WithName("QuerySubTasks");

        return endpoints;
    }
}
