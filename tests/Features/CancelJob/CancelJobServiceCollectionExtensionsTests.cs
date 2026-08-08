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
        using TemporaryJobStore store = new();
        ServiceCollection services = new();

        // Web 側がやることと同じ。IJobStore の実装を選ぶのは Features の関心ではない。
        // TimeProvider は機能をまたいで共有するもので、AddCancelJob は自分のハンドラしか
        // 登録しない（登録は AddJobFeatures がまとめて行う）。
        // logging も同じ扱い。ホストが既定で入れるものなので、ここではテストが入れる。
        services.AddLogging();
        services.AddSingleton<IJobStore>(store);
        services.AddSingleton(TimeProvider.System);
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
        using TemporaryJobStore store = new();
        using TemporarySubTaskStore subTasks = new();
        ServiceCollection services = new();

        services.AddLogging();
        services.AddSingleton<IJobStore>(store);
        services.AddSingleton<ISubTaskStore>(subTasks);

        // 監査ログの置き場は AddJobFeatures が持たない（既定を置くと、登録し忘れたホストで
        // 監査が黙って捨てられる）。ここで入れておかないと組み立ての検証で落ちる ──
        // その落ち方こそが「忘れたら起動できない」という保証になっている。
        services.AddSingleton<IAuditLogStore>(new RecordingAuditLogStore());

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
