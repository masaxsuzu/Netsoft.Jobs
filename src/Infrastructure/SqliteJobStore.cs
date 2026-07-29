using Microsoft.Data.Sqlite;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Infrastructure;

/// <summary>
/// SQLite による <see cref="IJobStore"/> の実装。
/// </summary>
/// <remarks>
/// 接続は呼び出しごとに開いて閉じる。Microsoft.Data.Sqlite は同じ接続文字列に対して
/// 接続をプールするので、都度開いても実際のファイルオープンは繰り返されない。
/// 長生きする接続を 1 本持ち回すと、同時に走る Job から共有されて排他の面倒を見る羽目になる。
/// </remarks>
public sealed class SqliteJobStore : IJobStore
{
    // 列の並びは読み出し側の序数と対応している。SELECT * にすると
    // スキーマを足したときに序数がずれるので、常にこの並びを明示する。
    private const string Columns =
        "Id, Name, JobType, Parameters, Status, CreatedAt, StartedAt, FinishedAt, FailureMessage";

    private readonly string _connectionString;

    /// <summary>
    /// DB ファイルのパスを指定して生成する。
    /// </summary>
    /// <remarks>
    /// ここでは接続文字列を組み立てるだけで I/O をしない。
    /// コンストラクタでファイルを触ると、DI コンテナの解決やテストの生成が
    /// ディスクの状態に依存してしまい、失敗したときの原因も追いにくくなる。
    /// スキーマ作成は <see cref="InitializeAsync"/> で明示的に行う。
    /// </remarks>
    /// <param name="databasePath">DB ファイルのパス。呼び出し側が決める。</param>
    public SqliteJobStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString();
    }

    /// <summary>
    /// テーブルとインデックスを用意する。何度呼んでも同じ結果になる。
    /// </summary>
    /// <remarks>
    /// 起動のたびに呼ばれる想定なので、既にある場合は何もしない書き方にしてある。
    /// </remarks>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // WAL にすると、読み取りが書き込みをブロックしなくなる。
        // 実行エンジンが Job の状態を書き換えている最中でも画面の一覧が読めるので、
        // 長時間実行 Job を抱えるこの用途では既定の rollback journal より素直に動く。
        // journal_mode は DB ファイル自体に記録されるため、一度設定すれば以後も維持される。
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS Jobs (
                Id             TEXT NOT NULL PRIMARY KEY,
                Name           TEXT NOT NULL,
                JobType        TEXT NOT NULL,
                Parameters     TEXT NOT NULL,
                Status         TEXT NOT NULL,
                CreatedAt      TEXT NOT NULL,
                StartedAt      TEXT NULL,
                FinishedAt     TEXT NULL,
                FailureMessage TEXT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        // FindOldestQueuedAsync と ListByStatusAsync はどちらも
        // 「状態で絞って作成日時で並べる」形なので、この 1 本で両方が賄える。
        await ExecuteAsync(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_Jobs_Status_CreatedAt ON Jobs (Status, CreatedAt);",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AddAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            INSERT INTO Jobs ({Columns})
            VALUES ($id, $name, $jobType, $parameters, $status, $createdAt, $startedAt, $finishedAt, $failureMessage);
            """;

        Bind(command, job);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="JobNotFoundException">
    /// 指定された Job が存在しない場合。
    /// </exception>
    /// <remarks>
    /// 存在しない Id を黙って無視しない。UpdateAsync を呼ぶ時点で呼び出し側は
    /// 「読み出して遷移させた Job を書き戻す」つもりでいるので、対象が無いのは
    /// 取り違えか、他所から消されたかのどちらかしかない。何もせずに成功を返すと
    /// Running → Completed のような遷移が失われたことに誰も気づけない。
    /// 保存されていないなら失敗として知らせるほうが、原因の近くで止まる。
    /// </remarks>
    public async Task UpdateAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // Id は WHERE でだけ使う。作成日時と種類は登録後に変わらないが、
        // 列を絞ると「何が更新対象か」を 2 箇所で管理することになるので全列を書き戻す。
        command.CommandText =
            """
            UPDATE Jobs
            SET Name = $name,
                JobType = $jobType,
                Parameters = $parameters,
                Status = $status,
                CreatedAt = $createdAt,
                StartedAt = $startedAt,
                FinishedAt = $finishedAt,
                FailureMessage = $failureMessage
            WHERE Id = $id;
            """;

        Bind(command, job);

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new JobNotFoundException(job.Id);
        }
    }

    /// <inheritdoc />
    public async Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM Jobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.Value);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Read(reader);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // CreatedAt は固定長の UTC 文字列なので、辞書順の降順がそのまま新しい順になる。
        // 同時刻の並びが実行ごとに変わらないよう、Id を第 2 キーに置いて全順序にする。
        command.CommandText = $"SELECT {Columns} FROM Jobs ORDER BY CreatedAt DESC, Id DESC;";

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT {Columns} FROM Jobs
            WHERE Status = $status
            ORDER BY CreatedAt ASC, Id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$status", ToText(JobStatus.Queued));

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Read(reader);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"""
            SELECT {Columns} FROM Jobs
            WHERE Status = $status
            ORDER BY CreatedAt DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$status", ToText(status));

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
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

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Job>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        List<Job> jobs = [];

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(Read(reader));
        }

        return jobs;
    }

    /// <summary>
    /// パラメータを束ねる。SQL は必ずここを通し、値を文字列連結で埋め込まない。
    /// </summary>
    private static void Bind(SqliteCommand command, Job job)
    {
        command.Parameters.AddWithValue("$id", job.Id.Value);
        command.Parameters.AddWithValue("$name", job.Name);
        command.Parameters.AddWithValue("$jobType", job.JobType);
        command.Parameters.AddWithValue("$parameters", job.Parameters);
        command.Parameters.AddWithValue("$status", ToText(job.Status));
        command.Parameters.AddWithValue("$createdAt", SqliteTimestamp.ToText(job.CreatedAt));
        command.Parameters.AddWithValue("$startedAt", ToNullable(job.StartedAt));
        command.Parameters.AddWithValue("$finishedAt", ToNullable(job.FinishedAt));
        command.Parameters.AddWithValue("$failureMessage", (object?)job.FailureMessage ?? DBNull.Value);
    }

    private static Job Read(SqliteDataReader reader) =>
        Job.Rehydrate(
            JobId.From(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            Enum.Parse<JobStatus>(reader.GetString(4)),
            SqliteTimestamp.FromText(reader.GetString(5)),
            ReadNullableTimestamp(reader, 6),
            ReadNullableTimestamp(reader, 7),
            reader.IsDBNull(8) ? null : reader.GetString(8));

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : SqliteTimestamp.FromText(reader.GetString(ordinal));

    private static object ToNullable(DateTimeOffset? value) =>
        value is { } present ? SqliteTimestamp.ToText(present) : DBNull.Value;

    /// <summary>
    /// 状態を列に書く形へ変換する。
    /// </summary>
    /// <remarks>
    /// 数値ではなく enum の名前で保存する。理由は 2 つある。
    /// 1 つは、数値だと DB を直接覗いたときに "3" が何を指すのか読めないこと。
    /// もう 1 つは、enum のメンバーを並べ替えたり途中に足したりした瞬間に
    /// 既存データの意味が変わってしまうこと。名前なら並び順から独立していられる。
    /// </remarks>
    private static string ToText(JobStatus status) => status.ToString();
}
