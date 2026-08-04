using Microsoft.Data.Sqlite;

namespace Netsoft.Jobs.Infrastructure.Tests;

/// <summary>
/// テスト 1 件ごとの使い捨て DB ファイル。
/// </summary>
/// <remarks>
/// SQLite の in-memory ではなく実ファイルを使う。この層で確かめたいのは
/// 「SQL が本当に正しいか」と「プロセスをまたいで残るか」なので、
/// ファイルに書かない構成にすると検証したいものが消える。
/// </remarks>
internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string _directory;

    public TemporaryDatabase()
    {
        // テストは並行して走るので、ディレクトリごと分けて衝突を避ける。
        _directory = Path.Combine(Path.GetTempPath(), "netsoft-jobs-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);
        FilePath = Path.Combine(_directory, "jobs.db");
    }

    /// <summary>DB ファイルのパス。</summary>
    public string FilePath { get; }

    /// <summary>初期化済みのストアを新しく開く。同じファイルを別インスタンスから触れる。</summary>
    public async Task<SqliteJobStore> OpenStoreAsync()
    {
        SqliteJobStore store = new(FilePath);
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    public void Dispose()
    {
        // プールが接続を握ったままだとファイルを開いたままになり、
        // Windows では削除に失敗する。閉じてから消す。
        // 閉じるのは自分の DB のプールだけ。ClearAllPools はプロセス全域に効き、
        // 並列で走っている他のテストが使っている最中の接続まで破棄して
        // ObjectDisposedException のフレークを起こす（実際に起きた）。
        // 接続文字列は store と同じ組み立て（DataSource のみ）なので、同じプールに当たる。
        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = FilePath }.ToString());
        SqliteConnection.ClearPool(connection);

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末に失敗してもテストの結果を変えたくない。
            // 一時ディレクトリなので、残ってもいずれ OS が回収する。
        }
    }
}
