namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Phase 18: a monotonic manual clock. <see cref="GetTimestamp"/> and <see cref="GetUtcNow"/> move only when the test calls
/// <see cref="AdvanceTo"/>; timers fire on the calling thread, in due order, each with the clock set to its own due time.
/// </summary>
internal sealed class MonotonicTestClock : TimeProvider
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    private readonly object _gate = new();
    private readonly List<ClockTimer> _timers = [];
    private long _ticks; // TimeSpan ticks since the origin

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
            return _ticks;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return Origin + TimeSpan.FromTicks(_ticks);
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>The time since the origin.</summary>
    public TimeSpan Now => TimeSpan.FromTicks(GetTimestamp());

    /// <summary>Timers that are armed (created and not disposed, with a due time).</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_gate)
                return _timers.Count(t => t.DueAt is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock to <paramref name="target"/> (since the origin), firing every timer that becomes due on the way.</summary>
    public void AdvanceTo(TimeSpan target)
    {
        while (true)
        {
            ClockTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.DueAt is { } at && at <= target.Ticks).OrderBy(t => t.DueAt).FirstOrDefault();
                if (next is null)
                {
                    if (target.Ticks > _ticks)
                        _ticks = target.Ticks;
                    return;
                }

                if (next.DueAt!.Value > _ticks)
                    _ticks = next.DueAt.Value;
                next.DueAt = null; // one-shot
            }

            next.Fire();
        }
    }

    public void AdvanceTo(double seconds) => AdvanceTo(TimeSpan.FromSeconds(seconds));

    private sealed class ClockTimer(MonotonicTestClock owner, TimerCallback callback, object? state) : ITimer
    {
        public long? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (!owner._timers.Contains(this))
                    owner._timers.Add(this);
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + Math.Max(0, dueTime.Ticks);
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                DueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Phase 18: a dispatcher-like context whose posted callbacks wait until the test runs them, on the test thread. A callback
/// can be taken out (<see cref="TakeAll"/>) and run later, to deliver a timer callback late.
/// </summary>
internal sealed class QueuedSynchronizationContext : SynchronizationContext
{
    private readonly object _gate = new();
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly AutoResetEvent _posted = new(false);

    public int Count
    {
        get
        {
            lock (_gate)
                return _queue.Count;
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        lock (_gate)
            _queue.Enqueue((d, state));
        _posted.Set();
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs queued callbacks (and those they post) until the queue is empty. Returns how many ran.</summary>
    public int RunAll()
    {
        var ran = 0;
        while (true)
        {
            (SendOrPostCallback Callback, object? State) item;
            lock (_gate)
            {
                if (_queue.Count == 0)
                    return ran;
                item = _queue.Dequeue();
            }

            item.Callback(item.State);
            ran++;
        }
    }

    /// <summary>Removes and returns every queued callback without running it.</summary>
    public List<Action> TakeAll()
    {
        lock (_gate)
        {
            var items = _queue.Select(i => (Action)(() => i.Callback(i.State))).ToList();
            _queue.Clear();
            return items;
        }
    }

    /// <summary>
    /// Runs queued callbacks until <paramref name="done"/> holds, waiting for callbacks posted from other threads (for example
    /// the continuation of a real SQLite call). A synchronization point, not a sleep.
    /// </summary>
    public void RunUntil(Func<bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            RunAll();
            if (done())
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The expected callbacks were not posted.");
            _posted.WaitOne(TimeSpan.FromMilliseconds(50));
        }
    }

    /// <summary>Makes this the current context of the calling thread until the result is disposed.</summary>
    public IDisposable Install()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        return new Restore(previous);
    }

    private sealed class Restore(SynchronizationContext? previous) : IDisposable
    {
        public void Dispose() => SetSynchronizationContext(previous);
    }
}
