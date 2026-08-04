using Microsoft.Data.Sqlite;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Infrastructure;

/// <summary>
/// 登録時 trace context（W3C traceparent）の SQLite 置き場。Jobs と同じ DB ファイルの別表に置く。
/// </summary>
/// <remarks>
/// <para>
/// port（Features の <c>IJobTraceContextStore</c>）は実装しない素のクラスである。
/// Infrastructure が Features を参照すると ASP.NET Core の FrameworkReference まで
/// 引きずってしまうため、interface への結線は両方を参照できる Web のアダプタ
/// （<c>JobTraceContextStoreAdapter</c>）が担う。ここにあるのは純粋な永続化だけ。
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
/// <see cref="SqliteJobStore"/> と同じで、共通部分は <see cref="SqliteConnections"/> に括ってある。
/// </para>
/// </remarks>
public sealed class SqliteJobTraceContextStore
{
    private readonly string _connectionString;

    /// <param name="databasePath">DB ファイルのパス。Jobs と同じファイルを渡す。</param>
    public SqliteJobTraceContextStore(string databasePath) =>
        _connectionString = SqliteConnections.BuildConnectionString(databasePath);

    /// <summary>
    /// テーブルを用意する。何度呼んでも同じ結果になる。
    /// </summary>
    /// <remarks>
    /// <see cref="SqliteJobStore.InitializeAsync"/> と同じく起動時に呼ばれる想定。
    /// journal_mode は DB ファイル自体に記録されるため、Jobs 側の初期化で設定した WAL がここにも効く。
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await SqliteConnections.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Job の登録時の traceparent を保存する。</summary>
    public async Task SaveAsync(JobId id, string traceParent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceParent);

        await using SqliteConnection connection =
            await SqliteConnections.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
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

    /// <summary>保存済みの traceparent を取得する。無ければ null。</summary>
    public async Task<string?> FindAsync(JobId id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await SqliteConnections.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT TraceParent FROM JobTraceContexts WHERE JobId = $id;";
        command.Parameters.AddWithValue("$id", id.Value);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
}
