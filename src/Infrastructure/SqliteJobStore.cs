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
        "Id, Name, JobType, Parameters, Status, CreatedAt, StartedAt, FinishedAt, FailureMessage, Version";

    // 実行を待っている状態。判断は Domain が持ち、ここは列挙するだけ
    // （FindOldestWaitingAsync の注記を参照）。
    private static readonly JobStatus[] WaitingStatuses =
        [.. Enum.GetValues<JobStatus>().Where(status => status.IsWaiting())];

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
    public SqliteJobStore(string databasePath) =>
        _connectionString = SqliteConnections.BuildConnectionString(databasePath);

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
                Id             TEXT    NOT NULL PRIMARY KEY,
                Name           TEXT    NOT NULL,
                JobType        TEXT    NOT NULL,
                Parameters     TEXT    NOT NULL,
                Status         TEXT    NOT NULL,
                CreatedAt      TEXT    NOT NULL,
                StartedAt      TEXT    NULL,
                FinishedAt     TEXT    NULL,
                FailureMessage TEXT    NULL,
                Version        INTEGER NOT NULL DEFAULT 1
            );
            """,
            cancellationToken).ConfigureAwait(false);

        // FindOldestWaitingAsync と ListByStatusAsync はどちらも
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
            VALUES ($id, $name, $jobType, $parameters, $status, $createdAt, $startedAt, $finishedAt, $failureMessage, $version);
            """;

        Bind(command, job);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="JobNotFoundException">
    /// 指定された Job が存在しない場合。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 条件付き更新（WHERE に版を含める）で、読み出しから書き込みまでの間に
    /// 他所が書いていないことを DB に確かめさせる。呼び出し側が読んだ内容を
    /// そのまま前提にすると、同時に動く実行エンジンとキャンセルが互いの結果を上書きする。
    /// 前提が成り立ったかどうかを判断できるのは、実際に書き込む DB だけである。
    /// </para>
    /// <para>
    /// 期待値が状態ではなく版なのは、<b>状態を変えない書き込みがあるから</b>。
    /// 編集（parameters の差し替え）は遷移ではないので状態が動かず、状態を期待値にすると
    /// 「読む → 誰かが編集する → 状態は同じなので通る → 全列を書くので編集が消える」が起きる。
    /// SET に版の +1 を含めるので、どの書き込みも次の期待値をずらす。
    /// </para>
    /// <para>
    /// 更新できなかったとき、版の食い違いと Id の取り違えを区別する。
    /// 前者は同時実行のもとで普通に起きることなので false を返して読み直させる。
    /// 後者は呼び出し側の誤り（または他所から消された）で、黙って no-op にすると
    /// Running → Completed のような遷移が失われたことに誰も気づけないので例外にする。
    /// 一緒くたに false を返すと、取り違えのバグが「競合に負けただけ」に化けて隠れる。
    /// </para>
    /// <para>
    /// 存在確認の SELECT は 0 行だったときにしか実行しない。
    /// 書き戻せる通常の経路に余分な問い合わせを足さないため。
    /// </para>
    /// </remarks>
    public async Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // Id と版は WHERE でだけ使う。作成日時と種類は登録後に変わらないが、
        // 列を絞ると「何が更新対象か」を 2 箇所で管理することになるので全列を書き戻す。
        // 全列を書くからこそ、守りは状態ではなく版でなければならない（上の注記を参照）。
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
                FailureMessage = $failureMessage,
                Version = $version + 1
            WHERE Id = $id AND Version = $version;
            """;

        Bind(command, job);

        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 0)
        {
            return true;
        }

        if (await ExistsAsync(connection, job.Id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        throw new JobNotFoundException(job.Id);
    }

    /// <inheritdoc />
    public async Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM Jobs WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.Value);

        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
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
    public async Task<Job?> FindOldestWaitingAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        // 待ち行列は複数の状態からなる。どれが待ちかを SQL に書き写さず
        // JobStatusExtensions.IsWaiting から組み立てるのは、状態が増えたときに
        // 「拾われない待ち行列」ができないようにするため（増えても黙って滞留するだけで、
        // エラーはどこにも出ない）。
        string[] placeholders = [.. WaitingStatuses.Select((_, index) => $"$status{index}")];

        command.CommandText =
            $"""
            SELECT {Columns} FROM Jobs
            WHERE Status IN ({string.Join(", ", placeholders)})
            ORDER BY CreatedAt ASC, Id ASC
            LIMIT 1;
            """;

        for (int index = 0; index < WaitingStatuses.Length; index++)
        {
            command.Parameters.AddWithValue(placeholders[index], JobStatusText.ToText(WaitingStatuses[index]));
        }

        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
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
        command.Parameters.AddWithValue("$status", JobStatusText.ToText(status));

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken) =>
        SqliteConnections.OpenAsync(_connectionString, cancellationToken);

    /// <summary>
    /// 行が在るかだけを見る。更新が 0 行だった理由を切り分けるために使う。
    /// </summary>
    private static async Task<bool> ExistsAsync(
        SqliteConnection connection,
        JobId id,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        // 列は要らない。在ることが分かればよいので 1 行取れるかだけを見る。
        command.CommandText = "SELECT 1 FROM Jobs WHERE Id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id.Value);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>1 件だけ読む。1 行も無ければ null。</summary>
    private static async Task<Job?> ReadOneAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
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
        command.Parameters.AddWithValue("$status", JobStatusText.ToText(job.Status));
        command.Parameters.AddWithValue("$createdAt", SqliteTimestamp.ToText(job.CreatedAt));
        command.Parameters.AddWithValue("$startedAt", ToNullable(job.StartedAt));
        command.Parameters.AddWithValue("$finishedAt", ToNullable(job.FinishedAt));
        command.Parameters.AddWithValue("$failureMessage", (object?)job.FailureMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", job.Version);
    }

    private static Job Read(SqliteDataReader reader) =>
        Job.Rehydrate(
            JobId.From(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            JobStatusText.FromText(reader.GetString(4)),
            SqliteTimestamp.FromText(reader.GetString(5)),
            ReadNullableTimestamp(reader, 6),
            ReadNullableTimestamp(reader, 7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetInt64(9));

    private static DateTimeOffset? ReadNullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : SqliteTimestamp.FromText(reader.GetString(ordinal));

    private static object ToNullable(DateTimeOffset? value) =>
        value is { } present ? SqliteTimestamp.ToText(present) : DBNull.Value;

}
