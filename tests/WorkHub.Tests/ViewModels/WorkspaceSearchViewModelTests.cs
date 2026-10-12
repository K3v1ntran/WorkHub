using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>Search box behavior (Phase 6 proposal §3, §4.7, §5.2): debounce, bounded work, stale results, states, activation.</summary>
public sealed class WorkspaceSearchViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedSearchStore _store;
    private readonly WorkspaceService _other;
    private readonly ProjectContentService _otherContent;
    private readonly RecordingLog _log = new();

    public WorkspaceSearchViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _store = new GatedSearchStore(new SqliteWorkspaceSearchStore(_connections));
        _other = new WorkspaceService(new SqliteWorkspaceStore(_connections));
        _otherContent = new ProjectContentService(new SqliteProjectContentStore(_connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
    }

    private readonly List<WorkspaceAreaViewModel> _areas = [];

    /// <summary>Background reads (searches, reveals, details, content) finish before the folder is deleted.</summary>
    public void Dispose()
    {
        foreach (var area in _areas)
        {
            var pending = new List<Task> { area.CurrentDetailsLoad, area.CurrentReveal, area.Search!.CurrentSearch, area.Search.CurrentActivation };
            if (area.SelectedContent is { } content)
                pending.AddRange([content.CurrentLoad, content.CurrentPreviewLoad]);
            Task.WhenAll(pending).ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(10));
        }

        _root.Dispose();
    }

    private async Task<WorkspaceAreaViewModel> Area(int delayMs = 0)
    {
        var area = new WorkspaceAreaViewModel(new WorkspaceService(new SqliteWorkspaceStore(_connections)), _dialogs, _log,
            new ProjectContentService(new SqliteProjectContentStore(_connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true)),
            new ScriptedLinkChecker(), new WorkspaceSearchService(_store), delayMs);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        return area;
    }

    private async Task<long> Project(string name, long? parent = null) => (await _other.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;

    private static async Task Type(WorkspaceSearchViewModel search, string text)
    {
        search.SearchText = text;
        await search.CurrentSearch;
    }

    [Fact]
    public async Task RapidTyping_IsDebounced_OneStoreCall_ForTheLastText()
    {
        await Project("Kickoff");
        await Project("Kick");
        var search = (await Area(delayMs: 150)).Search!;

        search.SearchText = "k";
        search.SearchText = "ki";
        search.SearchText = "kickoff";
        await search.CurrentSearch;

        Assert.Equal(1, _store.Searches);
        Assert.Equal(["Kickoff"], search.Results.Select(r => r.Name).ToList());
        Assert.Equal("1 result", search.StatusText);
        Assert.True(search.IsActive);
    }

    [Fact]
    public async Task ANewerQuery_SupersedesTheRunningOne_OnlyItsResultsAreShown_AndWorkIsBounded()
    {
        await Project("Alpha");
        await Project("Beta");
        var search = (await Area()).Search!;
        var gate = _store.Hold("search");

        search.SearchText = "alpha";
        gate.WaitEntered();
        search.SearchText = "gamma";
        search.SearchText = "beta";   // replaces "gamma" as the one pending query
        var latest = search.CurrentSearch;
        Assert.Equal(1, search.StoreCallsStarted);
        gate.Release();
        await latest;

        Assert.Equal(["Beta"], search.Results.Select(r => r.Name).ToList());
        Assert.Equal(2, search.StoreCallsStarted); // "alpha" (superseded) and "beta"; "gamma" never ran
    }

    [Fact]
    public async Task Failure_ShowsAnErrorWithRetry_AndNeverLogsTheQueryText()
    {
        await Project("Secret project");
        var search = (await Area()).Search!;
        _store.Hold("search").Fail(new IOException("disk error"));

        await Type(search, "secret-term");

        Assert.Equal("Search failed. Try again.", search.ErrorText);
        Assert.Empty(search.Results);
        Assert.True(search.RetryCommand.CanExecute(null));
        Assert.DoesNotContain(_log.Entries, e => e.Contains("secret-term", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(_log.Entries);

        search.SearchText = "secret";
        await search.CurrentSearch;
        Assert.Null(search.ErrorText);
        Assert.Single(search.Results);
    }

    [Fact]
    public async Task Retry_RunsTheSameQueryAgain()
    {
        await Project("Alpha");
        var search = (await Area()).Search!;
        _store.Hold("search").Fail(new IOException("disk error"));
        await Type(search, "alpha");

        search.RetryCommand.Execute(null);
        await search.CurrentSearch;

        Assert.Null(search.ErrorText);
        Assert.Single(search.Results);
    }

    [Fact]
    public async Task BusyBudgetExhausted_IsReportedAsSuch()
    {
        var search = (await Area()).Search!;
        _store.Hold("search").Fail(new WorkspaceSearchBusyException(TimeSpan.FromSeconds(30)));

        await Type(search, "anything");

        Assert.StartsWith("The database stayed busy for 30 seconds", search.ErrorText);
    }

    [Fact]
    public async Task WaitingForTheDatabase_IsShownOnlyAfterTheStoreReportedABusyAttempt()
    {
        await Project("Alpha");
        var search = (await Area()).Search!;
        var gate = _store.HoldBusy();

        search.SearchText = "alpha";
        gate.WaitEntered();
        Assert.True(SpinWait.SpinUntil(() => search.IsWaitingForDatabase, TimeSpan.FromSeconds(5))); // posted to the captured context
        Assert.StartsWith("Waiting for the database", search.StatusText);
        gate.Release();
        await search.CurrentSearch;

        Assert.False(search.IsWaitingForDatabase);
        Assert.Equal("1 result", search.StatusText);
    }

    [Fact]
    public async Task SearchingWithoutContention_NeverClaimsToWaitForTheDatabase()
    {
        await Project("Alpha");
        var search = (await Area()).Search!;
        var gate = _store.Hold("search");

        search.SearchText = "alpha";
        gate.WaitEntered();
        Assert.False(search.IsWaitingForDatabase);
        Assert.Equal("Searching…", search.StatusText);
        gate.Release();
        await search.CurrentSearch;
    }

    [Fact]
    public async Task Clear_RestoresTheTree_UntouchedWithItsSelection()
    {
        await Project("Alpha");
        var area = await Area();
        var search = area.Search!;
        var roots = area.Roots.ToList();
        area.SelectedNode = area.Roots[0];
        await Type(search, "alpha");

        search.ClearCommand.Execute(null);

        Assert.False(search.IsActive);
        Assert.Empty(search.Results);
        Assert.Null(search.StatusText);
        Assert.Equal(roots, area.Roots.ToList());
        Assert.Same(roots[0], area.SelectedNode);
    }

    [Fact]
    public async Task NoResults_AndTruncation_AndShortenedQuery_AreDescribed()
    {
        var search = (await Area()).Search!;
        await Type(search, "nothing-matches");
        Assert.Equal("No results for “nothing-matches”.", search.StatusText);

        var project = await Project("Bulk");
        using (var c = _root.Open())
        using (var tx = c.BeginTransaction())
        {
            for (var i = 0; i < 201; i++)
                Db.Exec(c, "INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, NULL, $t, '', $n, $n);", ("$p", project), ("$t", $"Item {i:D3}"), ("$n", Db.Now));
            tx.Commit();
        }

        await Type(search, "item");
        Assert.Equal(200, search.Results.Count);
        Assert.Equal("Showing the first 200 results. Refine the search to see more.", search.StatusText);

        await Type(search, "item a b c d e f g h");
        Assert.EndsWith("Only the first 8 words and 200 characters are used.", search.StatusText);
    }

    [Fact]
    public async Task Activation_RevealsOnly_AndNotFoundRemovesExactlyThatKindAndId()
    {
        var project = await Project("Alpha");
        using (var c = _root.Open())
        {
            Db.Exec(c, "INSERT INTO ProjectLinks (Id, ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES (900, $p, NULL, 'Web', 'Twin', 'https://t.test/', $n, $n);", ("$p", project), ("$n", Db.Now));
            Db.Exec(c, "INSERT INTO ProjectNotes (Id, ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES (900, $p, NULL, 'Twin', '', $n, $n);", ("$p", project), ("$n", Db.Now));
        }

        _dialogs.OnNoteEditor = _ => throw new InvalidOperationException("never");
        var area = await Area();
        var search = area.Search!;
        await Type(search, "twin");
        Assert.Equal(2, search.Results.Count);

        using (var c = _root.Open())
            Db.Exec(c, "DELETE FROM ProjectNotes WHERE Id = 900;");
        search.SelectedResult = search.Results.Single(r => r.Item.Kind == SearchResultKind.Note);
        search.ActivateSelectedCommand.Execute(null);
        await search.CurrentActivation;

        Assert.Equal("This item no longer exists.", search.NoticeText);
        var left = Assert.Single(search.Results);
        Assert.Equal(new SearchItemRef(SearchResultKind.Link, 900), left.Item);

        search.SelectedResult = left;
        search.ActivateSelectedCommand.Execute(null);
        await search.CurrentActivation;
        Assert.Null(search.NoticeText);
        Assert.Equal((ProjectContentItemKind.Link, 900L), (area.SelectedContent!.SelectedItem!.Kind, area.SelectedContent.SelectedItem.Id));
        Assert.True(search.IsActive); // results stay so the next one can be tried
    }

    [Fact]
    public async Task TypingAgain_InvalidatesARevealInFlight()
    {
        var target = await Project("Target");
        var area = await Area();
        var search = area.Search!;
        await Type(search, "target");
        var gate = _store.Hold("resolve");
        search.SelectedResult = search.Results[0];
        search.ActivateSelectedCommand.Execute(null);
        gate.WaitEntered();

        search.SearchText = "targe";
        gate.Release();
        await search.CurrentActivation;

        Assert.Equal(WorkspaceRevealOutcome.Superseded, await area.CurrentReveal);
        Assert.NotEqual(target, area.SelectedNode?.Id);
    }

    [Fact]
    public void HitText_DescribesKindLocationAndMatch()
    {
        var hit = new WorkspaceSearchHitViewModel(new WorkspaceSearchHit(new SearchItemRef(SearchResultKind.Note, 1), 2, "Weekly", "Clients › Alpha", SearchField.Content, "agenda: kickoff"));

        Assert.Equal("Note", hit.KindText);
        Assert.Equal("content: “agenda: kickoff”", hit.MatchText);
        Assert.Equal("Note Weekly, Clients › Alpha, matched in content", hit.AccessibleName);
        Assert.Equal("title", new WorkspaceSearchHitViewModel(hit.Hit with { MatchedField = SearchField.Name, Excerpt = null }).MatchText);
    }

    private sealed class RecordingLog : IAppLog
    {
        public List<string> Entries { get; } = [];

        public void Info(string message) => Entries.Add(message);

        public void Warn(string message) => Entries.Add(message);

        public void Error(string message, Exception? exception = null) => Entries.Add($"{message} {exception}");
    }
}
