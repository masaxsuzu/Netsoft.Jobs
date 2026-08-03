using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.Execution;

/// <summary>
/// DI 登録のテスト。Web 側が組み立てたときに解決できることをここで確かめる。
/// </summary>
public sealed class JobExecutionServiceCollectionExtensionsTests : IDisposable
{
    private readonly TemporaryJobStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void 実行エンジンをDIから解決できる()
    {
        using ServiceProvider provider = BuildProvider();

        JobExecutionEngine engine = provider.GetRequiredService<JobExecutionEngine>();

        // 起動時復旧を済ませたかどうかを覚えているので、毎回作られては困る。
        Assert.Same(engine, provider.GetRequiredService<JobExecutionEngine>());
    }

    [Fact]
    public void キャンセルの口はエンジンが使う実体と同じインスタンスになる()
    {
        // 別インスタンスだと、キャンセル要求が実行中のハンドラに届かず何も起きない。
        using ServiceProvider provider = BuildProvider();

        Assert.Same(
            provider.GetRequiredService<RunningJobRegistry>(),
            provider.GetRequiredService<IRunningJobRegistry>());
    }

    [Fact]
    public void デモJobのハンドラが登録される()
    {
        using ServiceProvider provider = BuildProvider();

        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();

        Assert.IsType<DemoJobHandler>(registry.Find(DemoJobHandler.DemoJobType));
    }

    [Fact]
    public void 書庫Jobのハンドラが登録される()
    {
        using ServiceProvider provider = BuildProvider();

        JobHandlerRegistry registry = provider.GetRequiredService<JobHandlerRegistry>();

        Assert.IsType<ArchiveJobHandler>(registry.Find(ArchiveJobHandler.ArchiveJobType));
    }

    private ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();

        // Web 側がやることと同じ。IJobStore の実装を選ぶのは Features の関心ではない。
        services.AddLogging();
        services.AddSingleton<IJobStore>(_store);

        services.AddJobExecution();

        // ValidateOnBuild で、Singleton が Scoped を抱え込む登録ミスをここで落とせる。
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }
}
