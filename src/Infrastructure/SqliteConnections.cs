using Microsoft.Data.Sqlite;

namespace Netsoft.Jobs.Infrastructure;

/// <summary>
/// SQLite 接続のイディオムの共有。接続文字列の組み立てと、開く処理の後始末。
/// </summary>
/// <remarks>
/// <see cref="SqliteJobStore"/> と <see cref="SqliteJobTraceContextStore"/> が
/// 逐語的に同じ 2 つの処理を持っていたので括った。接続の作法（呼び出しごとに開く・
/// プールに任せる）はストアごとに変えるものではなく、直すときは全ストアを一度に直したい。
/// </remarks>
internal static class SqliteConnections
{
    /// <summary>DB ファイルのパスから接続文字列を組み立てる。</summary>
    internal static string BuildConnectionString(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString();
    }

    /// <summary>接続を開いて返す。</summary>
    internal static async Task<SqliteConnection> OpenAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            // 開けなかった接続を握ったまま例外を投げると、プールへ返らずに滞留する。
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
