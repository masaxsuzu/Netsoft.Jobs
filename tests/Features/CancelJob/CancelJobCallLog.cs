using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Features.Tests.Fakes;

namespace Netsoft.Jobs.Features.Tests.CancelJob;

/// <summary>
/// 保存と伝達の呼び出し順を 1 本の列に記録する。
/// </summary>
/// <remarks>
/// 「保存してから伝える」はこの機能の要点で、逆順にすると実行中のハンドラが
/// Cancelling を経ずに終端へ進みうる。どちらも呼ばれたことを別々に確かめるだけでは
/// 順序の入れ替わりを検出できないので、同じ列に混ぜて記録する。
/// </remarks>
internal sealed class CancelJobCallLog
{
    private readonly List<string> _entries = [];

    /// <summary>記録された呼び出し。呼ばれた順。</summary>
    public IReadOnlyList<string> Entries => _entries;

    public void Record(string entry) => _entries.Add(entry);
}

/// <summary>
/// 保存の呼び出しを <see cref="CancelJobCallLog"/> に記録する <see cref="IJobStore"/>。
/// </summary>
/// <remarks>
/// 保存そのものの振る舞いは包んだ本物の store（<see cref="Fakes.TemporaryJobStore"/>）に任せる。
/// ここで作り直すと、他の機能のテストが見ている store と挙動がずれる。
/// </remarks>
internal sealed class CallLoggingJobStore : IJobStore
{
    private readonly IJobStore _inner;
    private readonly CancelJobCallLog _log;

    public CallLoggingJobStore(IJobStore inner, CancelJobCallLog log)
    {
        _inner = inner;
        _log = log;
    }

    /// <inheritdoc />
    public Task AddAsync(Job job, CancellationToken cancellationToken) =>
        _inner.AddAsync(job, cancellationToken);

    /// <inheritdoc />
    public Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        // 状態も一緒に残す。何を保存した時点で伝えたのかが分かるようにするため。
        // 書き戻せずにやり直した場合は、その分だけ記録が増える。
        _log.Record($"update:{job.Id.Value}:{job.Status}");
        return _inner.UpdateAsync(job, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken) =>
        _inner.FindAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken) =>
        _inner.ListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Job?> FindOldestWaitingAsync(CancellationToken cancellationToken) =>
        _inner.FindOldestWaitingAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken) =>
        _inner.ListByStatusAsync(status, cancellationToken);
}
