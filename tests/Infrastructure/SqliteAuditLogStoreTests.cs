using Microsoft.Data.Sqlite;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Infrastructure.Tests;

/// <summary>
/// 実ファイルの SQLite に対して <see cref="SqliteAuditLogStore"/> を確かめる。
/// </summary>
/// <remarks>
/// 見るのは書いた SQL が意図どおりかだけ。偽物の DB に差し替えたら確かめる対象が消える
/// （<see cref="SqliteJobStoreTests"/> と同じ理由）。
/// </remarks>
public sealed class SqliteAuditLogStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // 意図的に UTC ではないオフセット。UTC で書いて UTC で読むだけでは変換漏れに気づけない。
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 8, 9, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public async Task 書いた監査ログを読み戻すと全項目が一致する()
    {
        using TemporaryDatabase database = new();
        SqliteAuditLogStore store = await OpenAsync(database);

        AuditLog written = new(
            AuditActor.User,
            BaseTime,
            "パラメータを変更した（変更前=3 1, 変更後=5 2）",
            JobId.From("job-1"),
            "現在の状態（Completed）では編集できません。");

        await store.WriteAsync(written, None);

        AuditLog read = Assert.Single(await store.ListByJobAsync(JobId.From("job-1"), None));

        Assert.Equal(written.Actor, read.Actor);
        Assert.Equal(written.At, read.At);
        Assert.Equal(written.Content, read.Content);
        Assert.Equal(written.JobId, read.JobId);
        Assert.Equal(written.Error, read.Error);
    }

    /// <summary>
    /// Job に紐づかない実施と、通った実施。null が往復して null で戻る。
    /// </summary>
    /// <remarks>
    /// 空文字に化けると「どの Job についてでもない」が「Id が空の Job」になり、
    /// 通った実施が「理由が空の失敗」になる。どちらも黙って意味が変わる化け方。
    /// </remarks>
    [Fact]
    public async Task 紐づかない実施とエラーの無い実施はnullのまま戻る()
    {
        using TemporaryDatabase database = new();
        SqliteAuditLogStore store = await OpenAsync(database);

        await store.WriteAsync(
            new AuditLog(AuditActor.System, BaseTime, "実行を開始した（パラメータ=3 1）", JobId: null, Error: null),
            None);

        AuditLog read = Assert.Single(await store.ListAsync(None));

        Assert.Null(read.JobId);
        Assert.Null(read.Error);
        Assert.Equal(AuditActor.System, read.Actor);
    }

    /// <summary>
    /// Job 単位は古い順、全件は新しい順。<b>同じ時刻でも書いた順で決まる。</b>
    /// </summary>
    /// <remarks>
    /// 時刻だけを並びのキーにすると、1 回の操作が続けて 2 件書く場面で順序が崩れる
    /// （同じミリ秒に収まる）。ここは時刻をわざと同一にして、書いた順が効くことを見る。
    /// </remarks>
    [Fact]
    public async Task 同じ時刻でもJob単位は古い順で全件は新しい順になる()
    {
        using TemporaryDatabase database = new();
        SqliteAuditLogStore store = await OpenAsync(database);

        foreach (string content in (string[])["1 番目", "2 番目", "3 番目"])
        {
            await store.WriteAsync(
                new AuditLog(AuditActor.User, BaseTime, content, JobId.From("job-1"), Error: null),
                None);
        }

        Assert.Equal(
            ["1 番目", "2 番目", "3 番目"],
            (await store.ListByJobAsync(JobId.From("job-1"), None)).Select(log => log.Content));

        Assert.Equal(
            ["3 番目", "2 番目", "1 番目"],
            (await store.ListAsync(None)).Select(log => log.Content));
    }

    [Fact]
    public async Task Job単位は他のJobの監査ログを混ぜない()
    {
        using TemporaryDatabase database = new();
        SqliteAuditLogStore store = await OpenAsync(database);

        await store.WriteAsync(new AuditLog(AuditActor.User, BaseTime, "こちら", JobId.From("job-1"), null), None);
        await store.WriteAsync(new AuditLog(AuditActor.User, BaseTime, "あちら", JobId.From("job-2"), null), None);
        await store.WriteAsync(new AuditLog(AuditActor.User, BaseTime, "どこでもない", JobId: null, null), None);

        Assert.Equal(["こちら"], (await store.ListByJobAsync(JobId.From("job-1"), None)).Select(log => log.Content));
    }

    /// <summary>
    /// 実施者は数値ではなく enum の名前で保存される。値を足し引きしても、
    /// 保存済みの行の意味が黙って変わらないようにするため（状態と同じ規則）。
    /// </summary>
    [Fact]
    public async Task 実施者は数値ではなくenumの名前で保存される()
    {
        using TemporaryDatabase database = new();
        SqliteAuditLogStore store = await OpenAsync(database);

        await store.WriteAsync(new AuditLog(AuditActor.System, BaseTime, "実行を完了した", null, null), None);

        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database.FilePath }.ToString());
        await connection.OpenAsync(None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Actor FROM AuditLogs;";

        Assert.Equal("System", (string?)await command.ExecuteScalarAsync(None));
    }

    private static async Task<SqliteAuditLogStore> OpenAsync(TemporaryDatabase database)
    {
        SqliteAuditLogStore store = new(database.FilePath);
        await store.InitializeAsync(None);

        return store;
    }
}
