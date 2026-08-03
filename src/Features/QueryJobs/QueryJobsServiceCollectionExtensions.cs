using Microsoft.Extensions.DependencyInjection;

namespace Netsoft.Jobs.Features.QueryJobs;

/// <summary>
/// 読み出し機能の DI 登録。
/// </summary>
public static class QueryJobsServiceCollectionExtensions
{
    /// <summary>
    /// 読み出し機能に必要なサービスを登録する。
    /// </summary>
    /// <remarks>
    /// 機能ごとに登録の口を分けてあるのは、その機能が何を必要とするかを機能の側に置くため。
    /// ただし機能を足すときは、この形のファイルに加えて JobFeaturesServiceCollectionExtensions と
    /// JobFeaturesEndpointRouteBuilderExtensions にも 1 行ずつ足す必要がある。
    /// まとめて入れたい場合は <see cref="JobFeaturesServiceCollectionExtensions.AddJobFeatures"/> を使う。
    /// </remarks>
    public static IServiceCollection AddQueryJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<QueryJobsHandler>();

        return services;
    }
}
