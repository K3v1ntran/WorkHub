using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.TestSupportTests;

/// <summary>
/// Phase 11 teardown quiescence (§10.1 correction): the root is deleted only on POSITIVE evidence that every tracked
/// operation of every area was observed complete and the context confirmed its stop. A failure to establish quiescence
/// (pass limit, unreadable snapshot, unobserved area, replacement work, work published after settling) keeps the root.
/// Pending work is driven by explicit gates and handshakes; every gate is released before the test returns.
/// </summary>
public sealed class TeardownQuiescenceTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Handshake = TimeSpan.FromSeconds(10);
    private readonly SingleThreadContext _context = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkHubTests", Guid.NewGuid().ToString("N"));

    public TeardownQuiescenceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Assert.True(_context.IsStopped || _context.Stop(Bound), "the context did not stop");
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Delete() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void PassLimitExhausted_WithAPendingLatestTask_NeverDeletesTheRoot()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var deleted = false;
        ContextTeardown.TrackedSet restless = () =>
        {
            var n = Interlocked.Increment(ref calls);
            return [("Load", n <= 20 ? Task.FromResult(n) : pending.Task)]; // a new instance every read; the latest is pending
        };
        try
        {
            var failure = Record.Exception(() => ContextTeardown.Run(_context, () => { }, [restless], () => deleted = true, _root, Bound));

            Assert.False(deleted);
            Assert.True(Directory.Exists(_root));
            Assert.Equal(_root, Assert.IsType<ContextTeardownException>(failure).PreservedRoot);
        }
        finally
        {
            pending.TrySetResult();
        }
    }

    [Fact]
    public async Task AFaultInTheFirstArea_DoesNotPreventObservingASecondPendingArea()
    {
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondObserved = new ManualResetEventSlim();
        bool? deletedWhileSecondPending = null;
        var area1Load = Task.FromException(new IOException("area 1")); // one instance, as a view-model property holds it
        ContextTeardown.TrackedSet area1 = () => [("Area1.Load", area1Load)];
        ContextTeardown.TrackedSet area2 = () =>
        {
            secondObserved.Set();
            return [("Area2.Load", second.Task)];
        };
        var teardown = Task.Run(() => Record.Exception(() => ContextTeardown.Run(_context, () => { }, [area1, area2],
            () => { deletedWhileSecondPending = !second.Task.IsCompleted; Delete(); }, _root, Bound)));
        try
        {
            var first = WaitHandle.WaitAny([secondObserved.WaitHandle, ((IAsyncResult)teardown).AsyncWaitHandle], Handshake);
            Assert.Equal(0, first);                // area 2 was observed while teardown was still waiting
            Assert.Null(deletedWhileSecondPending); // and nothing was deleted
        }
        finally
        {
            second.TrySetResult();
        }

        var failure = await teardown;

        Assert.False(deletedWhileSecondPending);
        var reported = Assert.IsAssignableFrom<AggregateException>(failure);
        Assert.IsNotType<ContextTeardownException>(failure);
        Assert.Contains(reported.Flatten().InnerExceptions, e => e is IOException { Message: "area 1" });
    }

    [Fact]
    public async Task AFaultedOperationThatPublishesReplacementWork_CannotCauseEarlyDeletion()
    {
        var firstOp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new[] { firstOp.Task };
        using var sawFirst = new ManualResetEventSlim();
        using var sawReplacement = new ManualResetEventSlim();
        bool? deletedWhileReplacementPending = null;
        ContextTeardown.TrackedSet set = () =>
        {
            var task = Volatile.Read(ref current[0]);
            (task == firstOp.Task ? sawFirst : sawReplacement).Set();
            return [("Op", task)];
        };
        var teardown = Task.Run(() => Record.Exception(() => ContextTeardown.Run(_context, () => { }, [set],
            () => { deletedWhileReplacementPending = !replacement.Task.IsCompleted; Delete(); }, _root, Bound)));
        try
        {
            Assert.True(sawFirst.Wait(Handshake));
            Volatile.Write(ref current[0], replacement.Task); // the failing operation hands over to new work...
            firstOp.SetException(new IOException("first"));   // ...and then completes with its fault

            var first = WaitHandle.WaitAny([sawReplacement.WaitHandle, ((IAsyncResult)teardown).AsyncWaitHandle], Handshake);
            Assert.Equal(0, first);                     // the replacement was observed
            Assert.Null(deletedWhileReplacementPending); // and nothing was deleted
        }
        finally
        {
            replacement.TrySetResult();
        }

        var failure = await teardown;

        Assert.False(deletedWhileReplacementPending);
        Assert.Contains(Assert.IsAssignableFrom<AggregateException>(failure).Flatten().InnerExceptions, e => e is IOException { Message: "first" });
    }

    [Fact]
    public void AnUnreadableSnapshot_IsUnconfirmedQuiescence_AndReportsThePreservedRoot()
    {
        var deleted = false;
        ContextTeardown.TrackedSet broken = () => throw new InvalidOperationException("snapshot");

        var failure = Assert.IsType<ContextTeardownException>(Record.Exception(() =>
            ContextTeardown.Run(_context, () => { }, [broken], () => deleted = true, _root, Bound)));

        Assert.False(deleted);
        Assert.Equal(_root, failure.PreservedRoot);
        Assert.Contains(failure.Flatten().InnerExceptions, e => e.Message == "snapshot");
    }

    [Fact]
    public void WorkPublishedAfterSettling_IsSeenAtThePostStopCheck_AndKeepsTheRoot()
    {
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = false;
        // On the context the operation looks settled; read again after the stop, a new pending operation is there.
        ContextTeardown.TrackedSet set = () => _context.IsCurrentThread ? [("Op", Task.CompletedTask)] : [("Op", late.Task)];
        try
        {
            var failure = Assert.IsType<ContextTeardownException>(Record.Exception(() =>
                ContextTeardown.Run(_context, () => { }, [set], () => deleted = true, _root, Bound)));

            Assert.False(deleted);
            Assert.Equal(_root, failure.PreservedRoot);
        }
        finally
        {
            late.TrySetResult();
        }
    }

    [Fact]
    public void FullyObservedSettledFaults_AreReported_AfterAConfirmedStop_AndTheRootIsDeleted()
    {
        var load = Task.FromException(new IOException("disk"));
        var restore = Task.FromCanceled(new CancellationToken(true));
        ContextTeardown.TrackedSet area1 = () => [("Area1.Load", load)];
        ContextTeardown.TrackedSet area2 = () => [("Area2.Count", Task.CompletedTask), ("Area2.Restore", restore)];

        var failure = Record.Exception(() => ContextTeardown.Run(_context, () => { }, [area1, area2], Delete, _root, Bound));

        Assert.IsNotType<ContextTeardownException>(failure);
        Assert.Contains(Assert.IsAssignableFrom<AggregateException>(failure).Flatten().InnerExceptions, e => e is IOException { Message: "disk" });
        Assert.True(_context.IsStopped);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void SettledOperationsWithoutFaults_TearDownCleanly()
    {
        var gate = new StoreGate();
        var released = false;
        ContextTeardown.TrackedSet set = () => [("Held write", gate.Released)];

        ContextTeardown.Run(_context, () => { released = true; gate.TryRelease(); }, [set], Delete, _root, Bound);

        Assert.True(released);
        Assert.True(_context.IsStopped);
        Assert.False(Directory.Exists(_root));
    }
}
