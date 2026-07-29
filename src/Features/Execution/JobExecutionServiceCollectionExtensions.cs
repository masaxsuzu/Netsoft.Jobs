using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 実行エンジンの DI 登録。
/// </summary>
public static class JobExecutionServiceCollectionExtensions
{
    /// <summary>
    /// 実行エンジンとデモ Job を登録する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// すべて Singleton にする。エンジンは起動時復旧を済ませたかどうかを、
    /// <see cref="RunningJobRegistry"/> は実行中の Job をプロセス全体で 1 つ持つ必要があるため。
    /// スコープごとに作られると、キャンセル要求が別のインスタンスに届いて何も起きない。
    /// </para>
    /// <para>
    /// このため <see cref="Domain.IJobStore"/> も Singleton で登録すること
    /// （Scoped だと Singleton のエンジンに閉じ込められる）。
    /// エンジンを常駐させる殻（<c>BackgroundService</c>）は Web 側が用意する。
    /// </para>
    /// </remarks>
    public static IServiceCollection AddJobExecution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Job の種類を増やすときは、この形で IJobHandler を 1 行足す。
        // TryAddEnumerable にしているのは、同じハンドラを二重に登録しても増えないようにするため。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, DemoJobHandler>());

        services.TryAddSingleton<JobHandlerRegistry>();
        services.TryAddSingleton<RunningJobRegistry>();
        services.TryAddSingleton<IRunningJobRegistry>(provider => provider.GetRequiredService<RunningJobRegistry>());
        services.TryAddSingleton<JobExecutionEngine>();

        return services;
    }
}
