using Netsoft.Jobs.Ui;
using Netsoft.Jobs.Ui.Components;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// 既定のポートは 5100。API ホスト（Web）が既定の 5000 で待つので、素の起動同士で共存できる。
// appsettings.json の Urls で置くと環境変数（ASPNETCORE_URLS）より優先されてしまい、
// 上書きの手段が --urls 引数だけになる（実際に環境変数が効かず気づいた）。
// コードで「何も指定が無いときだけ」置けば、環境変数と --urls のどちらでも上書きできる。
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://localhost:5100");
}

UiOptions options = builder.Configuration.GetSection(UiOptions.SectionName).Get<UiOptions>()
    ?? new UiOptions();
builder.Services.AddSingleton(options);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// TimeProvider は購読サービスが間隔（再接続・ポーリング）を計るのに使う。
// テストは自前の時計を先に登録して針を進める。
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JobChangeFeed>();

builder.Services.AddHttpClient<JobsApiClient>(JobsApiClient.HttpClientName, client =>
    client.BaseAddress = new Uri(options.ApiBaseUrl));

// 画面の状態と操作。回路（サーキット）ごとに 1 つで、プリレンダリングと対話モードは
// 別のスコープなので状態は引き継がれない（画面は入口で読み直す作りになっている）。
builder.Services.AddScoped<JobBoard>();

// SSE 用のクライアントは API 用と分ける。SSE の応答は切断まで終わらないので
// Timeout を無限にする必要があり、通常の API 呼び出しにその設定を波及させたくない。
builder.Services.AddHttpClient(JobEventsSubscriptionService.HttpClientName, client =>
{
    client.BaseAddress = new Uri(options.ApiBaseUrl);
    client.Timeout = Timeout.InfiniteTimeSpan;
});

// 設定で止められるようにしてある。テストが購読を止めて、
// 合図の発火回数を前提にした検証を安定して行うため（Web の実行エンジンと同じ理屈）。
if (options.SubscribeToChanges)
{
    builder.Services.AddHostedService<JobEventsSubscriptionService>();
}

WebApplication app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
