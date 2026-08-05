using Microsoft.Extensions.DependencyInjection;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// 変更通知の配管全体の結合テスト。API ホストの store への書き込みが、
/// SSE → 購読サービス → UI 側 JobChangeFeed の合図として届くことを確かめる。
/// </summary>
/// <remarks>
/// 購読サービスを実際に立て（SubscribeToChanges=true）、HttpMessageHandler だけを
/// API ホストの TestServer へ差し替える。SSE の中身（行の形式や keep-alive）は
/// サービス単体のテストの領分で、ここでは端から端まで繋がることだけを見る。
/// </remarks>
public sealed class JobEventsIntegrationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task storeへの書き込みがSSE越しにUIの合図として届く()
    {
        using SemaphoreSlim signals = new(0);
        using UiHostFactory factory = new(subscribeToChanges: true, onChanged: () => signals.Release());

        // Services に触れた時点でホストが立ち、購読サービスが SSE へ繋ぎに行く。
        // 最初の合図は「接続直後の 1 回」。これが届いた時点で、SSE エンドポイントは
        // 応答開始より前に購読を確立している（JobEventsEndpoint 参照）ので、
        // 以後の書き込みが配信されないことはない。
        _ = factory.Services;
        Assert.True(await signals.WaitAsync(TimeSpan.FromSeconds(10)), "接続直後の合図が届かない。");

        IJobStore store = factory.Api.Services.GetRequiredService<IJobStore>();
        await store.AddAsync(
            Job.Create(JobId.From("job-sse-ui-1"), "通知される Job", "subtasks", "1 1", T0),
            CancellationToken.None);

        Assert.True(await signals.WaitAsync(TimeSpan.FromSeconds(10)), "変更の合図が届かない。");
    }
}
