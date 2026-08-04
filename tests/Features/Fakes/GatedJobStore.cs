using Netsoft.Jobs.Domain;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// 指定した状態への最初の <see cref="ListByStatusAsync"/> を、テストが放すまで止める
/// <see cref="IJobStore"/>。
/// </summary>
/// <remarks>
/// <para>
/// 起動時復旧の途中でスレッドを止めておかないと、「復旧が走っている最中に
/// 別の経路が Job を実行し始める」状況を作れない。時間で止めると再現しなくなるので、
/// 合図で止める。
/// </para>
/// <para>
/// 止めるのは最初の 1 回だけ。同じ状態への 2 回目以降は素通しにする。
/// 全部止めると、割り込ませたい側（同じ store を使う 2 本目の復旧）まで止まってしまう。
/// </para>
/// </remarks>
internal sealed class GatedJobStore : IJobStore
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IJobStore _inner;
    private readonly JobStatus _gatedStatus;

    private int _gateUsed;
    private int _listByStatusCalls;

    public GatedJobStore(IJobStore inner, JobStatus gatedStatus)
    {
        _inner = inner;
        _gatedStatus = gatedStatus;
    }

    /// <summary>止めた地点に到達したら完了する。</summary>
    public Task Entered => _entered.Task;

    /// <summary>止めていた読み出しを進ませる。</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>
    /// 状態で絞った読み出しの回数。
    /// </summary>
    /// <remarks>
    /// この読み出しをするのは起動時復旧だけなので、実質「復旧が走った回数」になる。
    /// 復旧が実行の入口から辿れないことを、呼ばれていない事実で示すのに使う。
    /// </remarks>
    public int ListByStatusCalls => Volatile.Read(ref _listByStatusCalls);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _listByStatusCalls);

        if (status == _gatedStatus && Interlocked.Exchange(ref _gateUsed, 1) == 0)
        {
            // 読み出しの「前」で止める。後で止めると、止めている間に他所が書いた変更が
            // この読み出しに反映されず、作りたい状況にならない。
            _entered.TrySetResult();
            await _released.Task;
        }

        return await _inner.ListByStatusAsync(status, cancellationToken);
    }

    /// <inheritdoc />
    public Task AddAsync(Job job, CancellationToken cancellationToken) =>
        _inner.AddAsync(job, cancellationToken);

    /// <inheritdoc />
    public Task<bool> UpdateAsync(Job job, JobStatus expectedStatus, CancellationToken cancellationToken) =>
        _inner.UpdateAsync(job, expectedStatus, cancellationToken);

    /// <inheritdoc />
    public Task<Job?> FindAsync(JobId id, CancellationToken cancellationToken) =>
        _inner.FindAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken) =>
        _inner.ListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Job?> FindOldestQueuedAsync(CancellationToken cancellationToken) =>
        _inner.FindOldestQueuedAsync(cancellationToken);
}
