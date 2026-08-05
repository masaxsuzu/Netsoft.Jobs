using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// 変更通知 SSE の結合テスト。実際のホストを通して、別プロセスの UI が見るものを確かめる。
/// </summary>
/// <remarks>
/// 応答は読み終わらない（切断まで流れ続ける）ので、ヘッダだけ受け取って body は
/// 自分のペースで読む。イベントの到着待ちは ReadLineAsync の完了そのもので、
/// 時間を測る待ちが要らない。切断とその後始末は JobEventsEndpointTests の領分。
/// </remarks>
public sealed class JobEventsApiTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly JobsWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public JobEventsApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task 接続するとイベントストリームとして応答が始まる()
    {
        using HttpResponseMessage response = await ConnectAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task storeへの書き込みでchangedイベントが届く()
    {
        using HttpResponseMessage response = await ConnectAsync();
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync());

        // 購読はヘッダ送出より前に確立している（JobEventsEndpoint 参照）ので、
        // 応答が返った後の書き込みが配信されないことはない。
        IJobStore store = _factory.Services.GetRequiredService<IJobStore>();
        await store.AddAsync(
            Job.Create(JobId.From("job-sse-1"), "通知される Job", "subtasks", "1 1", T0),
            CancellationToken.None);

        Assert.Equal("data: changed", await ReadNextEventAsync(reader));

        // イベントは空行で終端する。SSE では空行が来るまで配送されないので、
        // これが落ちると標準の EventSource から見て通知が一切届かなくなる
        // （行を読むだけの自前クライアントでは気づけない。実際に変異検査で素通りした）。
        Assert.Equal(string.Empty, await reader.ReadLineAsync());
    }

    /// <summary>
    /// 連続した発火でも合図は少なくとも 1 回届く。合体して回数が減るのは正しい
    /// （合図はペイロードを持たないので情報は失われない）が、0 回は変更が伝わっていない。
    /// </summary>
    [Fact]
    public async Task 連続した発火でも合図は少なくとも1回届く()
    {
        using HttpResponseMessage response = await ConnectAsync();
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync());

        JobChangeFeed feed = _factory.Services.GetRequiredService<JobChangeFeed>();
        for (int i = 0; i < 5; i++)
        {
            feed.Publish();
        }

        Assert.Equal("data: changed", await ReadNextEventAsync(reader));
    }

    /// <summary>
    /// ヘッダだけ受け取って接続を開いたままにする。既定の completion option だと
    /// body の終わりを待つが、SSE の body は切断まで終わらない。
    /// </summary>
    private async Task<HttpResponseMessage> ConnectAsync() =>
        await _client.GetAsync("/api/jobs/events", HttpCompletionOption.ResponseHeadersRead);

    /// <summary>コメント行や空行を読み飛ばし、次のイベントの data 行を返す。</summary>
    private static async Task<string?> ReadNextEventAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return null;
    }
}
