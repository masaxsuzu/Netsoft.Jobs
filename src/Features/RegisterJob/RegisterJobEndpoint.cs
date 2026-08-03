using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Features.RegisterJob;

/// <summary>
/// 登録の HTTP 入口。
/// </summary>
/// <remarks>
/// ここに置くのはハンドラを呼んで結果を HTTP へ写す処理だけ。判断を書かない。
/// 画面（Blazor）は HTTP を通らずハンドラを直接呼ぶので、ここにロジックがあると画面から使えない。
/// </remarks>
public static class RegisterJobEndpoint
{
    /// <summary>
    /// <c>POST /api/jobs</c> を登録する。Web 側はこれを呼ぶだけでよい。
    /// </summary>
    public static IEndpointRouteBuilder MapRegisterJob(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(JobApiRoutes.Jobs, async Task<Results<Created<JobDto>, ValidationProblem>> (
            RegisterJobCommand command,
            RegisterJobHandler handler,
            CancellationToken cancellationToken) =>
        {
            Result<JobDto> result = await handler.HandleAsync(command, cancellationToken);

            if (result.IsFailure)
            {
                return TypedResults.ValidationProblem(result.Errors.ToProblemDetailsErrors());
            }

            // 作られた資源の場所を返す。この URL が指す先は読み出しの詳細エンドポイントなので、
            // 両者が同じ値を使うように定数を Features 直下で共有している。
            return TypedResults.Created(
                $"{JobApiRoutes.Jobs}/{Uri.EscapeDataString(result.Value.Id)}",
                result.Value);
        })
        .WithName("RegisterJob");

        return endpoints;
    }
}
