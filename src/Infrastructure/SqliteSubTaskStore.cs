using Microsoft.Data.Sqlite;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Infrastructure;

/// <summary>
/// SQLite による <see cref="ISubTaskStore"/> の実装。Jobs と同じ DB ファイルに載る。
/// </summary>
/// <remarks>
/// 接続を呼び出しごとに開いて閉じる理由は <see cref="SqliteJobStore"/> と同じ。
/// </remarks>
public sealed class SqliteSubTaskStore : ISubTaskStore
{
    // 列の並びは読み出し側の序数と対応している（SqliteJobStore と同じ判断）。
    // 連番の列名が Position なのは、INDEX が SQL の予約語で引用が要るため。
    private const string Columns = "JobId, Position, Status";

    private readonly string _connectionString;

    /// <summary>
    /// DB ファイルのパスを指定して生成する。I/O はしない
    /// （理由は <see cref="SqliteJobStore(string)"/> と同じ）。
    /// </summary>
    /// <param name="databasePath">DB ファイルのパス。Jobs と同じものを渡す。</param>
    public SqliteSubTaskStore(string databasePath) =>
        _connectionString = SqliteConnections.BuildConnectionString(databasePath);

    /// <summary>
    /// テーブルを用意する。何度呼んでも同じ結果になる。
    /// </summary>
    /// <remarks>
    /// journal_mode は DB ファイル自体に記録されるので、同じファイルを先に初期化した
    /// <see cref="SqliteJobStore"/> が WAL にしていればそのまま維持される。ここでは触らない。
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // 主キー (JobId, Position) がそのまま ListByJobAsync の絞り込みと並びを支えるので、
        // 追加のインデックスは要らない。
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS SubTasks (
                JobId    TEXT    NOT NULL,
                Position INTEGER NOT NULL,
                Status   TEXT    NOT NULL,
                PRIMARY KEY (JobId, Position)
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(IReadOnlyList<SubTask> subTasks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subTasks);

        if (subTasks.Count == 0)
        {
            // 開く前に返す。空のトランザクションを張っても仕事が無い。
            return;
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // 全件入るか 1 件も入らないか（契約）。トランザクションで束ねる。
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (SubTask subTask in subTasks)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"INSERT INTO SubTasks ({Columns}) VALUES ($jobId, $position, $status);";
            command.Parameters.AddWithValue("$jobId", subTask.JobId.Value);
            command.Parameters.AddWithValue("$position", subTask.Index);
            command.Parameters.AddWithValue("$status", SubTaskStatusText.ToText(subTask.Status));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 条件付き更新と、0 行だったときの存在確認の組み立ては
    /// <see cref="SqliteJobStore.UpdateAsync"/> と同じ（理由もそちらの注記を参照）。
    /// </remarks>
    public async Task<bool> UpdateAsync(SubTask subTask, SubTaskStatus expectedStatus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subTask);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE SubTasks
            SET Status = $status
            WHERE JobId = $jobId AND Position = $position AND Status = $expectedStatus;
            """;
        command.Parameters.AddWithValue("$status", SubTaskStatusText.ToText(subTask.Status));
        command.Parameters.AddWithValue("$jobId", subTask.JobId.Value);
        command.Parameters.AddWithValue("$position", subTask.Index);
        command.Parameters.AddWithValue("$expectedStatus", SubTaskStatusText.ToText(expectedStatus));

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 0)
        {
            return true;
        }

        if (await ExistsAsync(connection, subTask, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        throw new SubTaskNotFoundException(subTask.JobId, subTask.Index);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubTask>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"SELECT {Columns} FROM SubTasks WHERE JobId = $jobId ORDER BY Position ASC;";
        command.Parameters.AddWithValue("$jobId", jobId.Value);

        List<SubTask> subTasks = [];

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            subTasks.Add(SubTask.Rehydrate(
                JobId.From(reader.GetString(0)),
                reader.GetInt32(1),
                SubTaskStatusText.FromText(reader.GetString(2))));
        }

        return subTasks;
    }

    /// <inheritdoc />
    public async Task RemovePendingFromAsync(JobId jobId, int firstIndex, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // Pending 以外は WHERE で残る。着手済みを消せないことは SQL が保証する。
        command.CommandText =
            "DELETE FROM SubTasks WHERE JobId = $jobId AND Position >= $firstIndex AND Status = $pending;";
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$firstIndex", firstIndex);
        command.Parameters.AddWithValue("$pending", SubTaskStatusText.ToText(SubTaskStatus.Pending));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ExistsAsync(
        SqliteConnection connection, SubTask subTask, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            "SELECT 1 FROM SubTasks WHERE JobId = $jobId AND Position = $position LIMIT 1;";
        command.Parameters.AddWithValue("$jobId", subTask.JobId.Value);
        command.Parameters.AddWithValue("$position", subTask.Index);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        await SqliteConnections.OpenAsync(_connectionString, cancellationToken).ConfigureAwait(false);
}
