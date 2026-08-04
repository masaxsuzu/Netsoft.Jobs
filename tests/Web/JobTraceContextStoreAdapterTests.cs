using Microsoft.Extensions.DependencyInjection;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Infrastructure;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// trace context の port（Features）と SQLite 実装（Infrastructure）を繋ぐアダプタのテスト。
/// SQL 本体の検証は tests/Infrastructure の SqliteJobTraceContextStoreTests の領分で、
/// ここで見るのは Web の関心である結線だけ。
/// </summary>
public sealed class JobTraceContextStoreAdapterTests : IDisposable
{
    private readonly JobsWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void ホストは既定のNoOpをアダプタで置き換える()
    {
        // Features の既定（no-op）のままだと、保存が黙って捨てられて Link が永遠に付かない。
        // 置き換えの登録が消えても、ここで気づける。
        Assert.IsType<JobTraceContextStoreAdapter>(
            _factory.Services.GetRequiredService<IJobTraceContextStore>());
    }

    [Fact]
    public async Task アダプタ越しの保存はSQLiteの実装から読める()
    {
        // 委譲先がホストに登録された具象と同じ 1 つであること。別インスタンスへ委譲すると、
        // 登録側が保存した traceparent を実行側が見つけられない。
        const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        IJobTraceContextStore port = _factory.Services.GetRequiredService<IJobTraceContextStore>();
        await port.SaveAsync(JobId.From("job-1"), TraceParent, CancellationToken.None);

        SqliteJobTraceContextStore concrete =
            _factory.Services.GetRequiredService<SqliteJobTraceContextStore>();
        Assert.Equal(TraceParent, await concrete.FindAsync(JobId.From("job-1"), CancellationToken.None));
        Assert.Equal(TraceParent, await port.FindAsync(JobId.From("job-1"), CancellationToken.None));
    }
}
