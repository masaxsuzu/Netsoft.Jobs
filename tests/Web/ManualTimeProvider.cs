namespace Netsoft.Jobs.Web.Tests;

/// <summary>
/// テストが針を進める時計。keep-alive の間隔を実時間で待たずに越えるために使う。
/// </summary>
/// <remarks>
/// <para>
/// 対応するのは一発だけのタイマー（period が無限）のみ。使う側が
/// <see cref="CancellationTokenSource"/> のタイムアウトだけなので、周期の再スケジュールを
/// 実装しても検証されないコードになる。想定外の使い方は例外で拒んで気づけるようにする。
/// </para>
/// <para>
/// コールバックはロックの外で呼ぶ。キャンセルの続き（keep-alive のループ）が同じスレッドで
/// 同期実行されて新しいタイマーを作りに来るので、握ったままだと自分のロックで詰まる。
/// </para>
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        ManualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>針を進め、期限が来たタイマーを発火させる。</summary>
    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due = [];
        lock (_gate)
        {
            _now += delta;
            foreach (ManualTimer timer in _timers)
            {
                if (timer.TryClaim(_now))
                {
                    due.Add(timer);
                }
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAt;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("周期タイマーは使う側がいないので対応しない。");
            }

            lock (_owner._gate)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
            }

            return true;
        }

        /// <summary>期限が来ていれば発火の権利を取る。呼び出し側がロックを握っていること。</summary>
        public bool TryClaim(DateTimeOffset now)
        {
            if (_dueAt is { } dueAt && dueAt <= now)
            {
                // 一発だけのタイマーなので、発火したら期限を消して二重発火を防ぐ。
                _dueAt = null;
                return true;
            }

            return false;
        }

        public void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._gate)
            {
                _dueAt = null;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
