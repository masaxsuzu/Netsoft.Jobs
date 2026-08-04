using Microsoft.Extensions.DependencyInjection;

namespace Netsoft.Jobs.Features.QueryJobTypes;

/// <summary>
/// 種類の読み出し機能の DI 登録。
/// </summary>
public static class QueryJobTypesServiceCollectionExtensions
{
    /// <summary>
    /// 種類の読み出し機能に必要なサービスを登録する。
    /// </summary>
    /// <remarks>
    /// 機能ごとに登録の口を分けてあるのは、その機能が何を必要とするかを機能の側に置くため。
    /// ただし機能を足すときは、この形のファイルに加えて JobFeaturesServiceCollectionExtensions と
    /// JobFeaturesEndpointRouteBuilderExtensions にも 1 行ずつ足す必要がある。
    /// まとめて入れたい場合は <see cref="JobFeaturesServiceCollectionExtensions.AddJobFeatures"/> を使う。
    /// この機能が使う JobHandlerRegistry は実行エンジン側（AddJobExecution）が入れる。
    /// </remarks>
    public static IServiceCollection AddQueryJobTypes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<QueryJobTypesHandler>();

        return services;
    }
}
