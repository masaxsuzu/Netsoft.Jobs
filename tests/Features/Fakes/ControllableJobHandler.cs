using Netsoft.Jobs.Domain;

using Netsoft.Jobs.Features.Execution;

namespace Netsoft.Jobs.Features.Tests.Fakes;

/// <summary>
/// テストが実行の途中で止めたり進めたりできる <see cref="IJobHandler"/>。
/// </summary>
/// <remarks>
/// <para>
/// 実行中の状態を確認するために「今ハンドラの中にいる」ことを確定させる必要がある。
/// <see cref="Task.Delay(TimeSpan)"/> で待つと、待ち時間が足りたかどうかにテストの成否が
/// 左右されて CI でランダムに落ちる。時間ではなく <see cref="TaskCompletionSource"/> の
/// 合図で同期する。
/// </para>
/// <para>
/// 使い方は「<see cref="Entered"/> を待つ → 確認や操作をする → <see cref="Release"/> で解放」。
/// 先に <see cref="Release"/> や <see cref="Throw"/> を呼んでおけば、止まらずに即座に終わる。
/// </para>
/// </remarks>
public sealed class ControllableJobHandler : IJobHandler
{
    // RunContinuationsAsynchronously を付けるのは、待っている側の続きが
    // ハンドラのスレッドを乗っ取って実行されるのを防ぐため。
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<string> _executions = [];

    public ControllableJobHandler(string jobType) => JobType = jobType;

    /// <inheritdoc />
    public string JobType { get; }

    /// <summary>ハンドラに入ったら完了するタスク。2 回目以降の実行では既に完了している。</summary>
    public Task Entered => _entered.Task;

    /// <summary>実行のたびに受け取った parameters。実行された順に並ぶ。</summary>
    public IReadOnlyList<string> Executions => _executions;

    /// <summary>渡された <see cref="CancellationToken"/> が発火したか。</summary>
    public bool CancellationObserved { get; private set; }

    /// <summary>ハンドラを解放して正常終了させる。</summary>
    public void Release() => _released.TrySetResult();

    /// <summary>ハンドラを解放して例外で終了させる。</summary>
    public void Throw(Exception exception) => _released.TrySetException(exception);

    /// <inheritdoc />
    public async Task ExecuteAsync(JobId jobId, string parameters, CancellationToken cancellationToken)
    {
        _executions.Add(parameters);
        _entered.TrySetResult();

        try
        {
            // 時間ではなく合図かキャンセルで抜ける。
            await _released.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CancellationObserved = true;
            throw;
        }
    }
}
