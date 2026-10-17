using System.Collections.Concurrent;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// A WPF-Dispatcher-like test context (Phase 11 B1): one dedicated thread runs every posted continuation in FIFO order, so
/// a view model's continuations never run concurrently. <see cref="PumpUntil"/> is the nested loop a modal dialog runs.
/// <para>
/// Lifetime (Phase 11 §10.1): <see cref="Stop"/> stops accepting work, drops what is still queued (never runs it), lets
/// the current callback return, and reports success only when the pump loop itself signals that it has exited. Being a
/// background thread is not treated as a shutdown. Threads are never aborted.
/// </para>
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext
{
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(10);

    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly ConcurrentQueue<Exception> _faults = new();
    private readonly ManualResetEventSlim _exited = new();
    private readonly Thread _thread;
    private volatile bool _stopping;
    private int _dropped;

    public SingleThreadContext()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "SingleThreadContext" };
        _thread.Start();
    }

    /// <summary>Items posted after <see cref="Stop"/> began, or still queued when it did: counted, never run.</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    public bool IsStopped => _exited.IsSet;

    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_stopping || !TryAdd(d, state))
            Interlocked.Increment(ref _dropped);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (IsCurrentThread)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? failure = null;
        Post(_ =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        }, null);
        if (!done.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Send was not executed by the test context.");
        if (failure is not null)
            throw new AggregateException("A Send callback failed.", failure);
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Runs <paramref name="body"/> on the context thread and completes with it. Faults of the body and of every posted
    /// callback are rethrown. A body that has not completed within <paramref name="timeout"/> fails with
    /// <see cref="TimeoutException"/> (the body is not cancelled; teardown decides what happens next).
    /// </summary>
    public async Task RunAsync(Func<Task> body, TimeSpan timeout)
    {
        if (IsCurrentThread)
            throw new InvalidOperationException("RunAsync is called from outside the context.");
        if (_stopping)
            throw new InvalidOperationException("The test context is stopped.");
        var completion = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            Task task;
            try
            {
                task = body();
            }
            catch (Exception ex)
            {
                task = Task.FromException(ex);
            }

            task.ContinueWith(t => completion.TrySetResult(t), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }, null);

        var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout)).ConfigureAwait(false);
        var callbackFaults = TakeFaults();
        if (finished != completion.Task)
            throw new TimeoutException($"The test body did not complete within {timeout.TotalSeconds:0.#} s (queued: {_queue.Count}).", Combine(callbackFaults));

        var bodyTask = completion.Task.Result;
        var faults = new List<Exception>();
        if (bodyTask.Exception is { } bodyFault)
            faults.AddRange(bodyFault.InnerExceptions);
        else if (bodyTask.IsCanceled)
            faults.Add(new TaskCanceledException(bodyTask));
        faults.AddRange(callbackFaults);
        if (faults.Count == 1 && bodyTask.IsFaulted)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(faults[0]).Throw();
        if (faults.Count > 0)
            throw new AggregateException("The test body or a posted callback failed.", faults);
    }

    public void Run(Func<Task> body, TimeSpan timeout) => RunAsync(body, timeout).GetAwaiter().GetResult();

    /// <summary>
    /// The nested message loop of a modal dialog: runs queued callbacks on the context thread until
    /// <paramref name="condition"/> holds. Returns false when it did not hold within <paramref name="timeout"/>.
    /// </summary>
    public bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        if (!IsCurrentThread)
            throw new InvalidOperationException("PumpUntil runs on the context thread only.");
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
                return false;
            if (_queue.TryTake(out var item, left < Slice ? left : Slice))
                Execute(item);
        }

        return true;
    }

    /// <summary>
    /// On the context thread: pumps until <paramref name="condition"/> holds (a modal wait). Elsewhere (classes that do not
    /// use the context): calls <paramref name="block"/>, the plain blocking wait they always used.
    /// </summary>
    public static bool WaitOrPump(Func<bool> condition, Func<TimeSpan, bool> block, TimeSpan timeout) =>
        Current is SingleThreadContext context && context.IsCurrentThread ? context.PumpUntil(condition, timeout) : block(timeout);

    /// <summary>
    /// Cooperative stop. Returns true only when the pump loop has signalled its exit within <paramref name="bound"/>; false
    /// means a callback may still be running (the caller must not delete resources it could use).
    /// </summary>
    public bool Stop(TimeSpan bound)
    {
        _stopping = true;
        _queue.CompleteAdding();
        if (!_exited.Wait(bound))
            return false;
        _thread.Join(bound); // after the exit signal only the loop's epilogue remains
        return true;
    }

    /// <summary>Faults of posted callbacks collected since the last call.</summary>
    public IReadOnlyList<Exception> TakeFaults()
    {
        var faults = new List<Exception>();
        while (_faults.TryDequeue(out var fault))
            faults.Add(fault);
        return faults;
    }

    private bool TryAdd(SendOrPostCallback d, object? state)
    {
        try
        {
            return _queue.TryAdd((d, state));
        }
        catch (InvalidOperationException)
        {
            return false; // adding completed
        }
    }

    private void Pump()
    {
        SetSynchronizationContext(this);
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (_stopping)
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }

                Execute(item);
            }
        }
        finally
        {
            _exited.Set();
        }
    }

    private void Execute((SendOrPostCallback Callback, object? State) item)
    {
        try
        {
            item.Callback(item.State);
        }
        catch (Exception ex)
        {
            _faults.Enqueue(ex);
        }
    }

    private static Exception? Combine(IReadOnlyList<Exception> faults) =>
        faults.Count switch { 0 => null, 1 => faults[0], _ => new AggregateException(faults) };
}

