using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 実行エンジンの DI 登録。
/// </summary>
public static class JobExecutionServiceCollectionExtensions
{
    /// <summary>
    /// 実行エンジンと、標準で用意している Job のハンドラを登録する。
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

        // 計装が使う IMeterFactory の供給元。AddMetrics は TryAdd の集まりなので、
        // ホスト（WebApplicationBuilder）が既に入れていても二重にならない。
        services.AddMetrics();
        services.TryAddSingleton<JobExecutionInstrumentation>();

        // 登録時 trace context の置き場は no-op を既定にする。観測は任意の関心で、
        // 必須依存にしない。ホストが差し替えなくても（保存は捨てる・検索は null のまま）
        // 全機能が動く。Web は SQLite のアダプタでこの登録を置き換える。
        services.TryAddSingleton<IJobTraceContextStore, NullJobTraceContextStore>();

        // Job の種類を増やすときは、この形で IJobHandler を 1 行足す。
        // TryAddEnumerable にしているのは、同じハンドラを二重に登録しても増えないようにするため。
        // ISubTaskStore の実装を選ぶのはホストの関心（IJobStore と同じ）。ここでは登録しない。
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, SubTaskJobHandler>());

        services.TryAddSingleton<JobHandlerRegistry>();
        services.TryAddSingleton<RunningJobRegistry>();

        // 合図はエンジンが待つものと書き込み側（ホストの結線）が鳴らすものが
        // 同じ 1 つでなければ意味を成さないので、これも Singleton。
        services.TryAddSingleton<JobQueueSignal>();
        services.TryAddSingleton<IRunningJobRegistry>(provider => provider.GetRequiredService<RunningJobRegistry>());
        // エンジンそのものは登録しない。生成に await（起動時復旧）が要るのに対して
        // GetRequiredService は同期なので、サービスにできるのはファクトリまで。
        services.TryAddSingleton<JobExecutionEngineFactory>();

        return services;
    }
}
