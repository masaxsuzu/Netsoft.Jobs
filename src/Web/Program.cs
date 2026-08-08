using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Infrastructure;
using Netsoft.Jobs.Web;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

JobsOptions options = builder.Configuration.GetSection(JobsOptions.SectionName).Get<JobsOptions>()
    ?? new JobsOptions();
builder.Services.AddSingleton(options);

builder.Services.AddJobFeatures();

// 相対パスはコンテンツルート基準にする。カレントディレクトリ基準だと
// dotnet run をどこから叩いたかで別の DB ができてしまう。
// 絶対パスなら Path.Combine がそのまま返すので、ルート判定の分岐は要らない。
string databasePath = Path.Combine(builder.Environment.ContentRootPath, options.DatabasePath);

// IJobStore は Singleton で登録する。実行エンジンが Singleton なので、
// Scoped にするとエンジンに捕まった 1 つが生き続けて意味を成さない
// （JobExecutionServiceCollectionExtensions の注記を参照）。
// 素の SqliteJobStore も登録しておくのは、起動時初期化が InitializeAsync を
// 具象型でしか呼べないため。IJobStore は変更通知のデコレータで包んで公開する。
builder.Services.AddSingleton(new SqliteJobStore(databasePath));
builder.Services.AddSingleton<JobChangeFeed>();
builder.Services.AddSingleton<IJobStore>(provider => new NotifyingJobStore(
    provider.GetRequiredService<SqliteJobStore>(),
    provider.GetRequiredService<JobChangeFeed>()));

// 登録時 trace context の置き場。AddJobFeatures が TryAdd した no-op より後に登録するので、
// 単一解決はこちら（最後の登録）が勝つ。port への結線はアダプタが担う。
// サブタスクと trace context も Jobs と同じ DB ファイルに載せる。
builder.Services.AddSingleton(new SqliteSubTaskStore(databasePath));
builder.Services.AddSingleton<ISubTaskStore>(provider => new NotifyingSubTaskStore(
    provider.GetRequiredService<SqliteSubTaskStore>(),
    provider.GetRequiredService<JobChangeFeed>()));

// 監査ログも同じ DB ファイル。デコレータは掛けない ── 変更通知は「Job が変わった」を
// 運ぶもので、監査ログが増えても画面の一覧は変わらないため。
builder.Services.AddSingleton(new SqliteAuditLogStore(databasePath));
builder.Services.AddSingleton<IAuditLogStore>(
    provider => provider.GetRequiredService<SqliteAuditLogStore>());

builder.Services.AddSingleton(new SqliteJobTraceContextStore(databasePath));
builder.Services.AddSingleton<IJobTraceContextStore>(
    provider => new JobTraceContextStoreAdapter(provider.GetRequiredService<SqliteJobTraceContextStore>()));

// 設定で止められるようにしてある。テストがエンジンを止めて、
// 「待機中のまま」のような状態を前提にした検証を安定して行うため。
if (options.RunExecutionEngine)
{
    builder.Services.AddHostedService<JobExecutionHostedService>();
}

// 購読はホストの寿命と同じだけ続ける。using で受けるのは、
// AddActivityListener が解除されないと購読が漏れるため。
using IDisposable? tracing = ConsoleActivityTracing.Enable(options.TraceToConsole, Console.Out);

WebApplication app = builder.Build();

// 画面は持たない。画面は別プロセスの UI ホスト（src/Ui）にあり、
// ここは API + 実行エンジンのホストとして HTTP の口だけを開ける。
app.MapJobFeatures();
app.MapJobEvents();

// スキーマの用意はホストの起動（= 実行エンジンの開始）より前に済ませる。
// エンジンは起動時復旧で store を読むので、逆順だとテーブルが無いまま読みに行く。
await app.Services.GetRequiredService<SqliteJobStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<SqliteSubTaskStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<SqliteJobTraceContextStore>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<SqliteAuditLogStore>().InitializeAsync(CancellationToken.None);

await app.RunAsync();

/// <summary>
/// テスト（WebApplicationFactory）がホストの入口を参照するための宣言。
/// </summary>
public partial class Program;
