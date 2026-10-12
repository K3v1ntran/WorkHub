namespace WorkHub.Core.Workspace.Search;

public enum RunStatus
{
    Completed,

    /// <summary>A newer request replaced this one (before it started, or while it ran); its result is discarded.</summary>
    Superseded,

    Failed,
}

public sealed record RunOutcome<T>(RunStatus Status, T? Value, Exception? Error)
{
    public static RunOutcome<T> Superseded { get; } = new(RunStatus.Superseded, default, null);
}

/// <summary>
/// Bounded "latest request wins" execution (Phase 6 proposal §5.2). At most ONE call of the work runs at a time and at most
/// ONE request waits: submitting while a call runs cancels that call's token and replaces any waiting request, whose task
/// completes as <see cref="RunStatus.Superseded"/>. When the running call returns (however long its cancellation takes),
/// the waiting request starts. Rapid submissions therefore never accumulate work.
/// </summary>
public sealed class LatestOnlyRunner<TRequest, TResult>(Func<TRequest, CancellationToken, Task<TResult>> work)
{
    private readonly object _gate = new();
    private Running? _running;
    private (TRequest Request, TaskCompletionSource<RunOutcome<TResult>> Completion)? _pending;
    private int _started;

    /// <summary>Calls of the work started so far (instrumentation for tests).</summary>
    public int StartedCount => Volatile.Read(ref _started);

    /// <summary>A call of the work is running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _running is not null;
        }
    }

    /// <summary>A request is waiting for the running call to return.</summary>
    public bool HasPending
    {
        get
        {
            lock (_gate)
                return _pending is not null;
        }
    }

    public Task<RunOutcome<TResult>> Submit(TRequest request)
    {
        var completion = new TaskCompletionSource<RunOutcome<TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Running? start = null;
        TaskCompletionSource<RunOutcome<TResult>>? replaced = null;
        lock (_gate)
        {
            if (_running is null)
            {
                start = _running = new Running(request, completion);
            }
            else
            {
                _running.Supersede();
                replaced = _pending?.Completion;
                _pending = (request, completion);
            }
        }

        replaced?.TrySetResult(RunOutcome<TResult>.Superseded);
        if (start is not null)
            _ = RunAsync(start);
        return completion.Task;
    }

    /// <summary>Cancels the running call and drops the waiting request (e.g. the query was cleared).</summary>
    public void CancelAll()
    {
        TaskCompletionSource<RunOutcome<TResult>>? dropped;
        lock (_gate)
        {
            _running?.Supersede();
            dropped = _pending?.Completion;
            _pending = null;
        }

        dropped?.TrySetResult(RunOutcome<TResult>.Superseded);
    }

    private async Task RunAsync(Running running)
    {
        while (true)
        {
            Interlocked.Increment(ref _started);
            RunOutcome<TResult> outcome;
            try
            {
                var value = await work(running.Request, running.Token).ConfigureAwait(false);
                outcome = running.IsSuperseded ? RunOutcome<TResult>.Superseded : new RunOutcome<TResult>(RunStatus.Completed, value, null);
            }
            catch (Exception ex)
            {
                outcome = running.IsSuperseded ? RunOutcome<TResult>.Superseded : new RunOutcome<TResult>(RunStatus.Failed, default, ex);
            }

            Running? next = null;
            lock (_gate)
            {
                if (_pending is { } pending)
                {
                    next = _running = new Running(pending.Request, pending.Completion);
                    _pending = null;
                }
                else
                {
                    _running = null;
                }
            }

            running.Dispose();
            running.Completion.TrySetResult(outcome);
            if (next is null)
                return;
            running = next;
        }
    }

    private sealed class Running(TRequest request, TaskCompletionSource<RunOutcome<TResult>> completion) : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private volatile bool _superseded;

        public TRequest Request { get; } = request;

        public TaskCompletionSource<RunOutcome<TResult>> Completion { get; } = completion;

        public CancellationToken Token => _cts.Token;

        public bool IsSuperseded => _superseded;

        public void Supersede()
        {
            _superseded = true;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the call has just returned; nothing left to cancel
            }
        }

        public void Dispose() => _cts.Dispose();
    }
}
