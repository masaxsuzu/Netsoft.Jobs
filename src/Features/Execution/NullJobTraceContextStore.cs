using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 何もしない既定の <see cref="IJobTraceContextStore"/>。保存は捨て、検索は常に null。
/// </summary>
/// <remarks>
/// 観測は任意の関心で、必須依存にしない。ホストがアダプタを差し替えなくても
/// 登録も実行も普通に動き、単に実行スパンに Link が付かないだけになる。
/// </remarks>
public sealed class NullJobTraceContextStore : IJobTraceContextStore
{
    /// <inheritdoc />
    public Task SaveAsync(JobId id, string traceParent, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task<string?> FindAsync(JobId id, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
