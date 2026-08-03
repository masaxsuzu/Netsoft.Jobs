using System.Net;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// 画面の入口が結線されていることのテスト。表示の中身は確かめない
/// （それは API クライアントのテストと目視の領分で、HTML の文字列比較は壊れやすいだけ）。
/// プリレンダリングは一覧の取得で API を呼ぶので、これが通ること自体が
/// 「UI ホスト → HttpClient → API ホスト」の結線の証明にもなっている。
/// </summary>
public sealed class HomePageTests : IDisposable
{
    private readonly UiHostFactory _factory = new();

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

    /// <summary>
    /// GET / の応答はプリレンダリング出力そのもの。この段階のフォームは見えるだけで
    /// 配線されておらず、操作すると入力が失われる（登録ボタンは素のフォーム送信に
    /// なって再読み込み、入力は回路の最初の描画で巻き戻る）。だから回路が繋がるまで
    /// 入力とボタンは disabled で出す。これが外れる回帰をここで塞ぐ。
    /// HTML 全体の文字列比較はしない（壊れやすい）。対象のタグに disabled が
    /// 付いていることだけを、的を絞って確かめる。
    /// </summary>
    [Fact]
    public async Task プリレンダリング出力では登録フォームの入力とボタンがdisabledになっている()
    {
        using HttpClient client = _factory.CreateClient();

        string html = await client.GetStringAsync("/");

        foreach (string id in new[] { "job-name", "job-type", "job-parameters" })
        {
            string tag = TagContaining(html, $"id=\"{id}\"");
            Assert.Contains("disabled", tag);
        }

        string submit = TagContaining(html, "type=\"submit\"");
        Assert.Contains("disabled", submit);
    }

    /// <summary>
    /// 目印の属性を含むタグの開始タグ部分（&lt; から &gt; まで）を取り出す。
    /// 属性の順序や他の属性の有無に依存しないための最小限の切り出し。
    /// </summary>
    private static string TagContaining(string html, string marker)
    {
        int markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"{marker} を含む要素がプリレンダリング出力にありません。");

        int start = html.LastIndexOf('<', markerIndex);
        int end = html.IndexOf('>', markerIndex);
        return html[start..(end + 1)];
    }
}
