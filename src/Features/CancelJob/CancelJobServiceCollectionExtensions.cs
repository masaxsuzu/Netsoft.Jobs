using Microsoft.Extensions.DependencyInjection;

namespace Netsoft.Jobs.Features.CancelJob;

/// <summary>
/// キャンセル機能の DI 登録。
/// </summary>
public static class CancelJobServiceCollectionExtensions
{
    /// <summary>
    /// キャンセル機能に必要なサービスを登録する。
    /// </summary>
    /// <remarks>
    /// 機能ごとに登録の口を分けてあるのは、その機能が何を必要とするかを機能の側に置くため。
    /// ただし機能を足すときは、この形のファイルに加えて JobFeaturesServiceCollectionExtensions と
    /// JobFeaturesEndpointRouteBuilderExtensions にも 1 行ずつ足す必要がある。
    /// まとめて入れたい場合は <see cref="JobFeaturesServiceCollectionExtensions.AddJobFeatures"/> を使う。
    /// <see cref="Execution.IRunningJobRegistry"/> はここでは登録しない。実体は実行エンジンが
    /// 持っているもので、<see cref="Execution.JobExecutionServiceCollectionExtensions.AddJobExecution"/>
    /// が入れる同じインスタンスでなければキャンセルがハンドラに届かないため。
    /// ここで別に登録すると、取り違えても解決だけは成功してしまう。
    /// </remarks>
    public static IServiceCollection AddCancelJob(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<CancelJobHandler>();

        return services;
    }
}
