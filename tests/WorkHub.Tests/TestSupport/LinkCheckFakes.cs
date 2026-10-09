using System.Collections.Concurrent;
using System.Diagnostics;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;

namespace WorkHub.Tests.TestSupport;

/// <summary>Records started processes instead of starting them.</summary>
internal sealed class RecordingProcessStarter : IProcessStarter
{
    public List<ProcessStartInfo> Started { get; } = [];

    public void Start(ProcessStartInfo startInfo) => Started.Add(startInfo);
}

/// <summary>A clock whose timers fire only when the test advances it (no dependency on wall-clock time).</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that became due (on the calling thread).</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt is { } at && at <= _now).OrderBy(t => t.DueAt).ToList();
            foreach (var timer in due)
                timer.DueAt = null; // one-shot
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                    return true;
                }

                DueAt = owner._now + dueTime;
                owner._timers.Add(this);
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
/// A filesystem probe the test controls: each call blocks until its target is released (or answers at once when the
/// target has a preset answer). Records every OS-level call. <see cref="Dispose"/> releases everything still blocked.
/// </summary>
internal sealed class GatedProbe : ILinkProbe, IDisposable
{
    private readonly ConcurrentDictionary<string, LinkProbeResult> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _calls = new();
    private int _active;

    /// <summary>Targets answer at once with this result (no gate).</summary>
    public void Answer(string target, LinkProbeResult result) => _answers[target] = result;

    /// <summary>The next call for this target blocks until <see cref="Release"/>.</summary>
    public void Block(string target) => _gates[target] = new ManualResetEventSlim(false);

    public void Release(string target, LinkProbeResult? result = null)
    {
        if (result is not null)
            _answers[target] = result;
        if (_gates.TryGetValue(target, out var gate))
            gate.Set();
    }

    public IReadOnlyList<string> Calls => _calls.ToList();

    /// <summary>OS calls currently inside <see cref="Probe"/>.</summary>
    public int Active => Volatile.Read(ref _active);

    public LinkProbeResult Probe(string path)
    {
        _calls.Enqueue(path);
        Interlocked.Increment(ref _active);
        try
        {
            if (_gates.TryGetValue(path, out var gate))
                gate.Wait(TimeSpan.FromSeconds(30)); // safety net so a broken test cannot hang the run
            return _answers.TryGetValue(path, out var answer) ? answer : LinkProbeResult.Found(isDirectory: true);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>Waits (real time, bounded) until <paramref name="count"/> calls have entered the probe.</summary>
    public void WaitForCalls(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_calls.Count < count)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Expected {count} probe calls, saw {_calls.Count}.");
            Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        foreach (var gate in _gates.Values)
            gate.Set();
    }
}

/// <summary>A scriptable checker for view-model tests: each request waits until the test completes it.</summary>
internal sealed class ScriptedLinkChecker : ILinkHealthChecker
{
    private readonly ConcurrentQueue<Request> _requests = new();

    public sealed record Request(ProjectLinkType Type, string Target, TaskCompletionSource<LinkHealthStatus> Completion, CancellationToken Token);

    public IReadOnlyList<Request> Requests => _requests.ToList();

    public Task<LinkHealthStatus> CheckAsync(ProjectLinkType type, string target, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<LinkHealthStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Enqueue(new Request(type, target, completion, cancellationToken));
        return completion.Task.WaitAsync(cancellationToken);
    }
}
