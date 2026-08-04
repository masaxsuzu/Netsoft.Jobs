using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;
using Netsoft.Jobs.Infrastructure;

namespace Netsoft.Jobs.Web;

/// <summary>
/// Features の port（<see cref="IJobTraceContextStore"/>）を Infrastructure の
/// <see cref="SqliteJobTraceContextStore"/> へ委譲するアダプタ。
/// </summary>
/// <remarks>
/// SQL は Infrastructure にあり、ここにあるのは結線だけ。port は Features にあり、
/// Infrastructure は Features を参照できない（ASP.NET Core の FrameworkReference を
/// 引きずる）ので、両方を参照できる Web が interface と具象を繋ぐ。それだけが Web の関心。
/// </remarks>
public sealed class JobTraceContextStoreAdapter : IJobTraceContextStore
{
    private readonly SqliteJobTraceContextStore _store;

    public JobTraceContextStoreAdapter(SqliteJobTraceContextStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <inheritdoc />
    public Task SaveAsync(JobId id, string traceParent, CancellationToken cancellationToken) =>
        _store.SaveAsync(id, traceParent, cancellationToken);

    /// <inheritdoc />
    public Task<string?> FindAsync(JobId id, CancellationToken cancellationToken) =>
        _store.FindAsync(id, cancellationToken);
}
