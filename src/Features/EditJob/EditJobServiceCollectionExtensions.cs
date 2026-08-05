using Microsoft.Extensions.DependencyInjection;

namespace Netsoft.Jobs.Features.EditJob;

/// <summary>
/// 編集の登録。依存は IJobStore と ISubTaskStore（着手済みの検証に読む）。
/// </summary>
public static class EditJobServiceCollectionExtensions
{
    /// <summary>編集のハンドラを登録する。</summary>
    public static IServiceCollection AddEditJob(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<EditJobHandler>();

        return services;
    }
}