/// <summary>
/// Teardown for a test that runs on a <see cref="SingleThreadContext"/> (Phase 11 §10.1 and its quiescence correction).
/// <list type="number">
/// <item>Release held gates.</item>
/// <item>Observe every area's tracked operations ON the context, which keeps pumping their continuations
/// (see <see cref="TrackedTasks"/>).</item>
/// <item>Stop the context and confirm that the pump loop exited.</item>
/// <item>Read the tracked sets again after the stop: they must still be the same completed instances.</item>
/// <item>Only then delete the root.</item>
/// </list>
/// Deletion requires POSITIVE evidence from steps 2–4. A fault of a fully observed, completed operation is reported but
/// does not block deletion; anything that leaves quiescence unconfirmed (the bound expired, a set could not be read, the
/// pass limit was reached, the observation itself failed, work appeared after settling, the stop was not confirmed) keeps
/// the root for diagnosis and fails with <see cref="ContextTeardownException"/>. A stopped pump says nothing about store
/// work on the thread pool; only completed tracked operations do. Threads are never aborted.
/// </summary>
internal static class ContextTeardown
{
    /// <summary>One area's tracked operations, read on the context (and once more after the stop).</summary>
    public delegate IReadOnlyList<(string Name, Task Task)> TrackedSet();

    public static void Run(SingleThreadContext context, Action releaseGates, IReadOnlyList<TrackedSet> trackedSets, Action deleteRoot, string rootPath, TimeSpan bound)
    {
        var faults = new List<Exception>();
        var outstanding = new List<string>();
        var sets = trackedSets.Select(set => (Func<IReadOnlyList<(string Name, Task Task)>>)(() => set())).ToList();

        try
        {
            releaseGates();
        }
        catch (Exception ex)
        {
            faults.Add(ex);
        }

        TrackedTasks.Observation? observation = null;
        try
        {
            context.Run(async () => observation = await TrackedTasks.ObserveAsync(sets), bound);
        }
        catch (TimeoutException ex)
        {
            outstanding.Add("tracked operations did not settle within the bound");
            faults.Add(ex);
        }
        catch (Exception ex)
        {
            faults.AddRange(Flatten(ex)); // posted-callback faults, or the observation could not run at all
        }

        if (observation is null)
        {
            if (outstanding.Count == 0)
                outstanding.Add("tracked operations could not be observed");
        }
        else
        {
            faults.AddRange(observation.Faults);
            if (!observation.Quiescent)
                outstanding.Add($"quiescence was not established ({observation.Detail})");
        }

        var stopped = context.Stop(bound);
        if (!stopped)
            outstanding.Add("the context did not confirm its stop (a callback may still be running)");
        faults.AddRange(context.TakeFaults());

        if (stopped && outstanding.Count == 0 && observation is not null)
        {
            try
            {
                var after = sets.SelectMany(set => set()).ToList();
                if (!TrackedTasks.SameAndCompleted(after, observation.Final))
                    outstanding.Add("tracked work changed after settling (read again after the stop)");
            }
            catch (Exception ex)
            {
                faults.Add(ex);
                outstanding.Add("the tracked sets could not be read again after the stop");
            }
        }

        if (outstanding.Count > 0)
            throw new ContextTeardownException(
                $"Teardown incomplete: {string.Join("; ", outstanding)}. The test root was NOT deleted and is kept for diagnosis: {rootPath}",
                rootPath, faults);

        try
        {
            deleteRoot();
        }
        catch (Exception ex)
        {
            faults.Add(ex);
        }

        if (faults.Count > 0)
            throw new AggregateException("Teardown completed (quiescence confirmed, root deleted) but reported faults.", faults);
    }

    private static IEnumerable<Exception> Flatten(Exception ex) => ex is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [ex];
}

internal sealed class ContextTeardownException(string message, string preservedRoot, IReadOnlyList<Exception> faults)
    : AggregateException(message, faults)
{
    public string PreservedRoot { get; } = preservedRoot;
}
