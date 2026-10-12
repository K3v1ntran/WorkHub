using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;
using Xunit.Abstractions;

namespace WorkHub.Tests.Workspace;

/// <summary>Runs only when WORKHUB_FEASIBILITY=1 (large fixtures, full 30 s budget); reported as skipped otherwise.</summary>
public sealed class FeasibilityFactAttribute : FactAttribute
{
    public FeasibilityFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WORKHUB_FEASIBILITY") != "1")
            Skip = "Feasibility measurement; set WORKHUB_FEASIBILITY=1 to run.";
    }
}

/// <summary>
/// Phase 6 feasibility gate (proposal §5.2, §8.1 and amendments A1/A2/P2) against the REAL Microsoft.Data.Sqlite provider
/// and isolated data: scan cancellation, RESERVED/EXCLUSIVE locks with the search-only short busy timeout and bounded
/// retries, bounded superseded work, performance. Measurements go to the test output.
/// </summary>
public sealed class WorkspaceSearchFeasibilityTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempWorkHubRoot _root = Initialized();
    private readonly List<SqliteConnectionFactory> _factories = [];

    private static TempWorkHubRoot Initialized()
    {
        var root = new TempWorkHubRoot();
        root.CreateInitializer().Initialize();
        return root;
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
            factory.ClearPools();
        _root.Dispose();
    }

    private SqliteConnectionFactory Factory(int? timeoutSeconds = null)
    {
        var factory = new SqliteConnectionFactory(_root.Paths.DatabaseFile, timeoutSeconds);
        _factories.Add(factory);
        return factory;
    }

    private SqliteWorkspaceSearchStore Store(WorkspaceSearchStoreOptions? options = null)
    {
        var store = new SqliteWorkspaceSearchStore(Factory(), options ?? WorkspaceSearchStoreOptions.Default);
        _factories.Add(store.SearchConnections);
        return store;
    }

    /// <summary>Projects, folders, links and notes; every note has the same generated content, no note contains "zzzneedle".</summary>
    private List<long> Seed(int projects, int folders, int links, int notes, int contentLength, string[]? vocabulary = null)
    {
        using var c = _root.Open();
        using var tx = c.BeginTransaction();
        var projectIds = new List<long>();
        for (var i = 0; i < projects; i++)
        {
            var id = Db.InsertNode(c, null, "Project", $"Project {i:D4}");
            Db.InsertProjectDetails(c, id, i.ToString("x32"));
            projectIds.Add(id);
        }

        for (var i = 0; i < folders; i++)
        {
            Db.ScalarLongWith(c,
                "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, $n, upper($n), $t, $t) RETURNING Id;",
                ("$p", projectIds[i % projectIds.Count]), ("$n", $"Folder {i:D4}"), ("$t", Db.Now));
        }

        using (var link = c.CreateCommand())
        {
            link.CommandText = "INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Web', $n, $u, $t, $t);";
            var p = link.Parameters.Add("$p", SqliteType.Integer);
            var n = link.Parameters.Add("$n", SqliteType.Text);
            var u = link.Parameters.Add("$u", SqliteType.Text);
            link.Parameters.AddWithValue("$t", Db.Now);
            for (var i = 0; i < links; i++)
            {
                p.Value = projectIds[i % projectIds.Count];
                n.Value = $"Link {i:D4}";
                u.Value = $"https://example.test/{i}";
                link.ExecuteNonQuery();
            }
        }

        var words = vocabulary ?? ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet"];
        var text = new StringBuilder();
        var random = new Random(42);
        while (text.Length < contentLength)
            text.Append(words[random.Next(words.Length)]).Append(random.Next(8) == 0 ? "\r\n" : " ");
        var content = text.ToString(0, contentLength);
        using (var note = c.CreateCommand())
        {
            note.CommandText = "INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, NULL, $n, $c, $t, $t);";
            var p = note.Parameters.Add("$p", SqliteType.Integer);
            var n = note.Parameters.Add("$n", SqliteType.Text);
            note.Parameters.AddWithValue("$c", content);
            note.Parameters.AddWithValue("$t", Db.Now);
            for (var i = 0; i < notes; i++)
            {
                p.Value = projectIds[i % projectIds.Count];
                n.Value = $"Note {i:D5}";
                note.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return projectIds;
    }

    private static WorkspaceSearchQuery Query(string text) => WorkspaceSearchText.Parse(text)!;

    private static async Task<(TimeSpan Latency, Exception? Error)> CancelAfter(Func<CancellationToken, Task> run, TimeSpan after)
    {
        using var cts = new CancellationTokenSource();
        var task = run(cts.Token);
        await Task.Delay(after);
        var watch = Stopwatch.StartNew();
        cts.Cancel();
        try
        {
            await task;
            return (watch.Elapsed, null);
        }
        catch (Exception ex)
        {
            return (watch.Elapsed, ex);
        }
    }

    private string Report(string name, IReadOnlyCollection<TimeSpan> samples, string? extra = null)
    {
        var ms = samples.Select(s => s.TotalMilliseconds).OrderBy(x => x).ToList();
        var line = $"{name}: runs={ms.Count} median={ms[ms.Count / 2]:F0} ms max={ms[^1]:F0} ms min={ms[0]:F0} ms{(extra is null ? string.Empty : " " + extra)}";
        output.WriteLine(line);
        return line;
    }

    private sealed class HeldLock : IDisposable
    {
        private readonly SqliteConnection _connection;
        private bool _released;

        public HeldLock(SqliteConnectionFactory factory, string begin = "BEGIN EXCLUSIVE;")
        {
            _connection = factory.Open(SqliteOpenMode.ReadWrite, pooling: false);
            Exec(begin);
        }

        public void Exec(string sql)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Commit() => End("COMMIT;");

        public void Dispose() => End("ROLLBACK;");

        private void End(string sql)
        {
            if (_released)
                return;
            _released = true;
            Exec(sql);
            _connection.Dispose();
        }
    }

    // ---- 1. Active scan cancellation (target ≤ 250 ms) ---------------------------------------------------------------

    [Fact]
    public async Task ScanCancellation_TokenCheckedInsideTheFunction_EndsTheScanWithin250ms_AndTheConnectionIsReusable()
    {
        Seed(projects: 10, folders: 0, links: 0, notes: 600, contentLength: 100_000, Japanese); // ~1 s to scan in Release, so every cancel lands mid-scan
        var store = Store();
        var samples = new List<TimeSpan>();
        for (var i = 0; i < 10; i++)
        {
            var (latency, error) = await CancelAfter(t => store.SearchAsync(Query("zzzneedle"), 200, t), TimeSpan.FromMilliseconds(60 + 15 * i));
            Assert.True(error is not null, "the scan finished before the cancel; the fixture is too small to measure cancellation");
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            samples.Add(latency);
        }

        Report("active scan: cancel -> return", samples);
        Assert.All(samples, s => Assert.True(s < TimeSpan.FromMilliseconds(250), $"cancellation took {s.TotalMilliseconds:F0} ms"));

        var after = await store.SearchAsync(Query("Note 00001"), 200, CancellationToken.None);
        Assert.Contains(after.Hits, h => h.Name == "Note 00001"); // the pooled connection is reused and works
    }

    [Fact]
    public void ProviderCancel_IsANoOp_ForARunningStatement_AsDocumented()
    {
        Seed(projects: 1, folders: 0, links: 0, notes: 200, contentLength: 100_000);
        using var connection = Factory().OpenExisting();
        connection.CreateFunction<string, long>("slow_fold", text => WorkspaceSearchText.Fold(text).Length, isDeterministic: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sum(slow_fold(Content)) FROM ProjectNotes;";
        using var canceller = new Timer(_ => command.Cancel(), null, 20, Timeout.Infinite);
        Assert.NotNull(command.ExecuteScalar()); // completed normally: Cancel() did not interrupt it
    }

    [Fact]
    public void FunctionThrowing_AbortsTheStatement_AndTheSameConnectionStaysUsable()
    {
        Seed(projects: 1, folders: 0, links: 0, notes: 5, contentLength: 1_000);
        using var connection = Factory().OpenExisting();
        connection.CreateFunction<string, long>("boom", _ => throw new OperationCanceledException(), isDeterministic: true);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT boom(Content) FROM ProjectNotes;";
            Assert.ThrowsAny<SqliteException>(() => command.ExecuteScalar());
        }

        using var again = connection.CreateCommand();
        again.CommandText = "SELECT count(*) FROM ProjectNotes;";
        Assert.Equal(5L, again.ExecuteScalar());
    }

    // ---- 2. Locks with the search-only short timeout and bounded retries -------------------------------------------

    [Fact]
    public void Timeouts_SearchConnectionsUseOneSecond_OtherConnectionsKeepTheProductionDefault()
    {
        var store = Store();
        using (var search = store.SearchConnections.OpenExisting())
            Assert.Equal(1, search.DefaultTimeout);
        using var app = new SqliteConnectionFactory(_root.Paths.DatabaseFile).OpenExisting(); // what WorkHubServices uses
        Assert.Equal(30, app.DefaultTimeout);
    }

    [Fact]
    public async Task OtherOperations_KeepTheirProductionWait_AWriteOutlastsAOneSecondLock()
    {
        var project = Seed(projects: 1, folders: 0, links: 0, notes: 0, contentLength: 0)[0];
        var content = new ProjectContentService(new SqliteProjectContentStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
        var held = new HeldLock(Factory());
        var write = content.CreateNoteAsync(project, null, new ProjectNoteDraft("written under a lock", "x"));
        await Task.Delay(2500); // longer than the search-only 1 s timeout
        Assert.False(write.IsCompleted);
        held.Commit();
        var result = await write;
        Assert.True(result.Succeeded, result.Error); // the write path still waits with the production timeout
    }

    [Fact]
    public async Task ReservedLock_HeldByAnotherConnection_DoesNotBlockSearchOrResolve()
    {
        var projects = Seed(projects: 3, folders: 3, links: 3, notes: 3, contentLength: 100);
        var store = Store();
        using var held = new HeldLock(Factory(), "BEGIN IMMEDIATE;");
        var watch = Stopwatch.StartNew();
        var result = await store.SearchAsync(Query("Note"), 200, CancellationToken.None);
        var resolved = await store.ResolveAsync(new SearchItemRef(SearchResultKind.Project, projects[0]), CancellationToken.None);
        output.WriteLine($"RESERVED: search + resolve in {watch.Elapsed.TotalMilliseconds:F0} ms, attempts {store.AttemptCount}");
        Assert.Equal(3, result.Hits.Count);
        Assert.Equal(RevealResolveStatus.Found, resolved.Status);
        Assert.Equal(2, store.AttemptCount); // one attempt each: no contention
    }

    [Fact]
    public async Task ExclusiveLock_ReleasedBeforeTheDeadline_SearchAndResolveRetryAndSucceed()
    {
        var projects = Seed(projects: 3, folders: 0, links: 0, notes: 3, contentLength: 100);
        var store = Store();
        var held = new HeldLock(Factory());
        var watch = Stopwatch.StartNew();
        var search = store.SearchAsync(Query("Note"), 200, CancellationToken.None);
        var resolve = store.ResolveAsync(new SearchItemRef(SearchResultKind.Project, projects[1]), CancellationToken.None);
        await Task.Delay(2600);
        Assert.False(search.IsCompleted);
        held.Dispose();
        var result = await search;
        var resolved = await resolve;
        output.WriteLine($"EXCLUSIVE released at 2.6 s: both returned after {watch.Elapsed.TotalMilliseconds:F0} ms, attempts {store.AttemptCount}");
        Assert.Equal(3, result.Hits.Count);
        Assert.Equal(RevealResolveStatus.Found, resolved.Status);
        Assert.True(store.AttemptCount >= 4, "each call should have retried at least once");
    }

    [Fact]
    public async Task ExclusiveLock_HeldThroughCancellation_ReturnsWithin1500ms()
    {
        Seed(projects: 3, folders: 0, links: 0, notes: 3, contentLength: 100);
        var store = Store();
        using var held = new HeldLock(Factory());
        var samples = new List<TimeSpan>();
        foreach (var after in new[] { 100, 300, 500, 700, 900, 1100, 1300, 1500, 1700, 1900 })
        {
            var (latency, error) = await CancelAfter(t => store.SearchAsync(Query("Note"), 200, t), TimeSpan.FromMilliseconds(after));
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            samples.Add(latency);
        }

        Report("EXCLUSIVE held: cancel -> return (search)", samples);
        var (resolveLatency, resolveError) = await CancelAfter(t => store.ResolveAsync(new SearchItemRef(SearchResultKind.Note, 1), t), TimeSpan.FromMilliseconds(400));
        output.WriteLine($"EXCLUSIVE held: cancel -> return (resolve): {resolveLatency.TotalMilliseconds:F0} ms");
        Assert.IsAssignableFrom<OperationCanceledException>(resolveError);
        Assert.All(samples.Append(resolveLatency), s => Assert.True(s <= TimeSpan.FromMilliseconds(1500), $"took {s.TotalMilliseconds:F0} ms"));
    }

    [Fact]
    public async Task ExclusiveLock_HeldThroughTheDeadline_FailsBoundedWithoutAPartialResult_ShortenedBudget()
    {
        Seed(projects: 3, folders: 0, links: 0, notes: 3, contentLength: 100);
        var store = Store(WorkspaceSearchStoreOptions.Default with { OverallBudget = TimeSpan.FromSeconds(3.5) });
        using (new HeldLock(Factory()))
        {
            var watch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<WorkspaceSearchBusyException>(() => store.SearchAsync(Query("Note"), 200, CancellationToken.None));
            output.WriteLine($"EXCLUSIVE through a 3.5 s budget: {error.Message} after {watch.Elapsed.TotalMilliseconds:F0} ms, attempts {store.AttemptCount}");
            Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3.5 + 0.5));
        }

        var result = await store.SearchAsync(Query("Note"), 200, CancellationToken.None); // connections reusable after the failures
        Assert.Equal(3, result.Hits.Count);
    }

    [FeasibilityFact]
    public async Task ExclusiveLock_HeldThroughTheDeadline_ProductionBudget()
    {
        Seed(projects: 3, folders: 0, links: 0, notes: 3, contentLength: 100);
        var store = Store();
        using var held = new HeldLock(Factory());
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<WorkspaceSearchBusyException>(() => store.SearchAsync(Query("Note"), 200, CancellationToken.None));
        output.WriteLine($"EXCLUSIVE through the production 30 s budget: '{error.Message}' after {watch.Elapsed.TotalMilliseconds:F0} ms, attempts {store.AttemptCount}");
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(28), TimeSpan.FromSeconds(30.5));
    }

    [Fact]
    public async Task CancellationBetweenAttempts_StopsWithoutAnotherAttempt()
    {
        Seed(projects: 1, folders: 0, links: 0, notes: 1, contentLength: 10);
        using var cts = new CancellationTokenSource();
        var store = Store(WorkspaceSearchStoreOptions.Default with { AttemptFailed = _ => cts.Cancel() });
        using var held = new HeldLock(Factory());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SearchAsync(Query("Note"), 200, cts.Token));
        Assert.Equal(1, store.AttemptCount);
    }

    [Fact]
    public async Task CancellationDuringTheRetryDelay_ReturnsPromptly()
    {
        Seed(projects: 1, folders: 0, links: 0, notes: 1, contentLength: 10);
        using var failed = new SemaphoreSlim(0);
        var store = Store(WorkspaceSearchStoreOptions.Default with { RetryDelay = TimeSpan.FromSeconds(10), AttemptFailed = _ => failed.Release() });
        using var held = new HeldLock(Factory());
        using var cts = new CancellationTokenSource();
        var search = store.SearchAsync(Query("Note"), 200, cts.Token);
        Assert.True(await failed.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(100); // now inside the 10 s retry delay
        var watch = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
        output.WriteLine($"cancel during retry delay -> return: {watch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500));
        Assert.Equal(1, store.AttemptCount);
    }

    [Fact]
    public async Task SuccessAfterRetry_ComesFromOneConsistentSnapshot()
    {
        var projects = Seed(projects: 2, folders: 0, links: 0, notes: 0, contentLength: 0);
        var store = Store();
        var folder = Factory();
        long folderId;
        using (var c = folder.OpenExisting())
            folderId = Db.InsertNode(c, null, "Folder", "Archive");

        // The writer changes several rows inside its EXCLUSIVE transaction; the reader must see all of it or none of it.
        var held = new HeldLock(Factory());
        held.Exec($"UPDATE WorkspaceNodes SET ParentId = {folderId} WHERE Id = {projects[0]};");
        held.Exec($"INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ({projects[0]}, NULL, 'Snap one', '', '{Db.Now}', '{Db.Now}');");
        held.Exec($"INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ({projects[1]}, NULL, 'Snap two', '', '{Db.Now}', '{Db.Now}');");
        var search = store.SearchAsync(Query("Snap"), 200, CancellationToken.None);
        var resolve = store.ResolveAsync(new SearchItemRef(SearchResultKind.Project, projects[0]), CancellationToken.None);
        await Task.Delay(1500);
        held.Commit();

        var result = await search;
        var resolved = (await resolve).Target!;
        Assert.True(store.AttemptCount >= 3);
        Assert.Equal(["Snap one", "Snap two"], result.Hits.Select(h => h.Name).Order().ToList());
        Assert.Contains(result.Hits, h => h.Name == "Snap one" && h.Location == "Archive › Project 0000"); // the move, same snapshot
        Assert.Equal([folderId], resolved.WorkspaceAncestorIds);
    }

    // ---- 3. Superseded work -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RapidQueries_UnderAHeldExclusiveLock_AtMostOneRunningAndOnePending_OnlyTheNewestCompletes()
    {
        Seed(projects: 3, folders: 0, links: 0, notes: 3, contentLength: 100);
        var store = Store();
        var concurrent = 0;
        var maxConcurrent = 0;
        var runner = new LatestOnlyRunner<string, WorkspaceSearchResult>(async (text, token) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            try
            {
                return await store.SearchAsync(Query(text), 200, token);
            }
            finally
            {
                Interlocked.Decrement(ref concurrent);
            }
        });

        var held = new HeldLock(Factory());
        var outcomes = new List<Task<RunOutcome<WorkspaceSearchResult>>>();
        for (var i = 0; i < 20; i++)
        {
            outcomes.Add(runner.Submit(i == 19 ? "Note 2" : $"q{i}"));
            await Task.Delay(10);
        }

        Assert.True(runner.IsRunning);
        Assert.True(runner.HasPending);
        // The first call notices its cancellation within one 1 s attempt; only the newest request then starts.
        var started = Stopwatch.StartNew();
        while (runner.StartedCount < 2 && started.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(20);
        output.WriteLine($"20 submissions under EXCLUSIVE: second call started after {started.Elapsed.TotalMilliseconds:F0} ms (cancelled first call returned)");
        held.Dispose();
        var results = await Task.WhenAll(outcomes);

        output.WriteLine($"work started {runner.StartedCount}x, max concurrent {maxConcurrent}");
        Assert.Equal(1, maxConcurrent);
        Assert.Equal(2, runner.StartedCount);
        Assert.All(results.Take(19), r => Assert.Equal(RunStatus.Superseded, r.Status));
        Assert.Equal(RunStatus.Completed, results[19].Status);
        Assert.Single(results[19].Value!.Hits);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    // ---- 4. Performance (Release; P2: full search ≤ 2 s on 100 M chars of Japanese/full-width content) -------------

    private const int Runs = 5;

    private static readonly string[] Japanese = ["販売管理", "システム", "会議", "議事録", "ｶﾀｶﾅ", "ＡＢＣ", "東京支社", "仕様書", "打合せ", "第２期"];

    [FeasibilityFact]
    public Task Performance_Ascii() =>
        Performance(null, [("no match", "zzzneedle"), ("broad, many matches", "charlie"), ("title + content", "Note 00001 charlie"), ("title matches", "Note 0001"), ("project names", "Project 0001")]);

    [FeasibilityFact]
    public Task Performance_JapaneseFullWidth() =>
        Performance(Japanese, [("no match", "zzzneedle"), ("broad, many matches", "会議"), ("title + content", "Note 00001 会議"), ("title matches", "Note 0001"), ("project names", "Project 0001"), ("width-folded (abc→ＡＢＣ)", "abc"), ("half-width kana (ｶﾀｶﾅ→カタカナ)", "カタカナ")]);

    private async Task Performance(string[]? vocabulary, (string Case, string Text)[] queries)
    {
        var seed = Stopwatch.StartNew();
        Seed(projects: 300, folders: 500, links: 2000, notes: 5000, contentLength: 20_000, vocabulary);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        output.WriteLine($"configuration: {configuration}; fixture: 300 projects, 500 project folders, 2000 links, 5000 notes x 20000 chars = 100,000,000 note characters ({(vocabulary is null ? "ASCII" : "Japanese/full-width")}); seeded in {seed.Elapsed.TotalSeconds:F1} s");
        var store = Store();
        await store.SearchAsync(Query("warmup"), 200, CancellationToken.None);

        var budgetFailures = new List<string>();
        foreach (var (name, text) in queries)
        {
            var samples = new List<TimeSpan>();
            WorkspaceSearchResult? last = null;
            for (var i = 0; i < Runs; i++)
            {
                var watch = Stopwatch.StartNew();
                last = await store.SearchAsync(Query(text), 200, CancellationToken.None);
                samples.Add(watch.Elapsed);
            }

            Report($"'{text}' [{name}]", samples, $"hits={last!.Hits.Count} truncated={last.Truncated}");
            if (vocabulary is not null && samples.Max() > TimeSpan.FromSeconds(2))
                budgetFailures.Add($"{name}: max {samples.Max().TotalMilliseconds:F0} ms");
        }

        Assert.True(budgetFailures.Count == 0, "over the 2 s budget: " + string.Join("; ", budgetFailures));
    }

    [FeasibilityFact]
    public async Task Performance_Resolve_20LevelsDeep_1000SiblingsPerLevel()
    {
        long target;
        using (var c = _root.Open())
        using (var tx = c.BeginTransaction())
        {
            long? parent = null;
            for (var level = 0; level < 20; level++)
            {
                for (var s = 0; s < 999; s++)
                    Db.InsertNode(c, parent, "Folder", $"Sibling {level:D2}-{s:D3}");
                parent = Db.InsertNode(c, parent, "Folder", $"Path {level:D2}");
            }

            target = Db.InsertNode(c, parent, "Project", "Deep project");
            Db.InsertProjectDetails(c, target);
            tx.Commit();
        }

        var store = Store();
        await store.ResolveAsync(new SearchItemRef(SearchResultKind.Project, target), CancellationToken.None);
        var samples = new List<TimeSpan>();
        RevealResolution? last = null;
        for (var i = 0; i < Runs; i++)
        {
            var watch = Stopwatch.StartNew();
            last = await store.ResolveAsync(new SearchItemRef(SearchResultKind.Project, target), CancellationToken.None);
            samples.Add(watch.Elapsed);
        }

        Report("resolve 20 levels x 1000 siblings", samples, $"branches={last!.Target!.Branches.Count} rows={last.Target.Branches.Sum(b => b.Children.Count)}");
        Assert.True(samples.Max() <= TimeSpan.FromMilliseconds(200), $"max {samples.Max().TotalMilliseconds:F0} ms");
    }
}
