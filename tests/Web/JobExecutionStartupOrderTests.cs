namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// 起動時復旧が HTTP の受付より前に終わっていることの検証。
/// </summary>
/// <remarks>
/// <para>
/// 復旧は「ハンドラが 1 つも走っていない」前提で非終端の Job を Failed へ畳む。
/// <b>API はその前提の外に居る別の書き手</b>で、受付が先に開くと、利用者の編集
/// （Running のまま版だけ進む）と復旧の書き戻しがぶつかる。負けた Job は Running のまま
/// 誰にも閉じられない ── エンジンは Queued しか拾わず、キャンセルは Cancelling、
/// 一時停止は Pausing で固着し、復旧はこのプロセスで二度と走らない。
/// </para>
/// <para>
/// 順序はホストのライフサイクルが決めるので、単体では確かめられない
/// （<c>Task.Yield</c> を外すだけでは閉じない。Kestrel の HostedService の方が
/// 先に登録されるため）。ホストを本当に立ち上げて、復旧を途中で止め、
/// その間に受付が始まっていないことを見る。
/// </para>
/// </remarks>
public sealed class JobExecutionStartupOrderTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task 起動時復旧が終わるまでHTTPの受付は始まらない()
    {
        using RecoveryGateWebApplicationFactory factory = new();

        // CreateClient がホストを起こす。復旧で止まるので、これ自体が返らない。
        Task<HttpClient> starting = Task.Run(factory.CreateClient);

        await factory.RecoveryEntered.WaitAsync(WaitLimit);

        // 復旧の最中。ここで起動が完了していたら、受付が先に開いている。
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(starting.IsCompleted, "起動時復旧が終わる前にホストの起動が完了しました。");

        factory.ReleaseRecovery();

        using HttpClient client = await starting.WaitAsync(WaitLimit);
        using HttpResponseMessage response = await client.GetAsync("/api/jobs");

        response.EnsureSuccessStatusCode();
    }
}
