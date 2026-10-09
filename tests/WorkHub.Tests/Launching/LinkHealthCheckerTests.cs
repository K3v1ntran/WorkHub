using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Launching;

/// <summary>
/// The application-wide manual link checker (revision 3 §6.4, D12). A fake clock drives every deadline; a gated probe
/// stands in for filesystem calls that block (offline servers) and records every OS-level call.
/// </summary>
public sealed class LinkHealthCheckerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5); // real-time safety bound for awaiting completions

    private readonly ManualTimeProvider _clock = new();
    private readonly GatedProbe _probe = new();

    public void Dispose() => _probe.Dispose();

    private LinkHealthChecker NewChecker(TimeSpan? probeTimeout = null) =>
        new(_probe, _clock, probeTimeout: probeTimeout);

    private static string Unc(string server, string file = "a.txt") => $@"\\{server}\share\{file}";

    private static async Task<bool> StaysPending(Task task)
    {
        await Task.Delay(50);
        return !task.IsCompleted;
    }

    [Fact]
    public async Task Web_IsNeverProbed_AndInvalidTargetsAreNotProbed()
    {
        var checker = NewChecker();

        Assert.Equal(LinkHealthStatus.NotCheckedWeb, await checker.CheckAsync(ProjectLinkType.Web, "https://portal.example.test/"));
        Assert.Equal(LinkHealthStatus.InvalidTarget, await checker.CheckAsync(ProjectLinkType.Web, "ftp://example.test/"));
        Assert.Equal(LinkHealthStatus.InvalidTarget, await checker.CheckAsync(ProjectLinkType.File, @"relative\x.txt"));
        Assert.Equal(LinkHealthStatus.InvalidTarget, await checker.CheckAsync(ProjectLinkType.Folder, "https://example.test/"));
        Assert.Empty(_probe.Calls);
    }

    [Theory]
    [InlineData(ProjectLinkType.File, false, LinkHealthStatus.Found)]
    [InlineData(ProjectLinkType.Folder, true, LinkHealthStatus.Found)]
    [InlineData(ProjectLinkType.File, true, LinkHealthStatus.WrongType)]
    [InlineData(ProjectLinkType.Folder, false, LinkHealthStatus.WrongType)]
    public void Classify_ExistingTargets(ProjectLinkType type, bool isDirectory, LinkHealthStatus expected) =>
        Assert.Equal(expected, LinkHealthChecker.Classify(type, LinkProbeResult.Found(isDirectory)));

    [Theory]
    [InlineData(2, LinkHealthStatus.Missing)]
    [InlineData(3, LinkHealthStatus.Missing)]
    [InlineData(5, LinkHealthStatus.Inaccessible)]
    [InlineData(1326, LinkHealthStatus.Inaccessible)]
    [InlineData(21, LinkHealthStatus.Unreachable)]
    [InlineData(53, LinkHealthStatus.Unreachable)]
    [InlineData(64, LinkHealthStatus.Unreachable)]
    [InlineData(67, LinkHealthStatus.Unreachable)]
    [InlineData(1203, LinkHealthStatus.Unreachable)]
    [InlineData(1231, LinkHealthStatus.Unreachable)]
    [InlineData(1232, LinkHealthStatus.Unreachable)]
    [InlineData(123, LinkHealthStatus.Unknown)]
    [InlineData(0, LinkHealthStatus.Unknown)]
    public void Classify_OnlyNotFoundCodesAreMissing(int win32, LinkHealthStatus expected) =>
        Assert.Equal(expected, LinkHealthChecker.Classify(ProjectLinkType.File, LinkProbeResult.Failed(win32)));

    [Fact]
    public void FileSystemProbe_ReadsRealAttributes_AndMapsMissingToNotFoundCodes()
    {
        var directory = Directory.CreateTempSubdirectory("WorkHubProbe");
        try
        {
            var file = Path.Combine(directory.FullName, "spec v3 #1.txt");
            File.WriteAllText(file, "x");
            var lastWrite = File.GetLastWriteTimeUtc(file);
            var probe = new FileSystemLinkProbe();

            Assert.Equal(LinkHealthStatus.Found, LinkHealthChecker.Classify(ProjectLinkType.File, probe.Probe(file)));
            Assert.Equal(LinkHealthStatus.Found, LinkHealthChecker.Classify(ProjectLinkType.Folder, probe.Probe(directory.FullName)));
            Assert.Equal(LinkHealthStatus.WrongType, LinkHealthChecker.Classify(ProjectLinkType.File, probe.Probe(directory.FullName)));
            Assert.Equal(LinkHealthStatus.Missing, LinkHealthChecker.Classify(ProjectLinkType.File, probe.Probe(Path.Combine(directory.FullName, "gone.txt"))));
            Assert.Equal(LinkHealthStatus.Missing, LinkHealthChecker.Classify(ProjectLinkType.File, probe.Probe(Path.Combine(directory.FullName, "no", "gone.txt"))));
            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(file)); // read-only
            Assert.Equal("x", File.ReadAllText(file));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Work\a.txt", "C:")]
    [InlineData(@"c:\Work", "C:")]
    [InlineData(@"\\fin01\shared\budget.xlsx", @"\\FIN01")]
    [InlineData(@"\\Fin01\other", @"\\FIN01")]
    public void LocationKey_IsTheDriveOrTheUncServer(string path, string expected) =>
        Assert.Equal(expected, LinkHealthChecker.LocationKey(path));

    [Fact]
    public async Task BlockedProbe_TimesOutAt8Seconds_KeepsItsSlot_AndBlocksItsLocation()
    {
        var checker = NewChecker();
        _probe.Block(Unc("dead"));

        var first = checker.CheckAsync(ProjectLinkType.File, Unc("dead"));
        _probe.WaitForCalls(1);
        _clock.Advance(TimeSpan.FromSeconds(7.9));
        Assert.True(await StaysPending(first));
        _clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(LinkHealthStatus.TimedOut, await first.WaitAsync(Wait));

        Assert.Equal(1, checker.OutstandingCalls); // the slot stays held until the OS call returns
        Assert.Equal(1, checker.StuckCalls);

        // Another target on the same server is not queued and makes no OS call.
        Assert.Equal(LinkHealthStatus.LocationBusy, await checker.CheckAsync(ProjectLinkType.File, Unc("dead", "b.txt")).WaitAsync(Wait));
        Assert.Single(_probe.Calls);

        // The stuck call finally returns: its late result is discarded and the slot and location are freed.
        _probe.Release(Unc("dead"), LinkProbeResult.Found(false));
        await WaitUntil(() => checker.OutstandingCalls == 0);
        Assert.Equal(0, checker.StuckCalls);
        Assert.Equal(LinkHealthStatus.TimedOut, await first);
        _probe.Answer(Unc("dead", "b.txt"), LinkProbeResult.Failed(2));
        Assert.Equal(LinkHealthStatus.Missing, await checker.CheckAsync(ProjectLinkType.File, Unc("dead", "b.txt")).WaitAsync(Wait));
    }

    [Fact]
    public async Task RepeatedChecksOfABlockedTarget_ShareOneOsCall()
    {
        var checker = NewChecker();
        _probe.Block(Unc("slow"));

        var requests = Enumerable.Range(0, 20).Select(_ => checker.CheckAsync(ProjectLinkType.File, Unc("slow"))).ToList();
        _probe.WaitForCalls(1);
        await Task.Delay(50);

        Assert.Single(_probe.Calls);
        Assert.Equal(1, checker.OutstandingCalls);
        _probe.Release(Unc("slow"), LinkProbeResult.Failed(2));
        Assert.All(await Task.WhenAll(requests).WaitAsync(Wait), s => Assert.Equal(LinkHealthStatus.Missing, s));
        Assert.Single(_probe.Calls);
    }

    [Fact]
    public async Task SixBlockedServers_NeverMoreThanFourOsCalls()
    {
        var checker = NewChecker(probeTimeout: TimeSpan.FromSeconds(30));
        var servers = Enumerable.Range(1, 6).Select(i => $"srv{i}").ToList();
        servers.ForEach(s => _probe.Block(Unc(s)));

        var requests = servers.Select(s => checker.CheckAsync(ProjectLinkType.File, Unc(s))).ToList();
        _probe.WaitForCalls(4);
        await Task.Delay(50);

        Assert.Equal(4, _probe.Calls.Count);
        Assert.Equal(4, checker.OutstandingCalls);
        Assert.Equal(2, checker.QueuedRequests);

        // One returns: exactly one queued request is admitted.
        _probe.Release(Unc("srv1"));
        _probe.WaitForCalls(5);
        await Task.Delay(50);
        Assert.Equal(5, _probe.Calls.Count);
        Assert.Equal(4, checker.OutstandingCalls);

        servers.ForEach(s => _probe.Release(Unc(s)));
        await Task.WhenAll(requests).WaitAsync(Wait);
    }

    [Fact]
    public async Task FourStuckProbes_FifthRequestAtStart_EndsCheckerBusyWhenTheyBecomeStuck_WithoutAnOsCall()
    {
        var checker = NewChecker();
        var servers = new[] { "s1", "s2", "s3", "s4" };
        foreach (var s in servers)
            _probe.Block(Unc(s));
        var stuck = servers.Select(s => checker.CheckAsync(ProjectLinkType.File, Unc(s))).ToList();
        _probe.WaitForCalls(4);

        var fifth = checker.CheckAsync(ProjectLinkType.File, Unc("s5")); // t = 0, different location
        Assert.True(await StaysPending(fifth));

        _clock.Advance(TimeSpan.FromSeconds(8)); // all four time out → every slot is held by a stuck call
        Assert.Equal(LinkHealthStatus.CheckerBusy, await fifth.WaitAsync(Wait));
        Assert.All(await Task.WhenAll(stuck).WaitAsync(Wait), s => Assert.Equal(LinkHealthStatus.TimedOut, s));
        Assert.Equal(4, _probe.Calls.Count); // no fifth OS call
        Assert.Equal(4, checker.OutstandingCalls);
        Assert.Equal(4, checker.StuckCalls);

        // Issued after the four became stuck: Checker busy immediately, still no OS call.
        var late = checker.CheckAsync(ProjectLinkType.File, Unc("s6"));
        Assert.True(late.IsCompleted);
        Assert.Equal(LinkHealthStatus.CheckerBusy, await late);
        Assert.Equal(4, _probe.Calls.Count);

        // Releasing one stuck call frees exactly one slot; its late result is not applied anywhere.
        _probe.Release(Unc("s1"), LinkProbeResult.Found(false));
        await WaitUntil(() => checker.OutstandingCalls == 3);
        Assert.Equal(3, checker.StuckCalls);
        Assert.Equal(LinkHealthStatus.TimedOut, await stuck[0]);
    }

    [Fact]
    public async Task AdmissionDeadline_WithoutStuckCalls_EndsCheckerBusyAtTenSeconds_NotTimedOut()
    {
        var checker = NewChecker(probeTimeout: TimeSpan.FromSeconds(30)); // slow but not stuck within the test
        var servers = new[] { "s1", "s2", "s3", "s4" };
        foreach (var s in servers)
            _probe.Block(Unc(s));
        _ = servers.Select(s => checker.CheckAsync(ProjectLinkType.File, Unc(s))).ToList();
        _probe.WaitForCalls(4);

        var fifth = checker.CheckAsync(ProjectLinkType.File, Unc("s5"));
        _clock.Advance(TimeSpan.FromSeconds(9.9));
        Assert.True(await StaysPending(fifth));
        _clock.Advance(TimeSpan.FromSeconds(0.1));

        Assert.Equal(LinkHealthStatus.CheckerBusy, await fifth.WaitAsync(Wait));
        Assert.Equal(4, _probe.Calls.Count);
        Assert.Equal(0, checker.StuckCalls);
    }

    [Fact]
    public async Task AQueuedRequestAdmittedLate_StillEndsWithinEighteenSeconds()
    {
        var checker = NewChecker();
        var servers = new[] { "s1", "s2", "s3", "s4", "s5" };
        foreach (var s in servers)
            _probe.Block(Unc(s));
        var first = servers.Take(4).Select(s => checker.CheckAsync(ProjectLinkType.File, Unc(s))).ToList();
        _probe.WaitForCalls(4);
        var fifth = checker.CheckAsync(ProjectLinkType.File, Unc("s5"));

        _clock.Advance(TimeSpan.FromSeconds(5));
        _probe.Release(Unc("s1"));            // a slot frees at t = 5: the fifth is admitted and its call blocks
        _probe.WaitForCalls(5);
        _clock.Advance(TimeSpan.FromSeconds(3)); // t = 8: the other three time out (3 stuck < 4: no fast fail)
        Assert.True(await StaysPending(fifth));
        _clock.Advance(TimeSpan.FromSeconds(5)); // t = 13: the fifth's own 8-second probe timeout

        Assert.Equal(LinkHealthStatus.TimedOut, await fifth.WaitAsync(Wait)); // 13 s ≤ 18 s bound
        await Task.WhenAll(first).WaitAsync(Wait);
    }

    [Fact]
    public async Task CancellingAQueuedRequest_RemovesIt_AndItNeverStarts()
    {
        var checker = NewChecker(probeTimeout: TimeSpan.FromSeconds(30));
        var servers = new[] { "s1", "s2", "s3", "s4" };
        foreach (var s in servers)
            _probe.Block(Unc(s));
        _ = servers.Select(s => checker.CheckAsync(ProjectLinkType.File, Unc(s))).ToList();
        _probe.WaitForCalls(4);

        using var cts = new CancellationTokenSource();
        var queued = checker.CheckAsync(ProjectLinkType.File, Unc("s5"), cts.Token);
        Assert.Equal(1, checker.QueuedRequests);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Wait));
        Assert.Equal(0, checker.QueuedRequests);
        _probe.Release(Unc("s1"));
        await WaitUntil(() => checker.OutstandingCalls == 3);
        await Task.Delay(50);
        Assert.DoesNotContain(Unc("s5"), _probe.Calls);
    }

    [Fact]
    public async Task CancellingARunningRequest_DiscardsItsResult_ButTheCallKeepsItsSlotUntilItReturns()
    {
        var checker = NewChecker();
        _probe.Block(Unc("slow"));
        using var cts = new CancellationTokenSource();
        var running = checker.CheckAsync(ProjectLinkType.File, Unc("slow"), cts.Token);
        _probe.WaitForCalls(1);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Wait));
        Assert.Equal(1, checker.OutstandingCalls);

        _probe.Release(Unc("slow"));
        await WaitUntil(() => checker.OutstandingCalls == 0);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached.");
            await Task.Delay(5);
        }
    }
}
