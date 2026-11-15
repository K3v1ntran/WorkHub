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
/// Phase 19 Bulk Archive in the Workspace area (proposal Revision 2 + R2a–R2d, §4 and §5): the checklist, the location-bound
/// batch (MovedElsewhere re-reads the Project; only LocationNotLive and an exception stop), Bulk Undo (only the batch's events,
/// never selects, continues after refusals), and D10 — conflicting reads (rules L and C, the bounded confirmation read and
/// R2d's row source), cases B22–B22h. A gated store holds reads (snapshot when called, delivered on release) and writes;
/// a second service on the same database plays the other window. Every interleaving is driven by gates, never by sleeps.
/// <para>Every body runs on a <see cref="SingleThreadContext"/>; teardown follows <see cref="ContextTeardown"/>.</para>
/// </summary>
public sealed class BulkArchiveViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedWorkspaceStore _treeStore;
    private readonly WorkspaceService _other;
    private readonly List<WorkspaceAreaViewModel> _areas = [];
    private readonly List<GatedSearchStore> _searchStores = [];
    private readonly GatedProjectContentStore _contentStore;
    private readonly ProjectContentService _otherContent;
    private readonly SingleThreadContext _context = new();

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ModalTimeout = TimeSpan.FromSeconds(10);

    public BulkArchiveViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _treeStore = new GatedWorkspaceStore(new SqliteWorkspaceStore(_connections));
        _other = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _contentStore = new GatedProjectContentStore(new SqliteProjectContentStore(_connections));
        _otherContent = new ProjectContentService(new SqliteProjectContentStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)),
            new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
    }

    public void Dispose() =>
        ContextTeardown.Run(_context, ReleaseGates, _areas.Select((area, i) => (ContextTeardown.TrackedSet)(() => Tracked(area, $"area{i}."))).ToList(),
            _root.Dispose, _root.Paths.Root, TeardownBound);

    private void ReleaseGates()
    {
        _treeStore.ReleaseAll();
        _contentStore.ReleaseAll();
        foreach (var store in _searchStores)
            store.ReleaseAll();
    }

    private Task InContext(Func<Task> body) => _context.RunAsync(body, BodyTimeout);

    private static Task Settle(WorkspaceAreaViewModel area) => TrackedTasks.SettleAsync(() => Tracked(area, ""));

    private static IReadOnlyList<(string Name, Task Task)> Tracked(WorkspaceAreaViewModel area, string prefix)
    {
        var tasks = new List<(string Name, Task Task)>
        {
            ("CurrentDetailsLoad", area.CurrentDetailsLoad), ("CurrentLifecycleOperation", area.CurrentLifecycleOperation),
            ("CurrentReconciliation", area.CurrentReconciliation), ("CurrentConfirmation", area.CurrentConfirmation),
            ("Trash.CurrentCount", area.Trash.CurrentCount), ("Archived.CurrentCount", area.Archived.CurrentCount),
        };
        if (area.Search is { } search)
            tasks.AddRange([("Search.CurrentSearch", search.CurrentSearch), ("Search.CurrentActivation", search.CurrentActivation)]);
        if (area.SelectedContent is { } content)
            tasks.AddRange([("Content.CurrentLoad", content.CurrentLoad), ("Content.CurrentPreviewLoad", content.CurrentPreviewLoad),
                ("Content.CurrentTrashOperation", content.CurrentTrashOperation), ("Content.CurrentReconciliation", content.CurrentReconciliation),
                ("Content.CurrentCountReconciliation", content.CurrentCountReconciliation)]);
        return tasks.Select(t => (prefix + t.Name, t.Task)).ToList();
    }

    /// <summary>B5/B6: an area with Project content (note editor) and Workspace search, on the same gated tree store.</summary>
    private async Task<WorkspaceAreaViewModel> FullArea()
    {
        var searchStore = new GatedSearchStore(new SqliteWorkspaceSearchStore(_connections, WorkspaceSearchStoreOptions.Default));
        _searchStores.Add(searchStore);
        var content = new ProjectContentService(_contentStore, new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance, content, new ScriptedLinkChecker(),
            new WorkspaceSearchService(searchStore), searchDelayMs: 0);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        return area;
    }

    private async Task<WorkspaceAreaViewModel> Area()
    {
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        return area;
    }

    private async Task<long> Folder(string name, long? parent = null) => (await _other.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null, ProjectStatus status = ProjectStatus.Completed) =>
        (await _other.CreateProjectAsync(parent, new ProjectDraft(name, status))).Value!.Id;

    private async Task MarkFolder(long folder, FolderLifecycleKind kind) =>
        Assert.Equal(FolderLifecycleOutcome.Committed,
            (await _other.MarkFolderAsync(folder, kind, (await _other.PrepareFolderLifecycleAsync(folder, kind)).Confirmation!.Token)).Outcome);

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) => nodes.Single(n => !n.IsPlaceholder && n.Name == name);

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

    /// <summary>Exactly one row of <paramref name="id"/>, under <paramref name="parentId"/> (null = top level).</summary>
    private static WorkspaceNodeViewModel OnceUnder(WorkspaceAreaViewModel area, long id, long? parentId)
    {
        var row = Assert.Single(Placements(area, id));
        Assert.Equal(parentId, row.Parent?.Id);
        return row;
    }

    private (string Status, long Serial, long? Parent, string Name) Row(long nodeId)
    {
        using var connection = _root.Open();
        var text = Db.ScalarString(connection,
            "SELECT p.Status || '|' || p.ArchiveSerial || '|' || ifnull(n.ParentId, '') || '|' || n.Name FROM Projects AS p JOIN WorkspaceNodes AS n ON n.Id = p.NodeId WHERE p.NodeId = $id;",
            ("$id", nodeId))!.Split('|');
        return (text[0], long.Parse(text[1], System.Globalization.CultureInfo.InvariantCulture),
            text[2].Length == 0 ? null : long.Parse(text[2], System.Globalization.CultureInfo.InvariantCulture), text[3]);
    }

    /// <summary>Details lines with this outcome (a MovedElsewhere line also carries "(when refused: …)").</summary>
    private static int Count(WorkspaceAreaViewModel area, string outcome) =>
        area.LastBulkArchiveDetails!.Count(l => l.EndsWith(": " + outcome, StringComparison.Ordinal) || l.Contains(": " + outcome + " (when refused: ", StringComparison.Ordinal));

    /// <summary>F (top level, loaded and selected) with P1–P5, all Completed unless stated; X a live top-level folder.</summary>
    private async Task<(WorkspaceAreaViewModel Area, long F, long X, long[] P)> World(int projects = 3, bool expandX = true)
    {
        var f = await Folder("F");
        var x = await Folder("X");
        var p = new long[projects];
        for (var i = 0; i < projects; i++)
            p[i] = await Project($"P{i + 1}", f);
        var area = await Area();
        await Expand(area, Node(area.Roots, "F"));
        if (expandX)
            await Expand(area, Node(area.Roots, "X"));
        area.SelectedNode = Node(area.Roots, "F");
        _dialogs.OnBulkArchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            return true;
        };
        return (area, f, x, p);
    }

    // ---- Checklist (pure view model) -----------------------------------------------------------------------------------

    [Fact]
    public void Checklist_PreticksCompleted_CountsLive_AndBindsTargetsToThePlace()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = new BulkArchiveViewModel(7, "Clients › 2025",
            [new(1, "A", ProjectStatus.Completed, "o", now), new(2, "B", ProjectStatus.Active, null, now), new(3, "C", ProjectStatus.Completed, null, now)], truncated: true);

        Assert.Equal("in “Clients › 2025”", vm.PlaceText);
        Assert.Equal(2, vm.CheckedCount);
        Assert.Equal("Archive 2 projects", vm.ArchiveText);
        Assert.NotNull(vm.TruncatedText);
        Assert.Equal([new BulkArchiveTarget(1, 7), new BulkArchiveTarget(3, 7)], vm.ConfirmedTargets);
        vm.SelectNoneCommand.Execute(null);
        Assert.False(vm.ArchiveCommand.CanExecute(null));
        vm.Toggle(vm.Rows[1]);
        Assert.Equal("Archive 1 project", vm.ArchiveText);
        Assert.Equal([new BulkArchiveTarget(2, 7)], vm.ConfirmedTargets);
        Assert.Equal("at the top level", new BulkArchiveViewModel(null, null, [], false).PlaceText);
    }

    // ---- Success, cancel, empty ---------------------------------------------------------------------------------------

    [Fact]
    public Task B1_PreticksCompleted_ArchivesInListOrder_LeavesTheTree() => InContext(async () =>
    {
        var f = await Folder("F");
        var p1 = await Project("P1", f);
        var p2 = await Project("P2", f);
        var p3 = await Project("P3", f, ProjectStatus.Active);
        var area = await Area();
        await Expand(area, Node(area.Roots, "F"));
        area.SelectedNode = Node(area.Roots, "F");
        _dialogs.OnBulkArchive = vm => true; // the preselection as shown

        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();

        var dialog = Assert.Single(_dialogs.BulkArchiveDialogs);
        Assert.Equal(["P1", "P2", "P3"], dialog.Rows.Select(r => r.Name).ToList());
        Assert.Equal([new BulkArchiveTarget(p1, f), new BulkArchiveTarget(p2, f)], dialog.ConfirmedTargets);
        Assert.Equal(2, _treeStore.Calls("archiveInPlace"));
        Assert.Empty(Placements(area, p1));
        Assert.Empty(Placements(area, p2));
        Assert.Single(Placements(area, p3));
        Assert.Equal("Archived 2 projects.", area.BannerMessage);
        Assert.True(area.IsUndoAvailable);
        Assert.Equal(2, area.BulkUndoEvents.Count);
        Assert.Equal(("Archived", 1L), (Row(p1).Status, Row(p1).Serial));
        await area.Archived.CurrentCount;
        Assert.Equal("Archived (2)", area.Archived.ButtonText);
        await Settle(area);
    });

    [Fact]
    public Task B7_Cancel_WritesNothing() => InContext(async () =>
    {
        var (area, _, _, p) = await World();
        _dialogs.OnBulkArchive = _ => false;

        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();

        Assert.Single(_dialogs.BulkArchiveDialogs);
        Assert.Equal(0, _treeStore.Calls("archiveInPlace"));
        Assert.All(p, id => Assert.Equal(0L, Row(id).Serial));
        await Settle(area);
    });

    [Fact]
    public Task B8_NoLiveProject_ShowsNoDialog() => InContext(async () =>
    {
        var f = await Folder("F");
        var archived = await Project("A", f);
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(archived)).Outcome);
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "F");

        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();

        Assert.Empty(_dialogs.BulkArchiveDialogs);
        Assert.Equal("No projects to archive here.", area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task B4_TopLevel_ListsTopLevelProjectsOnly_WithNullExpectedParent() => InContext(async () =>
    {
        await Folder("F");
        var t1 = await Project("T1");
        var t2 = await Project("T2");
        var area = await Area();
        _dialogs.OnBulkArchive = _ => true;

        await area.ArchiveTopLevelProjectsCommand.ExecuteAsync();

        Assert.Equal([new BulkArchiveTarget(t1, null), new BulkArchiveTarget(t2, null)], Assert.Single(_dialogs.BulkArchiveDialogs).ConfirmedTargets);
        Assert.Empty(Placements(area, t1));
        Assert.Empty(Placements(area, t2));
        await Settle(area);
    });

    [Fact]
    public Task B15_WhileAnotherLifecycleWriteIsPending_NoDialogAndNoCall() => InContext(async () =>
    {
        var (area, _, _, _) = await World();
        var single = await Project("S");
        var area2 = await Area(); // a fresh tree that shows S at the top level
        area2.SelectedNode = Node(area2.Roots, "S");
        var gate = _treeStore.Hold("archiveProject");
        var pending = area2.ArchiveProjectCommand.ExecuteAsync();
        gate.WaitEntered();
        area2.SelectedNode = Node(area2.Roots, "F");

        Assert.False(area2.ArchiveProjectsInFolderCommand.CanExecute(null));
        await area2.ArchiveProjectsInFolderCommand.ExecuteAsync();
        Assert.Empty(_dialogs.BulkArchiveDialogs);
        Assert.Equal(0, _treeStore.Calls("bulkCandidates"));

        gate.Release();
        await pending;
        Assert.Equal(("Archived", 1L), (Row(single).Status, Row(single).Serial));
        await Settle(area);
        await Settle(area2);
    });

    // ---- Location binding (R2-1, R2a) ---------------------------------------------------------------------------------

    [Fact]
    public Task B17a_MovedWhileTheDialogIsOpen_DestinationLoaded_ShowsOnceUnderTheNewFolder() => InContext(async () =>
    {
        var (area, f, x, p) = await World();
        _dialogs.OnBulkArchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            _dialogs.WaitModal(_other.MoveAsync(p[1], x), ModalTimeout); // the other window, after the dialog read
            return true;
        };

        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();

        Assert.Equal(3, _treeStore.Calls("archiveInPlace"));
        Assert.Equal(1, _treeStore.Calls("nodeState")); // the Project's own re-read; never the origin
        Assert.Equal(0, area.ConfirmationsStarted);
        OnceUnder(area, p[1], x);
        Assert.Equal(1, Count(area, "moved elsewhere"));
        Assert.Equal(2, Count(area, "archived"));
        Assert.Equal([p[0], p[2]], area.BulkUndoEvents.Select(e => e.NodeId).ToList());
        Assert.Equal(("Completed", 0L, (long?)x), (Row(p[1]).Status, Row(p[1]).Serial, Row(p[1]).Parent));
        Assert.Equal(("Archived", 1L), (Row(p[0]).Status, Row(p[0]).Serial));
        Assert.Contains(area.Roots, n => n.Id == f);
        await Settle(area);
    });

    [Fact]
    public Task B17b_MovedWhileTheDialogIsOpen_DestinationNotLoaded_RemovesTheRow_ThenShowsItOnceOnExpand() => InContext(async () =>
    {
        var (area, _, x, p) = await World(expandX: false);
        _dialogs.OnBulkArchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            _dialogs.WaitModal(_other.MoveAsync(p[1], x), ModalTimeout);
            return true;
        };

        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();

        Assert.Empty(Placements(area, p[1]));
        Assert.Null(area.CachedNode(p[1]));
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        Assert.Equal(0, _treeStore.Calls(GatedWorkspaceStore.Children(x)));
        await Expand(area, Node(area.Roots, "X"));
        Assert.Equal(1, _treeStore.Calls(GatedWorkspaceStore.Children(x)));
        OnceUnder(area, p[1], x);
        Assert.Equal(0L, Row(p[1]).Serial);
        Assert.DoesNotContain(area.BulkUndoEvents, e => e.NodeId == p[1]);
        await Settle(area);
    });

    [Fact]
    public Task B18_MovedBetweenTwoWrites_TheNextTargetIsStillArchived() => InContext(async () =>
    {
        var (area, _, x, p) = await World();
        var gate = _treeStore.HoldCommitted("archiveInPlace"); // P1 commits now; its result is delivered on release
        var batch = area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        gate.WaitEntered();
        Assert.True((await _other.MoveAsync(p[1], x)).Value);
        gate.Release();
        await batch;

        Assert.Equal(3, _treeStore.Calls("archiveInPlace"));
        OnceUnder(area, p[1], x);
        Assert.Equal(("Archived", 1L), (Row(p[2]).Status, Row(p[2]).Serial));
        Assert.Equal(("Completed", 0L, (long?)x), (Row(p[1]).Status, Row(p[1]).Serial, Row(p[1]).Parent));
        await Settle(area);
    });

    [Fact]
    public Task B19_TopLevel_MovedIntoAFolder_IsMovedElsewhere_AndPlacedOnce() => InContext(async () =>
    {
        var f = await Folder("F");
        var t1 = await Project("T1");
        var t2 = await Project("T2");
        var t3 = await Project("T3");
        var area = await Area();
        await Expand(area, Node(area.Roots, "F"));
        _dialogs.OnBulkArchive = vm =>
        {
            _dialogs.WaitModal(_other.MoveAsync(t2, f), ModalTimeout);
            return true;
        };

        await area.ArchiveTopLevelProjectsCommand.ExecuteAsync();

        OnceUnder(area, t2, f);
        Assert.DoesNotContain(area.Roots, n => n.Id == t2);
        Assert.Equal(1, Count(area, "moved elsewhere"));
        Assert.Equal(0, Count(area, "not archived: its folder is archived or in the Trash"));
        Assert.Equal(("Archived", 1L), (Row(t1).Status, Row(t1).Serial));
        Assert.Equal(("Archived", 1L), (Row(t3).Status, Row(t3).Serial));
        await Settle(area);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task B20_MovedIntoABranchThatIsThenArchived_IsRemovedAsNotLive_AndTheBatchContinues(bool xLoaded) => InContext(async () =>
    {
        var (area, f, x, p) = await World(expandX: xLoaded);
        var gate = _treeStore.HoldCommitted("archiveInPlace");
        var batch = area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        gate.WaitEntered();
        Assert.True((await _other.MoveAsync(p[1], x)).Value);
        await MarkFolder(x, FolderLifecycleKind.Archive);
        gate.Release();
        await batch;

        Assert.Empty(Placements(area, p[1])); // not under F, and not inserted under a hidden branch
        Assert.Null(area.CachedNode(p[1]));
        if (xLoaded)
            Assert.DoesNotContain(area.Roots, n => n.Id == x); // B20a: X is the outermost cached node of the chain
        Assert.Equal(1, _treeStore.Calls("nodeState")); // P2 only; the origin F is never re-read for MovedElsewhere
        Assert.Equal(3, _treeStore.Calls("archiveInPlace"));
        Assert.Equal(0, Count(area, "not attempted"));
        Assert.Equal([p[0], p[2]], area.BulkUndoEvents.Select(e => e.NodeId).ToList());
        Assert.StartsWith("Archived 2 of 3 projects.", area.BannerMessage);
        Assert.Contains(area.Roots, n => n.Id == f);
        await Settle(area);
    });

    // ---- Confirmed place marked: stop (R2-2) --------------------------------------------------------------------------

    [Theory]
    [InlineData(FolderLifecycleKind.Archive)]
    [InlineData(FolderLifecycleKind.Trash)]
    public Task B11_B11b_OriginMarkedMidBatch_StopsAndCountsNotAttempted_AndReconcilesTheOriginOnce(FolderLifecycleKind kind) => InContext(async () =>
    {
        var (area, f, _, p) = await World(projects: 5);
        var gate = _treeStore.HoldCommitted("archiveInPlace");
        var batch = area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        gate.WaitEntered();
        await MarkFolder(f, kind);
        gate.Release();
        await batch;

        Assert.Equal(2, _treeStore.Calls("archiveInPlace"));
        Assert.Equal(1, _treeStore.Calls("nodeState")); // exactly one reconcile of F
        Assert.Equal(3, Count(area, "not attempted"));
        Assert.Contains("3 projects were not attempted.", area.BannerMessage);
        Assert.Contains(kind == FolderLifecycleKind.Archive ? "was archived elsewhere" : "was moved to the Trash elsewhere", area.BannerMessage);
        Assert.Equal([p[0]], area.BulkUndoEvents.Select(e => e.NodeId).ToList());
        Assert.DoesNotContain(area.Roots, n => n.Id == f);
        Assert.Equal(("Archived", 1L), (Row(p[0]).Status, Row(p[0]).Serial));
        Assert.All(p.Skip(1), id => Assert.Equal(("Completed", 0L), (Row(id).Status, Row(id).Serial)));
        await Settle(area);
    });

    [Fact]
    public Task B11c_AncestorOfTheOriginArchived_SaysAFolderAbove() => InContext(async () =>
    {
        var g = await Folder("G");
        var f = await Folder("F", g);
        var p1 = await Project("P1", f);
        await Project("P2", f);
        var area = await Area();
        var gNode = Node(area.Roots, "G");
        await Expand(area, gNode);
        area.SelectedNode = Node(gNode.Children, "F");
        _dialogs.OnBulkArchive = vm => true;
        var gate = _treeStore.HoldCommitted("archiveInPlace");
        var batch = area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        gate.WaitEntered();
        await MarkFolder(g, FolderLifecycleKind.Archive);
        gate.Release();
        await batch;

        Assert.Contains("A folder above", area.BannerMessage);
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        Assert.Equal([p1], area.BulkUndoEvents.Select(e => e.NodeId).ToList());
        await Settle(area);
    });

    [Fact]
    public Task B11d_OriginMarkedBeforeTheFirstWrite_StopsAtOnce_NoUndo() => InContext(async () =>
    {
        var (area, f, _, p) = await World();
        _dialogs.OnBulkArchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            _dialogs.WaitModal(MarkFolder(f, FolderLifecycleKind.Archive), ModalTimeout);
            return true;
        };

        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();

        Assert.Equal(1, _treeStore.Calls("archiveInPlace"));
        Assert.Equal(2, Count(area, "not attempted"));
        Assert.False(area.IsUndoAvailable);
        Assert.All(p, id => Assert.Equal(0L, Row(id).Serial));
        await Settle(area);
    });

    [Fact]
    public Task B9_B11e_PerProjectRefusals_DoNotStopTheBatch() => InContext(async () =>
    {
        var (area, f, _, p) = await World();
        _dialogs.OnBulkArchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            _dialogs.WaitModal(_other.TrashProjectAsync(p[1]), ModalTimeout); // F stays live
            return true;
        };

        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();

        Assert.Equal(3, _treeStore.Calls("archiveInPlace"));
        Assert.Equal(0, _treeStore.Calls("nodeState")); // no reconcile of F for a per-Project refusal
        Assert.Equal(1, Count(area, "in the Trash"));
        Assert.Equal(("Archived", 1L), (Row(p[2]).Status, Row(p[2]).Serial));
        Assert.Contains(area.Roots, n => n.Id == f);
        await Settle(area);
    });

    [Fact]
    public Task B12_ExceptionOnTheSecondTarget_Stops_KeepsTheFirst_CountsNotAttempted() => InContext(async () =>
    {
        var (area, _, _, p) = await World(projects: 4);
        var gate = _treeStore.HoldCommitted("archiveInPlace");
        var batch = area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        gate.WaitEntered();
        _treeStore.ScriptArchiveInPlace = _ => Task.FromException<BulkArchiveResult>(new InvalidOperationException("disk"));
        gate.Release();
        await batch;

        Assert.Equal(2, _treeStore.Calls("archiveInPlace"));
        Assert.Equal(2, Count(area, "not attempted"));
        Assert.Contains("Could not archive “P2”. 2 projects were not attempted. Nothing else was changed.", area.BannerMessage);
        Assert.Equal([p[0]], area.BulkUndoEvents.Select(e => e.NodeId).ToList());
        Assert.All(p.Skip(1), id => Assert.Equal(0L, Row(id).Serial));
        await Settle(area);
    });

    [Fact]
    public Task B14_UndoIsNotOfferedAfterTheVisitEnded() => InContext(async () =>
    {
        var (area, _, _, _) = await World();
        var gate = _treeStore.HoldCommitted("archiveInPlace");
        var batch = area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        gate.WaitEntered();
        area.IsTabSelected = false;
        gate.Release();
        await batch;

        Assert.False(area.IsUndoAvailable);
        await Settle(area);
    });

    // ---- Bulk Undo (R2-3) -----------------------------------------------------------------------------------------------

    [Fact]
    public Task U1_BulkUndo_RestoresEachEventOnce_AndNeverSelects() => InContext(async () =>
    {
        var (area, f, _, p) = await World();
        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        Assert.Equal(f, area.SelectedNode!.Id);

        await area.UndoCommand.ExecuteAsync();

        Assert.Equal(3, _treeStore.Calls("undoArchive"));
        Assert.Equal(f, area.SelectedNode!.Id);
        Assert.All(p, id => OnceUnder(area, id, f));
        Assert.All(p, id => Assert.Equal(("Completed", 1L), (Row(id).Status, Row(id).Serial)));
        Assert.Equal("Restored 3 projects.", area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task U2_RefusalInTheMiddle_ContinuesWithTheRest() => InContext(async () =>
    {
        var (area, f, _, p) = await World();
        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        var gate = _treeStore.HoldCommitted("undoArchive"); // e1 commits now
        var undo = area.UndoCommand.ExecuteAsync();
        gate.WaitEntered();
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(p[1], ProjectStatus.Completed)).Outcome);
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(p[1])).Outcome); // serial 2
        gate.Release();
        await undo;

        Assert.Equal(3, _treeStore.Calls("undoArchive"));
        Assert.Equal(("Completed", 1L), (Row(p[0]).Status, Row(p[0]).Serial));
        Assert.Equal(("Archived", 2L), (Row(p[1]).Status, Row(p[1]).Serial));
        Assert.Equal(("Completed", 1L), (Row(p[2]).Status, Row(p[2]).Serial));
        Assert.Equal("Restored 2 of 3 projects. Not restored: 1 archived again elsewhere.", area.BannerMessage);
        Assert.Equal(f, area.SelectedNode!.Id);
        await Settle(area);
    });

    [Fact]
    public Task U3_ExceptionAtTheSecondEvent_KeepsTheFirstRestored_ReportsFailedAndNotAttempted() => InContext(async () =>
    {
        var (area, _, _, p) = await World();
        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        var first = _treeStore.HoldCommitted("undoArchive");
        var undo = area.UndoCommand.ExecuteAsync();
        first.WaitEntered();
        var second = _treeStore.Hold("undoArchive"); // a held write runs on release; failing it means it never ran
        first.Release();
        second.WaitEntered();
        second.Fail(new InvalidOperationException("busy"));
        await undo;

        Assert.Equal(2, _treeStore.Calls("undoArchive"));
        Assert.Equal(("Completed", 1L), (Row(p[0]).Status, Row(p[0]).Serial));
        Assert.Equal(("Archived", 1L), (Row(p[1]).Status, Row(p[1]).Serial));
        Assert.Equal(("Archived", 1L), (Row(p[2]).Status, Row(p[2]).Serial));
        Assert.Equal("Restored 1 of 3 projects. Could not restore “P2”. 1 project was not attempted. They are still archived.", area.BannerMessage);
        Assert.Contains("“P3”: not attempted; still archived", area.LastBulkArchiveDetails!);
        await Settle(area);
    });

    [Fact]
    public Task U4_SelectionChangedWhileUndoIsPending_IsKept() => InContext(async () =>
    {
        var (area, _, x, p) = await World();
        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        var gate = _treeStore.Hold("undoArchive");
        var undo = area.UndoCommand.ExecuteAsync();
        gate.WaitEntered();
        area.SelectedNode = Node(area.Roots, "X");
        var changes = 0;
        area.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceAreaViewModel.SelectedNode))
                changes++;
        };
        gate.Release();
        await undo;

        Assert.Equal(x, area.SelectedNode!.Id);
        Assert.Equal(0, changes); // no restored Project was ever selected
        Assert.Equal(3, _treeStore.Calls("undoArchive"));
        Assert.All(p, id => Assert.Single(Placements(area, id)));
        await Settle(area);
    });

    [Fact]
    public Task U5_OnlyTheBatchsOwnEvents_AreUndone() => InContext(async () =>
    {
        var (area, _, _, p) = await World();
        _dialogs.OnBulkArchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            _dialogs.WaitModal(_other.ArchiveProjectAsync(p[1]), ModalTimeout); // archived by the other window: AlreadyArchived
            return true;
        };
        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        Assert.Equal([p[0], p[2]], area.BulkUndoEvents.Select(e => e.NodeId).ToList());

        await area.UndoCommand.ExecuteAsync();

        Assert.Equal(2, _treeStore.Calls("undoArchive"));
        Assert.Equal(("Archived", 1L), (Row(p[1]).Status, Row(p[1]).Serial));
        await Settle(area);
    });

    // ---- D10: conflicting reads (B22–B22h) -----------------------------------------------------------------------------

    /// <summary>F (loaded) holds P2 (cached); X and Z are top-level folders; in the database P2 has moved to X.</summary>
    private async Task<(WorkspaceAreaViewModel Area, long F, long X, long Z, long P2)> MovedWorld(bool xLoaded, bool zLoaded, string name = "P2")
    {
        var f = await Folder("F");
        var x = await Folder("X");
        var z = await Folder("Z");
        var p2 = await Project(name, f);
        var area = await Area();
        await Expand(area, Node(area.Roots, "F"));
        if (xLoaded)
            await Expand(area, Node(area.Roots, "X"));
        if (zLoaded)
            await Expand(area, Node(area.Roots, "Z"));
        Assert.True((await _other.MoveAsync(p2, x)).Value);
        return (area, f, x, z, p2);
    }

    [Fact]
    public Task B22_TwoReconcilesOverlap_TheOlderTriggersOneConfirmation() => InContext(async () =>
    {
        var (area, f, x, z, p2) = await MovedWorld(xLoaded: true, zLoaded: true);
        var older = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(p2);
        older.WaitEntered(); // snapshot: P2 in X
        Assert.True((await _other.MoveAsync(p2, z)).Value);
        await area.ReconcileNodeAsync(p2); // ungated, newer: reads Z and places P2 there (read-update stamp)
        OnceUnder(area, p2, z);
        var confirm = _treeStore.Hold("nodeState");
        older.Release();
        confirm.WaitEntered(); // rule C: the older result disagreed with the display
        confirm.Release();
        await reconcile;

        OnceUnder(area, p2, z);
        Assert.Equal(3, _treeStore.Calls("nodeState"));
        Assert.Equal(1, area.ConfirmationsStarted);
        Assert.Equal(z, Row(p2).Parent);
        Assert.DoesNotContain(Node(area.Roots, "X").Children, n => n.Id == p2);
        Assert.DoesNotContain(Node(area.Roots, "F").Children, n => n.Id == p2);
        _ = f;
        _ = x;
        await Settle(area);
    });

    [Fact]
    public Task B22b_ReconcileReadsTheOldPlace_LazyLoadReadsTheNewPlaceAndAppliesFirst() => InContext(async () =>
    {
        var (area, _, _, z, p2) = await MovedWorld(xLoaded: false, zLoaded: false);
        var held = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(p2);
        held.WaitEntered(); // snapshot: P2 in X
        Assert.True((await _other.MoveAsync(p2, z)).Value);
        await Expand(area, Node(area.Roots, "Z")); // reads [P2] and moves the cached P2 from F to Z (read-update stamp)
        OnceUnder(area, p2, z);
        var confirm = _treeStore.Hold("nodeState");
        held.Release();
        confirm.WaitEntered();
        confirm.Release();
        await reconcile;

        OnceUnder(area, p2, z);
        Assert.Equal(1, _treeStore.Calls(GatedWorkspaceStore.Children(z)));
        Assert.Equal(2, _treeStore.Calls("nodeState")); // reconcile + one confirmation
        Assert.Equal(1, area.ConfirmationsStarted);
        Assert.Equal(z, Row(p2).Parent);
        await Settle(area);
    });

    [Fact]
    public Task B22c_LazyLoadReadsTheOldPlace_ReconcileReadsTheNewPlace_LoadReleasedFirst() => InContext(async () =>
    {
        var (area, f, x, z, p2) = await MovedWorld(xLoaded: false, zLoaded: true);
        var zLoadsBefore = _treeStore.Calls(GatedWorkspaceStore.Children(z));
        var loadGate = _treeStore.Hold(GatedWorkspaceStore.Children(x));
        var xNode = Node(area.Roots, "X");
        xNode.IsExpanded = true;
        var load = area.EnsureChildrenLoadedAsync(xNode);
        loadGate.WaitEntered(); // snapshot: [P2 in X]
        Assert.True((await _other.MoveAsync(p2, z)).Value);
        var reconcileGate = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(p2);
        reconcileGate.WaitEntered(); // snapshot: P2 in Z

        loadGate.Release();
        Assert.True(await load);
        OnceUnder(area, p2, x); // intermediate display from the older load (allowed)
        var confirm = _treeStore.Hold("nodeState");
        reconcileGate.Release(); // rule C, not rule L: the load's stamp is a read-update stamp
        confirm.WaitEntered();
        confirm.Release();
        await reconcile;

        OnceUnder(area, p2, z);
        Assert.DoesNotContain(Node(area.Roots, "F").Children, n => n.Id == p2);
        Assert.DoesNotContain(xNode.Children, n => n.Id == p2);
        Assert.Equal(1, _treeStore.Calls(GatedWorkspaceStore.Children(x)));
        Assert.Equal(zLoadsBefore, _treeStore.Calls(GatedWorkspaceStore.Children(z)));
        Assert.Equal(2, _treeStore.Calls("nodeState"));
        Assert.Equal(1, area.ConfirmationsStarted);
        Assert.Equal(z, Row(p2).Parent);
        _ = f;
        await Settle(area);
    });

    [Fact]
    public Task B22d_OlderReconcileAppliedFirst_NewerLazyLoadArrivesLater() => InContext(async () =>
    {
        var (area, _, x, z, p2) = await MovedWorld(xLoaded: true, zLoaded: false);
        var held = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(p2);
        held.WaitEntered(); // snapshot: P2 in X
        Assert.True((await _other.MoveAsync(p2, z)).Value);
        var loadGate = _treeStore.Hold(GatedWorkspaceStore.Children(z));
        var zNode = Node(area.Roots, "Z");
        zNode.IsExpanded = true;
        var load = area.EnsureChildrenLoadedAsync(zNode);
        loadGate.WaitEntered(); // snapshot: [P2]
        held.Release();
        await reconcile;
        OnceUnder(area, p2, x); // the older reconcile applied (read-update stamp, no Touch)

        var confirm = _treeStore.Hold("nodeState");
        loadGate.Release(); // rule C on the load side: the P2 row is held back
        confirm.WaitEntered();
        confirm.Release();
        Assert.True(await load);
        await area.CurrentConfirmation;

        OnceUnder(area, p2, z);
        Assert.Equal(2, _treeStore.Calls("nodeState"));
        Assert.Equal(1, _treeStore.Calls(GatedWorkspaceStore.Children(z)));
        await Settle(area);
    });

    [Fact]
    public Task B22e_ConfirmationFails_NothingAppliedNoRetry_LaterReconcileCorrects() => InContext(async () =>
    {
        var (area, _, x, z, p2) = await MovedWorld(xLoaded: false, zLoaded: true);
        var loadGate = _treeStore.Hold(GatedWorkspaceStore.Children(x));
        var xNode = Node(area.Roots, "X");
        xNode.IsExpanded = true;
        var load = area.EnsureChildrenLoadedAsync(xNode);
        loadGate.WaitEntered();
        Assert.True((await _other.MoveAsync(p2, z)).Value);
        var reconcileGate = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(p2);
        reconcileGate.WaitEntered();
        loadGate.Release();
        Assert.True(await load);
        var confirm = _treeStore.Hold("nodeState");
        reconcileGate.Release();
        confirm.WaitEntered();
        confirm.Fail(new InvalidOperationException("busy"));
        await reconcile;

        OnceUnder(area, p2, x); // nothing applied
        Assert.Equal(2, _treeStore.Calls("nodeState"));
        Assert.Equal(1, area.ConfirmationsStarted);

        await area.ReconcileNodeAsync(p2); // an ordinary read later, with no overlap
        OnceUnder(area, p2, z);
        Assert.Equal(3, _treeStore.Calls("nodeState"));
        Assert.Equal(1, area.ConfirmationsStarted);
        await Settle(area);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task B22f_B22fr_ArchivedReadRemovesFirst_LazyLoadSeesActiveUnderZ_ConfirmationRecreatesFromItsOwnNode(bool rename) => InContext(async () =>
    {
        var f = await Folder("F");
        var z = await Folder("Z");
        var p2 = await Project("Alpha", f);
        var area = await Area();
        await Expand(area, Node(area.Roots, "F"));
        OnceUnder(area, p2, f);

        // (1) Archived elsewhere; the reconcile reads Archived and is held.
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(p2)).Outcome);
        var reconcileGate = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(p2);
        reconcileGate.WaitEntered();
        // (2) Unarchived and moved to Z; the lazy load of Z reads [P2 active under Z, "Alpha"] and is held.
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(p2, ProjectStatus.Completed)).Outcome);
        Assert.True((await _other.MoveAsync(p2, z)).Value);
        var loadGate = _treeStore.Hold(GatedWorkspaceStore.Children(z));
        var zNode = Node(area.Roots, "Z");
        zNode.IsExpanded = true;
        var load = area.EnsureChildrenLoadedAsync(zNode);
        loadGate.WaitEntered();
        // (3, B22f-r) Renamed in Z, committed before any confirmation starts.
        if (rename)
            Assert.True((await _other.RenameAsync(p2, "Beta")).Succeeded);

        var sequenceBefore = area.CurrentSequence;
        reconcileGate.Release();
        await reconcile; // read-class removal: the row leaves F and the instance is forgotten
        Assert.Empty(Placements(area, p2));
        Assert.Null(area.CachedNode(p2));
        Assert.True(area.HasReadUpdate(p2)); // the stamp outlives the forgotten instance (the load is still in flight)
        Assert.False(area.IsCommittedSince(p2, sequenceBefore)); // no Touch

        var confirm = _treeStore.Hold("nodeState");
        loadGate.Release(); // rule L does not discard the row; rule C holds it back and starts the confirmation
        confirm.WaitEntered();
        confirm.Release();
        Assert.True(await load);
        await area.CurrentConfirmation;

        var row = OnceUnder(area, p2, z);
        Assert.Equal(rename ? "Beta" : "Alpha", row.Name);
        Assert.DoesNotContain(Node(area.Roots, "F").Children, n => n.Id == p2);
        Assert.Equal(1, _treeStore.Calls(GatedWorkspaceStore.Children(z)));
        Assert.Equal(1, _treeStore.Calls(GatedWorkspaceStore.Children(f)));
        Assert.Equal(2, _treeStore.Calls("nodeState")); // reconcile + one confirmation
        Assert.Equal(1, area.ConfirmationsStarted);
        Assert.Equal(("Completed", 1L, (long?)z, rename ? "Beta" : "Alpha"), Row(p2));
        await Settle(area);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public Task B22h_NodeMissingOrInconsistent_CreatesNoRow_NoRetry_TreeReloadShowsIt(int variant) => InContext(async () =>
    {
        var f = await Folder("F");
        var z = await Folder("Z");
        var p2 = await Project("P2", f);
        var area = await Area();
        await Expand(area, Node(area.Roots, "F"));
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(p2)).Outcome);
        var reconcileGate = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(p2);
        reconcileGate.WaitEntered();
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(p2, ProjectStatus.Completed)).Outcome);
        Assert.True((await _other.MoveAsync(p2, z)).Value);
        var loadGate = _treeStore.Hold(GatedWorkspaceStore.Children(z));
        var zNode = Node(area.Roots, "Z");
        zNode.IsExpanded = true;
        var load = area.EnsureChildrenLoadedAsync(zNode);
        loadGate.WaitEntered();
        reconcileGate.Release();
        await reconcile;

        var scripted = new WorkspaceNodeState(p2, WorkspaceNodeType.Project, z, false)
        {
            Node = variant switch
            {
                1 => null,
                2 => new WorkspaceNode(p2, f, WorkspaceNodeType.Project, "P2", false),
                _ => new WorkspaceNode(p2, z, WorkspaceNodeType.Folder, "P2", false),
            },
        };
        _treeStore.ScriptNodeState[p2] = () => Task.FromResult<WorkspaceNodeState?>(scripted);
        var confirm = _treeStore.Hold("nodeState");
        loadGate.Release();
        confirm.WaitEntered();
        confirm.Release();
        Assert.True(await load);
        await area.CurrentConfirmation;

        Assert.Empty(Placements(area, p2)); // nothing built from the held load row or any other data
        Assert.Null(area.CachedNode(p2));
        Assert.Equal(2, _treeStore.Calls("nodeState"));
        Assert.Equal(1, area.ConfirmationsStarted);

        await area.ReconcileNodeAsync(p2); // Z is loaded: an ordinary reconcile does not re-create the row
        Assert.Empty(Placements(area, p2));
        Assert.Equal(1, area.ConfirmationsStarted);

        var reloaded = await Area(); // a tree reload (next start / another window) shows it once under Z
        await Expand(reloaded, Node(reloaded.Roots, "Z"));
        OnceUnder(reloaded, p2, z);
        await Settle(area);
        await Settle(reloaded);
    });

    [Fact]
    public Task B22g_NotFoundReadAndALoadOverlap_TheNodeStaysRemoved() => InContext(async () =>
    {
        var f = await Folder("F");
        var y = await Folder("Y");
        var k = await Folder("K", f);
        var area = await Area();
        await Expand(area, Node(area.Roots, "F"));
        Assert.True((await _other.MoveAsync(k, y)).Value);
        var loadGate = _treeStore.Hold(GatedWorkspaceStore.Children(y));
        var yNode = Node(area.Roots, "Y");
        yNode.IsExpanded = true;
        var load = area.EnsureChildrenLoadedAsync(yNode);
        loadGate.WaitEntered(); // snapshot: [K]
        Assert.Equal(FolderRemoveOutcome.Removed, (await _other.RemoveEmptyFolderAsync(k)).Outcome);
        var reconcileGate = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(k);
        reconcileGate.WaitEntered(); // snapshot: missing
        reconcileGate.Release();
        await reconcile;
        Assert.Empty(Placements(area, k));

        var confirm = _treeStore.Hold("nodeState");
        loadGate.Release();
        confirm.WaitEntered();
        confirm.Release();
        Assert.True(await load);
        await area.CurrentConfirmation;

        Assert.Empty(Placements(area, k));
        Assert.Equal(2, _treeStore.Calls("nodeState"));
        Assert.Equal(1, _treeStore.Calls(GatedWorkspaceStore.Children(y)));
        Assert.Equal(1, area.ConfirmationsStarted);
        await Settle(area);
    });

    // ---- R8a: rule L after a committed write is unchanged ----------------------------------------------------------------

    [Fact]
    public Task R8a_OlderReconcile_IsDiscardedAfterThisWindowsCommittedArchive_WithoutConfirmation() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var area = await Area();
        var fNode = Node(area.Roots, "F");
        await Expand(area, fNode);
        var held = _treeStore.Hold("nodeState");
        var reconcile = area.ReconcileNodeAsync(p);
        held.WaitEntered(); // snapshot: P live in F
        area.SelectedNode = Node(fNode.Children, "P");
        await area.ArchiveProjectCommand.ExecuteAsync(); // committed: Touch (rule L)
        held.Release();
        await reconcile;

        Assert.Empty(Placements(area, p));
        Assert.Equal(0, area.ConfirmationsStarted);
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        await Settle(area);
    });

    [Fact]
    public Task R8a_OlderLazyLoad_SkipsANodeThisWindowArchived_WithoutConfirmation() => InContext(async () =>
    {
        var f = await Folder("F");
        var x = await Folder("X");
        var p = await Project("P", f);
        var area = await Area();
        var fNode = Node(area.Roots, "F");
        await Expand(area, fNode);
        Assert.True((await _other.MoveAsync(p, x)).Value);
        var loadGate = _treeStore.Hold(GatedWorkspaceStore.Children(x));
        var xNode = Node(area.Roots, "X");
        xNode.IsExpanded = true;
        var load = area.EnsureChildrenLoadedAsync(xNode);
        loadGate.WaitEntered(); // snapshot: [P]
        area.SelectedNode = Node(fNode.Children, "P");
        await area.ArchiveProjectCommand.ExecuteAsync(); // the single Archive acts by id: committed, Touch
        loadGate.Release();
        Assert.True(await load);

        Assert.Empty(Placements(area, p));
        Assert.Equal(0, area.ConfirmationsStarted);
        Assert.Equal(0, _treeStore.Calls("nodeState"));
        await Settle(area);
    });

    // ---- Review round 1, fix 1: rule L ends a running confirmation, queued request included -----------------------------------

    [Fact]
    public Task RuleL_ThisWindowsCommittedArchive_DiscardsTheRunningConfirmation_AndDropsItsQueuedRequest() => InContext(async () =>
    {
        var (area, _, x, z, p2) = await MovedWorld(xLoaded: true, zLoaded: true); // database: P2 in X; display: P2 in F

        // (1) B22's conflict: an older reconcile (snapshot X) and a newer one (Z) → one confirmation, held AFTER its read.
        var older = _treeStore.Hold("nodeState");
        var olderReconcile = area.ReconcileNodeAsync(p2);
        older.WaitEntered();
        Assert.True((await _other.MoveAsync(p2, z)).Value);
        await area.ReconcileNodeAsync(p2);
        OnceUnder(area, p2, z);
        var confirm = _treeStore.Hold("nodeState");
        older.Release();
        confirm.WaitEntered(); // the confirmation has read (snapshot: P2 in Z); its result is held
        Assert.Equal(1, area.ConfirmationsStarted);
        Assert.False(area.IsConfirmationQueued(p2));

        // (2) A second rule-C conflict for the same node while it runs: a held reconcile (snapshot Z), a newer ungated one (X),
        // then the held result disagrees with the display → a request is queued behind the running confirmation.
        var third = _treeStore.Hold("nodeState");
        var thirdReconcile = area.ReconcileNodeAsync(p2);
        third.WaitEntered();
        Assert.True((await _other.MoveAsync(p2, x)).Value);
        await area.ReconcileNodeAsync(p2);
        var row = OnceUnder(area, p2, x);
        third.Release();
        Assert.True(_context.PumpUntil(() => area.IsConfirmationQueued(p2), ModalTimeout), "the second conflict was never queued");
        Assert.Equal(1, area.ConfirmationsStarted); // queued, not started

        // (3) This window's own Archive commits (Touch) while the old confirmation is still held.
        area.SelectedNode = row;
        await area.ArchiveProjectCommand.ExecuteAsync();
        Assert.Equal(("Archived", 1L, (long?)x), (Row(p2).Status, Row(p2).Serial, Row(p2).Parent));
        Assert.Empty(Placements(area, p2));
        var nodeStateCalls = _treeStore.Calls("nodeState");
        Assert.Equal(5, nodeStateCalls); // older, newer, confirmation, third, fourth

        confirm.Release();
        await olderReconcile;
        await thirdReconcile;
        await Settle(area);

        Assert.Empty(Placements(area, p2)); // the committed state; the held result (P2 active in Z) was not applied
        Assert.Equal(nodeStateCalls, _treeStore.Calls("nodeState")); // no read for the queued request
        Assert.Equal(1, area.ConfirmationsStarted);
        Assert.Equal(1, area.ConfirmationsSuperseded);
        Assert.False(area.IsConfirmationQueued(p2));
        Assert.Equal(("Archived", 1L, (long?)x), (Row(p2).Status, Row(p2).Serial, Row(p2).Parent));
    });

    // ---- Review round 1, fix 2: MovedElsewhere Details record where the refusal saw it -------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task B17c_MovedElsewhere_DetailsShowTheRefusalsParent_TheTreeFollowsTheReconcile(bool firstToTopLevel) => InContext(async () =>
    {
        var f = await Folder("F");
        var x = await Folder("X");
        var z = await Folder("Z");
        var p = new[] { await Project("P1", f), await Project("P2", f), await Project("P3", f) };
        var area = await Area();
        await Expand(area, Node(area.Roots, "F"));
        await Expand(area, Node(area.Roots, "X"));
        await Expand(area, Node(area.Roots, "Z"));
        area.SelectedNode = Node(area.Roots, "F");
        long? seenParent = firstToTopLevel ? null : x;
        _dialogs.OnBulkArchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            _dialogs.WaitModal(_other.MoveAsync(p[1], seenParent), ModalTimeout); // the other window, after the dialog read
            return true;
        };

        var first = _treeStore.Hold("archiveInPlace"); // P1's write
        var batch = area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        first.WaitEntered();
        var second = _treeStore.HoldCommitted("archiveInPlace"); // P2's refusal is decided when called; delivered on release
        first.Release();
        second.WaitEntered();
        Assert.True((await _other.MoveAsync(p[1], z)).Value); // moved again AFTER the store refused, before the reconcile reads
        second.Release();
        await batch;

        var expected = firstToTopLevel
            ? "“P2”: moved elsewhere (when refused: at the top level)"
            : $"“P2”: moved elsewhere (when refused: in folder #{x} “X”)";
        Assert.Contains(expected, area.LastBulkArchiveDetails!);
        OnceUnder(area, p[1], z); // the tree follows ReconcileNodeAsync(projectId), not CurrentParentId
        Assert.Equal(("Completed", 0L, (long?)z), (Row(p[1]).Status, Row(p[1]).Serial, Row(p[1]).Parent));
        Assert.Equal(1, _treeStore.Calls("nodeState")); // the reconcile only; Details made no store call
        Assert.Equal(3, _treeStore.Calls("archiveInPlace")); // the batch continued
        Assert.Equal(("Archived", 1L), (Row(p[2]).Status, Row(p[2]).Serial));
        Assert.Equal([p[0], p[2]], area.BulkUndoEvents.Select(e => e.NodeId).ToList()); // no event for the moved Project
        Assert.Equal(1, Count(area, "moved elsewhere"));
        Assert.Equal(2, Count(area, "archived"));
        await Settle(area);
    });

    // ---- B5, B6, B16 ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public Task B5_ANoteEditorOpenBeforeTheBatch_StaysUsable_AndItsManualSaveSucceeds() => InContext(async () =>
    {
        var p = await Project("Alpha");
        var note = (await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Kickoff", "v1"))).Note!;
        Assert.Equal(1L, note.Revision);
        var area = await FullArea();
        area.SelectedNode = Node(area.Roots, "Alpha");
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        view.SelectedItem = view.Items.Single();
        _dialogs.OnBulkArchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            return true;
        };
        var checkedInside = false;
        _dialogs.OnNoteEditor = editor =>
        {
            editor.Content = "edited across the batch";
            _dialogs.WaitModal(area.ArchiveTopLevelProjectsCommand.ExecuteAsync(), ModalTimeout); // the editor is already open
            Assert.Equal(("Archived", 1L), (Row(p).Status, Row(p).Serial));
            Assert.Equal(NoteEditorState.Editing, editor.State);
            Assert.False(editor.IsReadOnly);
            Assert.Equal("edited across the batch", editor.Content);
            Assert.True(editor.SaveCommand.CanExecute(null));
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), ModalTimeout); // manual Save
            Assert.Null(editor.GeneralError);
            checkedInside = true;
            editor.RequestClose();
            return editor.SavedAny;
        };

        await view.OpenCommand.ExecuteAsync();
        await Settle(area);

        Assert.True(checkedInside);
        Assert.Equal(1, _treeStore.Calls("archiveInPlace"));
        Assert.Empty(Placements(area, p));
        var saved = (await _otherContent.GetNoteAsync(note.Id))!;
        Assert.Equal(("Kickoff", "edited across the batch", 2L), (saved.Title, saved.Content, saved.Revision));
    });

    [Fact]
    public Task B6_SearchHitsShown_AreMarkedArchived_AndTheProjectIsStillFoundAfterwards() => InContext(async () =>
    {
        var p = await Project("Alpha");
        await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Alpha note", "x"));
        var area = await FullArea();
        var search = area.Search!;
        search.SearchText = "alpha";
        await search.CurrentSearch;
        Assert.Contains(search.Results, r => r.Item.Kind == SearchResultKind.Project && r.Hit.ProjectNodeId == p);
        Assert.All(search.Results, r => Assert.False(r.IsArchived));
        _dialogs.OnBulkArchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            return true;
        };

        await area.ArchiveTopLevelProjectsCommand.ExecuteAsync();

        Assert.Equal(("Archived", 1L), (Row(p).Status, Row(p).Serial));
        Assert.NotEmpty(search.Results);
        Assert.All(search.Results, r => Assert.True(r.IsArchived)); // the hits on screen are re-marked by this window

        search.SearchText = "alph"; // a new search after the batch
        await search.CurrentSearch;
        var hit = Assert.Single(search.Results, r => r.Item.Kind == SearchResultKind.Project);
        Assert.Equal(p, hit.Hit.ProjectNodeId);
        Assert.True(hit.IsArchived);
        Assert.StartsWith("Archived · ", hit.Location);
        await Settle(area);
    });

    [Fact]
    public Task B16_501Projects_TheChecklistShows500WithTheLimitNotice_AndArchivesExactlyTheShown500() => InContext(async () =>
    {
        var f = await Folder("F");
        var all = new List<long>();
        for (var i = 1; i <= BulkArchiveCandidates.MaxRows + 1; i++)
            all.Add(await Project($"P{i:000}", f));
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "F");
        _dialogs.OnBulkArchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            return true;
        };

        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();

        var dialog = Assert.Single(_dialogs.BulkArchiveDialogs);
        Assert.Equal(500, dialog.Rows.Count);
        Assert.False(string.IsNullOrWhiteSpace(dialog.TruncatedText));
        var shown = dialog.Rows.Select(r => r.NodeId).ToList();
        Assert.Equal(500, shown.Distinct().Count());
        var outside = Assert.Single(all.Except(shown));
        Assert.Equal(shown.Select(id => new BulkArchiveTarget(id, f)), dialog.ConfirmedTargets);

        Assert.Equal(500, _treeStore.Calls("archiveInPlace"));
        Assert.Equal(shown, area.BulkUndoEvents.Select(e => e.NodeId).ToList()); // called in the displayed order
        Assert.Equal(("Completed", 0L), (Row(outside).Status, Row(outside).Serial)); // never called
        using (var connection = _root.Open())
            Assert.Equal(500L, Db.ScalarLong(connection, "SELECT count(*) FROM Projects WHERE Status = 'Archived';"));
        Assert.Equal("Archived 500 projects.", area.BannerMessage);
        await Settle(area);
    });
}
