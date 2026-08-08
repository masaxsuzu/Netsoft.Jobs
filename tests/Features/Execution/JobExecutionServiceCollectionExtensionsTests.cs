using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Audit;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// DI 登録のテスト。Web 側が組み立てたときに解決できることをここで確かめる。
/// </summary>
public sealed class JobExecutionServiceCollectionExtensionsTests : IDisposable
{
    private readonly TemporaryJobStore _store = new();
    private readonly TemporarySubTaskStore _subTaskStore = new();

    public void Dispose()
    {
        _subTaskStore.Dispose();
        _store.Dispose();
    }

    /// <summary>
    /// エンジンではなくファクトリが解決できること。
    /// </summary>
    /// <remarks>
    /// エンジンは起動時復旧をやり切ってからでないと手に入らず、生成に await が要る。
    /// <c>GetRequiredService</c> は同期なので、サービスにできるのはファクトリまで
    /// （<see cref="JobExecutionEngineFactory"/> の注記を参照）。
    /// </remarks>
    [Fact]
    public void 実行エンジンのファクトリをDIから解決できる()
    {
        using ServiceProvider provider = BuildProvider();

        JobExecutionEngineFactory factory = provider.GetRequiredService<JobExecutionEngineFactory>();

        // 依存はエンジンと同じ実体を指す必要があるので、毎回作られては困る。
        Assert.Same(factory, provider.GetRequiredService<JobExecutionEngineFactory>());
    }

    [Fact]
    public async Task ファクトリが起こしたエンジンは復旧を済ませている()
    {
        Job leftover = Job.Create(JobId.From("job-1"), "残骸", "demo", string.Empty, DateTimeOffset.UnixEpoch);
        await _store.AddAsync(leftover, CancellationToken.None);
        Assert.True(leftover.Apply(JobTrigger.Start, DateTimeOffset.UnixEpoch).IsAllowed);
        Assert.True(await _store.UpdateAsync(leftover, CancellationToken.None));

        using ServiceProvider provider = BuildProvider();

        _ = await provider.GetRequiredService<JobExecutionEngineFactory>().StartAsync(CancellationToken.None);

        Job? job = await _store.FindAsync(JobId.From("job-1"), CancellationToken.None);
        Assert.Equal(JobStatus.Failed, job?.Status);
    }

    [Fact]
    public void 計装をDIから解決できる()
    {
        using ServiceProvider provider = BuildProvider();

        JobExecutionInstrumentation instrumentation = provider.GetRequiredService<JobExecutionInstrumentation>();

        // Meter と ActivitySource がプロセスに 1 組であってほしいので Singleton。
        Assert.Same(instrumentation, provider.GetRequiredService<JobExecutionInstrumentation>());
    }

    [Fact]
    public async Task 既定のTraceContextの置き場は保存を捨て検索はNullを返す()
    {
        // ホスト（Web）が差し替えなくても登録と実行が動くための no-op。
        using ServiceProvider provider = BuildProvider();

        IJobTraceContextStore traceContexts = provider.GetRequiredService<IJobTraceContextStore>();
        Assert.IsType<NullJobTraceContextStore>(traceContexts);

        await traceContexts.SaveAsync(JobId.From("job-1"), "00-traceparent", CancellationToken.None);
        Assert.Null(await traceContexts.FindAsync(JobId.From("job-1"), CancellationToken.None));
    }

    [Fact]
    public void サブタスクJobのハンドラが登録される()
    {
        using ServiceProvider provider = BuildProvider();

        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();

        Assert.IsType<SubTaskJobHandler>(registry.Find(SubTaskJobHandler.SubTaskJobType));
    }

    private ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // Web 側がやることと同じ。IJobStore / ISubTaskStore の実装を選ぶのは Features の関心ではない。
        services.AddLogging();
        services.AddSingleton<IJobStore>(_store);
        services.AddSingleton<ISubTaskStore>(_subTaskStore);

        // エンジンは監査ログを書く（実行の開始・結末・起動時復旧）。置き場は AddJobExecution が
        // 持たないので、ここで入れる ── 入れ忘れたホストが組み立ての時点で落ちる形にしてある。
        services.AddSingleton<IAuditLogStore>(new RecordingAuditLogStore());
        services.AddSingleton<AuditRecorder>();

        services.AddJobExecution();

        // ValidateOnBuild で、Singleton が Scoped を抱え込む登録ミスをここで落とせる。
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }
}
