using System.ComponentModel;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.Services;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Workspace lifecycle in the Workspace area (Phase 8 proposal revision 2 §8.2–§8.6, §10.3): stale-read reconciliation
/// (tests 1–10, 10a), ownership and timing (11–17), the visit-bound Undo (17a–17d) and stale nodes (18–21). Gated stores
/// hold reads (snapshot when called, delivered on release) and writes (run on release). A second set of services on the
/// same database plays the other window.
/// <para>
/// Phase 11: every test body runs on a <see cref="SingleThreadContext"/> (one thread, like the WPF Dispatcher), so the view
/// models' continuations never run concurrently; modal callbacks pump instead of blocking. Teardown follows
/// <see cref="ContextTeardown"/>: release gates, settle on the context, confirm the stop, then delete the root.
/// </para>
/// </summary>
public sealed class WorkspaceTrashViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedWorkspaceStore _treeStore;
    private readonly GatedProjectContentStore _contentStore;
    private readonly RecordingProcessStarter _starter = new();
    private readonly WorkspaceService _other;
    private readonly ProjectContentService _otherContent;
    private readonly List<WorkspaceAreaViewModel> _areas = [];
    private readonly List<GatedSearchStore> _searchStores = [];
    private readonly SingleThreadContext _context = new();

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(30);

    public WorkspaceTrashViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var real = new SqliteWorkspaceStore(_connections);
        _treeStore = new GatedWorkspaceStore(real);
        _other = new WorkspaceService(real);
        _contentStore = new GatedProjectContentStore(new SqliteProjectContentStore(_connections));
        _otherContent = new ProjectContentService(new SqliteProjectContentStore(_connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
    }

    /// <summary>Every area is observed as its own tracked set, even when another faults (see <see cref="ContextTeardown"/>).</summary>
    public void Dispose() =>
        ContextTeardown.Run(_context, ReleaseGates, _areas.Select((area, i) => (ContextTeardown.TrackedSet)(() => Tracked(area, $"area{i}."))).ToList(),
            _root.Dispose, _root.Paths.Root, TeardownBound);

    private Task InContext(Func<Task> body) => _context.RunAsync(body, BodyTimeout);

    private void ReleaseGates()
    {
        _treeStore.ReleaseAll();
        _contentStore.ReleaseAll();
        foreach (var store in _searchStores)
            store.ReleaseAll();
    }

    /// <summary>Awaits the area's tracked operations until they are quiescent (faults propagate; see <see cref="TrackedTasks"/>).</summary>
    private static Task Settle(WorkspaceAreaViewModel area) => TrackedTasks.SettleAsync(() => Tracked(area, ""));

    private static IReadOnlyList<(string Name, Task Task)> Tracked(WorkspaceAreaViewModel area, string prefix)
    {
        var tasks = new List<(string Name, Task Task)>
        {
            ("CurrentDetailsLoad", area.CurrentDetailsLoad), ("CurrentReveal", area.CurrentReveal),
            ("CurrentLifecycleOperation", area.CurrentLifecycleOperation), ("CurrentReconciliation", area.CurrentReconciliation),
            ("Trash.CurrentLoad", area.Trash.CurrentLoad), ("Trash.CurrentCount", area.Trash.CurrentCount), ("Trash.CurrentRestore", area.Trash.CurrentRestore),
        };
        if (area.SelectedContent is { } content)
            tasks.AddRange([("Content.CurrentLoad", content.CurrentLoad), ("Content.CurrentPreviewLoad", content.CurrentPreviewLoad)]);
        return tasks.Select(t => (prefix + t.Name, t.Task)).ToList();
    }

    private GatedSearchStore SearchStore { get; set; } = null!;

    private async Task<WorkspaceAreaViewModel> Area(WorkspaceSearchStoreOptions? options = null)
    {
        SearchStore = new GatedSearchStore(new SqliteWorkspaceSearchStore(_connections, options ?? WorkspaceSearchStoreOptions.Default));
        _searchStores.Add(SearchStore);
        var content = new ProjectContentService(_contentStore, new ProcessProjectLinkLauncher(_starter, _ => true, _ => true));
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance, content, new ScriptedLinkChecker(),
            new WorkspaceSearchService(SearchStore), searchDelayMs: 0);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        return area;
    }

    private async Task<long> Folder(string name, long? parent = null) => (await _other.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null) => (await _other.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) => nodes.Single(n => !n.IsPlaceholder && n.Name == name);

    private static List<string> Names(IEnumerable<WorkspaceNodeViewModel> nodes) => nodes.Where(n => !n.IsPlaceholder).Select(n => n.Name).ToList();

    private static async Task Expand(WorkspaceAreaViewModel area, WorkspaceNodeViewModel folder)
    {
        folder.IsExpanded = true;
        Assert.True(await area.EnsureChildrenLoadedAsync(folder));
    }

    private static List<WorkspaceNodeViewModel> Placements(WorkspaceAreaViewModel area, long id)
    {
        var found = new List<WorkspaceNodeViewModel>();
        void Walk(IEnumerable<WorkspaceNodeViewModel> nodes)
        {
            foreach (var node in nodes.Where(n => !n.IsPlaceholder))
            {
                if (node.Id == id)
                    found.Add(node);
                Walk(node.Children);
            }
        }

        Walk(area.Roots);
        return found;
    }

    private static Task<WorkspaceRevealOutcome> Reveal(WorkspaceAreaViewModel area, SearchResultKind kind, long id) => area.RevealAsync(new SearchItemRef(kind, id));

    /// <summary>Selects the node and starts Move to Trash with the store write held; returns the pending operation and its gate.</summary>
    private (Task Operation, StoreGate Gate) StartHeldTrash(WorkspaceAreaViewModel area, WorkspaceNodeViewModel project)
    {
        area.SelectedNode = project;
        var gate = _treeStore.Hold("trashProject");
        var operation = area.TrashProjectCommand.ExecuteAsync();
        gate.WaitEntered();
        return (operation, gate);
    }

    private static async Task TrashNow(WorkspaceAreaViewModel area, WorkspaceNodeViewModel project)
    {
        area.SelectedNode = project;
        await area.TrashProjectCommand.ExecuteAsync();
    }

    private bool IsTrashedInDb(long id)
    {
        using var connection = _root.Open();
        return Db.ScalarLongWith(connection, "SELECT TrashedAt IS NOT NULL FROM WorkspaceNodes WHERE Id = $id;", ("$id", id)) == 1;
    }

    // ==== Basic flows ===========================================================================================================

    [Fact]
    public Task MoveToTrash_ConfirmsThenRemovesTheNodeAfterCommit_AndOffersUndo() => InContext(async () =>
    {
        var alpha = await Project("Alpha Portal");
        await Project("Beta");
        var area = await Area();

        await TrashNow(area, Node(area.Roots, "Alpha Portal"));

        Assert.Equal("Move project “Alpha Portal” to the Trash? Its folders, links and notes stay with it and come back when you restore it. Nothing on disk is touched.",
            _dialogs.Confirmations.Single().Message);
        Assert.Equal(["Beta"], Names(area.Roots));
        Assert.Null(area.SelectedNode);
        Assert.Equal("Moved “Alpha Portal” to the Trash.", area.BannerMessage);
        Assert.True(area.IsUndoAvailable);
        Assert.True(IsTrashedInDb(alpha));
        await area.Trash.CurrentCount;
        Assert.Equal("Trashed projects (1)", area.Trash.ButtonText);
    });

    [Fact]
    public Task ARemovedSelectedRow_IsUnselectedBeforeItLeavesItsCollection_SoNoParentIsSelectedForIt() => InContext(async () =>
    {
        var f = await Folder("F");
        await Project("P", f);
        var area = await Area();
        var fNode = Node(area.Roots, "F");
        await Expand(area, fNode);
        var pNode = Node(fNode.Children, "P");
        bool? selectedWhenRemoved = null;
        fNode.Children.CollectionChanged += (_, e) =>
        {
            if (e.OldItems?.Contains(pNode) == true)
                selectedWhenRemoved = pNode.IsSelected;
        };

        await TrashNow(area, pNode);

        Assert.False(selectedWhenRemoved); // a WPF tree selects the parent of a removed SELECTED row by itself
        Assert.Null(area.SelectedNode);
        Assert.False(fNode.IsSelected);
    });

    [Fact]
    public Task Undo_RestoresAndSelects_InTheSameVisit() => InContext(async () =>
    {
        await Project("Alpha");
        var area = await Area();
        await TrashNow(area, Node(area.Roots, "Alpha"));

        await area.UndoCommand.ExecuteAsync();

        Assert.Equal("Alpha", area.SelectedNode!.Name);
        Assert.Equal("Restored “Alpha” to Workspace (top level).", area.BannerMessage);
        Assert.False(area.IsUndoAvailable);
        await area.Trash.CurrentCount;
        Assert.Equal("Trashed projects (0)", area.Trash.ButtonText);
    });

    [Fact]
    public Task RemoveFolder_EmptyOnlyAfterConfirmation_OtherwiseNoDialogAndAReason() => InContext(async () =>
    {
        var empty = await Folder("Drafts");
        var clients = await Folder("Clients");
        await Project("Alpha", clients);
        var holder = await Folder("Holder");
        var held = await Project("Held", holder);
        await _other.TrashProjectAsync(held);
        var area = await Area();

        area.SelectedNode = Node(area.Roots, "Clients");
        await area.DeleteCommand.ExecuteAsync(); // Del on a folder
        Assert.Equal("“Clients” is not empty (1 item). Move or remove them first.", area.BannerMessage);
        area.SelectedNode = Node(area.Roots, "Holder");
        await area.RemoveFolderCommand.ExecuteAsync();
        Assert.Equal("“Holder” still holds 1 project in the Trash. Restore it and move it elsewhere first.", area.BannerMessage);
        Assert.Empty(_dialogs.Confirmations);

        _dialogs.ConfirmAnswer = false;
        area.SelectedNode = Node(area.Roots, "Drafts");
        await area.RemoveFolderCommand.ExecuteAsync();
        Assert.Contains("Drafts", Names(area.Roots));

        _dialogs.ConfirmAnswer = true;
        await area.RemoveFolderCommand.ExecuteAsync();
        Assert.Equal("Remove the empty folder “Drafts”? This removes only the WorkHub folder and cannot be undone. Nothing on disk is touched.", _dialogs.Confirmations[^1].Message);
        Assert.DoesNotContain("Drafts", Names(area.Roots));
        Assert.Null(area.SelectedNode);
        Assert.Null(await _other.GetNodeStateAsync(empty));
    });

    // ==== Stale reads (§8.3) =========================================================================================================

    [Fact]
    public Task T01_OlderLazyLoad_CannotResurrectATrashedProject_OtherRowsAndExpansionKept() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        await Project("A", f);
        var keep = await Folder("Keep");
        await Project("Inside", keep);
        var area = await Area(WorkspaceSearchStoreOptions.Default with { MaxRowsPerLevel = 1 });
        var keepNode = Node(area.Roots, "Keep");
        await Expand(area, keepNode);
        var inside = Node(keepNode.Children, "Inside");
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(f));
        // The reveal places P in F from an incomplete snapshot (F stays unloaded) and expands F, which starts F's lazy load.
        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, p));
        var fNode = Node(area.Roots, "F");
        load.WaitEntered(); // snapshot [P, A] taken now
        Assert.False(fNode.IsChildrenLoaded);

        await TrashNow(area, Node(fNode.Children, "P"));
        load.Release();
        await area.EnsureChildrenLoadedAsync(fNode);

        Assert.Equal(["A"], Names(fNode.Children));
        Assert.True(fNode.IsChildrenLoaded);
        Assert.Empty(Placements(area, p));
        Assert.Same(inside, Node(keepNode.Children, "Inside"));
        Assert.True(keepNode.IsExpanded);
    });

    [Fact]
    public Task T02_LazyLoadStartedDuringAPendingTrash_CannotResurrectIt() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        await Project("A", f);
        var area = await Area(WorkspaceSearchStoreOptions.Default with { MaxRowsPerLevel = 1 });
        var failing = _treeStore.Hold(GatedWorkspaceStore.Children(f));
        await Reveal(area, SearchResultKind.Project, p); // P placed in unloaded F; F's expansion starts a load
        var fNode = Node(area.Roots, "F");
        failing.WaitEntered();
        failing.Fail(new IOException("busy")); // that load fails: F collapses, stays unloaded, P stays placed
        await Task.Run(() => SpinWait.SpinUntil(() => !fNode.IsExpanded, TimeSpan.FromSeconds(10)));
        var (trash, gate) = StartHeldTrash(area, Node(fNode.Children, "P"));
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(f));
        fNode.IsExpanded = true; // a new lazy load starts DURING the pending Trash
        load.WaitEntered(); // before the commit

        gate.Release();
        await trash;
        load.Release();
        await area.EnsureChildrenLoadedAsync(fNode);

        Assert.Equal(["A"], Names(fNode.Children));
        Assert.Empty(Placements(area, p));
    });

    [Fact]
    public Task T03_Restore_AwaitsTheParentsLoad_ThenAppearsExactlyOnce() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        await Project("A", f);
        await _other.TrashProjectAsync(p);
        var area = await Area();
        await area.OpenTrashCommand.ExecuteAsync();
        await area.Trash.CurrentLoad;
        area.Trash.SelectedEntry = area.Trash.Entries.Single();
        var fNode = Node(area.Roots, "F");
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(f));
        fNode.IsExpanded = true;
        load.WaitEntered(); // snapshot WITHOUT P (before the Restore commit)

        var restore = area.Trash.RestoreCommand.ExecuteAsync();
        await Task.Run(() => SpinWait.SpinUntil(() => !IsTrashedInDb(p), TimeSpan.FromSeconds(10)));
        Assert.False(area.Trash.CurrentRestore.IsCompleted); // the application waits for F's load
        load.Release();
        await restore;
        await area.Trash.CurrentRestore;

        Assert.Equal(["A", "P"], Names(fNode.Children));
        Assert.Single(Placements(area, p));
        Assert.Null(area.SelectedNode); // Restore from the panel never changes the selection
    });

    [Fact]
    public Task T03b_RestoreCommittedBeforeTheLoadStarted_IsNeitherFilteredOutNorDuplicated() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        await _other.TrashProjectAsync(p);
        var area = await Area();
        await area.OpenTrashCommand.ExecuteAsync();
        await area.Trash.CurrentLoad;
        area.Trash.SelectedEntry = area.Trash.Entries.Single();
        var result = _treeStore.HoldCommitted("restoreProject");
        var restore = area.Trash.RestoreCommand.ExecuteAsync();
        result.WaitEntered(); // committed; the area has not applied it yet
        var fNode = Node(area.Roots, "F");
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(f));
        fNode.IsExpanded = true;
        load.WaitEntered(); // snapshot WITH P

        result.Release();
        load.Release();
        await restore;
        await area.Trash.CurrentRestore;

        Assert.Equal(["P"], Names(fNode.Children));
        Assert.Single(Placements(area, p));
    });

    [Fact]
    public Task T04_OlderLazyLoad_CannotResurrectARemovedFolder() => InContext(async () =>
    {
        var g = await Folder("G");
        var e = await Folder("E", g);
        await Project("B", g);
        var area = await Area(WorkspaceSearchStoreOptions.Default with { MaxRowsPerLevel = 1 });
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(g));
        await Reveal(area, SearchResultKind.WorkspaceFolder, e); // E placed in unloaded G; G's expansion starts its load
        var gNode = Node(area.Roots, "G");
        load.WaitEntered(); // snapshot [E, B]

        area.SelectedNode = Node(gNode.Children, "E");
        await area.RemoveFolderCommand.ExecuteAsync();
        load.Release();
        await area.EnsureChildrenLoadedAsync(gNode);

        Assert.Equal(["B"], Names(gNode.Children));
        Assert.Empty(Placements(area, e));
    });

    [Fact]
    public Task T05_RevealStartedDuringAPendingTrash_IsRetried_AndNeverResurrectsIt() => InContext(async () =>
    {
        var p = await Project("P");
        var f = await Folder("F");
        var s = await Project("S", f);
        var area = await Area();
        var (trash, gate) = StartHeldTrash(area, Node(area.Roots, "P"));
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, s);
        first.WaitEntered(); // the root snapshot lists P (before the commit)

        gate.Release();
        await trash;
        first.Release();

        Assert.Equal(WorkspaceRevealOutcome.Selected, await reveal);
        Assert.Equal(2, SearchStore.Resolves); // one fresh resolve after the detected completion
        Assert.Equal(["F"], Names(area.Roots));
        Assert.Empty(Placements(area, p));
        Assert.Same(Assert.Single(Placements(area, s)), area.SelectedNode);
        Assert.Single(Placements(area, f));
    });

    [Fact]
    public Task T05b_RevealStartedDuringAPendingTrash_InALoadedBranch_DoesNotPutItBack() => InContext(async () =>
    {
        var f = await Folder("F");
        await Project("P", f);
        var s = await Project("S", f);
        var area = await Area();
        var fNode = Node(area.Roots, "F");
        await Expand(area, fNode);
        var sInstance = Node(fNode.Children, "S");
        var (trash, gate) = StartHeldTrash(area, Node(fNode.Children, "P"));
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, s);
        first.WaitEntered();

        gate.Release();
        await trash;
        first.Release();

        Assert.Equal(WorkspaceRevealOutcome.Selected, await reveal);
        Assert.Equal(["S"], Names(fNode.Children));
        Assert.Same(sInstance, area.SelectedNode);
    });

    [Fact]
    public Task T06_RevealOfTheProjectItself_DuringAPendingTrash_ReportsTrashed_NothingPlaced() => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        var area = await Area();
        var (trash, gate) = StartHeldTrash(area, Node(area.Roots, "P"));
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, p);
        first.WaitEntered(); // Found (before the commit)

        gate.Release();
        await trash;
        first.Release();

        Assert.Equal(WorkspaceRevealOutcome.Trashed, await reveal);
        Assert.Empty(Placements(area, p));
        Assert.Null(area.SelectedNode);
    });

    [Fact]
    public Task T07_RevealAppliedBeforeTheCompletion_IsCorrectedByTheCompletion() => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        var area = await Area();
        await Task.WhenAll(area.CurrentDetailsLoad);
        var (trash, gate) = StartHeldTrash(area, Node(area.Roots, "P"));
        var content = area.SelectedContent!;

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, p));
        gate.Release();
        await trash;

        Assert.Empty(Placements(area, p));
        Assert.Null(area.SelectedNode);
        Assert.True(content.IsDeactivated);
    });

    [Fact]
    public Task T08_RevealSnapshotReadBeforeARestore_IsRetried_RestoredProjectPresentOnce() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var x = await Project("X", f);
        await _other.TrashProjectAsync(p);
        var area = await Area();
        var fNode = Node(area.Roots, "F");
        await Expand(area, fNode);
        await area.OpenTrashCommand.ExecuteAsync();
        await area.Trash.CurrentLoad;
        area.Trash.SelectedEntry = area.Trash.Entries.Single();
        var write = _treeStore.Hold("restoreProject");
        var restore = area.Trash.RestoreCommand.ExecuteAsync(); // the Restore is pending (a reveal started BEFORE it would be superseded)
        write.WaitEntered();
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, x);
        first.WaitEntered(); // complete branch [X] without P (before the commit)

        write.Release();
        await restore;
        first.Release();

        Assert.Equal(WorkspaceRevealOutcome.Selected, await reveal);
        Assert.Equal(2, SearchStore.Resolves);
        Assert.Equal(["P", "X"], Names(fNode.Children));
        Assert.Single(Placements(area, p));
    });

    [Fact]
    public Task T08b_RestoreIntoAnUnloadedFolder_DuringAReveal_ChangesNoBranchStamp_YetTheStaleSnapshotIsNotApplied() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var x = await Project("X", f);
        await _other.TrashProjectAsync(p);
        var area = await Area();
        var fNode = Node(area.Roots, "F");
        Assert.False(fNode.IsChildrenLoaded); // the Restore below changes no cached collection: only the completion count shows it
        await area.OpenTrashCommand.ExecuteAsync();
        await area.Trash.CurrentLoad;
        area.Trash.SelectedEntry = area.Trash.Entries.Single();
        var write = _treeStore.Hold("restoreProject");
        var restore = area.Trash.RestoreCommand.ExecuteAsync();
        write.WaitEntered();
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, x);
        first.WaitEntered(); // F's complete snapshot [X] — before the Restore commit

        write.Release();
        await restore;
        first.Release();

        Assert.Equal(WorkspaceRevealOutcome.Selected, await reveal);
        Assert.Equal(2, SearchStore.Resolves);
        Assert.True(fNode.IsChildrenLoaded);
        Assert.Equal(["P", "X"], Names(fNode.Children)); // the stale [X] would have dropped the restored P
        Assert.Single(Placements(area, p));
    });

    [Fact]
    public Task T09_TrashListAndCount_ReadBeforeACompletion_AreDiscardedAndReadAgain() => InContext(async () =>
    {
        var p = await Project("P");
        var q = await Project("Q");
        await _other.TrashProjectAsync(p);
        var area = await Area();
        var list = _treeStore.Hold("listTrash");
        var open = area.OpenTrashCommand.ExecuteAsync();
        list.WaitEntered(); // snapshot [P]
        await _other.TrashProjectAsync(q);
        await area.ReconcileNodeAsync(q); // Q found trashed: a completion without a list reload (selecting would close the panel)
        Assert.Empty(Placements(area, q));

        list.Release();
        await open;
        await area.Trash.CurrentLoad;

        Assert.Equal(["Q", "P"], area.Trash.Entries.Select(e => e.Name));
    });

    [Fact]
    public Task T09b_CountReadBeforeACompletion_IsNeverShown() => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        var area = await Area();
        Assert.Equal(0, area.Trash.Count);
        var shown = new List<int?>();
        area.Trash.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WorkspaceTrashViewModel.Count)) shown.Add(area.Trash.Count); };
        var count = _treeStore.Hold("countTrash");
        await _other.TrashProjectAsync(p);
        var refresh = area.Trash.RefreshCount();
        count.WaitEntered(); // snapshot 1

        await TrashNow(area, Node(area.Roots, "Q")); // completion; another count request collapses into the running one
        count.Release();
        await refresh;
        await area.Trash.CurrentCount;

        Assert.Equal([2], shown);
    });

    [Fact]
    public Task T10_RevealStartedBeforeTheCommand_IsSuperseded() => InContext(async () =>
    {
        var p = await Project("P");
        var s = await Project("S");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, s);
        first.WaitEntered();

        await area.TrashProjectCommand.ExecuteAsync();
        first.Release();

        Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal);
        Assert.Empty(Placements(area, p));
    });

    [Fact]
    public Task T10a_RevealSpanningTwoSequentialCompletions_ReturnsWorkspaceChanged_SecondSnapshotNotApplied() => InContext(async () =>
    {
        var p = await Project("P");
        var q = await Project("Q");
        var f = await Folder("F");
        var s = await Project("S", f);
        var area = await Area();
        var (trash, gate) = StartHeldTrash(area, Node(area.Roots, "P"));
        await _other.TrashProjectAsync(q); // trashed by another window
        var details = _treeStore.Hold($"details:{q}");
        var listing = _contentStore.Hold(GatedProjectContentStore.List(null)); // Q's content view would detect it too: keep that read held
        area.SelectedNode = Node(area.Roots, "Q");
        details.WaitEntered(); // reports Q trashed when released
        listing.WaitEntered();
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, s);
        first.WaitEntered(); // R1: before completion 1, lists P

        gate.Release();
        await trash; // completion 1
        var second = SearchStore.Hold("resolve");
        first.Release();
        second.WaitEntered(); // R2: after completion 1, before completion 2
        details.Release();
        await area.CurrentDetailsLoad; // completion 2: Q leaves the tree
        var fNode = Node(area.Roots, "F");
        var roots = Names(area.Roots);
        var (loaded, children) = (fNode.IsChildrenLoaded, fNode.Children.ToList());
        var selected = area.SelectedNode;
        second.Release();

        Assert.Equal(WorkspaceRevealOutcome.WorkspaceChanged, await reveal);
        Assert.Equal(2, SearchStore.Resolves);
        Assert.Equal(roots, Names(area.Roots));
        Assert.Equal(["F"], roots);
        Assert.Equal(loaded, fNode.IsChildrenLoaded);
        Assert.Equal(children, fNode.Children.ToList());
        Assert.Same(selected, area.SelectedNode);
        Assert.Empty(Placements(area, s));
        Assert.Empty(Placements(area, p));
        Assert.Empty(Placements(area, q));
        listing.Release(); // its view was deactivated when Q left the tree: the late result changes nothing
    });

    // ==== Stale NEGATIVE resolutions (Codex review of Phase 8) =====================================================================

    /// <summary>P trashed by another window and already reconciled here; the Workspace Trash panel shows it, selected.</summary>
    private async Task<WorkspaceAreaViewModel> AreaWithTrashedEntry(long project)
    {
        await _other.TrashProjectAsync(project);
        var area = await Area();
        await area.ReconcileNodeAsync(project);
        await area.OpenTrashCommand.ExecuteAsync();
        await area.Trash.CurrentLoad;
        area.Trash.SelectedEntry = area.Trash.Entries.Single(e => e.NodeId == project);
        return area;
    }

    /// <summary>Starts a panel Restore with the write held; returns the pending command and its gate.</summary>
    private (Task Restore, StoreGate Gate) StartHeldRestore(WorkspaceAreaViewModel area)
    {
        var gate = _treeStore.Hold("restoreProject");
        var restore = area.Trash.RestoreCommand.ExecuteAsync();
        gate.WaitEntered();
        return (restore, gate);
    }

    [Fact]
    public Task N1_StaleTrashedResolution_DeliveredAfterRestore_IsReResolved_AndSelectsTheActiveProject() => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        var area = await AreaWithTrashedEntry(p);
        var (restore, gate) = StartHeldRestore(area);
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, p);
        first.WaitEntered(); // reads Trashed (before the Restore commit)

        gate.Release();
        await restore;
        Assert.Single(Placements(area, p)); // committed and reconciled before the stale result arrives
        first.Release();

        Assert.Equal(WorkspaceRevealOutcome.Selected, await reveal);
        Assert.Equal(2, SearchStore.Resolves);
        Assert.Same(Assert.Single(Placements(area, p)), area.SelectedNode);
    });

    [Fact]
    public Task N2_StaleProjectTrashedResolution_ForContent_DeliveredAfterRestore_RevealsTheActiveContent() => InContext(async () =>
    {
        var p = await Project("P");
        var note = (await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Kickoff", "x"))).Note!.Id;
        var area = await AreaWithTrashedEntry(p);
        var (restore, gate) = StartHeldRestore(area);
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Note, note);
        first.WaitEntered(); // reads ProjectTrashed

        gate.Release();
        await restore;
        first.Release();

        Assert.Equal(WorkspaceRevealOutcome.Selected, await reveal);
        Assert.Equal(2, SearchStore.Resolves);
        Assert.Equal(p, area.SelectedNode!.Id);
        Assert.Equal(new ContentRef(ContentKind.Note, note), area.SelectedContent!.SelectedItem!.Ref);
    });

    [Fact]
    public Task N3_TwoConsecutiveInvalidatedResolutions_FirstNegative_ReturnWorkspaceChanged_NothingApplied() => InContext(async () =>
    {
        var p = await Project("P");
        var q = await Project("Q");
        var area = await AreaWithTrashedEntry(p);
        var (restore, gate) = StartHeldRestore(area);
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, p);
        first.WaitEntered(); // R1: Trashed (stale once the Restore completes)

        gate.Release();
        await restore; // completion 1
        var second = SearchStore.Hold("resolve");
        first.Release();
        second.WaitEntered(); // R2: Found (after completion 1)
        await _other.TrashProjectAsync(q);
        await area.ReconcileNodeAsync(q); // completion 2, during R2
        var roots = Names(area.Roots);
        var selected = area.SelectedNode;
        second.Release();

        Assert.Equal(WorkspaceRevealOutcome.WorkspaceChanged, await reveal);
        Assert.Equal(2, SearchStore.Resolves);
        Assert.Equal(roots, Names(area.Roots));
        Assert.Same(selected, area.SelectedNode); // R2 not applied (P not selected by the reveal)
        Assert.Single(Placements(area, p));
    });

    [Fact]
    public Task N3b_TwoConsecutiveInvalidatedResolutions_SecondNegative_ReturnWorkspaceChanged() => InContext(async () =>
    {
        var p = await Project("P");
        var q = await Project("Q");
        var r = await Project("R");
        var area = await AreaWithTrashedEntry(p);
        var (restore, gate) = StartHeldRestore(area);
        var first = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, q);
        first.WaitEntered(); // R1: Found Q

        gate.Release();
        await restore; // completion 1
        var second = SearchStore.Hold("resolve");
        await _other.TrashProjectAsync(q); // trashed elsewhere before R2 reads
        first.Release();
        second.WaitEntered(); // R2: Trashed
        await _other.RestoreProjectAsync(q); // R2 is now stale as well
        await _other.TrashProjectAsync(r);
        await area.ReconcileNodeAsync(r); // completion 2 in this window, during R2
        second.Release();

        Assert.Equal(WorkspaceRevealOutcome.WorkspaceChanged, await reveal); // never a false "in the Trash"
        Assert.Equal(2, SearchStore.Resolves);
        Assert.Single(Placements(area, q));
    });

    [Fact]
    public Task N4_NegativeResolutionsWithoutAnInterveningCompletion_KeepTheirBehavior() => InContext(async () =>
    {
        var p = await Project("P");
        var note = (await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Kickoff", "x"))).Note!.Id;
        await _other.TrashProjectAsync(p);
        var area = await Area();

        Assert.Equal(WorkspaceRevealOutcome.Trashed, await Reveal(area, SearchResultKind.Project, p));
        Assert.Equal(1, SearchStore.Resolves);
        Assert.Equal(WorkspaceRevealOutcome.ProjectTrashed, await Reveal(area, SearchResultKind.Note, note));
        Assert.Equal(2, SearchStore.Resolves);
        Assert.Equal(WorkspaceRevealOutcome.NotFound, await Reveal(area, SearchResultKind.Project, 9999));
        Assert.Equal(3, SearchStore.Resolves);
    });

    [Fact]
    public Task N5_Search_HitOfARestoredProject_IsNotRemovedByAStaleTrashedResolution() => InContext(async () =>
    {
        var p = await Project("Alpha Portal");
        var area = await Area();
        var search = area.Search!;
        search.SearchText = "alpha";
        await search.CurrentSearch;
        var hit = search.Results.Single();
        await _other.TrashProjectAsync(p); // another window; this window's results are a snapshot
        await area.ReconcileNodeAsync(p);
        await area.OpenTrashCommand.ExecuteAsync();
        await area.Trash.CurrentLoad;
        area.Trash.SelectedEntry = area.Trash.Entries.Single();
        var (restore, gate) = StartHeldRestore(area);
        var first = SearchStore.Hold("resolve");
        search.SelectedResult = hit;
        search.ActivateSelectedCommand.Execute(null);
        first.WaitEntered(); // reads Trashed

        gate.Release();
        await restore;
        first.Release();
        await search.CurrentActivation;

        Assert.Contains(hit, search.Results);
        Assert.Null(search.NoticeText);
        Assert.Equal(p, area.SelectedNode!.Id);
    });

    // ==== Ownership and timing (§8.2) ============================================================================================

    [Fact]
    public Task T11_SelectionMovesToQ_DuringAPendingTrash_QsViewVisitAndDraftAreUntouched() => InContext(async () =>
    {
        await Project("P");
        await Project("Q");
        var area = await Area();
        var (trash, gate) = StartHeldTrash(area, Node(area.Roots, "P"));
        var pView = area.SelectedContent!;
        var qNode = Node(area.Roots, "Q");
        area.SelectedNode = qNode;
        var qView = area.SelectedContent!;
        await qView.CurrentLoad;
        Assert.True(pView.IsDeactivated);
        var (folder, generation) = (qView.CurrentFolderId, qView.Generation);
        var checkedInside = false;
        _dialogs.OnNoteEditor = editor =>
        {
            editor.Title = "Draft title";
            editor.Content = "draft text";
            gate.Release();
            Assert.True(_dialogs.PumpWhileModal(() => trash.IsCompleted, TimeSpan.FromSeconds(10))); // the Trash completes while this editor is open

            Assert.Same(qNode, area.SelectedNode);
            Assert.Same(qView, area.SelectedContent);
            Assert.False(qView.IsDeactivated);
            Assert.Equal((folder, generation), (qView.CurrentFolderId, qView.Generation));
            Assert.Equal(("Draft title", "draft text"), (editor.Title, editor.Content));
            Assert.Equal(NoteEditorState.Editing, editor.State);
            checkedInside = true;
            _dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Discard;
            editor.RequestClose();
            return false;
        };

        await qView.NewNoteCommand.ExecuteAsync();

        Assert.True(checkedInside);
        Assert.Equal(["Q"], Names(area.Roots));
        Assert.Same(qNode, area.SelectedNode);

        await Settle(area); // Phase 11: the end state holds once every tracked operation has finished, too
        Assert.Equal(["Q"], Names(area.Roots));
        Assert.Same(qNode, area.SelectedNode);
        Assert.False(qView.IsDeactivated);
    });

    [Fact]
    public Task T12_P_Q_P_DuringAPendingTrash_DeactivatesOnlyTheCurrentPView() => InContext(async () =>
    {
        await Project("P");
        await Project("Q");
        var area = await Area();
        var pNode = Node(area.Roots, "P");
        var (trash, gate) = StartHeldTrash(area, pNode);
        var first = area.SelectedContent!;
        area.SelectedNode = Node(area.Roots, "Q");
        var qView = area.SelectedContent!;
        area.SelectedNode = pNode;
        var current = area.SelectedContent!;
        Assert.NotSame(first, current);

        gate.Release();
        await trash;

        Assert.True(current.IsDeactivated);
        Assert.Null(area.SelectedContent);
        Assert.True(qView.IsDeactivated); // deactivated earlier by the user's own selection change, as before
        Assert.Equal(["Q"], Names(area.Roots));

        await Settle(area); // Phase 11: the end state holds once every tracked operation has finished, too
        Assert.True(current.IsDeactivated);
        Assert.Null(area.SelectedContent);
        Assert.Equal(["Q"], Names(area.Roots));
    });

    [Fact]
    public Task T13_TrashFailure_ChangesNothing_AndReleasesTheSlot() => InContext(async () =>
    {
        await Project("P");
        var area = await Area();
        var pNode = Node(area.Roots, "P");
        var (trash, gate) = StartHeldTrash(area, pNode);
        var view = area.SelectedContent!;

        gate.Fail(new IOException("disk"));
        await trash;

        Assert.Same(pNode, area.SelectedNode);
        Assert.Same(view, area.SelectedContent);
        Assert.False(view.IsDeactivated);
        Assert.Equal(["P"], Names(area.Roots));
        Assert.Equal("Could not move “P” to the Trash. Nothing was changed.", area.BannerMessage);
        Assert.False(area.IsLifecycleBusy);
        Assert.False(area.IsUndoAvailable);
        Assert.True(area.TrashProjectCommand.CanExecute(null));
    });

    [Fact]
    public Task T14_DeclinedConfirmation_MakesNoStoreCall() => InContext(async () =>
    {
        await Project("P");
        var area = await Area();
        _dialogs.ConfirmAnswer = false;

        await TrashNow(area, Node(area.Roots, "P"));

        Assert.Equal(0, _treeStore.Calls("trashProject"));
        Assert.Equal(["P"], Names(area.Roots));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task T15_ContentWriteDuringAPendingTrash_BothCommitOrders(bool contentFirst) => InContext(async () =>
    {
        var p = await Project("P");
        var link = (await _otherContent.CreateLinkAsync(p, null, new ProjectLinkDraft("Wiki", ProjectLinkType.Web, "https://wiki.example.test/"))).Value!.Id;
        var area = await Area();
        var (trash, gate) = StartHeldTrash(area, Node(area.Roots, "P"));
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        view.SelectedItem = view.Items.Single();
        var contentGate = _contentStore.Hold("trash");
        var contentTrash = view.MoveToTrashCommand.ExecuteAsync();
        contentGate.WaitEntered();

        if (contentFirst)
        {
            contentGate.Release();
            await contentTrash;
            gate.Release();
            await trash;
        }
        else
        {
            gate.Release();
            await trash;
            contentGate.Release();
            await contentTrash;
        }

        Assert.True(IsTrashedInDb(p));
        using var connection = _root.Open();
        Assert.Equal(contentFirst ? 1 : 0, Db.Count(connection, "ProjectTrashEntries")); // saved and kept / refused, nothing written
        Assert.Equal(contentFirst ? 1 : 0, Db.ScalarLongWith(connection, "SELECT TrashEntryId IS NOT NULL FROM ProjectLinks WHERE Id = $id;", ("$id", link)));
        Assert.True(view.IsDeactivated);
    });

    [Fact]
    public Task T16_PendingTrash_KeepsTheNodeAndView_DisablesWorkspaceWrites_AllowsTheRest() => InContext(async () =>
    {
        await Project("P");
        await Project("Q");
        var area = await Area();
        var pNode = Node(area.Roots, "P");
        var (trash, gate) = StartHeldTrash(area, pNode);
        var view = area.SelectedContent!;
        await view.CurrentLoad;

        Assert.Equal("Moving “P” to the Trash…", area.BannerMessage);
        Assert.Contains("P", Names(area.Roots));
        Assert.False(view.IsDeactivated);
        Assert.True(area.IsLifecycleBusy);
        foreach (var command in new[] { area.NewFolderCommand, area.NewProjectCommand, area.RenameCommand, area.EditProjectCommand, area.MoveCommand,
                     area.TrashProjectCommand, area.RemoveFolderCommand, area.DeleteCommand, area.UndoCommand })
            Assert.False(command.CanExecute(null));
        Assert.True(view.NewFolderCommand.CanExecute(null));
        await area.DeleteCommand.ExecuteAsync(); // Del again: no second store call
        Assert.Equal(1, _treeStore.Calls("trashProject"));
        area.SelectedNode = Node(area.Roots, "Q"); // selection stays available
        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, (await _other.ListChildrenAsync(null)).Single(n => n.Name == "Q").Id));

        gate.Release();
        await trash;
        Assert.True(area.NewFolderCommand.CanExecute(null));
    });

    [Fact]
    public Task T17_UndoSelectsOnlyIfTheSelectionIsUnchanged_PanelRestoreNeverSelects_BackKeepsTheSelection() => InContext(async () =>
    {
        await Project("P");
        await Project("Q");
        var r = await Project("R");
        await _other.TrashProjectAsync(r);
        var area = await Area();
        await TrashNow(area, Node(area.Roots, "P"));
        var restore = _treeStore.Hold("restoreProject");
        var undo = area.UndoCommand.ExecuteAsync();
        restore.WaitEntered();
        area.SelectedNode = Node(area.Roots, "Q"); // changed after the Undo click
        restore.Release();
        await undo;
        Assert.Equal("Q", area.SelectedNode!.Name);
        Assert.Contains("P", Names(area.Roots));

        var qView = area.SelectedContent;
        await area.OpenTrashCommand.ExecuteAsync();
        await area.Trash.CurrentLoad;
        area.Trash.SelectedEntry = area.Trash.Entries.Single();
        await area.Trash.RestoreCommand.ExecuteAsync();
        Assert.Equal("Restored “R” to Workspace (top level).", area.Trash.Notice);
        Assert.Equal("Q", area.SelectedNode!.Name);
        area.CloseTrashCommand.Execute(null); // Back
        Assert.False(area.IsTrashOpen);
        Assert.Same(qView, area.SelectedContent);
        Assert.False(qView!.IsDeactivated);
        Assert.Contains("R", Names(area.Roots));
    });

    // ==== Visit-bound Undo (§8.2) =================================================================================================

    [Fact]
    public Task T17a_TrashCompletingAfterTheTabWasLeft_ReconcilesTheTree_OffersNoUndo() => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        var area = await Area();
        var (trash, gate) = StartHeldTrash(area, Node(area.Roots, "P"));
        area.IsTabSelected = false;

        gate.Release();
        await trash;
        area.IsTabSelected = true;

        Assert.Empty(Placements(area, p));
        Assert.Equal(["Q"], Names(area.Roots));
        Assert.Equal("Moved “P” to the Trash.", area.BannerMessage);
        Assert.False(area.IsUndoAvailable);
        Assert.False(area.UndoCommand.CanExecute(null));
        await area.UndoCommand.ExecuteAsync();
        Assert.Equal(0, _treeStore.Calls("restoreProject"));
    });

    [Fact]
    public Task T17b_LeavingAndReEnteringBeforeTheCompletion_DoesNotRevivetheOldVisit() => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        var area = await Area();
        var (trash, gate) = StartHeldTrash(area, Node(area.Roots, "P"));
        area.IsTabSelected = false;
        area.IsTabSelected = true;

        gate.Release();
        await trash;

        Assert.Empty(Placements(area, p));
        Assert.False(area.IsUndoAvailable);
        Assert.False(area.UndoCommand.CanExecute(null));
    });

    [Fact]
    public Task T17c_PendingUndo_LeaveAndReEnter_RestoresIntoTheTree_WithoutSelecting() => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        var area = await Area();
        await TrashNow(area, Node(area.Roots, "P"));
        Assert.Null(area.SelectedNode);
        var restore = _treeStore.Hold("restoreProject");
        var undo = area.UndoCommand.ExecuteAsync();
        restore.WaitEntered();
        area.IsTabSelected = false;
        area.IsTabSelected = true;

        restore.Release();
        await undo;

        Assert.False(IsTrashedInDb(p));
        Assert.Single(Placements(area, p));
        Assert.Equal(["P", "Q"], Names(area.Roots));
        Assert.Null(area.SelectedNode); // selection unchanged, but the Undo's visit ended
        Assert.Equal("Restored “P” to Workspace (top level).", area.BannerMessage);
    });

    [Fact]
    public Task T17d_SameVisit_KeepsTheApprovedUndoBehavior() => InContext(async () =>
    {
        await Project("P");
        await Project("Q");
        var area = await Area();
        await TrashNow(area, Node(area.Roots, "P"));
        area.SelectedNode = Node(area.Roots, "Q");
        Assert.True(area.IsUndoAvailable); // selection changes alone keep it

        await area.UndoCommand.ExecuteAsync();

        Assert.Equal("P", area.SelectedNode!.Name); // selection unchanged since the click
        Assert.False(area.IsUndoAvailable);
    });

    [Fact]
    public Task DismissingOrReplacingTheBannerMessage_WithdrawsItsUndo() => InContext(async () =>
    {
        var p = await Project("P");
        var q = await Project("Q");
        var area = await Area();
        await TrashNow(area, Node(area.Roots, "P"));
        area.DismissBannerCommand.Execute(null);
        Assert.False(area.IsUndoAvailable);

        await TrashNow(area, Node(area.Roots, "Q"));
        Assert.True(area.IsUndoAvailable);
        await _other.RestoreProjectAsync(p);
        await _other.TrashProjectAsync(p);
        await area.ReconcileNodeAsync(p); // another message replaces "Moved “Q” …"
        Assert.False(area.IsUndoAvailable);
        Assert.True(IsTrashedInDb(q));
    });

    // ==== Stale nodes (§8.4) =====================================================================================================

    [Fact]
    public Task T18_RefusedRename_FreshRead_RemovesP_WhileQStaysSelected() => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        await _other.TrashProjectAsync(p);
        _dialogs.OnWorkspaceNameEditor = editor =>
        {
            editor.Name = "P2";
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10)); // a modal dialog keeps dispatching
            Assert.Equal("This project is in the Trash.", editor.Error);
            return false; // the dialog kept its values; the user closes it
        };
        var state = _treeStore.Hold("nodeState");
        var rename = area.RenameCommand.ExecuteAsync();
        state.WaitEntered();
        var qNode = Node(area.Roots, "Q");
        area.SelectedNode = qNode;
        var qView = area.SelectedContent;

        state.Release();
        await rename;
        await area.CurrentReconciliation;

        Assert.Empty(Placements(area, p));
        Assert.Same(qNode, area.SelectedNode);
        Assert.Same(qView, area.SelectedContent);
        Assert.Equal("This project is in the Trash.", area.BannerMessage);
    });

    [Fact]
    public Task T19_FreshReadOlderThanANewerCompletion_IsDiscarded() => InContext(async () =>
    {
        var p = await Project("P");
        var area = await Area();
        await TrashNow(area, Node(area.Roots, "P"));
        var state = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(p);
        state.WaitEntered(); // says: trashed

        await area.UndoCommand.ExecuteAsync(); // restores P (a newer completion)
        state.Release();
        await reconcile;

        Assert.Single(Placements(area, p));
        Assert.Equal("P", area.SelectedNode!.Name);
    });

    [Fact]
    public Task T20_CreateRefusedBecauseTheParentWasRemoved_RemovesThatFolderById() => InContext(async () =>
    {
        var e = await Folder("E");
        await Folder("Keep");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "E");
        await _other.RemoveEmptyFolderAsync(e);
        _dialogs.OnWorkspaceNameEditor = editor =>
        {
            editor.Name = "Child";
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10)); // a modal dialog keeps dispatching
            Assert.Equal(WorkspaceFailure.ParentNotFound, editor.Refusal);
            return false;
        };

        await area.NewFolderCommand.ExecuteAsync();
        await area.CurrentReconciliation;

        Assert.Equal(["Keep"], Names(area.Roots));
        Assert.Null(area.SelectedNode);
        Assert.Equal("The destination folder no longer exists. Nothing was created.", area.BannerMessage);
    });

    [Fact]
    public Task T21_DetailsReportTrashed_RemovesTheSelectedProject_StaleDetailsForAnOlderSelectionIgnored() => InContext(async () =>
    {
        var p = await Project("P");
        var q = await Project("Q");
        var area = await Area();
        await _other.TrashProjectAsync(p);
        area.SelectedNode = Node(area.Roots, "P");
        await area.CurrentDetailsLoad;
        Assert.Empty(Placements(area, p));
        Assert.Equal("This project is in the Trash.", area.BannerMessage);

        await _other.TrashProjectAsync(q);
        var details = _treeStore.Hold($"details:{q}");
        var listing = _contentStore.Hold(GatedProjectContentStore.List(null)); // Q's content view would detect it too
        var qNode = Node(area.Roots, "Q");
        area.SelectedNode = qNode;
        var qLoad = area.CurrentDetailsLoad;
        details.WaitEntered();
        listing.WaitEntered();
        area.SelectedNode = null; // an older selection's result arrives late
        details.Release();
        await qLoad;
        listing.Release(); // its view was deactivated by the selection change: nothing is observed

        Assert.Single(Placements(area, q)); // ignored; reconciled when it is next read
    });

    // ==== Content views, note editor and search ====================================================================================

    [Fact]
    public Task ContentViewObservingProjectTrashed_RemovesTheNodeById() => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        await area.CurrentDetailsLoad; // Phase 11: else this selection's details read can observe the Trash first (a controlled reproduction showed it)
        await _other.TrashProjectAsync(p);

        await view.NavigateAsync(null); // the next load reports the Project in the Trash
        await area.CurrentReconciliation;

        Assert.Equal("This project is in the Trash.", view.ContainerMessage);
        Assert.Empty(Placements(area, p));
        Assert.True(view.IsDeactivated);
    });

    [Fact]
    public Task NoteEditor_ProjectTrashedElsewhere_KeepsTheDraft_CopyDraftOnly() => InContext(async () =>
    {
        var p = await Project("P");
        await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Kickoff", "v1"));
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        view.SelectedItem = view.Items.Single();
        var checkedInside = false;
        _dialogs.OnNoteEditor = editor =>
        {
            editor.Content = "my draft";
            _dialogs.WaitModal(_other.TrashProjectAsync(p), TimeSpan.FromSeconds(10));
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10)); // a modal dialog keeps dispatching

            Assert.Equal("This project is in the Trash. Your text is unchanged here.", editor.GeneralError);
            Assert.Equal("my draft", editor.Content);
            Assert.True(editor.IsProjectTrashed);
            Assert.True(editor.CopyDraftCommand.CanExecute(null));
            Assert.False(editor.SaveAsNewCommand.CanExecute(null));
            editor.CopyDraftCommand.Execute(null);
            checkedInside = true;
            _dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Discard;
            editor.RequestClose();
            return false;
        };

        await view.OpenCommand.ExecuteAsync();

        Assert.True(checkedInside);
        Assert.Equal("Kickoff\r\n\r\nmy draft", _dialogs.Clipboard);
        using var connection = _root.Open();
        Assert.Equal("v1", Db.ScalarString(connection, "SELECT Content FROM ProjectNotes;"));
    });

    [Fact]
    public Task Search_TrashRemovesTheProjectsHit_StaleContentHitReportsItsProjectInTheTrash() => InContext(async () =>
    {
        var p = await Project("Alpha Portal");
        var note = (await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Alpha kickoff", "x"))).Note!.Id;
        var other = await Project("Beta");
        var area = await Area();
        var search = area.Search!;
        search.SearchText = "alpha";
        await search.CurrentSearch;
        Assert.Equal(2, search.Results.Count);

        await TrashNow(area, Node(area.Roots, "Alpha Portal"));
        Assert.DoesNotContain(search.Results, r => r.Item == new SearchItemRef(SearchResultKind.Project, p));

        search.SelectedResult = search.Results.Single();
        search.ActivateSelectedCommand.Execute(null);
        await search.CurrentActivation;
        Assert.Equal("This item's project is in the Trash.", search.NoticeText);
        Assert.Empty(search.Results);
        Assert.NotEqual(0, other);
        Assert.NotEqual(0, note);
    });
}
