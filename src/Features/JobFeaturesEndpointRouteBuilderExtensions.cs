using Microsoft.AspNetCore.Routing;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.QueryJobs;
using Netsoft.Jobs.Features.RegisterJob;

namespace Netsoft.Jobs.Features;

/// <summary>
/// Features 全体のエンドポイント登録。Web 側の入口。
/// </summary>
public static class JobFeaturesEndpointRouteBuilderExtensions
{
    /// <summary>
    /// すべての機能の HTTP エンドポイントを登録する。機能が増えたらここに Map を 1 行足す。
    /// </summary>
    public static IEndpointRouteBuilder MapJobFeatures(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapRegisterJob();
        endpoints.MapQueryJobs();
        endpoints.MapCancelJob();

        return endpoints;
    }
}
