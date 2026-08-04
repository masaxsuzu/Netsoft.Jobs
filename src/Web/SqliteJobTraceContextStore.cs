using Microsoft.Data.Sqlite;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Web;

/// <summary>
/// SQLite による <see cref="IJobTraceContextStore"/> の実装。Jobs と同じ DB ファイルの別表に置く。
/// </summary>
/// <remarks>
/// <para>
/// port（<see cref="IJobTraceContextStore"/>）は Features にあり、Infrastructure は Features を
/// 参照できない（参照すると ASP.NET Core の FrameworkReference まで引きずる）。観測の結線は
/// ホストの関心なので、<see cref="NotifyingJobStore"/> と同じく Web のアダプタとして置く。
/// Microsoft.Data.Sqlite は Infrastructure から推移的に見えるので、参照の追加は要らない。
/// </para>
/// <para>
/// Jobs 行に traceparent の列を足さず別表にするのは、Domain（Job と IJobStore）に観測を
/// 知らせないため。messaging semantic conventions の「メッセージに trace context を同乗させる」を、
/// Job 行の外で実現している。
/// </para>
/// <para>
/// 行は削除しない。Jobs 行も消さない設計と揃える。1 行数十バイトで、Job と同じ速さでしか増えない。
/// </para>
/// <para>
/// 接続の作法（呼び出しごとに開く・パラメータバインド・ConfigureAwait(false)）は
/// <see cref="Infrastructure.SqliteJobStore"/> に倣う。
/// </para>
/// </remarks>
public sealed class SqliteJobTraceContextStore : IJobTraceContextStore
{
    private readonly string _connectionString;

    /// <param name="databasePath">DB ファイルのパス。Jobs と同じファイルを渡す。</param>
    public SqliteJobTraceContextStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString();
    }

    /// <summary>
    /// テーブルを用意する。何度呼んでも同じ結果になる。
    /// </summary>
    /// <remarks>
    /// <see cref="Infrastructure.SqliteJobStore.InitializeAsync"/> と同じく起動時に呼ばれる想定。
    /// journal_mode は DB ファイル自体に記録されるため、Jobs 側の初期化で設定した WAL がここにも効く。
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS JobTraceContexts (
                JobId       TEXT NOT NULL PRIMARY KEY,
                TraceParent TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveAsync(JobId id, string traceParent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceParent);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // 登録は Job ごとに 1 回だが、観測の保存が一意制約で例外を出しても誰も得をしないので
        // upsert にして保存を冪等にする。
        command.CommandText =
            """
            INSERT INTO JobTraceContexts (JobId, TraceParent)
            VALUES ($id, $traceParent)
            ON CONFLICT (JobId) DO UPDATE SET TraceParent = excluded.TraceParent;
            """;
        command.Parameters.AddWithValue("$id", id.Value);
        command.Parameters.AddWithValue("$traceParent", traceParent);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> FindAsync(JobId id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT TraceParent FROM JobTraceContexts WHERE JobId = $id;";
        command.Parameters.AddWithValue("$id", id.Value);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
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
