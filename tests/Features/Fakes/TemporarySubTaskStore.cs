using Microsoft.Data.Sqlite;

using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Infrastructure;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// テスト 1 件ごとの使い捨て DB に載せた、本物の <see cref="SqliteSubTaskStore"/>。
/// 本物を使う理由と同期初期化の理由は <see cref="TemporaryJobStore"/> と同じ。
/// </summary>
public sealed class TemporarySubTaskStore : ISubTaskStore, IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly string _databasePath;
    private readonly SqliteSubTaskStore _store;

    public TemporarySubTaskStore()
    {
        _databasePath = _directory.Combine("jobs.db");
        _store = new SqliteSubTaskStore(_databasePath);
        _store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public Task AddRangeAsync(IReadOnlyList<SubTask> subTasks, CancellationToken cancellationToken) =>
        _store.AddRangeAsync(subTasks, cancellationToken);

    /// <inheritdoc />
    public Task<bool> UpdateAsync(SubTask subTask, SubTaskStatus expectedStatus, CancellationToken cancellationToken) =>
        _store.UpdateAsync(subTask, expectedStatus, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SubTask>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) =>
        _store.ListByJobAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public Task RemovePendingFromAsync(JobId jobId, int firstIndex, CancellationToken cancellationToken) =>
        _store.RemovePendingFromAsync(jobId, firstIndex, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<JobId, SubTaskProgress>> CountByJobAsync(CancellationToken cancellationToken) =>
        _store.CountByJobAsync(cancellationToken);

    public void Dispose()
    {
        // 自分の DB のプールだけを閉じてから消す（理由は TemporaryJobStore と同じ）。
        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        SqliteConnection.ClearPool(connection);

        _directory.Dispose();
    }
}
