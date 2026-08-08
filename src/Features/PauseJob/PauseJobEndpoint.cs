using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Features.Audit;

namespace Netsoft.Jobs.Features.PauseJob;

/// <summary>
/// 一時停止・再開の HTTP 入口。写し方はキャンセル（<see cref="CancelJob.CancelJobEndpoint"/>）と同じ。
/// </summary>
public static class PauseJobEndpoint
{
    /// <summary><c>POST /api/jobs/{id}/pause</c> と <c>POST /api/jobs/{id}/resume</c> を登録する。</summary>
    public static IEndpointRouteBuilder MapPauseJob(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost($"{JobApiRoutes.Jobs}/{{id}}/pause",
            async Task<Results<Ok<JobDto>, NotFound, Conflict<JobDto>>> (
                string id,
                PauseJobHandler handler,
                AuditRecorder recorder,
                CancellationToken cancellationToken) =>
                ToHttp(await recorder.RecordAsync(
                    await handler.HandleAsync(id, cancellationToken), cancellationToken)))
            .WithName("PauseJob");

        endpoints.MapPost($"{JobApiRoutes.Jobs}/{{id}}/resume",
            async Task<Results<Ok<JobDto>, NotFound, Conflict<JobDto>>> (
                string id,
                ResumeJobHandler handler,
                AuditRecorder recorder,
                CancellationToken cancellationToken) =>
                ToHttp(await recorder.RecordAsync(
                    await handler.HandleAsync(id, cancellationToken), cancellationToken)))
            .WithName("ResumeJob");

        return endpoints;
    }

    private static Results<Ok<JobDto>, NotFound, Conflict<JobDto>> ToHttp(JobControlResult result)
    {
        JobDto? job = result.Job;
        if (job is null)
        {
            return TypedResults.NotFound();
        }

        // 冪等に成功として返すかどうかはハンドラが決めている。ここでは写すだけ。
        return result.IsSuccess ? TypedResults.Ok(job) : TypedResults.Conflict(job);
    }
}
