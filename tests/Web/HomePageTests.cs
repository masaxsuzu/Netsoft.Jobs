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

    /// <summary>
    /// このスクリプトが配信されないと、画面は静的 HTML のまま一度も対話モードに
    /// ならず、フォームもボタンも効かない。実際に Production 起動で 404 になり、
    /// 「ルートが 200 を返す」だけのテストでは検出できなかった回帰をここで塞ぐ。
    /// 中身の長さまで見るのは、壊れた配信経路が「200 だが本文 0 バイト」を
    /// 返すことがあり、ステータスコードだけでは素通りするため。
    /// </summary>
    [Fact]
    public async Task Blazorのスクリプトが中身つきで配信される()
    {
        using HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/_framework/blazor.web.js");
        byte[] body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.Length > 10_000, $"blazor.web.js が {body.Length} バイトしかありません。空配信の回帰です。");
    }
}
