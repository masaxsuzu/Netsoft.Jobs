using Microsoft.Extensions.DependencyInjection;

namespace Netsoft.Jobs.Features.PauseJob;

/// <summary>
/// 一時停止と再開の登録。
/// </summary>
/// <remarks>
/// 依存は IJobStore と TimeProvider だけで、実行エンジン側の実体は使わない
/// （伝達の口が要らない理由は <see cref="PauseJobHandler"/> の注記を参照）。
/// </remarks>
public static class PauseJobServiceCollectionExtensions
{
    /// <summary>一時停止・再開のハンドラを登録する。</summary>
    public static IServiceCollection AddPauseJob(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<PauseJobHandler>();
        services.AddScoped<ResumeJobHandler>();

        return services;
    }
}
