using System.Net;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// 画面の入口が結線されていることのテスト。表示の中身は確かめない
/// （それはハンドラのテストと目視の領分で、HTML の文字列比較は壊れやすいだけ）。
/// </summary>
public sealed class HomePageTests : IDisposable
{
    private readonly JobsWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task 画面のルートは200を返す()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
