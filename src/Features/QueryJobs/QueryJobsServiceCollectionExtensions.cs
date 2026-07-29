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
    /// 機能ごとに登録の口を分けておくと、機能を足すときに触るのが自分のファイルだけで済む。
    /// まとめて入れたい場合は <see cref="JobFeaturesServiceCollectionExtensions.AddJobFeatures"/> を使う。
    /// </remarks>
    public static IServiceCollection AddQueryJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<QueryJobsHandler>();

        return services;
    }
}
