using Microsoft.Data.Sqlite;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Infrastructure;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// テスト 1 件ごとの使い捨て DB に載せた、本物の <see cref="SqliteJobStore"/>。
/// </summary>
/// <remarks>
/// 以前はメモリ上の偽 store を使っていたが、並び順などの仕様を「本物と同じにする」と
/// 手で約束して写す作りだったため、本物が変わると黙って乖離する。本物を使えばその約束ごと消える。
/// 時刻・採番・ハンドラの偽物は決定性のために残す。store は決定性を損なわないので本物でよい。
/// </remarks>
public sealed class TemporaryJobStore : IJobStore, IDisposable
{
    private readonly string _directory;
    private readonly SqliteJobStore _store;

    /// <remarks>
    /// 初期化を同期で完結させるのは、既存のテストクラスがフィールド初期化子と
    /// 同期コンストラクタで組み立てられているから。非同期にすると全クラスが
    /// IAsyncLifetime を実装し直す羽目になる。ブロッキングはこの 1 箇所に閉じ込める。
    /// </remarks>
    public TemporaryJobStore()
    {
        // テストは並行して走るので、ディレクトリごと分けて衝突を避ける。
        _directory = Path.Combine(Path.GetTempPath(), "netsoft-jobs-features-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);

        _store = new SqliteJobStore(Path.Combine(_directory, "jobs.db"));
        _store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public Task AddAsync(Job job, CancellationToken cancellationToken) =>
        _store.AddAsync(job, cancellationToken);

    /// <inheritdoc />
    public Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken) =>
        _store.UpdateAsync(job, cancellationToken);

    /// <inheritdoc />
    public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken) =>
        _store.FindAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken) =>
        _store.ListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken) =>
        _store.FindOldestQueuedAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken) =>
        _store.ListByStatusAsync(status, cancellationToken);

    public void Dispose()
    {
        // プールが接続を握ったままだとファイルが開きっぱなしになり、削除に失敗しうる。閉じてから消す。
        // 閉じるのは自分の DB のプールだけ。ClearAllPools はプロセス全域に効き、
        // 並列で走っている他のテストの接続まで破棄してフレークを起こす（実際に起きた）。
        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "jobs.db") }.ToString());
        SqliteConnection.ClearPool(connection);

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗でテストの結果を変えたくない。一時ディレクトリはいずれ OS が回収する。
        }
    }
}
