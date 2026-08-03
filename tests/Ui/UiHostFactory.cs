using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// テスト用の UI ホスト。自前の API ホスト（<see cref="ApiHostFactory"/>）を抱え、
/// UI の HttpClient の HttpMessageHandler をその TestServer のハンドラへ差し替える。
/// 2 つの in-process ホストが実 HTTP なしで繋がり、「UI の操作が API 経由で保存される」
/// を決定的に検証できる。
/// </summary>
/// <remarks>
/// <para>
/// 型引数が <see cref="UiOptions"/> なのは Program の同名衝突のため
/// （<see cref="ApiHostFactory"/> の注記を参照）。
/// </para>
/// <para>
/// 変更通知の購読サービスは既定で止める。実時間の間隔で勝手に合図を発火されると、
/// 発火回数を前提にした検証が時間に左右されるため（Web の実行エンジンと同じ理屈）。
/// SSE の配管そのものを見るテストだけが true で立てる。
/// </para>
/// </remarks>
internal sealed class UiHostFactory : WebApplicationFactory<UiOptions>
{
    private readonly bool _subscribeToChanges;
    private readonly Action? _onChanged;

    /// <param name="subscribeToChanges">変更通知の購読サービスを立てるか。既定は止める。</param>
    /// <param name="onChanged">
    /// UI 側 JobChangeFeed への購読をホストの起動前に仕込む口。テストが起動後に購読すると、
    /// 購読サービスの「接続直後の合図」が先に発火していて取りこぼす競合があるため、
    /// feed の生成時（誰かが使うより前）に繋いでおく。
    /// </param>
    public UiHostFactory(bool subscribeToChanges = false, Action? onChanged = null)
    {
        _subscribeToChanges = subscribeToChanges;
        _onChanged = onChanged;
    }

    /// <summary>繋ぎ先の API ホスト。テストは store の準備や検証にここを使う。</summary>
    public ApiHostFactory Api { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 本番相当の環境でテストする。Development だけで成立する構成 (静的アセットの
        // 扱いなど) の欠陥が素通りするため。実際に blazor.web.js の 404 が
        // Development 既定のテストでは検出できなかった（Web に画面があった頃の実績）。
        builder.UseEnvironment("Production");

        builder.UseSetting("Ui:SubscribeToChanges", _subscribeToChanges ? "true" : "false");

        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        builder.ConfigureTestServices(services =>
        {
            // 名前付きクライアントの一次ハンドラを API ホストの TestServer へ向ける。
            // BaseAddress（appsettings の ApiBaseUrl）はそのままだが、TestServer の
            // ハンドラはホスト名を見ずにパスで処理するので届く。
            services.AddHttpClient(JobsApiClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Api.Server.CreateHandler());
            services.AddHttpClient(JobEventsSubscriptionService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Api.Server.CreateHandler());

            if (_onChanged is not null)
            {
                // Program の登録より後に追加されるので、こちらの JobChangeFeed が勝つ。
                Action onChanged = _onChanged;
                services.AddSingleton(_ =>
                {
                    JobChangeFeed feed = new();
                    feed.Changed += onChanged;
                    return feed;
                });
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            Api.Dispose();
        }
    }
}
