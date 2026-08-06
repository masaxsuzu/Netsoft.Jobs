using Microsoft.Extensions.DependencyInjection;

namespace Netsoft.Jobs.Features.RegisterJob;

/// <summary>
/// 登録機能の DI 登録。
/// </summary>
public static class RegisterJobServiceCollectionExtensions
{
    /// <summary>
    /// 登録機能に必要なサービスを登録する。
    /// </summary>
    public static IServiceCollection AddRegisterJob(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<RegisterJobHandler>();

        return services;
    }
}
