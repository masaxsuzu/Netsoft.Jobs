using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.QueryJobs;
using Netsoft.Jobs.Features.QueryJobTypes;
using Netsoft.Jobs.Features.RegisterJob;

namespace Netsoft.Jobs.Features;

/// <summary>
/// Features 全体の DI 登録。Web 側の入口。
/// </summary>
public static class JobFeaturesServiceCollectionExtensions
{
    /// <summary>
    /// すべての機能と、機能が共通で使うサービスを登録する。
    /// </summary>
    /// <remarks>
    /// <see cref="Domain.IJobStore"/> の実装はここでは登録しない。どの永続化を使うかは
    /// Features の関心ではなく、Web 側が選んで登録する。
    /// 共通のものを TryAdd で入れているのは、Web 側やテストが先に自前の実装
    /// （固定した <see cref="TimeProvider"/> など）を登録していればそちらを勝たせるため。
    /// 機能が増えたらここに AddXxx を 1 行足す。
    /// </remarks>
    public static IServiceCollection AddJobFeatures(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJobIdFactory, GuidV7JobIdFactory>();

        services.AddRegisterJob();
        services.AddJobExecution();
        services.AddQueryJobs();

        // 種類の一覧は実行エンジンの JobHandlerRegistry を使う。上の AddJobExecution が
        // 入れた実体をそのまま解決させたいので、この順に並べておく。
        services.AddQueryJobTypes();

        // キャンセルは実行エンジンの IRunningJobRegistry を使う。上の AddJobExecution が
        // 入れた実体をそのまま解決させたいので、この順に並べておく。
        services.AddCancelJob();

        return services;
    }
}
