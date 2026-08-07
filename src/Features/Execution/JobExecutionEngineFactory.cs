using Microsoft.Extensions.Logging;

using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Execution;

/// <summary>
/// DI が組み立てた依存から <see cref="JobExecutionEngine"/> を起こす。
/// </summary>
/// <remarks>
/// <para>
/// エンジンは起動時復旧をやり切ってからでないと手に入らない
/// （<see cref="JobExecutionEngine.StartAsync"/> の注記を参照）。つまり生成に await が要る。
/// ところが <c>GetRequiredService</c> は同期なので、エンジン自体はサービスとして登録できない。
/// そこで<b>同期に作れるもの</b>＝依存を束ねただけのこの型を登録し、
/// await が要る部分は呼び出し側の非同期な文脈で行う。
/// </para>
/// <para>
/// エンジンをここで保持して使い回すことはしない。持たせると「まだ作っていない／作っている最中／
/// 作り終えた」を表す状態がこの型に戻ってきて、型で消したはずのものが場所を変えて復活する。
/// 呼ぶのはホストの常駐ループが立ち上がるときの 1 回だけなので、持つ必要もない。
/// </para>
/// </remarks>
public sealed class JobExecutionEngineFactory
{
    private readonly IJobStore _store;
    private readonly JobHandlerRegistry _handlers;
    private readonly JobQueueSignal _signal;
    private readonly TimeProvider _timeProvider;
    private readonly JobExecutionInstrumentation _instrumentation;
    private readonly ILogger<JobExecutionEngine> _logger;

    public JobExecutionEngineFactory(
        IJobStore store,
        JobHandlerRegistry handlers,
        JobQueueSignal signal,
        TimeProvider timeProvider,
        JobExecutionInstrumentation instrumentation,
        ILogger<JobExecutionEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(instrumentation);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _handlers = handlers;
        _signal = signal;
        _timeProvider = timeProvider;
        _instrumentation = instrumentation;
        _logger = logger;
    }

    /// <summary>
    /// 起動時復旧を済ませたエンジンを起こす。
    /// </summary>
    /// <remarks>
    /// 2 回呼べば 2 つできる。エンジンを 2 つ動かしてはいけない理由は
    /// <see cref="JobExecutionEngine.StartAsync"/> の注記にある。
    /// </remarks>
    public Task<JobExecutionEngine> StartAsync(CancellationToken cancellationToken) =>
        JobExecutionEngine.StartAsync(
            _store,
            _handlers,
            _signal,
            _timeProvider,
            _instrumentation,
            _logger,
            cancellationToken);
}
