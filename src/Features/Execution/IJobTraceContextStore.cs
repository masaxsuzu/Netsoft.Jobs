using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// 登録時の trace context（W3C traceparent）の置き場。
/// </summary>
/// <remarks>
/// <para>
/// messaging semantic conventions の「メッセージに trace context を同乗させる」パターンを、
/// Job 行の外の別表で実現するための口。登録（producer）が traceparent を保存し、
/// 実行（consumer）が読み出して job.execute スパンから登録トレースへ Link を張る。
/// </para>
/// <para>
/// Domain の <see cref="IJobStore"/> ではなく Features に置くのは、これが観測
/// （アプリケーション）の関心であって Domain の契約ではないから。Job 行に traceparent の
/// 列を足すと、Domain が観測を知ることになる。
/// </para>
/// <para>
/// 実装（アダプタ）はホストが差し替える（Web の SqliteJobTraceContextStore）。
/// 差し替えなくても既定の no-op で全機能が動く
/// （<see cref="JobExecutionServiceCollectionExtensions.AddJobExecution"/> を参照）。
/// </para>
/// </remarks>
public interface IJobTraceContextStore
{
    /// <summary>Job の登録時の traceparent を保存する。</summary>
    Task SaveAsync(JobId id, string traceParent, CancellationToken cancellationToken);

    /// <summary>保存済みの traceparent を取得する。無ければ null。</summary>
    Task<string?> FindAsync(JobId id, CancellationToken cancellationToken);
}
