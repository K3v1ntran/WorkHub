using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Folder Archive and Folder Trash in the Workspace area (Phase 13 proposal revision 4 §7.2-§7.5, §7.9 C1-C12; B6, B8, B13,
/// B18, B19, B35-B46). Ordering is established with gates on the stores (a held read takes its snapshot when called and
/// delivers it on release; a held write runs on release) and by awaiting the view model's exposed tasks — never with delays.
/// A second set of services on the same database plays the other window.
/// <para>Every body runs on a <see cref="SingleThreadContext"/>; teardown follows <see cref="ContextTeardown"/>.</para>
/// </summary>
public sealed class FolderLifecycleViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedWorkspaceStore _treeStore;
    private readonly GatedProjectContentStore _contentStore;
    private readonly WorkspaceService _other;
    private readonly ProjectContentService _otherContent;
    private readonly List<WorkspaceAreaViewModel> _areas = [];
    private readonly List<GatedSearchStore> _searchStores = [];
    private readonly SingleThreadContext _context = new();

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(30);

    public FolderLifecycleViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _treeStore = new GatedWorkspaceStore(new SqliteWorkspaceStore(_connections));
        var otherConnections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _other = new WorkspaceService(new SqliteWorkspaceStore(otherConnections));
        _contentStore = new GatedProjectContentStore(new SqliteProjectContentStore(_connections));
        _otherContent = new ProjectContentService(new SqliteProjectContentStore(otherConnections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
    }

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

    private static Task Settle(WorkspaceAreaViewModel area) => TrackedTasks.SettleAsync(() => Tracked(area, ""));

    private static IReadOnlyList<(string Name, Task Task)> Tracked(WorkspaceAreaViewModel area, string prefix)
    {
        var tasks = new List<(string Name, Task Task)>
        {
            ("CurrentDetailsLoad", area.CurrentDetailsLoad), ("CurrentReveal", area.CurrentReveal), ("CurrentFolderInfoLoad", area.CurrentFolderInfoLoad),
            ("CurrentLifecycleOperation", area.CurrentLifecycleOperation), ("CurrentReconciliation", area.CurrentReconciliation),
            ("Trash.CurrentLoad", area.Trash.CurrentLoad), ("Trash.CurrentCount", area.Trash.CurrentCount), ("Trash.CurrentRestore", area.Trash.CurrentRestore),
            ("Archived.CurrentLoad", area.Archived.CurrentLoad), ("Archived.CurrentCount", area.Archived.CurrentCount), ("Archived.CurrentUnarchive", area.Archived.CurrentUnarchive),
        };
        if (area.Search is { } search)
            tasks.AddRange([("Search.CurrentSearch", search.CurrentSearch), ("Search.CurrentActivation", search.CurrentActivation)]);
        if (area.SelectedContent is { } content)
            tasks.AddRange([("Content.CurrentLoad", content.CurrentLoad), ("Content.CurrentPreviewLoad", content.CurrentPreviewLoad),
                ("Content.CurrentTrashOperation", content.CurrentTrashOperation), ("Content.CurrentReconciliation", content.CurrentReconciliation)]);
        return tasks.Select(t => (prefix + t.Name, t.Task)).ToList();
    }

    private GatedSearchStore SearchStore { get; set; } = null!;

    private async Task<WorkspaceAreaViewModel> Area()
    {
        SearchStore = new GatedSearchStore(new SqliteWorkspaceSearchStore(_connections, WorkspaceSearchStoreOptions.Default));
        _searchStores.Add(SearchStore);
        var content = new ProjectContentService(_contentStore, new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance, content, new ScriptedLinkChecker(),
            new WorkspaceSearchService(SearchStore), searchDelayMs: 0);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        return area;
    }

    private async Task<long> Folder(string name, long? parent = null) => (await _other.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null) =>
        (await _other.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;

    /// <summary>The other window confirms and commits at once.</summary>
    private async Task OtherMarks(long folder, FolderLifecycleKind kind) =>
        Assert.Equal(FolderLifecycleOutcome.Committed,
            (await _other.MarkFolderAsync(folder, kind, (await _other.PrepareFolderLifecycleAsync(folder, kind)).Confirmation!.Token)).Outcome);

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) => nodes.Single(n => !n.IsPlaceholder && n.Name == name);

    private static async Task<WorkspaceNodeViewModel> Expand(WorkspaceAreaViewModel area, WorkspaceNodeViewModel folder)
    {
        folder.IsExpanded = true;
        Assert.True(await area.EnsureChildrenLoadedAsync(folder));
        return folder;
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

    private List<string> Data()
    {
        using var connection = _root.Open();
        return Persistence.ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private string Markers(long id)
    {
        using var connection = _root.Open();
        return Db.ScalarString(connection, "SELECT coalesce(FolderArchivedAt, '-') || '|' || coalesce(FolderTrashedAt, '-') || '|' || LifecycleSerial FROM WorkspaceNodes WHERE Id = $id;", ("$id", id))!;
    }

    // ---- Archive / Trash here: the confirmation, C1, Undo, Del --------------------------------------------------------------------

    [Fact]
    public Task Archive_ShowsOneBoundedConfirmation_CommitsWithItsToken_RemovesTheWholeCachedSubtree_AndOffersUndo() => InContext(async () =>
    {
        var f = await Folder("2024");
        var d = await Folder("Q1", f);
        foreach (var name in new[] { "A", "B", "C", "E" })
            await Project(name, f);
        var p = await Project("Alpha", d);
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "2024"));
        var dNode = await Expand(area, Node(fNode.Children, "Q1"));
        area.SelectedNode = fNode;

        await area.ArchiveFolderCommand.ExecuteAsync();

        var shown = Assert.Single(_dialogs.FolderConfirmations);
        Assert.Equal(("Archive Folder", "Archive", false), (shown.Title, shown.ConfirmText, shown.IsStale));
        Assert.Equal("In: Workspace (top level)", shown.LocationText);
        Assert.Equal("Contains 1 folder, 5 projects, 0 links and 0 notes.", shown.CountsText);
        Assert.Equal(["Q1", "A", "B"], shown.ShownChildren);
        Assert.Equal("and 2 more", shown.MoreText);
        Assert.Empty(Placements(area, f));
        Assert.Null(area.CachedNode(f));
        Assert.Null(area.CachedNode(d)); // C1: the root and EVERY cached descendant are forgotten
        Assert.Null(area.CachedNode(p));
        Assert.Null(area.SelectedNode);
        Assert.Equal("Archived folder “2024”.", area.BannerMessage);
        Assert.True(area.IsUndoAvailable);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT[^|]+\|-\|1$", Markers(f)); // archived (a timestamp), serial 1
        await area.Archived.CurrentCount;
        Assert.Equal("Archived (1)", area.Archived.ButtonText);
        Assert.DoesNotContain(dNode.Children, c => c.IsSelected);

        await area.UndoCommand.ExecuteAsync(); // event-specific Undo: the same serial
        Assert.Equal("-|-|1", Markers(f));
        Assert.Equal(f, area.SelectedNode!.Id); // same visit, no selection since the click
        Assert.False(area.SelectedNode.IsExpanded); // C9: placed collapsed and unloaded
        await Settle(area);
    });

    [Fact]
    public Task Del_OnANonEmptyFolder_OffersTheSubtreeTrash_OnAnEmptyOne_KeepsRemove() => InContext(async () =>
    {
        var full = await Folder("Full");
        await Project("Inside", full);
        var empty = await Folder("Empty");
        var area = await Area();

        area.SelectedNode = Node(area.Roots, "Full");
        await area.DeleteCommand.ExecuteAsync();
        Assert.Equal("Move Folder to Trash", Assert.Single(_dialogs.FolderConfirmations).Title); // D12
        Assert.Empty(_dialogs.Confirmations);
        Assert.Matches(@"^-\|\d{4}-\d\d-\d\dT[^|]+\|1$", Markers(full)); // trashed, serial 1
        Assert.Equal("Moved folder “Full” to the Trash.", area.BannerMessage);
        Assert.True(area.IsUndoAvailable);

        area.SelectedNode = Node(area.Roots, "Empty");
        await area.DeleteCommand.ExecuteAsync();
        Assert.Equal("Remove folder", Assert.Single(_dialogs.Confirmations).Title); // the existing Remove behavior
        Assert.Single(_dialogs.FolderConfirmations);
        Assert.Null(await _other.GetNodeStateAsync(empty));
        await Settle(area);
    });

    // ---- B18: a stale confirmation is never resubmitted ----------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task B18_AStaleCommit_WritesNothing_ShowsAFreshConfirmation_AndOnlyANewClickCommits(bool confirmAgain) => InContext(async () =>
    {
        var f = await Folder("2024");
        var a = await Project("A", f);
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "2024");
        var shownCount = 0;
        _dialogs.OnFolderLifecycle = dialog =>
        {
            if (++shownCount == 1)
            {
                _dialogs.WaitModal(_other.MoveAsync(a, null), TimeSpan.FromSeconds(10)); // the other window changes the subtree
                return true;
            }

            Assert.True(dialog.IsStale);
            Assert.Equal("The folder is empty.", dialog.CountsText); // the fresh preview shows the change
            return confirmAgain;
        };
        var before = Data();

        await area.TrashFolderCommand.ExecuteAsync();

        Assert.Equal(2, _dialogs.FolderConfirmations.Count);
        if (confirmAgain)
        {
            Assert.Equal(2, _treeStore.Calls("trashFolder")); // the second commit came from the second, explicit click
            Assert.Equal("Moved folder “2024” to the Trash.", area.BannerMessage);
            Assert.Empty(Placements(area, f));
        }
        else
        {
            Assert.Equal(1, _treeStore.Calls("trashFolder")); // never resubmitted
            Assert.Equal(before.Where(r => !r.Contains($"|Id={a}|", StringComparison.Ordinal)), Data().Where(r => !r.Contains($"|Id={a}|", StringComparison.Ordinal)));
            Assert.Equal("-|-|0", Markers(f));
            Assert.False(area.IsUndoAvailable);
            Assert.Single(Placements(area, f));
            Assert.StartsWith("Nothing was changed", area.BannerMessage);
        }

        await Settle(area);
    });

    // ---- B35, B46: loads of a forgotten subtree (C1, C2) ---------------------------------------------------------------------------

    [Theory]
    [InlineData(FolderLifecycleKind.Archive)]
    [InlineData(FolderLifecycleKind.Trash)]
    public Task B35_AChildLoadInFlightBelowTheFolderArchivedOrTrashedHere_IsDiscarded_AndRegistersNothing(FolderLifecycleKind kind) => InContext(async () =>
    {
        var f = await Folder("F");
        var d = await Folder("D", f);
        var x = await Folder("X", d);
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        var dNode = Node(fNode.Children, "D");
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(d));
        dNode.IsExpanded = true;
        var pending = dNode.ChildrenLoad!;
        load.WaitEntered();

        area.SelectedNode = fNode;
        await (kind == FolderLifecycleKind.Archive ? area.ArchiveFolderCommand : area.TrashFolderCommand).ExecuteAsync();
        Assert.Null(area.CachedNode(d));
        load.Release();

        Assert.False(await pending);
        Assert.Null(area.CachedNode(x)); // C2: the detached instance's rows are never registered
        Assert.Null(area.CachedNode(d));
        Assert.Empty(Placements(area, f));
        Assert.DoesNotContain(dNode.Children, c => !c.IsPlaceholder);
        Assert.Null(area.SelectedNode);
        await Settle(area);
    });

    [Fact]
    public Task B46_AnOldLoadCanNeverFillTheInstanceARestoreCreated() => InContext(async () =>
    {
        var f = await Folder("F");
        var d = await Folder("D", f);
        await Folder("X", d);
        var area = await Area();
        var oldF = await Expand(area, Node(area.Roots, "F"));
        var oldD = Node(oldF.Children, "D");
        var oldLoad = _treeStore.Hold(GatedWorkspaceStore.Children(d));
        oldD.IsExpanded = true; // its snapshot is taken now: X only
        var pending = oldD.ChildrenLoad!;
        oldLoad.WaitEntered();

        area.SelectedNode = oldF;
        await area.TrashFolderCommand.ExecuteAsync(); // D is forgotten
        await area.UndoCommand.ExecuteAsync(); // Restore: a NEW instance for F
        await Folder("Y", d); // newer than the old snapshot
        var newF = await Expand(area, Node(area.Roots, "F"));
        var newD = await Expand(area, Node(newF.Children, "D"));
        Assert.NotSame(oldF, newF);
        Assert.NotSame(oldD, newD);
        oldLoad.Release();

        Assert.False(await pending); // discarded
        Assert.Equal(["X", "Y"], newD.Children.Where(c => !c.IsPlaceholder).Select(c => c.Name)); // the latest read
        Assert.Same(newD, area.CachedNode(d));
        await Settle(area);
    });

    // ---- B36: a child read after another window's archive or trash (C3) ------------------------------------------------------------

    [Theory]
    [InlineData(FolderLifecycleKind.Archive, "This item's folder is archived.")]
    [InlineData(FolderLifecycleKind.Trash, "This item's folder is in the Trash.")]
    public Task B36_AChildReadBelowAFolderMarkedElsewhere_RemovesTheOutermostCachedBranch_AndAppliesNoRow(FolderLifecycleKind kind, string notice) => InContext(async () =>
    {
        var f = await Folder("F");
        var d = await Folder("D", f);
        var x = await Folder("X", d);
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        var dNode = Node(fNode.Children, "D"); // cached, never expanded
        await OtherMarks(f, kind);

        dNode.IsExpanded = true;
        Assert.False(await area.EnsureChildrenLoadedAsync(dNode));

        Assert.Empty(Placements(area, f)); // the OUTERMOST cached node of the chain (F) left with its branch
        Assert.Null(area.CachedNode(f));
        Assert.Null(area.CachedNode(x)); // no row applied
        Assert.Equal(notice, area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task C4_ReconciliationDecidesByTheEffectiveState_ADetailsReadBelowAnArchivedFolderRemovesTheBranch() => InContext(async () =>
    {
        var f = await Folder("F");
        var d = await Folder("D", f);
        await Project("P", d);
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        var dNode = await Expand(area, Node(fNode.Children, "D"));
        await OtherMarks(f, FolderLifecycleKind.Archive);

        area.SelectedNode = Node(dNode.Children, "P"); // the details read is fresh: the Project's effective state is Archived
        await area.CurrentDetailsLoad;

        Assert.Empty(Placements(area, f));
        Assert.Null(area.SelectedNode);
        Assert.Equal("This item's folder is archived.", area.BannerMessage);
        await Settle(area);
    });

    // ---- B37, B38: content load and selection ownership at application time (C5, C8) ---------------------------------------------

    [Fact]
    public Task B37_B38_ASelectionMadeInsideDuringTheCommit_IsCleared_ItsContentLoadAppliesNothing_InfoCloses() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("N", "x"));
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        var pNode = Node(fNode.Children, "P");
        area.SelectedNode = fNode;
        var commit = _treeStore.Hold("archiveFolder");
        var operation = area.ArchiveFolderCommand.ExecuteAsync();
        commit.WaitEntered();

        var list = _contentStore.Hold(GatedProjectContentStore.List(null));
        area.SelectedNode = pNode; // the user selects INSIDE F meanwhile
        var view = area.SelectedContent!;
        var load = view.CurrentLoad;
        list.WaitEntered();
        area.IsInfoOpen = true;
        commit.Release();
        await operation;

        Assert.Null(area.SelectedNode); // C8: that row is gone
        Assert.Null(area.SelectedContent);
        Assert.True(view.IsDeactivated);
        list.Release();
        await load;
        Assert.Empty(view.Items); // C5: the in-flight content load applied nothing
        await Settle(area);
        Assert.False(area.IsInfoOpen);
    });

    [Fact]
    public Task B37_B38_ASelectionMadeOutsideDuringTheCommit_KeepsItsContentViewAndInfo() => InContext(async () =>
    {
        var f = await Folder("F");
        var q = await Project("Q");
        await _otherContent.CreateNoteAsync(q, null, new ProjectNoteDraft("N", "x"));
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "F");
        var commit = _treeStore.Hold("archiveFolder");
        var operation = area.ArchiveFolderCommand.ExecuteAsync();
        commit.WaitEntered();

        var qNode = Node(area.Roots, "Q");
        area.SelectedNode = qNode; // OUTSIDE F
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        area.IsInfoOpen = true;
        commit.Release();
        await operation;

        Assert.Same(qNode, area.SelectedNode);
        Assert.Same(view, area.SelectedContent);
        Assert.False(view.IsDeactivated);
        Assert.Single(view.Items);
        Assert.True(area.IsInfoOpen);
        Assert.Empty(Placements(area, f));
        Assert.True(area.IsUndoAvailable);
        await Settle(area);
        Assert.True(area.IsInfoOpen);
    });

    // ---- B39: search reveals (C6) -------------------------------------------------------------------------------------------------

    [Fact]
    public Task B39a_AFolderArchiveHere_SupersedesARevealInFlight_TheSelectionStaysAsTheArchiveLeftIt() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "F");
        var resolve = SearchStore.Hold("resolve");
        var reveal = area.RevealAsync(new SearchItemRef(SearchResultKind.Project, p));
        resolve.WaitEntered();

        await area.ArchiveFolderCommand.ExecuteAsync(); // a write command: the reveal is invalidated
        resolve.Release();

        Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal);
        Assert.Null(area.SelectedNode); // C1 cleared F; the reveal did not select P
        Assert.Empty(Placements(area, p));
        await Settle(area);
    });

    [Fact]
    public Task B39b_ARevealBelowAFolderArchivedElsewhere_OpensArchivedWithTheRootSelected_FromAReadThatStartedAfterTheResolve() => InContext(async () =>
    {
        var f = await Folder("F");
        var inner = await Folder("Inner", f);
        var p = await Project("P", inner);
        await OtherMarks(f, FolderLifecycleKind.Archive);
        var area = await Area();
        var list = _treeStore.Hold("listArchived");

        var reveal = area.RevealAsync(new SearchItemRef(SearchResultKind.Project, p));
        list.WaitEntered(); // the panel read starts only after the resolve answered
        Assert.True(area.IsArchivedOpen);
        list.Release();

        Assert.Equal(WorkspaceRevealOutcome.InArchivedFolder, await reveal);
        Assert.Equal(f, area.Archived.SelectedEntry!.NodeId); // the OUTERMOST archived Folder, never the item
        Assert.True(area.Archived.SelectedEntry.IsFolder);
        Assert.Null(area.SelectedNode);
        await Settle(area);
    });

    [Fact]
    public Task B19_AStaleHitBelowATrashedFolder_IsRemovedWithTheFolderNotice() => InContext(async () =>
    {
        var f = await Folder("F");
        var c = await Folder("C", f);
        await Project("Findme", c);
        var area = await Area();
        var search = area.Search!;
        search.SearchText = "findme";
        await search.CurrentSearch;
        var hit = Assert.Single(search.Results);
        await OtherMarks(c, FolderLifecycleKind.Archive);
        await OtherMarks(f, FolderLifecycleKind.Trash);

        search.SelectedResult = hit;
        search.ActivateSelectedCommand.Execute(null);
        await search.CurrentActivation;

        Assert.Equal("This item's folder is in the Trash.", search.NoticeText);
        Assert.Empty(search.Results);
        await Settle(area);
    });

    // ---- B40, B41: hidden tab and Undo selection rules (C7) -------------------------------------------------------------------------

    [Fact]
    public Task B40_ACommitWhileTheTabIsHidden_StillReconcilesTheTree_ButOffersNoUndo() => InContext(async () =>
    {
        var f = await Folder("F");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "F");
        var commit = _treeStore.Hold("archiveFolder");
        var operation = area.ArchiveFolderCommand.ExecuteAsync();
        commit.WaitEntered();

        area.IsTabSelected = false;
        commit.Release();
        await operation;

        Assert.Empty(Placements(area, f));
        Assert.False(area.IsUndoAvailable);
        await area.Archived.CurrentCount;
        Assert.Equal("Archived (1)", area.Archived.ButtonText);
        area.IsTabSelected = true;
        Assert.Empty(Placements(area, f));
        Assert.False(area.IsUndoAvailable);
        await Settle(area);
    });

    [Fact]
    public Task B41_Undo_SelectsTheFolderOnlyWithoutAnInterveningSelection_AndNeverAfterTheVisitEnded() => InContext(async () =>
    {
        var f = await Folder("F");
        await Project("Q");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "F");
        await area.ArchiveFolderCommand.ExecuteAsync();
        var undo = _treeStore.Hold("undoFolder");
        var undone = area.UndoCommand.ExecuteAsync();
        undo.WaitEntered();
        var q = Node(area.Roots, "Q");
        area.SelectedNode = q; // the user selects meanwhile
        undo.Release();
        await undone;
        Assert.Single(Placements(area, f));
        Assert.Same(q, area.SelectedNode);

        area.SelectedNode = Node(area.Roots, "F");
        await area.ArchiveFolderCommand.ExecuteAsync();
        await area.UndoCommand.ExecuteAsync(); // no intervening selection: F is selected
        Assert.Equal(f, area.SelectedNode!.Id);

        await area.ArchiveFolderCommand.ExecuteAsync();
        Assert.True(area.IsUndoAvailable);
        area.IsTabSelected = false;
        area.IsTabSelected = true;
        Assert.False(area.IsUndoAvailable); // the visit ended
        await Settle(area);
    });

    // ---- B42: Restore / Unarchive placement (C9) ---------------------------------------------------------------------------------

    private static async Task UnarchiveFromPanel(WorkspaceAreaViewModel area, long id)
    {
        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        area.Archived.SelectedEntry = area.Archived.Entries.Single(e => e.NodeId == id);
        await area.Archived.UnarchiveCommand.ExecuteAsync();
    }

    [Fact]
    public Task B42a_AParentLoadedAndExpanded_GetsTheFolderOnce_CollapsedAndUnloaded() => InContext(async () =>
    {
        var parent = await Folder("Parent");
        var f = await Folder("F", parent);
        await Folder("X", f);
        var area = await Area();
        var parentNode = await Expand(area, Node(area.Roots, "Parent"));
        await Expand(area, Node(parentNode.Children, "F"));
        area.SelectedNode = Node(parentNode.Children, "F");
        await area.ArchiveFolderCommand.ExecuteAsync();
        await UnarchiveFromPanel(area, f);

        var placed = Assert.Single(Placements(area, f));
        Assert.Same(parentNode, placed.Parent);
        Assert.False(placed.IsExpanded);
        Assert.False(placed.IsChildrenLoaded);
        Assert.True(placed.Children.Single().IsPlaceholder); // HasChildren from the result; descendants load lazily
        Assert.Null(area.SelectedNode); // a panel Unarchive never selects
        await Settle(area);
    });

    [Fact]
    public Task B42b_B42c_AParentCachedButNeverExpanded_GetsOnlyAnExpander_AnUncachedParent_GetsNothing() => InContext(async () =>
    {
        var cached = await Folder("Cached");
        var f1 = await Folder("F1", cached);
        var top = await Folder("Top");
        var uncached = await Folder("Uncached", top);
        var f2 = await Folder("F2", uncached);
        await OtherMarks(f1, FolderLifecycleKind.Archive);
        await OtherMarks(f2, FolderLifecycleKind.Archive);
        var area = await Area();
        var cachedNode = Node(area.Roots, "Cached");
        Assert.Empty(cachedNode.Children); // it held only the archived F1

        await UnarchiveFromPanel(area, f1);
        Assert.True(cachedNode.Children.Single().IsPlaceholder); // SetHasChildren only
        Assert.Null(area.CachedNode(f1));

        await UnarchiveFromPanel(area, f2);
        Assert.Null(area.CachedNode(uncached));
        Assert.Null(area.CachedNode(f2)); // it appears with that folder's next load
        await Settle(area);
    });

    [Fact]
    public Task B42d_AParentWhoseChildLoadIsInFlight_GetsTheFolderExactlyOnce() => InContext(async () =>
    {
        var parent = await Folder("Parent");
        var f = await Folder("F", parent);
        await Project("Sibling", parent);
        await OtherMarks(f, FolderLifecycleKind.Archive);
        var area = await Area();
        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        area.Archived.SelectedEntry = area.Archived.Entries.Single();
        var parentNode = Node(area.Roots, "Parent");
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(parent));
        parentNode.IsExpanded = true; // snapshot now: without F
        load.WaitEntered();

        var unarchive = area.Archived.UnarchiveCommand.ExecuteAsync(); // waits for the parent's load first
        Assert.False(unarchive.IsCompleted);
        load.Release();
        await unarchive;

        Assert.Single(Placements(area, f));
        Assert.Equal(["F", "Sibling"], parentNode.Children.Select(c => c.Name));
        await Settle(area);
    });

    [Fact]
    public Task B42e_AParentArchivedElsewhereRightAfterTheCommit_TheNextReadRemovesTheBranch() => InContext(async () =>
    {
        var parent = await Folder("Parent");
        var f = await Folder("F", parent);
        await Folder("X", f);
        var area = await Area();
        var parentNode = await Expand(area, Node(area.Roots, "Parent"));
        area.SelectedNode = Node(parentNode.Children, "F");
        await area.ArchiveFolderCommand.ExecuteAsync();
        await UnarchiveFromPanel(area, f);
        var placed = Assert.Single(Placements(area, f));
        await OtherMarks(parent, FolderLifecycleKind.Archive);

        placed.IsExpanded = true;
        Assert.False(await area.EnsureChildrenLoadedAsync(placed)); // ParentNotLive (root: Parent)

        Assert.Empty(Placements(area, parent));
        Assert.Null(area.CachedNode(f));
        Assert.Null(area.SelectedNode);
        await Settle(area);
    });

    // ---- B43: panel reads out of order (C10) ---------------------------------------------------------------------------------------

    [Fact]
    public Task B43_AListAndACountThatPredateAFolderCompletion_AreDiscardedAndReadAgain() => InContext(async () =>
    {
        var f1 = await Folder("F1");
        var f2 = await Folder("F2");
        var area = await Area();
        var listGate = _treeStore.Hold("listTrash");
        var countGate = _treeStore.Hold("countTrash");
        var open = area.OpenTrashCommand.ExecuteAsync(); // the first list read: its snapshot is an empty Trash
        listGate.WaitEntered();
        var count = area.Trash.RefreshCount(); // the first count read: 0
        countGate.WaitEntered();

        await OtherMarks(f1, FolderLifecycleKind.Trash);
        await OtherMarks(f2, FolderLifecycleKind.Trash);
        await area.ReconcileNodeAsync(f1); // this window applies a Folder lifecycle result (C4 → C1): a completion
        listGate.Release();
        countGate.Release();
        await open;
        await area.Trash.CurrentLoad;
        await count;
        await area.Trash.CurrentCount;

        Assert.True(area.IsTrashOpen);
        Assert.Equal(["F2", "F1"], area.Trash.Entries.Select(e => e.Name)); // the latest read only (newest first; ties by Id)
        Assert.All(area.Trash.Entries, e => Assert.True(e.IsFolder));
        Assert.Equal("Trash (2)", area.Trash.ButtonText);
        await Settle(area);
    });

    [Fact]
    public Task B43_AnOpenArchivedPanel_ReadsAgainAfterACompletion_AndShowsOnlyTheLatestRead() => InContext(async () =>
    {
        await Folder("F1");
        await Folder("F2");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "F1");
        await area.ArchiveFolderCommand.ExecuteAsync();
        var listGate = _treeStore.Hold("listArchived");
        var open = area.OpenArchivedCommand.ExecuteAsync(); // snapshot now: F1
        listGate.WaitEntered();

        var f2 = (await _other.ListChildrenAsync(null)).Nodes.Single(n => n.Name == "F2").Id;
        await OtherMarks(f2, FolderLifecycleKind.Archive);
        await area.ReconcileNodeAsync(f2); // this window learns it by a fresh read: a completion (Touch)
        listGate.Release();
        await open;
        await area.Archived.CurrentLoad;

        Assert.Equal(["F1", "F2"], area.Archived.Entries.Select(e => e.Name));
        Assert.Equal("Archived (2)", area.Archived.ButtonText);
        await Settle(area);
    });

    // ---- B44: refusals and failures (C11) ------------------------------------------------------------------------------------------

    private async Task<(WorkspaceAreaViewModel Area, WorkspaceNodeViewModel Q, ProjectContentViewModel View)> AreaWithUnrelatedSelection()
    {
        var q = await Project("Q");
        await _otherContent.CreateNoteAsync(q, null, new ProjectNoteDraft("N", "x"));
        var area = await Area();
        var qNode = Node(area.Roots, "Q");
        area.SelectedNode = qNode;
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        return (area, qNode, view);
    }

    private static void AssertUntouched(WorkspaceAreaViewModel area, WorkspaceNodeViewModel q, ProjectContentViewModel view)
    {
        Assert.Same(q, area.SelectedNode);
        Assert.Same(view, area.SelectedContent);
        Assert.False(view.IsDeactivated);
        Assert.False(area.IsUndoAvailable);
    }

    public static TheoryData<string> PanelRefusals() => ["NameConflict", "ParentArchived", "ParentTrashed", "NotTrashed", "StoreException"];

    [Theory]
    [MemberData(nameof(PanelRefusals))]
    public Task B44_APanelRestoreRefusalOrFailure_WritesNothing_OffersNoUndo_AndLeavesTheUnrelatedSelection(string refusal) => InContext(async () =>
    {
        var holder = await Folder("Holder");
        var f = await Folder("F", holder);
        await OtherMarks(f, FolderLifecycleKind.Trash);
        var (area, q, view) = await AreaWithUnrelatedSelection();
        await area.OpenTrashCommand.ExecuteAsync();
        await area.Trash.CurrentLoad;
        area.Trash.SelectedEntry = area.Trash.Entries.Single(e => e.NodeId == f);
        StoreGate? failing = null;
        switch (refusal)
        {
            case "NameConflict":
                await _other.CreateFolderAsync(holder, "F");
                break;
            case "ParentArchived":
                await OtherMarks(holder, FolderLifecycleKind.Archive);
                break;
            case "ParentTrashed":
                await OtherMarks(holder, FolderLifecycleKind.Trash);
                break;
            case "NotTrashed":
                await _other.ClearFolderAsync(f, FolderLifecycleKind.Trash);
                break;
            default:
                failing = _treeStore.Hold("restoreFolder");
                break;
        }

        var before = Data();
        var restore = area.Trash.RestoreCommand.ExecuteAsync();
        failing?.WaitEntered();
        failing?.Fail(new IOException("injected"));
        await restore;

        Assert.Equal(before, Data());
        Assert.Equal(refusal switch
        {
            "NameConflict" => "The folder it came from already contains “F”. Rename or move that item first; nothing was restored.",
            "ParentArchived" => "“F” is inside an archived folder. Unarchive that folder first; nothing was changed.",
            "ParentTrashed" => "The folder that holds “F” is in the Trash. Restore that folder first; nothing was changed.",
            "NotTrashed" => "“F” is no longer in the Trash (it may have been restored in another window).",
            _ => "Could not restore “F”. It is still in the Trash.",
        }, area.Trash.Notice);
        area.CloseTrashCommand.Execute(null);
        AssertUntouched(area, q, view);
        await Settle(area);
    });

    [Fact]
    public Task B44_AnAncestorArchivedElsewhere_RefusesBeforeAnyDialog_AndReconcilesOnlyThatBranch() => InContext(async () =>
    {
        var top = await Folder("Top");
        var f = await Folder("F", top);
        await Project("Other");
        var area = await Area();
        var topNode = await Expand(area, Node(area.Roots, "Top"));
        area.SelectedNode = Node(topNode.Children, "F");
        await OtherMarks(top, FolderLifecycleKind.Archive);
        var before = Data();

        await area.TrashFolderCommand.ExecuteAsync();

        Assert.Empty(_dialogs.FolderConfirmations);
        Assert.Equal(before, Data());
        Assert.Empty(Placements(area, top)); // C4 on the node the outcome reported not live: the outermost cached branch
        Assert.Single(Placements(area, (await _other.ListChildrenAsync(null)).Nodes.Single(n => n.Name == "Other").Id));
        Assert.False(area.IsUndoAvailable);
        Assert.Equal("A folder above this folder is archived or in the Trash. Nothing was changed.", area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task B44_AnInjectedCommitFailure_WritesNothing_AndKeepsTheTree() => InContext(async () =>
    {
        var f = await Folder("F");
        var area = await Area();
        var fNode = Node(area.Roots, "F");
        area.SelectedNode = fNode;
        var commit = _treeStore.Hold("archiveFolder");
        var before = Data();
        var operation = area.ArchiveFolderCommand.ExecuteAsync();
        commit.WaitEntered();
        commit.Fail(new IOException("injected"));
        await operation;

        Assert.Equal(before, Data());
        Assert.Same(fNode, area.SelectedNode);
        Assert.Single(Placements(area, f));
        Assert.False(area.IsUndoAvailable);
        Assert.Equal("Could not archive “F”. Nothing was changed.", area.BannerMessage);
        await Settle(area);
    });

    // ---- B6: cross-window rows (view-model side) ------------------------------------------------------------------------------------

    [Fact]
    public Task B6_AMoveOnAStaleTreeBelowAFolderArchivedElsewhere_IsRefused_AndTheBranchIsReconciledById() => InContext(async () =>
    {
        var f = await Folder("2024");
        var p = await Project("P", f);
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "2024"));
        area.SelectedNode = Node(fNode.Children, "P");
        await OtherMarks(f, FolderLifecycleKind.Archive);
        _dialogs.OnWorkspaceMove = editor =>
        {
            editor.SelectedDestination = editor.Destinations.Single(d => d.FolderId is null);
            _dialogs.WaitModal(editor.MoveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10));
            Assert.Equal(WorkspaceFailure.AncestorNotLive, editor.Refusal);
            return false;
        };
        var before = Data();

        await area.MoveCommand.ExecuteAsync();

        Assert.Equal(before, Data());
        Assert.Empty(Placements(area, f));
        Assert.Empty(Placements(area, p));
        Assert.Null(area.SelectedNode);
        await Settle(area);
    });

    // ---- Review finding 1: a stale effective-state result never mutates an ancestor that changed after the read started -----------

    /// <summary>Opens the panel of <paramref name="kind"/> and clears F there (Unarchive / Restore): a committed result that touches F.</summary>
    private static async Task ClearFromPanel(WorkspaceAreaViewModel area, long f, FolderLifecycleKind kind)
    {
        if (kind == FolderLifecycleKind.Archive)
        {
            await UnarchiveFromPanel(area, f);
            area.CloseArchivedCommand.Execute(null);
            return;
        }

        await area.OpenTrashCommand.ExecuteAsync();
        await area.Trash.CurrentLoad;
        area.Trash.SelectedEntry = area.Trash.Entries.Single(e => e.NodeId == f);
        await area.Trash.RestoreCommand.ExecuteAsync();
        area.CloseTrashCommand.Execute(null);
    }

    private static readonly string[] StaleNotices =
        ["This item's folder is archived.", "This item's folder is in the Trash.", "This folder is archived.", "This folder is in the Trash."];

    [Theory]
    [InlineData(FolderLifecycleKind.Archive)]
    [InlineData(FolderLifecycleKind.Trash)]
    public Task Finding1_AStaleDescendantStateRead_NeverRemovesTheAncestorClearedMeanwhile(FolderLifecycleKind kind) => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        await OtherMarks(f, kind);
        var state = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(p); // its snapshot: P is archived/trashed through F
        state.WaitEntered();

        await ClearFromPanel(area, f, kind); // F is live again here: the committed result touches F
        state.Release();
        await reconcile;

        Assert.Same(fNode, Assert.Single(Placements(area, f)));
        Assert.Single(Placements(area, p));
        Assert.DoesNotContain(area.BannerMessage, StaleNotices);
        await Settle(area);
    });

    [Theory]
    [InlineData(FolderLifecycleKind.Archive)]
    [InlineData(FolderLifecycleKind.Trash)]
    public Task Finding1_AStaleDetailsRead_NeverRemovesTheAncestorClearedMeanwhile(FolderLifecycleKind kind) => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        await OtherMarks(f, kind);
        var list = _contentStore.Hold(GatedProjectContentStore.List(null)); // the content listing would reconcile P itself: held meanwhile
        var details = _treeStore.Hold($"details:{p}");
        area.SelectedNode = Node(fNode.Children, "P");
        details.WaitEntered();

        await ClearFromPanel(area, f, kind);
        details.Release();
        await area.CurrentDetailsLoad;

        await area.CurrentDetailsLoad; // the stale result was not applied: the details were read again (now live)

        Assert.Same(fNode, Assert.Single(Placements(area, f)));
        Assert.Equal(p, area.SelectedNode?.Id); // the selection stays
        Assert.Equal(p, area.SelectedProject?.NodeId);
        Assert.DoesNotContain(area.BannerMessage, StaleNotices);
        list.Release(); // the content listing is live now: it applies and reconciles nothing
        await Settle(area);
        Assert.Same(fNode, Assert.Single(Placements(area, f)));
    });

    [Theory]
    [InlineData(FolderLifecycleKind.Archive)]
    [InlineData(FolderLifecycleKind.Trash)]
    public Task Finding1_AStaleChildRead_NeverRemovesTheAncestorClearedMeanwhile(FolderLifecycleKind kind) => InContext(async () =>
    {
        var f = await Folder("F");
        var d = await Folder("D", f);
        await Project("X", d);
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        var dNode = Node(fNode.Children, "D");
        await OtherMarks(f, kind);
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(d));
        dNode.IsExpanded = true; // its snapshot: ParentNotLive through F
        var pending = dNode.ChildrenLoad!;
        load.WaitEntered();

        await ClearFromPanel(area, f, kind);
        load.Release();
        await pending;

        Assert.Same(fNode, Assert.Single(Placements(area, f)));
        Assert.Single(Placements(area, d));
        Assert.DoesNotContain(area.BannerMessage, StaleNotices);
        await Settle(area);
    });

    // ---- Review finding 2: the effective state, not the Project's own markers, decides removal and the notice ----------------------

    public static TheoryData<string, string, string, string, bool> OwnStateCases() => new()
    {
        // case, Project's own change, then the folder's change, expected notice, whole F branch removed
        { "archived project below a trashed folder", "archive", "trash", "This item's folder is in the Trash.", true },
        { "trashed project below a trashed folder", "trash", "trash", "This item's folder is in the Trash.", true },
        { "archived project below an archived folder", "archive", "archive", "This item's folder is archived.", true },
        { "archived project alone (control)", "archive", "none", "This project is archived.", false },
        { "trashed project alone (control)", "trash", "none", "This project is in the Trash.", false },
    };

    private async Task OwnState(long p, long f, string project, string folder)
    {
        if (project == "archive")
            Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(p)).Outcome);
        else
            Assert.Equal(ProjectTrashOutcome.Trashed, (await _other.TrashProjectAsync(p)).Outcome);
        if (folder != "none")
            await OtherMarks(f, folder == "archive" ? FolderLifecycleKind.Archive : FolderLifecycleKind.Trash);
    }

    [Theory]
    [MemberData(nameof(OwnStateCases))]
    public Task Finding2_TheDetailsRead_RemovesAndExplainsByTheEffectiveRoot(string label, string project, string folder, string notice, bool branch) => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        await Project("Sibling", f);
        var other = await Folder("Other");
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        await OwnState(p, f, project, folder);
        _contentStore.Hold(GatedProjectContentStore.List(null)); // no competing content-side reconciliation

        area.SelectedNode = Node(fNode.Children, "P");
        await area.CurrentDetailsLoad;

        Assert.True(notice == area.BannerMessage, $"{label}: {area.BannerMessage}");
        Assert.Empty(Placements(area, p));
        Assert.Equal(branch, Placements(area, f).Count == 0); // the outermost cached branch of the effective root
        Assert.Single(Placements(area, other)); // unrelated branches stay
        Assert.Null(area.SelectedNode);
        await Settle(area);
    });

    [Theory]
    [MemberData(nameof(OwnStateCases))]
    public Task Finding2_EditProject_RemovesAndExplainsByTheEffectiveRoot(string label, string project, string folder, string notice, bool branch) => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        await Project("Sibling", f);
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        await OwnState(p, f, project, folder);
        _contentStore.Hold(GatedProjectContentStore.List(null));
        _treeStore.Hold($"details:{p}"); // the selection's own details read stays pending: Edit reads the Project itself

        area.SelectedNode = Node(fNode.Children, "P");
        await area.EditProjectCommand.ExecuteAsync();

        Assert.True(notice == area.BannerMessage, $"{label}: {area.BannerMessage}");
        Assert.Empty(Placements(area, p));
        Assert.Equal(branch, Placements(area, f).Count == 0);
        Assert.Null(area.SelectedNode);
        await Settle(area);
    });

    [Fact]
    public Task Finding2_AnUnrelatedSelectionWithItsContentAndInfo_IsUntouchedByTheEffectiveRemoval() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var q = await Project("Q");
        await _otherContent.CreateNoteAsync(q, null, new ProjectNoteDraft("N", "x"));
        var area = await Area();
        await Expand(area, Node(area.Roots, "F"));
        var qNode = Node(area.Roots, "Q");
        await OwnState(p, f, "archive", "trash");

        await area.ReconcileNodeAsync(p); // another read path learns P's effective state while Q is selected
        area.SelectedNode = qNode;
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        area.IsInfoOpen = true;
        await area.ReconcileNodeAsync(p);

        Assert.Empty(Placements(area, f));
        Assert.Same(qNode, area.SelectedNode);
        Assert.Same(view, area.SelectedContent);
        Assert.False(view.IsDeactivated);
        Assert.True(area.IsInfoOpen);
        await Settle(area);
        Assert.True(area.IsInfoOpen);
    });

    // ---- B8, B45: open note editors (C12) -------------------------------------------------------------------------------------------

    [Fact]
    public Task B45_AnEditorBelowAFolderTrashedElsewhere_KeepsItsDraft_CopyDraftStays_SaveSucceedsAfterRestore() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var note = (await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Kickoff", "v1"))).Note!.Id;
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        area.SelectedNode = Node(fNode.Children, "P");
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        view.SelectedItem = view.Items.Single();
        var checkedInside = false;
        _dialogs.OnNoteEditor = editor =>
        {
            editor.Content = "my draft";
            _dialogs.WaitModal(OtherMarks(f, FolderLifecycleKind.Trash), TimeSpan.FromSeconds(10));
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10));
            Assert.True(editor.IsProjectTrashed); // the existing trashed path: refused, nothing written
            Assert.Equal("my draft", editor.Content);
            Assert.True(editor.CopyDraftCommand.CanExecute(null));
            Assert.False(editor.SaveAsNewCommand.CanExecute(null));

            _dialogs.WaitModal(_other.ClearFolderAsync(f, FolderLifecycleKind.Trash), TimeSpan.FromSeconds(10));
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10)); // after Restore, Save succeeds again
            Assert.Null(editor.GeneralError);
            checkedInside = true;
            editor.RequestClose();
            return editor.SavedAny;
        };

        await view.OpenCommand.ExecuteAsync();
        await Settle(area);

        Assert.True(checkedInside);
        Assert.Equal("my draft", (await _otherContent.GetNoteAsync(note))!.Content);
    });

    [Fact]
    public Task B8_AnEditorBelowAFolderArchivedElsewhere_KeepsSaving() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var note = (await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Kickoff", "v1"))).Note!.Id;
        var area = await Area();
        var fNode = await Expand(area, Node(area.Roots, "F"));
        area.SelectedNode = Node(fNode.Children, "P");
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        view.SelectedItem = view.Items.Single();
        _dialogs.OnNoteEditor = editor =>
        {
            editor.Content = "archived draft";
            _dialogs.WaitModal(OtherMarks(f, FolderLifecycleKind.Archive), TimeSpan.FromSeconds(10));
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10));
            Assert.Null(editor.GeneralError);
            Assert.False(editor.IsProjectTrashed);
            editor.RequestClose();
            return editor.SavedAny;
        };

        await view.OpenCommand.ExecuteAsync();
        await Settle(area);

        Assert.Equal("archived draft", (await _otherContent.GetNoteAsync(note))!.Content);
    });
}
