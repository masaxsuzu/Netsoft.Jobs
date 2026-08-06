using System.Net.Http.Json;

using Microsoft.Data.Sqlite;

namespace Netsoft.Jobs.Chaos.Tests;

/// <summary>
/// 別のプロセスが DB の書き込みロックを握っている間の振る舞い。
/// </summary>
/// <remarks>
/// <para>
/// SQLite の書き込みロックはファイル単位で、外部のツール（sqlite3、バックアップ、
/// 同期ソフト）が握ることもある。握られている間に書き込みが即座に失敗すると、
/// 利用者から見ると「たまたま登録できないときがある」になる。
/// </para>
/// <para>
/// ロックはテストのプロセスから本物の別接続で取る。デコレータでは
/// 「SQLite が待つかどうか」を確かめられない（待つのは実装の中）。
/// </para>
/// </remarks>
public sealed class LockedDatabaseTests : IAsyncLifetime
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "netsoft-jobs-chaos", Path.GetRandomFileName());

    private JobsHost? _host;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        SqliteConnection.ClearPool(new SqliteConnection(ConnectionString));

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストの結果を変えない（docs/build.md「テストの後始末」）。
        }
    }

    private string DatabasePath => Path.Combine(_directory, "jobs.db");

    private string ConnectionString =>
        new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();

    /// <summary>
    /// ロックが外れるまで待って書き込みは成立する。1 度弾かれて諦めない。
    /// </summary>
    /// <remarks>
    /// 待つのは Microsoft.Data.Sqlite の再試行で、こちらのコードには無い。
    /// つまりこれは「実装が正しい」ではなく「使っている部品の既定に乗っている」ことの確認で、
    /// 部品を替えたときに黙って壊れる場所でもある。だから外側から押さえておく。
    /// </remarks>
    [Fact]
    public async Task ロックが外れれば登録は諦めずに成立する()
    {
        _host = await JobsHost.StartAsync(DatabasePath);

        using SqliteConnection blocker = new(ConnectionString);
        await blocker.OpenAsync();

        using (SqliteTransaction locking = (SqliteTransaction)await blocker.BeginTransactionAsync())
        {
            // 書き込みロックを実際に取る。BEGIN だけでは遅延ロックなので握れない。
            SqliteCommand write = blocker.CreateCommand();
            write.Transaction = locking;
            write.CommandText = "UPDATE Jobs SET Name = Name;";
            await write.ExecuteNonQueryAsync();

            using HttpClient client = new() { BaseAddress = new Uri(_host.BaseUrl) };
            Task<HttpResponseMessage> registration = client.PostAsJsonAsync(
                "/api/jobs",
                new { name = "ロック中の登録", jobType = "subtasks", parameters = "1 1" });

            // 握っている間は決着しない。ここで成功していたら、ロックが効いていないか
            // 別のファイルへ書いている（テストの土台が壊れている）。
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(registration.IsCompleted, "ロックを握っている間に登録が決着しました。");

            await locking.RollbackAsync();

            HttpResponseMessage response = await registration.WaitAsync(TimeSpan.FromSeconds(60));
            response.EnsureSuccessStatusCode();
        }
    }
}
