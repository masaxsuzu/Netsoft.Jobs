using Netsoft.Jobs.Domain;
using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 保存と呼び出し回数を記録する <see cref="IJobTraceContextStore"/>。
/// </summary>
/// <remarks>
/// 「購読者がいなければ <see cref="FindAsync"/> が呼ばれない」ことは、状態を後から見るだけでは
/// 分からない（no-op でも結果は同じになる）ので、呼び出し回数を控える偽物で確かめる。
/// 失敗を仕掛けられるのは、観測の失敗が本体（登録・実行）を妨げないことを検証するため。
/// </remarks>
public sealed class RecordingJobTraceContextStore : IJobTraceContextStore
{
    private readonly Dictionary<JobId, string> _saved = [];

    /// <summary>保存された traceparent。JobId ごとに最後の値。</summary>
    public IReadOnlyDictionary<JobId, string> Saved => _saved;

    /// <summary><see cref="FindAsync"/> が呼ばれた回数。</summary>
    public int FindCalls { get; private set; }

    /// <summary>設定すると <see cref="SaveAsync"/> がこの例外を投げる。</summary>
    public Exception? SaveFailure { get; set; }

    /// <summary>設定すると <see cref="FindAsync"/> がこの例外を投げる。</summary>
    public Exception? FindFailure { get; set; }

    /// <inheritdoc />
    public Task SaveAsync(JobId id, string traceParent, CancellationToken cancellationToken)
    {
        if (SaveFailure is { } failure)
        {
            throw failure;
        }

        _saved[id] = traceParent;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> FindAsync(JobId id, CancellationToken cancellationToken)
    {
        FindCalls++;

        if (FindFailure is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(_saved.GetValueOrDefault(id));
    }
}
