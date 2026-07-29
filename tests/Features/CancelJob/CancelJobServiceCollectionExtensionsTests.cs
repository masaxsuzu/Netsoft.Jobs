using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.CancelJob;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.CancelJob;

/// <summary>
/// DI 登録のテスト。Web 側が組み立てたときに解決できることをここで確かめる。
/// </summary>
public sealed class CancelJobServiceCollectionExtensionsTests
{
    [Fact]
    public void キャンセルのハンドラをDIから解決できる()
    {
        ServiceCollection services = new();

        // Web 側がやることと同じ。IJobStore の実装を選ぶのは Features の関心ではない。
        // TimeProvider と IRunningJobRegistry は機能をまたいで共有するもので、
        // AddCancelJob は自分のハンドラしか登録しない（登録は AddJobFeatures がまとめて行う）。
        services.AddSingleton<IJobStore>(new InMemoryJobStore());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IRunningJobRegistry>(new RecordingRunningJobRegistry(new CancelJobCallLog()));
        services.AddCancelJob();

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        using IServiceScope scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CancelJobHandler>());
    }

    [Fact]
    public void 全機能をまとめて登録してもキャンセルのハンドラを解決できる()
    {
        // キャンセルは実行エンジン側の IRunningJobRegistry に依存する。
        // AddCancelJob が自分で登録しない以上、まとめた登録に取りこぼしが無いことを見ておく。
        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton<IJobStore>(new InMemoryJobStore());
        services.AddJobFeatures();

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        using IServiceScope scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CancelJobHandler>());
    }
}
