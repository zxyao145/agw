namespace Agw.Testing;

/// <summary>Deterministic one-shot timers for persistence scheduling tests.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private TaskCompletionSource _timerChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _now = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
            return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
            timer.Change(dueTime, period);
        }
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        List<ManualTimer> ready;
        lock (_sync)
        {
            _now += duration;
            ready = _timers.Where(timer => timer.Due <= _now).ToList();
            foreach (var timer in ready)
                timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _now + timer.Period;
        }
        foreach (var timer in ready)
            timer.Fire();
    }

    // Wait for background code to arm its delay before advancing virtual time.
    public async Task WaitForTimerAsync(TimeSpan dueTime, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_timers.Any(timer => timer.Due == _now + dueTime))
                    return;
                changed = _timerChanged.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private bool _disposed;
        public DateTimeOffset Due { get; set; }
        public TimeSpan Period { get; private set; }

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._sync)
            {
                if (_disposed)
                    return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _owner._now + dueTime;
                Period = period;
                var changed = _owner._timerChanged;
                _owner._timerChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
                changed.TrySetResult();
                return true;
            }
        }

        public void Fire()
        {
            lock (_owner._sync)
            {
                if (_disposed)
                    return;
            }
            _callback(_state);
        }

        public void Dispose()
        {
            lock (_owner._sync)
            {
                _disposed = true;
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
