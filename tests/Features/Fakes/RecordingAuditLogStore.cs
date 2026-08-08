using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 書かれた監査ログを手元に溜めるだけの置き場。
/// </summary>
/// <remarks>
/// 件数と中身をそのまま見たいときに使う。並び順は書いた順で、実装（SQLite）と同じ
/// 「Job 単位は古い順」になる。全件は実装が新しい順なので、こちらも逆順で返す。
/// </remarks>
public sealed class RecordingAuditLogStore : IAuditLogStore
{
    private readonly List<AuditLog> _logs = [];

    /// <summary>書かれた監査ログ。書いた順。</summary>
    public IReadOnlyList<AuditLog> Logs => _logs;

    public Task WriteAsync(AuditLog log, CancellationToken cancellationToken)
    {
        _logs.Add(log);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditLog>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditLog>>(
            [.. _logs.Where(log => log.JobId == jobId)]);

    public Task<IReadOnlyList<AuditLog>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditLog>>([.. Enumerable.Reverse(_logs)]);
}
