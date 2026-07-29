using Microsoft.Extensions.DependencyInjection;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.QueryJobs;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.QueryJobs;

/// <summary>
/// DI 登録のテスト。Web 側が組み立てたときに解決できることをここで確かめる。
/// </summary>
public sealed class QueryJobsServiceCollectionExtensionsTests
{
    [Fact]
    public void 読み出しのハンドラをDIから解決できる()
    {
        ServiceCollection services = new();

        // Web 側がやることと同じ。IJobStore の実装を選ぶのは Features の関心ではない。
        services.AddSingleton<IJobStore>(new InMemoryJobStore());
        services.AddQueryJobs();

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        using IServiceScope scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<QueryJobsHandler>());
    }
}
