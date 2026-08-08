using Microsoft.Data.Sqlite;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Infrastructure;

/// <summary>
/// 監査ログの SQLite 実装。
/// </summary>
/// <remarks>
/// <para>
/// <b>並び順の第 2 キーは書いた順（<c>Id</c>）。</b>時刻だけでは決まらない ── 1 回の操作が
/// 要求と確定の 2 件を続けて書くことがあり、そこは同じミリ秒に収まる。
/// <c>INTEGER PRIMARY KEY</c> は SQLite の rowid そのもので、INSERT のたびに単調増加する。
/// </para>
/// <para>
/// <c>Id</c> を <see cref="AuditLog"/> へは載せない。並びをここで決められる以上、
/// 外へ出す意味が無い（型の注記を参照）。
/// </para>
/// </remarks>
public sealed class SqliteAuditLogStore : IAuditLogStore
{
    private const string Columns = "Actor, At, Content, JobId, Error";

    private readonly string _connectionString;

    /// <param name="databasePath">DB ファイルのパス。呼び出し側が決める。</param>
    public SqliteAuditLogStore(string databasePath) =>
        _connectionString = SqliteConnections.BuildConnectionString(databasePath);

    /// <summary>テーブルとインデックスを用意する。何度呼んでも同じ結果になる。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand table = connection.CreateCommand();

        // JobId だけが NULL 可。紐づかない実施（登録の入力エラー、存在しない Id への操作）が
        // あるため。「どの Job か分からない」ではなく「どの Job についてでもない」を表す。
        table.CommandText =
            """
            CREATE TABLE IF NOT EXISTS AuditLogs (
                Id      INTEGER NOT NULL PRIMARY KEY,
                Actor   TEXT    NOT NULL,
                At      TEXT    NOT NULL,
                Content TEXT    NOT NULL,
                JobId   TEXT    NULL,
                Error   TEXT    NULL
            );
            """;

        await table.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand index = connection.CreateCommand();

        // 画面が叩くのは Job 単位の読み出しだけ。全件は Id の降順で、
        // rowid の逆走査で済むのでインデックスは要らない。
        index.CommandText = "CREATE INDEX IF NOT EXISTS IX_AuditLogs_JobId_Id ON AuditLogs (JobId, Id);";

        await index.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteAsync(AuditLog log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // Id は書かない。省くと SQLite が rowid を採番する。
        command.CommandText =
            $"INSERT INTO AuditLogs ({Columns}) VALUES ($actor, $at, $content, $jobId, $error);";

        command.Parameters.AddWithValue("$actor", AuditActorText.ToText(log.Actor));
        command.Parameters.AddWithValue("$at", SqliteTimestamp.ToText(log.At));
        command.Parameters.AddWithValue("$content", log.Content);
        command.Parameters.AddWithValue("$jobId", (object?)log.JobId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)log.Error ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLog>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM AuditLogs WHERE JobId = $jobId ORDER BY Id ASC;";
        command.Parameters.AddWithValue("$jobId", jobId.Value);

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditLog>> ListAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM AuditLogs ORDER BY Id DESC;";

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AuditLog>> ReadAllAsync(
        SqliteCommand command, CancellationToken cancellationToken)
    {
        List<AuditLog> logs = [];

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            logs.Add(new AuditLog(
                AuditActorText.FromText(reader.GetString(0)),
                SqliteTimestamp.FromText(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : JobId.From(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return logs;
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        SqliteConnections.OpenAsync(_connectionString, cancellationToken);
}
