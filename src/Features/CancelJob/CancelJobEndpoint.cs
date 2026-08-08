using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netsoft.Jobs.Contracts;
using Netsoft.Jobs.Features.Audit;

namespace Netsoft.Jobs.Features.CancelJob;

/// <summary>
/// キャンセルの HTTP 入口。
/// </summary>
public static class CancelJobEndpoint
{
    /// <summary>
    /// <c>POST /api/jobs/{id}/cancel</c> を登録する。Web 側はこれを呼ぶだけでよい。
    /// </summary>
    /// <remarks>
    /// GET でも DELETE でもなく POST にする。読み出しではないので GET は使えず、
    /// Job という資源を消すわけでもないので DELETE も合わない。
    /// 資源の下に操作を生やす形にしてある。
    /// </remarks>
    public static IEndpointRouteBuilder MapCancelJob(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost($"{JobApiRoutes.Jobs}/{{id}}/cancel",
            async Task<Results<Ok<JobDto>, NotFound, Conflict<JobDto>>> (
                string id,
                CancelJobHandler handler,
                AuditRecorder recorder,
                CancellationToken cancellationToken) =>
        {
            // 監査ログを書くのは recorder だけ（AuditRecorder の注記）。ハンドラの戻り値は
            // Audited<T> なので、ここを通さずに結果を取り出す形は書けるが書かない。
            CancelJobResult result = await recorder.RecordAsync(
                await handler.HandleAsync(id, cancellationToken), cancellationToken);

            JobDto? job = result.Job;
            if (job is null)
            {
                return TypedResults.NotFound();
            }

            // 冪等に成功として返すかどうかはハンドラが決めている。ここでは写すだけ。
            // 拒否のときも現在の Job を本文に載せる。文言を組み立てると判断がここに漏れるうえ、
            // 呼び出し側は「今どうなっているのか」が分かれば表示を決められる。
            return result.IsSuccess
                ? TypedResults.Ok(job)
                : TypedResults.Conflict(job);
        })
        .WithName("CancelJob");

        return endpoints;
    }
}
