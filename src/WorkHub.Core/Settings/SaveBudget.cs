namespace WorkHub.Core.Settings;

/// <summary>
/// Phase 16 (D12): the time a save may spend <b>before writing starts</b>, measured on the monotonic timestamp of one
/// <see cref="TimeProvider"/> (<see cref="TimeProvider.GetTimestamp"/> at the start, <see cref="TimeProvider.GetElapsedTime(long)"/>
/// for every check). Wall-clock (UTC) changes, forward or back, never move it. The provider travels with the budget, so the
/// snapshot in <c>WindowStateSaver</c>, the lock wait and the last check in the store all read the same time source.
/// </summary>
public sealed class SaveBudget
{
    private readonly long _started;

    private SaveBudget(TimeProvider time, TimeSpan limit)
    {
        if (limit < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limit));
        Time = time;
        Limit = limit;
        _started = time.GetTimestamp();
    }

    /// <summary>Starts a budget of <paramref name="limit"/> now, on <paramref name="time"/>'s monotonic timestamp.</summary>
    public static SaveBudget StartNow(TimeProvider time, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(time);
        return new SaveBudget(time, limit);
    }

    /// <summary>A budget that never runs out (tests, and callers without a deadline).</summary>
    public static SaveBudget Unlimited => new(TimeProvider.System, TimeSpan.MaxValue);

    /// <summary>The time source every check of this budget uses.</summary>
    public TimeProvider Time { get; }

    /// <summary>The whole budget.</summary>
    public TimeSpan Limit { get; }

    /// <summary>Monotonic time since the budget started.</summary>
    public TimeSpan Elapsed => Time.GetElapsedTime(_started);

    public bool IsExpired => Elapsed >= Limit;

    /// <summary>What is left of the budget (never negative).</summary>
    public TimeSpan Remaining
    {
        get
        {
            var elapsed = Elapsed;
            if (elapsed >= Limit)
                return TimeSpan.Zero;
            return elapsed <= TimeSpan.Zero ? Limit : Limit - elapsed;
        }
    }
}
