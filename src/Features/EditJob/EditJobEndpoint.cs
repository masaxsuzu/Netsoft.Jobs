using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Features.EditJob;

/// <summary>
/// パラメータ編集の HTTP 入口。
/// </summary>
public static class EditJobEndpoint
{
    /// <summary>
    /// 差し替える parameters。編集できるのはこの 1 項目だけなので、専用の形にしてある。
    /// </summary>
    public sealed record EditJobParametersRequest(string? Parameters);

    /// <summary>
    /// <c>PUT /api/jobs/{id}/parameters</c> を登録する。
    /// </summary>
    /// <remarks>
    /// PUT なのは「その項目の値を丸ごと差し替える」操作だから。400（入力）と
    /// 409（状態）を分けて返す。400 は登録と同じ ValidationProblem の形で、
    /// 画面が項目別の表示を使い回せる。
    /// </remarks>
    public static IEndpointRouteBuilder MapEditJob(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPut($"{JobApiRoutes.Jobs}/{{id}}/parameters",
            async Task<Results<Ok<JobDto>, ValidationProblem, NotFound, Conflict<JobDto>>> (
                string id,
                EditJobParametersRequest request,
                EditJobHandler handler,
                CancellationToken cancellationToken) =>
        {
            EditJobResult result = await handler.HandleAsync(
                id, request.Parameters ?? string.Empty, cancellationToken);

            if (result.Errors.Count > 0)
            {
                return TypedResults.ValidationProblem(result.Errors.ToProblemDetailsErrors());
            }

            JobDto? job = result.Job;
            if (job is null)
            {
                return TypedResults.NotFound();
            }

            return result.IsSuccess ? TypedResults.Ok(job) : TypedResults.Conflict(job);
        })
        .WithName("EditJobParameters");

        return endpoints;
    }
}
