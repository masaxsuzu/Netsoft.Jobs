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
    public static IServiceCollection AddQueryJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<QueryJobsHandler>();
        services.AddScoped<QuerySubTasksHandler>();

        return services;
    }
}
