using Netsoft.Jobs.Ui;
using Netsoft.Jobs.Ui.Components;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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
