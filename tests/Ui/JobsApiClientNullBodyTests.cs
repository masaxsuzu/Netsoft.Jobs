using System.Net;
using System.Text;

using Netsoft.Jobs.Contracts;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// API の応答本文が JSON の <c>null</c> だったときの写し方。
/// </summary>
/// <remarks>
/// <para>
/// 本物の API ホストはこの応答を返さないので、<see cref="JobsApiClientTests"/> の
/// 結合（in-process ホスト直結）では作れない。ここだけ偽のハンドラで応答を作る。
/// プロセスが分かれた以上、途中の何か（プロキシ・改修中のサーバ）が契約を破る余地は
/// 常にあり、そのとき画面へ何を渡すかはクライアントだけの関心。
/// </para>
/// <para>
/// 方針は「一覧の null は空として写し、単体の Job の null は例外にする」。
/// 空の一覧は画面がそのまま表示できる正常な形だが、「登録できたのに Job が無い」は
/// 画面に出せる形が無く、無理に作ると嘘の表示になるため。
/// </para>
/// </remarks>
public sealed class JobsApiClientNullBodyTests
{
    [Fact]
    public async Task Job一覧のnull本文は空の一覧として写る()
    {
        JobsApiClient client = ClientReturning(HttpStatusCode.OK);

        Assert.Empty(await client.ListJobsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 種類一覧のnull本文は空の一覧として写る()
    {
        JobsApiClient client = ClientReturning(HttpStatusCode.OK);

        Assert.Empty(await client.ListJobTypesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task 検証エラーの本文がnullでも項目なしのエラーとして写る()
    {
        JobsApiClient client = ClientReturning(HttpStatusCode.BadRequest);

        RegisterJobResponse response = await client.RegisterJobAsync(
            "夜間バッチ", "subtasks", "1 1", CancellationToken.None);

        // 失敗であることは保ちつつ、項目別の内訳は無い。ここで例外にしないのは、
        // 400 が「入力を直せば通る」contract どおりの失敗であることに変わりないため。
        Assert.False(response.IsSuccess);
        Assert.Empty(response.Errors);
    }

    [Fact]
    public async Task 登録成功の本文がnullなら例外になる()
    {
        JobsApiClient client = ClientReturning(HttpStatusCode.OK);

        // 200 なのに Job が無いのは契約違反で、画面に出せる形が無い。
        // 空の Job をでっち上げると「登録できたように見えるが ID の無い行」という嘘になる。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RegisterJobAsync("夜間バッチ", "subtasks", "1 1", CancellationToken.None));
    }

    /// <summary>どのリクエストにも指定ステータスと <c>null</c> 本文で応える偽のクライアント。</summary>
    private static JobsApiClient ClientReturning(HttpStatusCode statusCode) =>
        new(new HttpClient(new NullBodyHandler(statusCode))
        {
            BaseAddress = new Uri("http://localhost"),
        });

    private sealed class NullBodyHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json"),
            });
    }
}
