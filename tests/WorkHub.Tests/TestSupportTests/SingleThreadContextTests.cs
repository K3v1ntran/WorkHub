using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.TestSupportTests;

/// <summary>
/// Phase 11 B1/B3 and §10.1: the single-threaded test context, its teardown sequence and Settle. Every test that blocks a
/// callback releases it before it returns and confirms the context stopped, so no thread is left blocked.
/// </summary>
public sealed class SingleThreadContextTests : IDisposable
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);
    private readonly SingleThreadContext _context = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkHubTests", Guid.NewGuid().ToString("N"));

    public SingleThreadContextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Assert.True(_context.IsStopped || _context.Stop(Long), "the context did not stop");
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ---- Context -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Continuations_RunOnTheContextThread()
    {
        var threads = new List<int>();
        await _context.RunAsync(async () =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Run(() => Thread.Sleep(1)); // store-like work elsewhere; the continuation comes back
            threads.Add(Environment.CurrentManagedThreadId);
        }, Long);

        Assert.Single(threads.Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, threads[0]);
    }

    [Fact]
    public async Task ANestedPump_RunsQueuedCallbacks_LikeAModalDialog()
    {
        await _context.RunAsync(() =>
        {
            var ran = false;
            SynchronizationContext.Current!.Post(_ => ran = true, null);
            Assert.True(_context.PumpUntil(() => ran, Long));
            Assert.False(_context.PumpUntil(() => false, TimeSpan.FromMilliseconds(50))); // a condition that never holds times out
            return Task.CompletedTask;
        }, Long);
    }

    [Fact]
    public async Task TheBodysFault_IsRethrown()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => _context.RunAsync(async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("body");
        }, Long));

        Assert.Equal("body", failure.Message);
    }

    [Fact]
    public async Task APostedCallbacksFault_IsRethrown_EvenWhenTheBodySucceeds()
    {
        var failure = await Assert.ThrowsAsync<AggregateException>(() => _context.RunAsync(() =>
        {
            var ran = false;
            SynchronizationContext.Current!.Post(_ =>
            {
                ran = true;
                throw new InvalidOperationException("callback");
            }, null);
            Assert.True(_context.PumpUntil(() => ran, Long));
            return Task.CompletedTask;
        }, Long));

        Assert.Contains(failure.InnerExceptions, e => e.Message == "callback");
    }

    [Fact]
    public async Task ABodyThatDoesNotFinish_TimesOut_AndCanStillBeStoppedCleanly()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(() => _context.RunAsync(() => hold.Task, TimeSpan.FromMilliseconds(200)));

        hold.SetResult(); // the body is awaiting, not blocking the thread
        Assert.True(_context.Stop(Long));
    }

    [Fact]
    public void AfterStop_QueuedAndLaterPosts_AreDroppedNeverRun()
    {
        Assert.True(_context.Stop(Long));
        var ran = false;

        _context.Post(_ => ran = true, null);

        Assert.False(ran);
        Assert.Equal(1, _context.Dropped);
        Assert.Throws<InvalidOperationException>(() => _context.Run(() => Task.CompletedTask, Long));
    }

    [Fact]
    public void Stop_IsNotConfirmed_WhileACallbackIsStillRunning()
    {
        using var blocker = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        _context.Post(_ =>
        {
            entered.Set();
            blocker.Wait();
        }, null);
        Assert.True(entered.Wait(Long));

        Assert.False(_context.Stop(TimeSpan.FromMilliseconds(200)));
        Assert.False(_context.IsStopped); // a background thread is not a confirmed stop

        blocker.Set();
        Assert.True(_context.Stop(Long));
        Assert.True(_context.IsStopped);
    }

    // ---- Teardown (§10.1) ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Teardown_ReleasesGates_ThenSettlesOnTheContext_ThenStops_ThenDeletes()
    {
        var steps = new List<string>();
        var gate = new StoreGate();
        var settled = gate.Released.ContinueWith(_ => steps.Add("settled"), TaskScheduler.Default);

        ContextTeardown.Run(
            _context,
            releaseGates: () =>
            {
                steps.Add("release");
                gate.TryRelease();
            },
            trackedSets:
            [
                () =>
                {
                    Assert.True(_context.IsCurrentThread || _context.IsStopped); // observed on the context; read again after the stop
                    return [("Held write", settled)]; // would never complete if the gate were not released first
                },
            ],
            deleteRoot: () =>
            {
                Assert.True(_context.IsStopped);
                steps.Add("delete");
                Directory.Delete(_root, recursive: true);
            },
            _root,
            Long);

        Assert.Equal(["release", "settled", "delete"], steps);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Teardown_WhenACallbackIsStillRunning_FailsClearly_AndKeepsTheRoot()
    {
        using var blocker = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        _context.Post(_ =>
        {
            entered.Set();
            blocker.Wait();
        }, null);
        Assert.True(entered.Wait(Long));
        var deleted = false;

        var failure = Assert.Throws<ContextTeardownException>(() => ContextTeardown.Run(
            _context, () => { }, [() => []], () => deleted = true, _root, TimeSpan.FromMilliseconds(200)));

        Assert.False(deleted);
        Assert.True(Directory.Exists(_root));
        Assert.Equal(_root, failure.PreservedRoot);
        Assert.Contains("NOT deleted", failure.Message);
        Assert.Contains("did not confirm its stop", failure.Message);

        blocker.Set(); // no thread is left blocked
        Assert.True(_context.Stop(Long));
    }

    [Fact]
    public void Teardown_ASettleFault_IsReported_AfterAConfirmedStop_AndTheRootIsDeleted()
    {
        var load = Task.FromException(new IOException("tracked"));
        var failure = Assert.Throws<AggregateException>(() => ContextTeardown.Run(
            _context, () => { }, [() => [("Load", load)]], () => Directory.Delete(_root, true), _root, Long));

        Assert.Contains(failure.InnerExceptions, e => e is IOException { Message: "tracked" });
        Assert.True(_context.IsStopped);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Teardown_AGateReleaseFault_IsReported_AndTheRestStillRuns()
    {
        var failure = Assert.Throws<AggregateException>(() => ContextTeardown.Run(
            _context, () => throw new InvalidOperationException("release"), [() => []], () => Directory.Delete(_root, true), _root, Long));

        Assert.Contains(failure.InnerExceptions, e => e.Message == "release");
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Teardown_ALeftoverCallbackFault_IsReported()
    {
        using var ran = new ManualResetEventSlim();
        _context.Post(_ =>
        {
            ran.Set();
            throw new InvalidOperationException("late callback");
        }, null);
        Assert.True(ran.Wait(Long));

        var failure = Assert.Throws<AggregateException>(() => ContextTeardown.Run(
            _context, () => { }, [() => []], () => Directory.Delete(_root, true), _root, Long));

        Assert.Contains(failure.InnerExceptions, e => e.Message == "late callback");
    }

    [Fact]
    public void Teardown_WhenTrackedWorkNeverSettles_KeepsTheRoot()
    {
        var never = new TaskCompletionSource();
        var deleted = false;

        var failure = Assert.Throws<ContextTeardownException>(() => ContextTeardown.Run(
            _context, () => { }, [() => [("Never", never.Task)]], () => deleted = true, _root, TimeSpan.FromMilliseconds(200)));

        Assert.False(deleted);
        Assert.Contains("did not settle", failure.Message);
        never.SetResult(); // the abandoned observation completes; its continuation is dropped by the stopped context
    }

    // ---- Settle (B3) ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Settle_PropagatesAFaultedTrackedTask()
    {
        var load = Task.FromException(new IOException("disk")); // one instance, as a view-model property holds it
        var failure = await Assert.ThrowsAsync<AggregateException>(() => TrackedTasks.SettleAsync(() =>
            [("Load", load), ("Count", Task.CompletedTask)]));

        Assert.Contains("Load", failure.Message);
        Assert.IsType<IOException>(Assert.Single(failure.InnerExceptions));
    }

    [Fact]
    public async Task Settle_TreatsADesignedCancellation_AsCompletion()
    {
        var cancelled = Task.FromCanceled(new CancellationToken(canceled: true));

        await TrackedTasks.SettleAsync(() => [("Superseded load", cancelled)]);
    }

    [Fact]
    public async Task Settle_WaitsForWorkThatStartsWhileSettling()
    {
        var first = Task.CompletedTask;
        var second = Task.Run(() => { });
        var snapshots = 0;

        // The first snapshot sees the first operation; by the next one, its completion has started another (as a
        // reconciliation does). Settle only returns once two consecutive snapshots agree.
        await TrackedTasks.SettleAsync(() => [("Op", ++snapshots == 1 ? first : second)]);

        Assert.True(second.IsCompleted);
        Assert.Equal(3, snapshots); // read; pass 1 re-read: changed; pass 2 re-read: same instance, completed
    }

    [Fact]
    public async Task Settle_FailsWhenTheTrackedSetKeepsChanging()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TrackedTasks.SettleAsync(() => [("Restless", Task.FromResult(Guid.NewGuid()))], maxPasses: 3));

        Assert.Contains("did not settle within 3 passes", failure.Message);
    }
}
