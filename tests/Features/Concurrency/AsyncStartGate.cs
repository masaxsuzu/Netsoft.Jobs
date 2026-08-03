namespace Netsoft.Jobs.Features.Tests.Concurrency;

/// <summary>
/// 参加者全員が揃うまで待ち合わせる。待ち方は非同期。
/// </summary>
/// <remarks>
/// <see cref="Barrier"/> を使わないのは、あちらがスレッドを止めて待つから。
/// CI のコア数（2〜4）より多い参加者で待ち合わせると、スレッドプールが
/// 新しいスレッドを注ぎ足すまで全員が止まり、試験が秒単位で遅くなる。
/// 待ち合わせ自体は競合を起こすための道具であって、測りたいものではない。
/// </remarks>
public sealed class AsyncStartGate
{
    // 待っている側の続きがここのスレッドを乗っ取らないようにする。
    // 乗っ取られると「全員が揃ってから一斉に」が崩れる。
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _remaining;

    public AsyncStartGate(int participants)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(participants, 1);

        _remaining = participants;
    }

    /// <summary>自分が着いたことを知らせ、全員が揃うまで待つ。</summary>
    public Task SignalAndWaitAsync()
    {
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _opened.TrySetResult();
        }

        return _opened.Task;
    }
}
