using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Netsoft.Jobs.Ui.Tests;

/// <summary>
/// UI の結合テストが繋ぐ先の API ホスト（Web）。DB を使い捨ての一時ファイルへ
/// 差し替え、実行エンジンを止める。tests/Web の JobsWebApplicationFactory と同じ構成。
/// </summary>
/// <remarks>
/// <para>
/// 型引数がホストの Program ではなく <see cref="Web.JobsOptions"/> なのは、Web と Ui の
/// Program が同名でどちらもグローバル名前空間に居るため。両方を参照するこの
/// アセンブリからは型名で区別できないので、対象アセンブリ内の代表的な公開型を
/// 目印にする（WebApplicationFactory は型が属するアセンブリしか見ない）。
/// </para>
/// <para>
/// エンジンを止めるのは、UI 側のテストが「登録した Job は Queued のまま」を
/// 前提にするため。DI の検証（ValidateOnBuild / ValidateScopes）は常に有効にして、
/// 登録漏れがホストを立てるすべてのテストで露見するようにする。
/// </para>
/// </remarks>
internal sealed class ApiHostFactory : WebApplicationFactory<Web.JobsOptions>
{
    private readonly string _directory;

    public ApiHostFactory()
    {
        // テストは並行して走るので、ディレクトリごと分けて衝突を避ける。
        _directory = Path.Combine(Path.GetTempPath(), "netsoft-jobs-ui-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 本番相当の環境でテストする。Development だけで成立する構成の欠陥を素通ししない。
        builder.UseEnvironment("Production");

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
        // 閉じるのは自分の DB のプールだけ。ClearAllPools はプロセス全域に効き、
        // 並列で走っている他のテストの接続まで破棄してフレークを起こす（実際に起きた）。
        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "jobs.db") }.ToString());
        SqliteConnection.ClearPool(connection);

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
