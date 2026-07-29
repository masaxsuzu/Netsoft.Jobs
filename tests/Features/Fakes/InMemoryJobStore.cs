using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// メモリ上の <see cref="IJobStore"/>。機能のテストが永続化に依存しないようにするためのもの。
/// </summary>
/// <remarks>
/// Infrastructure を参照しないので SQLite 実装は使えない。SQLite 実装の正しさは
/// tests/Infrastructure が見ているので、ここでは「保存されたか」だけを見られれば足りる。
/// 後続の機能（実行・一覧・キャンセル）のテストでも共通に使う。
/// </remarks>
public sealed class InMemoryJobStore : IJobStore
{
    // 登録順を保ちたいのでリストで持つ。件数はテストの規模なので線形探索で困らない。
    private readonly List<Job> _jobs = [];

    /// <summary>保存されている Job。登録された順。</summary>
    public IReadOnlyList<Job> Jobs => _jobs;

    /// <inheritdoc />
    public Task AddAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        if (_jobs.Any(existing => existing.Id == job.Id))
        {
            // 実装が主キー制約で弾くのと同じ振る舞いにしておく。
            // 採番が重複したときにテストが黙って通ってしまうのを防ぐ。
            throw new InvalidOperationException($"JobId {job.Id} は既に保存されています。");
        }

        _jobs.Add(job);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        int index = _jobs.FindIndex(existing => existing.Id == job.Id);
        if (index < 0)
        {
            throw new InvalidOperationException($"JobId {job.Id} は保存されていません。");
        }

        // エンティティは可変なので同一インスタンスであることが多いが、
        // 別インスタンスで書き戻される場合にも備えて差し替える。
        _jobs[index] = job;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(_jobs.FirstOrDefault(job => job.Id == id));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 契約は「作成日時の新しい順」。同時刻は Id の降順で、SQLite 実装と同じ並びにそろえる。
        IReadOnlyList<Job> jobs = [.. _jobs
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id.Value, StringComparer.Ordinal)];

        return Task.FromResult(jobs);
    }

    /// <inheritdoc />
    public Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Job? job = _jobs
            .Where(job => job.Status == JobStatus.Queued)
            .OrderBy(job => job.CreatedAt)
            .ThenBy(job => job.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault();

        return Task.FromResult(job);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<Job> jobs = [.. _jobs
            .Where(job => job.Status == status)
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id.Value, StringComparer.Ordinal)];

        return Task.FromResult(jobs);
    }
}
