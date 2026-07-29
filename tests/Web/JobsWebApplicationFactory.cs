using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// テスト用のホスト。DB を使い捨ての一時ファイルへ差し替え、実行エンジンを止める。
/// </summary>
/// <remarks>
/// <para>
/// エンジンを止めるのは設定（<c>Jobs:RunExecutionEngine</c>）で行う。Program が
/// この設定を見て HostedService を登録しないので、テスト側でサービスを抜き取る細工が要らない。
/// 止めないと「待機中のまま」を前提にした検証をエンジンが勝手に進めて、結果が時間に左右される。
/// </para>
/// <para>
/// DI の検証（ValidateOnBuild / ValidateScopes）はここで常に有効にする。
/// 登録漏れが特定のテストではなく、ホストを立てるすべてのテストで露見する。
/// </para>
/// </remarks>
internal sealed class JobsWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _directory;

    public JobsWebApplicationFactory()
    {
        // テストは並行して走るので、ディレクトリごと分けて衝突を避ける。
        _directory = Path.Combine(Path.GetTempPath(), "netsoft-jobs-web-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Jobs:DatabasePath", Path.Combine(_directory, "jobs.db"));
        builder.UseSetting("Jobs:RunExecutionEngine", "false");

        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        // プールが接続を握ったままだとファイルが開きっぱなしになり、削除に失敗しうる。
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストの結果を変えたくない。一時ディレクトリはいずれ OS が回収する。
        }
    }
}
