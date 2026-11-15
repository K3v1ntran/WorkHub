using System.ComponentModel;
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
/// Phase 20 Bulk Unarchive from the Archived panel (proposal Revision 2 + R2a, approved with D9(a); §4 and §5): the checklist, the
/// serial-bound batch (ArchivedAgain / NotArchived write nothing; only an exception stops), the silent read-based re-placement of
/// NotArchived / ProjectTrashed Projects under rules L and C (BU6a–j), the panel visit scope (BU17, BU18), the post-confirmation
/// guard and the slot order (BU19, BU20, BU20-c), and the Workspace banner / selection rules of R2a-2. A gated store holds reads
/// (snapshot when called, delivered on release) and writes; a second service on the same database plays the other window. Every
/// interleaving is driven by gates or by the fake dialog callback, never by sleeps.
/// <para>Every body runs on a <see cref="SingleThreadContext"/>; teardown follows <see cref="ContextTeardown"/>.</para>
/// </summary>
public sealed class BulkUnarchiveViewModelTests : IDisposable
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

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ModalTimeout = TimeSpan.FromSeconds(10);

    public BulkUnarchiveViewModelTests()
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
            ("Archived.CurrentLoad", area.Archived.CurrentLoad), ("Archived.CurrentUnarchive", area.Archived.CurrentUnarchive),
            ("Archived.CurrentBulkUnarchive", area.Archived.CurrentBulkUnarchive),
        };
        if (area.Search is { } search)
            tasks.AddRange([("Search.CurrentSearch", search.CurrentSearch), ("Search.CurrentActivation", search.CurrentActivation)]);
        if (area.SelectedContent is { } content)
            tasks.AddRange([("Content.CurrentLoad", content.CurrentLoad), ("Content.CurrentPreviewLoad", content.CurrentPreviewLoad),
                ("Content.CurrentTrashOperation", content.CurrentTrashOperation), ("Content.CurrentReconciliation", content.CurrentReconciliation),
                ("Content.CurrentCountReconciliation", content.CurrentCountReconciliation)]);
        return tasks.Select(t => (prefix + t.Name, t.Task)).ToList();
    }

    /// <summary>BU5: window A — an area with Project content (the note editor) on the same database.</summary>
    private async Task<WorkspaceAreaViewModel> ContentArea()
    {
        var content = new ProjectContentService(_contentStore, new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance, content, new ScriptedLinkChecker());
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        return area;
    }

    private async Task<WorkspaceAreaViewModel> Area(IAppLog? log = null)
    {
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, log ?? NullAppLog.Instance);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        return area;
    }

    /// <summary>R4: an area with Workspace search on the same gated tree store.</summary>
    private async Task<WorkspaceAreaViewModel> SearchArea()
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

    private async Task<long> Folder(string name, long? parent = null) => (await _other.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null, ProjectStatus status = ProjectStatus.Completed) =>
        (await _other.CreateProjectAsync(parent, new ProjectDraft(name, status))).Value!.Id;

    private async Task Archive(long project) => Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(project)).Outcome);

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

    private static WorkspaceNodeViewModel OnceUnder(WorkspaceAreaViewModel area, long id, long? parentId)
    {
        var row = Assert.Single(Placements(area, id));
        Assert.Equal(parentId, row.Parent?.Id);
        return row;
    }

    private (string Status, long Serial, long? Parent, bool Trashed) Row(long nodeId)
    {
        using var connection = _root.Open();
        var text = Db.ScalarString(connection,
            "SELECT p.Status || '|' || p.ArchiveSerial || '|' || ifnull(n.ParentId, '') || '|' || (n.TrashedAt IS NOT NULL) FROM Projects AS p JOIN WorkspaceNodes AS n ON n.Id = p.NodeId WHERE p.NodeId = $id;",
            ("$id", nodeId))!.Split('|');
        return (text[0], long.Parse(text[1], System.Globalization.CultureInfo.InvariantCulture),
            text[2].Length == 0 ? null : long.Parse(text[2], System.Globalization.CultureInfo.InvariantCulture), text[3] == "1");
    }

    /// <summary>The dialog plays: tick these names, choose <paramref name="status"/>, then run <paramref name="inside"/> (the other window
    /// after the checklist read) and confirm.</summary>
    private void Confirm(ProjectStatus status, string[] names, Action<BulkUnarchiveViewModel>? inside = null) =>
        _dialogs.OnBulkUnarchive = vm =>
        {
            foreach (var row in vm.Rows.Where(r => names.Contains(r.Name)))
                row.IsChecked = true;
            vm.Status = vm.Statuses.Single(s => s.Status == status);
            inside?.Invoke(vm);
            return true;
        };

    private void Other(Task task) => _dialogs.WaitModal(task, ModalTimeout);

    private static Task Run(WorkspaceAreaViewModel area) => area.Archived.UnarchiveProjectsCommand.ExecuteAsync();

    private static async Task OpenArchived(WorkspaceAreaViewModel area)
    {
        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
    }

    /// <summary>The Workspace banner and Phase 19 Details as they were before the batch (R2a-2).</summary>
    private sealed record BannerState(string? Message, bool DetailsAvailable, IReadOnlyList<string>? Details);

    private static BannerState Banner(WorkspaceAreaViewModel area) => new(area.BannerMessage, area.IsBulkDetailsAvailable, area.LastBulkArchiveDetails);

    private static void AssertBannerKept(WorkspaceAreaViewModel area, BannerState before)
    {
        Assert.Equal(before.Message, area.BannerMessage);
        Assert.True(area.IsBulkDetailsAvailable);
        Assert.Same(before.Details, area.LastBulkArchiveDetails);
    }

    // ---- The world --------------------------------------------------------------------------------------------------------

    private sealed record World(WorkspaceAreaViewModel Area, long F, long G, long P, long S, long[] A, long P1, long P2, BannerState Banner);

    /// <summary>
    /// §5 fixture: live folders F and G; archived Projects A1–A3 in F, A4 in G, A5 at the top level (serial 1); archived Folder X with
    /// archived A6; archived Folder Y with live A7; a live Project S in F (the unrelated selection); a live folder P whose Projects
    /// P1/P2 a Phase 19 Bulk Archive archives first (so the banner shows its summary and its Details are available). G is collapsed;
    /// F is expanded unless <paramref name="expandF"/> is false; the Archived panel is open.
    /// </summary>
    private async Task<World> MakeWorld(bool expandF = true, bool phase19Banner = true)
    {
        var f = await Folder("F");
        var g = await Folder("G");
        var p = await Folder("P");
        var x = await Folder("X");
        var y = await Folder("Y");
        var s = await Project("S", f, ProjectStatus.Active);
        long[] a = [await Project("A1", f), await Project("A2", f), await Project("A3", f), await Project("A4", g), await Project("A5"), await Project("A6", x), await Project("A7", y)];
        var p1 = await Project("P1", p);
        var p2 = await Project("P2", p);
        foreach (var id in a.Take(6))
            await Archive(id);
        await MarkFolder(x, FolderLifecycleKind.Archive);
        await MarkFolder(y, FolderLifecycleKind.Archive);

        var area = await Area();
        if (phase19Banner)
        {
            await Expand(area, Node(area.Roots, "P"));
            area.SelectedNode = Node(area.Roots, "P");
            _dialogs.OnBulkArchive = vm => vm.CheckedCount > 0; // P1, P2 (Completed) start ticked
            await area.ArchiveProjectsInFolderCommand.ExecuteAsync();
            Assert.True(area.IsBulkDetailsAvailable);
        }

        if (expandF)
        {
            await Expand(area, Node(area.Roots, "F"));
            area.SelectedNode = Node(Node(area.Roots, "F").Children, "S");
        }

        await OpenArchived(area);
        await Settle(area);
        _treeStore.ResetCalls(); // every count below is the test's own
        return new World(area, f, g, p, s, a, p1, p2, Banner(area));
    }

    // ---- Checklist (pure view model) -----------------------------------------------------------------------------------------

    [Fact]
    public void Checklist_NothingTicked_CompletedPreselected_TargetsCarryTheirSerials()
    {
        var now = DateTimeOffset.UtcNow;
        var vm = new BulkUnarchiveViewModel([new(1, "A", 7, "F", now, 1), new(2, "B", null, null, now, 3)], truncated: true);

        Assert.Equal(0, vm.CheckedCount);
        Assert.Equal("Unarchive 0 projects", vm.UnarchiveText);
        Assert.False(vm.UnarchiveCommand.CanExecute(null));
        Assert.Equal(ProjectStatus.Completed, vm.Status.Status);
        Assert.DoesNotContain(vm.Statuses, s => s.Status == ProjectStatus.Archived);
        Assert.NotNull(vm.TruncatedText);
        Assert.Equal("in Workspace (top level)", vm.Rows[1].LocationText);
        vm.SelectAllCommand.Execute(null);
        Assert.Equal("Unarchive 2 projects", vm.UnarchiveText);
        Assert.Equal([new BulkUnarchiveTarget(1, 1), new BulkUnarchiveTarget(2, 3)], vm.ConfirmedTargets);
        vm.Toggle(vm.Rows[0]);
        Assert.Equal("Unarchive 1 project", vm.UnarchiveText);
        vm.SelectNoneCommand.Execute(null);
        Assert.False(vm.UnarchiveCommand.CanExecute(null));
    }

    // ---- 5.1 Main flow ----------------------------------------------------------------------------------------------------

    [Fact]
    public Task BU1_TheDialogListsTheArchivedProjectRoots_NoneTicked_WithSerials() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        BulkUnarchiveViewModel? shown = null;
        _dialogs.OnBulkUnarchive = vm =>
        {
            shown = vm;
            return false;
        };

        await Run(w.Area);

        Assert.NotNull(shown);
        Assert.Equal(new[] { "A1", "A2", "A3", "A4", "A5" }, shown!.Rows.Select(r => r.Name)); // not X, Y, A6 or A7
        Assert.All(shown.Rows, r => Assert.Equal(1L, r.ArchiveSerial));
        Assert.Equal(new[] { "in F", "in F", "in F", "in G", "in Workspace (top level)" }, shown.Rows.Select(r => r.LocationText));
        Assert.Equal(0, shown.CheckedCount);
        Assert.Equal(ProjectStatus.Completed, shown.Status.Status);
        Assert.Equal("Unarchive 0 projects", shown.UnarchiveText);
        Assert.False(shown.UnarchiveCommand.CanExecute(null));
        Assert.Equal(1, _treeStore.Calls("bulkUnarchiveCandidates"));
        Assert.Equal(0, _treeStore.Calls("unarchiveBound"));
        await Settle(w.Area);
    });

    [Fact]
    public Task BU2_ThreeTicked_AreUnarchivedInListOrder_PlacedOnce_ListAndCountReloaded() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        var area = w.Area;
        var selected = area.SelectedNode;
        Assert.Equal(7, area.Archived.Count); // A1–A5, X and Y (no Phase 19 batch in this world)
        Confirm(ProjectStatus.Active, ["A1", "A2", "A4"]);

        await Run(area);
        await Settle(area);

        Assert.Equal(new[]
        {
            (new BulkUnarchiveTarget(w.A[0], 1), ProjectStatus.Active), (new BulkUnarchiveTarget(w.A[1], 1), ProjectStatus.Active),
            (new BulkUnarchiveTarget(w.A[3], 1), ProjectStatus.Active),
        }, _treeStore.UnarchiveBoundCalls.ToArray());
        OnceUnder(area, w.A[0], w.F);
        OnceUnder(area, w.A[1], w.F);
        Assert.Empty(Placements(area, w.A[3]));
        var g = Node(area.Roots, "G");
        Assert.True(g.HasChildren);
        await Expand(area, g);
        OnceUnder(area, w.A[3], w.G);
        Assert.Same(selected, area.SelectedNode);
        Assert.Equal(new[] { "A3", "A5", "X", "Y" }.Order(), area.Archived.Entries.Select(e => e.Name).Order());
        Assert.Equal(4, area.Archived.Count);
        Assert.Equal("Unarchived 3 projects.", area.Archived.Notice);
        Assert.Equal(3, area.Archived.BulkDetails!.Count);
        Assert.Contains("“A1”: unarchived to F", area.Archived.BulkDetails);
        Assert.Equal(("Active", 1L), (Row(w.A[0]).Status, Row(w.A[0]).Serial));
        Assert.Equal(("Archived", 1L), (Row(w.A[2]).Status, Row(w.A[2]).Serial));
        Assert.Equal(0, _treeStore.Calls("nodeState"));
        await Settle(area);
    });

    [Fact]
    public Task BU4_TopLevelProject_ReturnsToTheTopLevel() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        Confirm(ProjectStatus.OnHold, ["A5"]);

        await Run(w.Area);
        await Settle(w.Area);

        OnceUnder(w.Area, w.A[4], null);
        Assert.Equal(("OnHold", 1L, (long?)null), (Row(w.A[4]).Status, Row(w.A[4]).Serial, Row(w.A[4]).Parent));
    });

    /// <summary>
    /// BU5 (round 2): two windows on one database. Window A opens the note editor while the Project is live and keeps it open;
    /// window B archives the Project, then unarchives it through the Archived panel's Bulk Unarchive; back in A's SAME editor the
    /// user types and saves manually. Never built by selecting an archived Project in the tree.
    /// </summary>
    [Fact]
    public Task BU5_ANoteEditorOpenInWindowA_StaysUsableAcrossArchiveAndBulkUnarchiveInWindowB_AndItsManualSaveSucceeds() => InContext(async () =>
    {
        var p = await Project("Alpha", status: ProjectStatus.Active);
        var note = (await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Kickoff", "v1"))).Note!;
        Assert.Equal(1L, note.Revision);
        var windowA = await ContentArea();
        var windowB = await Area();
        windowA.SelectedNode = Node(windowA.Roots, "Alpha"); // (1) A: the Project is live
        var view = windowA.SelectedContent!;
        await view.CurrentLoad;
        view.SelectedItem = view.Items.Single();
        Confirm(ProjectStatus.Maintenance, ["Alpha"]);
        var checkedInside = false;
        _dialogs.OnNoteEditor = editor =>
        {
            Assert.Equal(NoteEditorState.Editing, editor.State); // A's editor is open

            // (2) Window B: Archive, then Bulk Unarchive the same Project with a valid status.
            windowB.SelectedNode = Node(windowB.Roots, "Alpha");
            _dialogs.WaitModal(windowB.ArchiveProjectCommand.ExecuteAsync(), ModalTimeout);
            Assert.Equal(("Archived", 1L), (Row(p).Status, Row(p).Serial));
            _dialogs.WaitModal(OpenArchived(windowB), ModalTimeout);
            Assert.Contains(windowB.Archived.Entries, e => e.NodeId == p);
            _dialogs.WaitModal(Run(windowB), ModalTimeout);
            Assert.Equal(new[] { (new BulkUnarchiveTarget(p, 1), ProjectStatus.Maintenance) }, _treeStore.UnarchiveBoundCalls.ToArray());
            Assert.Equal("Unarchived 1 project.", windowB.Archived.Notice);
            Assert.Equal(("Maintenance", 1L), (Row(p).Status, Row(p).Serial)); // the chosen status is stored

            // (3) Back in A's SAME editor: still editable; new text; manual Save.
            Assert.Equal(NoteEditorState.Editing, editor.State);
            Assert.False(editor.IsReadOnly);
            editor.Content = "edited across archive and bulk unarchive";
            Assert.True(editor.SaveCommand.CanExecute(null));
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), ModalTimeout);
            Assert.Null(editor.GeneralError);
            checkedInside = true;
            editor.RequestClose();
            return editor.SavedAny;
        };

        await view.OpenCommand.ExecuteAsync();
        await Settle(windowA);
        await Settle(windowB);

        Assert.True(checkedInside);
        var saved = (await _otherContent.GetNoteAsync(note.Id))!;
        Assert.Equal(("Kickoff", "edited across archive and bulk unarchive", 2L), (saved.Title, saved.Content, saved.Revision)); // exactly one more revision
        Assert.Equal(("Maintenance", 1L), (Row(p).Status, Row(p).Serial));
        Assert.Equal(1, _treeStore.Calls("unarchiveBound"));
    });

    // ---- 5.2 Bound to the archive event the user saw (R2-1) ----------------------------------------------------------------------

    [Fact]
    public Task BU15_ArchivedAgainAfterTheChecklistRead_IsRefused_TheNextItemIsStillUnarchived() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        var area = w.Area;
        Confirm(ProjectStatus.Active, ["A1", "A2", "A4"], _ =>
        {
            Other(_other.UnarchiveProjectAsync(w.A[1], ProjectStatus.Completed)); // the other window, after the checklist read
            Other(_other.ArchiveProjectAsync(w.A[1]));                              // ... archives it again: serial 2
        });

        await Run(area);
        await Settle(area);

        Assert.Equal(3, _treeStore.Calls("unarchiveBound"));
        Assert.Equal(0, _treeStore.Calls("nodeState"));
        Assert.Empty(Placements(area, w.A[1]));
        Assert.Contains(area.Archived.Entries, e => e.NodeId == w.A[1]); // listed again after the reload
        Assert.Contains("“A2”: archived again elsewhere; not unarchived", area.Archived.BulkDetails!);
        Assert.Equal("Unarchived 2 of 3 projects. Not unarchived: 1 archived again elsewhere (“A2”).", area.Archived.Notice);
        Assert.Equal(("Archived", 2L), (Row(w.A[1]).Status, Row(w.A[1]).Serial));
        Assert.Equal(("Active", 1L), (Row(w.A[0]).Status, Row(w.A[0]).Serial));
        Assert.Equal(("Active", 1L), (Row(w.A[3]).Status, Row(w.A[3]).Serial));
    });

    [Fact]
    public Task BU15b_Control_TheSingleUnarchiveStaysStateBased() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        var area = w.Area;
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(w.A[1], ProjectStatus.Completed)).Outcome);
        await Archive(w.A[1]); // serial 2
        area.Archived.ReloadCommand.Execute(null);
        await area.Archived.CurrentLoad;
        area.Archived.SelectedEntry = area.Archived.Entries.Single(e => e.NodeId == w.A[1]);

        await area.Archived.UnarchiveCommand.ExecuteAsync();
        await Settle(area);

        OnceUnder(area, w.A[1], w.F);
        Assert.Equal(("Completed", 2L), (Row(w.A[1]).Status, Row(w.A[1]).Serial));
        Assert.Equal(0, _treeStore.Calls("unarchiveBound"));
    });

    // ---- 5.3 NotArchived: tree re-placement (R2-2), banner and selection (R2a-2) -------------------------------------------------

    private void AssertCommon(World w, long project, BannerState banner)
    {
        Assert.True(Placements(w.Area, project).Count <= 1);
        AssertBannerKept(w.Area, banner);
        Assert.Equal(("Completed", 1L), (Row(project).Status, Row(project).Serial)); // the other window's values; nothing by the batch
    }

    [Fact]
    public Task BU6a_ParentLoaded_NotCached_TheRowIsReCreatedFromTheFreshReadsNode() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        var s = area.SelectedNode;
        var children = _treeStore.Calls(GatedWorkspaceStore.Children(w.F));
        Assert.Null(area.CachedNode(w.A[1]));
        Confirm(ProjectStatus.Active, ["A1", "A2", "A4"], _ => Other(_other.UnarchiveProjectAsync(w.A[1], ProjectStatus.Completed)));

        await Run(area);
        await Settle(area);

        OnceUnder(area, w.A[1], w.F);
        OnceUnder(area, w.A[0], w.F);
        Assert.Equal(3, _treeStore.Calls("unarchiveBound"));
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        Assert.Equal(children, _treeStore.Calls(GatedWorkspaceStore.Children(w.F)));
        Assert.Same(s, area.SelectedNode);
        Assert.Contains("“A2”: no longer archived (unarchived elsewhere)", area.Archived.BulkDetails!);
        Assert.Equal(("Active", 1L), (Row(w.A[3]).Status, Row(w.A[3]).Serial)); // the batch continued
        AssertCommon(w, w.A[1], w.Banner);
    });

    [Fact]
    public Task BU6b_ParentNotLoaded_TheFolderGetsChildren_AndShowsItOnceOnExpand() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        Confirm(ProjectStatus.Active, ["A1", "A4"], _ => Other(_other.UnarchiveProjectAsync(w.A[3], ProjectStatus.Completed)));

        await Run(area);
        await Settle(area);

        var g = Node(area.Roots, "G");
        Assert.True(g.HasChildren);
        Assert.Empty(Placements(area, w.A[3]));
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        Assert.Equal(0, _treeStore.Calls(GatedWorkspaceStore.Children(w.G)));
        await Expand(area, g);
        Assert.Equal(1, _treeStore.Calls(GatedWorkspaceStore.Children(w.G)));
        OnceUnder(area, w.A[3], w.G);
        AssertCommon(w, w.A[3], w.Banner);
    });

    [Fact]
    public Task BU6c_TopLevel_OneRowAmongTheRoots() => InContext(async () =>
    {
        var w = await MakeWorld();
        Confirm(ProjectStatus.Active, ["A1", "A5"], _ => Other(_other.UnarchiveProjectAsync(w.A[4], ProjectStatus.Completed)));

        await Run(w.Area);
        await Settle(w.Area);

        OnceUnder(w.Area, w.A[4], null);
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        AssertCommon(w, w.A[4], w.Banner);
    });

    [Fact]
    public Task BU6d_ArchivedAgainBeforeTheFreshRead_NoRow_BannerAndPhase19DetailsKept() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        var s = area.SelectedNode;
        Confirm(ProjectStatus.Active, ["A2", "A4"], _ => Other(_other.UnarchiveProjectAsync(w.A[1], ProjectStatus.Completed)));
        var refusal = _treeStore.HoldCommitted("unarchiveBound"); // A2's refusal is decided when called; delivered on release

        var run = Run(area);
        refusal.WaitEntered();
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(w.A[1])).Outcome); // before the fresh read
        refusal.Release();
        await run;
        await Settle(area);

        Assert.Empty(Placements(area, w.A[1]));
        Assert.Null(area.CachedNode(w.A[1]));
        Assert.Equal(2, _treeStore.Calls("unarchiveBound"));
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        Assert.Equal(0, area.ConfirmationsStarted);
        Assert.NotEqual(WorkspaceAreaViewModel.ProjectArchivedNotice, area.BannerMessage);
        AssertBannerKept(area, w.Banner);
        Assert.Same(s, area.SelectedNode);
        Assert.Contains("“A2”: no longer archived (unarchived elsewhere)", area.Archived.BulkDetails!);
        Assert.Contains(area.Archived.Entries, e => e.NodeId == w.A[1]);
        Assert.Equal(("Active", 1L), (Row(w.A[3]).Status, Row(w.A[3]).Serial));
        Assert.Equal(("Archived", 2L), (Row(w.A[1]).Status, Row(w.A[1]).Serial));
    });

    [Fact]
    public Task BU6e_TheFreshReadFails_NothingApplied_NoRetry_TheBatchContinues() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        Confirm(ProjectStatus.Active, ["A2", "A4"], _ => Other(_other.UnarchiveProjectAsync(w.A[1], ProjectStatus.Completed)));
        _treeStore.ScriptNodeState[w.A[1]] = () => Task.FromException<WorkspaceNodeState?>(new IOException("scripted read failure"));

        await Run(area);
        await Settle(area);

        Assert.Empty(Placements(area, w.A[1]));
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        Assert.Equal(0, area.ConfirmationsStarted);
        Assert.Equal(("Active", 1L), (Row(w.A[3]).Status, Row(w.A[3]).Serial));
        Assert.Contains("“A2”: no longer archived (unarchived elsewhere)", area.Archived.BulkDetails!);
        AssertCommon(w, w.A[1], w.Banner);
    });

    [Fact]
    public Task BU6f_RuleC_ANewerLoadPlacedItElsewhere_OneSilentConfirmationDecides() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        Confirm(ProjectStatus.Active, ["A2", "A5"], _ => Other(_other.UnarchiveProjectAsync(w.A[1], ProjectStatus.Completed)));
        var held = _treeStore.Hold("nodeState");

        var run = Run(area);
        held.WaitEntered(); // snapshot: A2 live in F
        Assert.True((await _other.MoveAsync(w.A[1], w.G)).Value);
        await Expand(area, Node(area.Roots, "G")); // a newer read places A2 under G (a new instance after the read mark)
        OnceUnder(area, w.A[1], w.G);
        var confirm = _treeStore.Hold("nodeState");
        held.Release();
        confirm.WaitEntered();
        confirm.Release();
        await run;
        await Settle(area);

        OnceUnder(area, w.A[1], w.G);
        Assert.DoesNotContain(Node(area.Roots, "F").Children, n => n.Id == w.A[1]);
        Assert.Equal(2, _treeStore.Calls("nodeState"));
        Assert.Equal(1, area.ConfirmationsStarted);
        AssertBannerKept(area, w.Banner);
        Assert.Equal(("Completed", 1L, (long?)w.G), (Row(w.A[1]).Status, Row(w.A[1]).Serial, Row(w.A[1]).Parent));
    });

    [Fact]
    public Task BU6g_RuleL_ThisWindowsCommittedArchive_DiscardsTheOlderReplacementRead() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(w.A[1], ProjectStatus.Completed)).Outcome);
        await area.ReplaceByReadAsync(w.A[1]); // F was loaded before: the re-placement entry shows A2 there (not cached → from Node)
        var row = OnceUnder(area, w.A[1], w.F);
        var calls = _treeStore.Calls("nodeState");

        var held = _treeStore.Hold("nodeState");
        var replace = area.ReplaceByReadAsync(w.A[1]);
        held.WaitEntered(); // snapshot: A2 live in F
        area.SelectedNode = row;
        await area.ArchiveProjectCommand.ExecuteAsync(); // this window's committed Archive (Touch)
        held.Release();
        await replace;
        await Settle(area);

        Assert.Empty(Placements(area, w.A[1]));
        Assert.Equal(calls + 1, _treeStore.Calls("nodeState"));
        Assert.Equal(0, area.ConfirmationsStarted);
        Assert.Equal(("Archived", 2L), (Row(w.A[1]).Status, Row(w.A[1]).Serial));
    });

    [Fact]
    public Task BU6h_AnUnrelatedSelectionIsKept_NoDetailsReload_NothingSelected() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        var s = area.SelectedNode!;
        Assert.Equal(w.S, s.Id);
        await area.CurrentDetailsLoad;
        var details = _treeStore.Calls($"details:{w.S}");
        Confirm(ProjectStatus.Active, ["A1", "A2", "A4"], _ => Other(_other.UnarchiveProjectAsync(w.A[1], ProjectStatus.Completed)));

        await Run(area);
        await Settle(area);

        OnceUnder(area, w.A[1], w.F);
        OnceUnder(area, w.A[0], w.F);
        Assert.Same(s, area.SelectedNode);
        Assert.True(s.IsSelected);
        Assert.Equal(details, _treeStore.Calls($"details:{w.S}"));
        Assert.All(Placements(area, w.A[0]).Concat(Placements(area, w.A[1])), n => Assert.False(n.IsSelected));
        AssertBannerKept(area, w.Banner);
    });

    [Fact]
    public Task BU6i_TheSelectedRowMovesToAnUnloadedFolder_TheExistingRuleClearsTheSelection() => InContext(async () =>
    {
        var w = await MakeWorld(expandF: false);
        var area = w.Area;
        Confirm(ProjectStatus.Active, ["A1", "A2", "A4"], _ =>
        {
            Other(_other.UnarchiveProjectAsync(w.A[1], ProjectStatus.Completed));
            _dialogs.WaitModal(Expand(area, Node(area.Roots, "F")), ModalTimeout); // F's first load shows A2 (before the batch)
            area.SelectedNode = OnceUnder(area, w.A[1], w.F);
            Other(_other.MoveAsync(w.A[1], w.G));
        });

        await Run(area);
        await Settle(area);

        Assert.Null(area.SelectedNode);
        Assert.Empty(Placements(area, w.A[1]));
        Assert.Equal(1, _treeStore.Calls(GatedWorkspaceStore.Children(w.F)));
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        Assert.Equal(0, area.ConfirmationsStarted);
        var g = Node(area.Roots, "G");
        Assert.True(g.HasChildren);
        await Expand(area, g);
        var row = OnceUnder(area, w.A[1], w.G);
        Assert.False(row.IsSelected);
        Assert.Null(area.SelectedNode);
        Assert.Equal(("Active", 1L), (Row(w.A[3]).Status, Row(w.A[3]).Serial));
        AssertBannerKept(area, w.Banner);
    });

    [Theory]
    [InlineData(true)]  // BU6j: a Show request joins the silent confirmation → the single flow's banner
    [InlineData(false)] // BU6j-b: only the bulk request → silent
    public Task BU6j_SilentConfirmation_ShowsTheBannerOnlyWhenAShowRequestJoins(bool showJoins) => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        Confirm(ProjectStatus.Active, ["A2"], _ => Other(_other.UnarchiveProjectAsync(w.A[1], ProjectStatus.Completed)));
        var held = _treeStore.Hold("nodeState");

        var run = Run(area);
        held.WaitEntered(); // (1) snapshot: A2 live in F
        Assert.True((await _other.MoveAsync(w.A[1], w.G)).Value); // (2)
        await Expand(area, Node(area.Roots, "G"));
        OnceUnder(area, w.A[1], w.G);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _other.TrashProjectAsync(w.A[1])).Outcome); // (3)
        var confirm = _treeStore.Hold("nodeState"); // (4)
        held.Release();
        confirm.WaitEntered();
        if (showJoins)
            _ = area.RequestConfirmationForTests(w.A[1], WorkspaceAreaViewModel.ReadNotice.Show); // (5)
        confirm.Release(); // (6)
        await run;
        await Settle(area);

        Assert.Empty(Placements(area, w.A[1]));
        if (showJoins)
        {
            Assert.Equal(3, _treeStore.Calls("nodeState"));
            Assert.Equal(2, area.ConfirmationsStarted);
            Assert.Equal(WorkspaceService.ProjectInTrash, area.BannerMessage);
        }
        else
        {
            Assert.Equal(2, _treeStore.Calls("nodeState"));
            Assert.Equal(1, area.ConfirmationsStarted);
            AssertBannerKept(area, w.Banner);
        }

        Assert.True(Row(w.A[1]).Trashed);
    });

    // ---- 5.4 Panel visit scope (R2-3) and the post-confirmation guard (R2a-1) --------------------------------------------------------

    [Fact]
    public Task BU17_TheChecklistReadReturnsIntoALaterVisit_NoDialog_NoWrite() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        var area = w.Area;
        var listed = _treeStore.Calls("listArchived");
        var count = area.Archived.Count;
        var read = _treeStore.Hold("bulkUnarchiveCandidates");

        var run = Run(area);
        read.WaitEntered();
        var v1 = area.Archived.VisitToken;
        area.CloseArchivedCommand.Execute(null);   // v2
        await OpenArchived(area);                  // v3
        Assert.Equal(v1 + 2, area.Archived.VisitToken);
        read.Release();
        await run;
        await Settle(area);

        Assert.Empty(_dialogs.BulkUnarchiveDialogs);
        Assert.Equal(1, _treeStore.Calls("bulkUnarchiveCandidates"));
        Assert.Equal(0, _treeStore.Calls("unarchiveBound"));
        Assert.Equal(listed + 1, _treeStore.Calls("listArchived"));
        Assert.Null(area.Archived.Notice);
        Assert.Null(area.Archived.BulkDetails);
        Assert.Equal(count, area.Archived.Count);
        Assert.Empty(Placements(area, w.A[0]));
    });

    [Fact]
    public Task BU18_ThePanelIsReopenedWhileAStartedBatchRuns_TreeAndCountUpdated_NewVisitUntouched() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        var area = w.Area;
        var count = area.Archived.Count!.Value;
        Confirm(ProjectStatus.Active, ["A1", "A2", "A4"]);
        var first = _treeStore.HoldCommitted("unarchiveBound");

        var run = Run(area);
        first.WaitEntered(); // A1 committed; its result is held
        area.CloseArchivedCommand.Execute(null);
        await OpenArchived(area); // v3
        Assert.DoesNotContain(area.Archived.Entries, e => e.NodeId == w.A[0]);
        Assert.False(area.Archived.UnarchiveCommand.CanExecute(null)); // the variant: the slot refuses a single Unarchive in v3
        first.Release();
        await run;
        await Settle(area);

        Assert.Equal(3, _treeStore.Calls("unarchiveBound"));
        OnceUnder(area, w.A[0], w.F);
        OnceUnder(area, w.A[1], w.F);
        await Expand(area, Node(area.Roots, "G"));
        OnceUnder(area, w.A[3], w.G);
        Assert.Equal(count - 3, area.Archived.Count);
        Assert.DoesNotContain(area.Archived.Entries, e => e.NodeId == w.A[1] || e.NodeId == w.A[3]); // reloaded after the batch
        Assert.Null(area.Archived.Notice);
        Assert.Null(area.Archived.BulkDetails);
        Assert.Equal("Unarchived 3 projects.", area.LastBulkUnarchiveReport!.Notice); // produced, not written into v3
        foreach (var id in new[] { w.A[0], w.A[1], w.A[3] })
            Assert.Equal(("Active", 1L), (Row(id).Status, Row(id).Serial));
    });

    [Fact]
    public Task BU19_TheConfirmationReturnsIntoALaterVisit_NoWrite_TheNewVisitKeepsItsNotice() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        string? n3 = null;
        BannerState? bannerAtStep2 = null;
        var listedAtStep2 = 0;
        Confirm(ProjectStatus.Active, ["A1", "A2", "A4"], _ =>
        {
            area.CloseArchivedCommand.Execute(null);                       // v2
            _dialogs.WaitModal(OpenArchived(area), ModalTimeout);          // v3
            area.Archived.SelectedEntry = area.Archived.Entries.Single(e => e.NodeId == w.A[4]);
            _dialogs.WaitModal(area.Archived.UnarchiveCommand.ExecuteAsync(), ModalTimeout); // a single Unarchive of A5 in v3
            _dialogs.WaitModal(area.Archived.CurrentLoad, ModalTimeout); // its list reload (never Settle here: this request is tracked)
            n3 = area.Archived.Notice;
            bannerAtStep2 = Banner(area);
            listedAtStep2 = _treeStore.Calls("listArchived");
        });

        await Run(area);
        await Settle(area);

        Assert.NotNull(n3);
        Assert.Equal(n3, area.Archived.Notice);
        Assert.Null(area.Archived.BulkDetails);
        Assert.Equal(0, _treeStore.Calls("unarchiveBound"));
        Assert.Equal(0, _treeStore.Calls("nodeState"));
        Assert.Equal(1, _treeStore.Calls("bulkUnarchiveCandidates"));
        Assert.Equal(1, _treeStore.Calls("unarchiveProject"));
        Assert.Equal(listedAtStep2, _treeStore.Calls("listArchived")); // nothing after the refused confirmation
        Assert.Equal(bannerAtStep2!.Message, area.BannerMessage);
        Assert.False(area.IsLifecycleBusy);
        OnceUnder(area, w.A[4], null);
        Assert.Empty(Placements(area, w.A[0]));
        foreach (var id in new[] { w.A[0], w.A[1], w.A[3] })
            Assert.Equal(("Archived", 1L), (Row(id).Status, Row(id).Serial));
        Assert.Equal("Completed", Row(w.A[4]).Status);
    });

    [Fact]
    public Task BU19b_ThePanelIsClosedWhileTheDialogIsOpen_NoWrite_TheUndoOfferStays() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        Assert.True(area.IsUndoAvailable); // the Phase 19 batch's Bulk Undo
        var listed = _treeStore.Calls("listArchived");
        var busyChanges = 0;
        area.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceAreaViewModel.IsLifecycleBusy))
                busyChanges++;
        };
        Confirm(ProjectStatus.Active, ["A1"], _ => area.CloseArchivedCommand.Execute(null));

        await Run(area);
        await Settle(area);

        Assert.Equal(0, _treeStore.Calls("unarchiveBound"));
        Assert.Equal(0, busyChanges);
        Assert.True(area.IsUndoAvailable); // no BeginWorkspaceWrite
        AssertBannerKept(area, w.Banner);
        Assert.Equal(listed, _treeStore.Calls("listArchived"));
        Assert.False(area.Archived.IsOpen);
        Assert.Equal(("Archived", 1L), (Row(w.A[0]).Status, Row(w.A[0]).Serial));
    });

    [Fact]
    public Task BU20_AnotherLifecycleWriteTakesTheSlotWhileTheDialogIsOpen_NoBatch() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        var area = w.Area;
        var t = await Project("T", w.F); // a live Project in F, shown after a reload of the tree
        await area.ReplaceByReadAsync(t);
        var tRow = OnceUnder(area, t, w.F);
        _treeStore.ResetCalls();
        var noticeChanges = new List<string?>();
        area.Archived.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArchivedProjectsViewModel.Notice))
                noticeChanges.Add(area.Archived.Notice);
        };
        StoreGate? archiveGate = null;
        Task? archive = null;
        Confirm(ProjectStatus.Active, ["A1", "A2", "A4"], _ =>
        {
            archiveGate = _treeStore.Hold("archiveProject");
            area.SelectedNode = tRow;
            archive = area.ArchiveProjectCommand.ExecuteAsync(); // takes the slot; its store call is held
            archiveGate.WaitEntered();
            Assert.True(area.IsLifecycleBusy);
        });

        await Run(area); // step 3: the dialog confirmed while the Archive holds the slot
        Assert.True(area.IsLifecycleBusy); // still the Archive's
        Assert.Equal(0, _treeStore.Calls("unarchiveBound"));
        Assert.Empty(noticeChanges); // no progress, Notice or Details from the bulk request
        Assert.Null(area.Archived.BulkDetails);

        archiveGate!.Release(); // step 4
        await archive!;
        await Settle(area);

        Assert.False(area.IsLifecycleBusy);
        Assert.Equal($"Archived “T”.", area.BannerMessage);
        Assert.True(area.IsUndoAvailable);
        Assert.Equal(("Archived", 1L), (Row(t).Status, Row(t).Serial));
        Assert.Contains(area.Archived.Entries, e => e.NodeId == t);
        Assert.Equal(1, _treeStore.Calls("archiveProject"));
        Assert.Equal(0, _treeStore.Calls("unarchiveBound"));
        Assert.Equal(0, _treeStore.Calls("nodeState"));
        foreach (var id in new[] { w.A[0], w.A[1], w.A[3] })
            Assert.Equal(("Archived", 1L), (Row(id).Status, Row(id).Serial));
    });

    [Fact]
    public Task BU20c_Control_TheSlotIsTakenBeforeBeginWorkspaceWrite_AndBeforeTheFirstWrite() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        Assert.True(area.IsUndoAvailable); // withdrawn by BeginWorkspaceWrite's ClearUndo: the observation point
        var order = new List<string>();
        area.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceAreaViewModel.IsLifecycleBusy))
                order.Add($"busy={area.IsLifecycleBusy}");
            else if (e.PropertyName == nameof(WorkspaceAreaViewModel.IsUndoAvailable))
                order.Add($"undo={area.IsUndoAvailable} while busy={area.IsLifecycleBusy}");
        };
        area.Archived.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArchivedProjectsViewModel.Notice))
                order.Add($"notice={area.Archived.Notice} while busy={area.IsLifecycleBusy}");
        };
        Confirm(ProjectStatus.Active, ["A1", "A2", "A4"]);
        var first = _treeStore.Hold("unarchiveBound");

        var run = Run(area);
        first.WaitEntered(); // inside the first bound write
        Assert.True(area.IsLifecycleBusy);
        Assert.False(area.IsUndoAvailable);
        Assert.Equal("Unarchiving 3 projects…", area.Archived.Notice);
        Assert.Equal(new[] { "busy=True", "undo=False while busy=True", "notice=Unarchiving 3 projects… while busy=True" }, order.Take(3).ToArray());
        first.Release();
        await run;
        await Settle(area);

        Assert.Equal(3, _treeStore.Calls("unarchiveBound"));
        Assert.Equal("busy=False", order.Last(o => o.StartsWith("busy=", StringComparison.Ordinal)));
        Assert.False(area.IsLifecycleBusy);
        Assert.Equal("Unarchived 3 projects.", area.Archived.Notice);
        AssertBannerKept(area, w.Banner); // the text and Details stay; only the Undo offer ended
    });

    // ---- 5.5 Other refusals and stop ---------------------------------------------------------------------------------------

    [Fact]
    public Task BU7_ParentArchivedAfterTheRead_Refused_TheOthersContinue() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        Confirm(ProjectStatus.Active, ["A1", "A4"], _ => Other(MarkFolder(w.F, FolderLifecycleKind.Archive)));

        await Run(w.Area);
        await Settle(w.Area);

        Assert.Contains("“A1”: inside an archived folder", w.Area.Archived.BulkDetails!);
        Assert.Equal("Unarchived 1 of 2 projects. Not unarchived: 1 inside an archived folder (“A1”).", w.Area.Archived.Notice);
        Assert.Equal(("Archived", 1L), (Row(w.A[0]).Status, Row(w.A[0]).Serial));
        Assert.Equal(("Active", 1L), (Row(w.A[3]).Status, Row(w.A[3]).Serial));
    });

    [Fact]
    public Task BU8_ParentTrashedAfterTheRead_Refused_TheOthersContinue() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        Confirm(ProjectStatus.Active, ["A1", "A4"], _ => Other(MarkFolder(w.G, FolderLifecycleKind.Trash)));

        await Run(w.Area);
        await Settle(w.Area);

        Assert.Contains("“A4”: inside a folder in the Trash", w.Area.Archived.BulkDetails!);
        Assert.Equal(("Active", 1L), (Row(w.A[0]).Status, Row(w.A[0]).Serial));
        Assert.Equal(("Archived", 1L), (Row(w.A[3]).Status, Row(w.A[3]).Serial));
    });

    [Fact]
    public Task BU8b_ProjectTrashedAfterTheRead_NoRow_BannerAndPhase19DetailsKept() => InContext(async () =>
    {
        var w = await MakeWorld();
        var area = w.Area;
        var s = area.SelectedNode;
        Confirm(ProjectStatus.Active, ["A1", "A3", "A4"], _ =>
        {
            Other(_other.UnarchiveProjectAsync(w.A[2], ProjectStatus.Completed));
            Other(_other.TrashProjectAsync(w.A[2]));
        });

        await Run(area);
        await Settle(area);

        Assert.Empty(Placements(area, w.A[2]));
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        Assert.Equal(3, _treeStore.Calls("unarchiveBound"));
        Assert.NotEqual(WorkspaceService.ProjectInTrash, area.BannerMessage);
        AssertBannerKept(area, w.Banner);
        Assert.Same(s, area.SelectedNode);
        Assert.Equal(1, area.Trash.Count);
        Assert.Contains("“A3”: in the Trash", area.Archived.BulkDetails!);
        Assert.Equal(("Completed", 1L, true), (Row(w.A[2]).Status, Row(w.A[2]).Serial, Row(w.A[2]).Trashed));
        Assert.Equal(("Active", 1L), (Row(w.A[3]).Status, Row(w.A[3]).Serial));
    });

    [Fact]
    public Task BU8c_TheSelectedRowIsTrashed_TheExistingRuleClearsTheSelection() => InContext(async () =>
    {
        var w = await MakeWorld(expandF: false);
        var area = w.Area;
        Confirm(ProjectStatus.Active, ["A1", "A3", "A4"], _ =>
        {
            Other(_other.UnarchiveProjectAsync(w.A[2], ProjectStatus.Completed));
            _dialogs.WaitModal(Expand(area, Node(area.Roots, "F")), ModalTimeout);
            area.SelectedNode = OnceUnder(area, w.A[2], w.F);
            Other(_other.TrashProjectAsync(w.A[2]));
        });

        await Run(area);
        await Settle(area);

        Assert.Null(area.SelectedNode);
        Assert.Empty(Placements(area, w.A[2]));
        Assert.Equal(3, _treeStore.Calls("unarchiveBound"));
        Assert.Equal(1, _treeStore.Calls("nodeState"));
        Assert.Equal(0, area.ConfirmationsStarted);
        AssertBannerKept(area, w.Banner);
        Assert.Equal(("Active", 1L), (Row(w.A[3]).Status, Row(w.A[3]).Serial));
    });

    [Fact]
    public Task BU9_AnExceptionStopsTheBatch_TheRestAreNotAttempted_TheLogHasNoNames() => InContext(async () =>
    {
        var log = new ListLog();
        var f = await Folder("F");
        long[] a = [await Project("A1", f), await Project("A2", f), await Project("A4", f)];
        foreach (var id in a)
            await Archive(id);
        var area = await Area(log);
        await OpenArchived(area);
        Confirm(ProjectStatus.Active, ["A1", "A2", "A4"]);
        var first = _treeStore.HoldCommitted("unarchiveBound");

        var run = Run(area);
        first.WaitEntered();
        _treeStore.ScriptUnarchiveBound = _ => Task.FromException<BulkUnarchiveResult>(new IOException("scripted write failure"));
        first.Release();
        await run;
        await Settle(area);

        Assert.Equal(2, _treeStore.Calls("unarchiveBound"));
        Assert.Equal("Active", Row(a[0]).Status);
        Assert.Equal("Archived", Row(a[1]).Status);
        Assert.Equal("Archived", Row(a[2]).Status);
        // Round 2: only what the batch knows — no status is claimed for the failed or the not-attempted Project.
        Assert.Equal("Unarchived 1 of 3 projects. Could not unarchive “A2”. 1 project was not attempted.", area.Archived.Notice);
        Assert.Equal(new[] { "“A1”: unarchived to F", "“A2”: could not be unarchived", "“A4”: not attempted" }, area.Archived.BulkDetails!.ToArray());
        AssertNoArchivedClaim(area);
        Assert.Contains(log.Lines, l => l == "ERROR Bulk unarchive failed.");
        Assert.DoesNotContain(log.Lines, l => l.Contains("A1", StringComparison.Ordinal) || l.Contains("A2", StringComparison.Ordinal) || l.Contains("A4", StringComparison.Ordinal));
        Assert.False(area.IsLifecycleBusy);
    });

    /// <summary>Round 2: neither the Notice nor any Details line says a Project is (still) archived.</summary>
    private static void AssertNoArchivedClaim(WorkspaceAreaViewModel area)
    {
        foreach (var text in area.Archived.BulkDetails!.Append(area.Archived.Notice!))
        {
            Assert.DoesNotContain("still archived", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("is archived", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("are archived", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public Task BU9b_AnotherWindowUnarchivesTheNextItemWhileTheFailingWriteIsHeld_ItIsReportedNotAttempted_AndKeepsItsStatus() => InContext(async () =>
    {
        var log = new ListLog();
        var f = await Folder("F");
        long[] a = [await Project("A1", f), await Project("A2", f), await Project("A3", f)];
        foreach (var id in a)
            await Archive(id);
        var area = await Area(log);
        await OpenArchived(area);
        Confirm(ProjectStatus.Active, ["A1", "A2", "A3"]);
        var first = _treeStore.HoldCommitted("unarchiveBound"); // A1 commits when called; its result is held

        var run = Run(area);
        first.WaitEntered();
        var second = _treeStore.Hold("unarchiveBound"); // A2's write: held, never reaches the database
        first.Release();
        second.WaitEntered();
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(a[2], ProjectStatus.Planning)).Outcome); // the other window
        second.Fail(new IOException("scripted write failure"));
        await run;
        await Settle(area);

        Assert.Equal(2, _treeStore.Calls("unarchiveBound"));
        Assert.Equal(new[] { a[0], a[1] }, _treeStore.UnarchiveBoundCalls.Select(c => c.Target.ProjectId).ToArray());
        Assert.Equal(("Active", 1L), (Row(a[0]).Status, Row(a[0]).Serial));   // committed before the stop
        Assert.Equal(("Archived", 1L), (Row(a[1]).Status, Row(a[1]).Serial)); // the failed write never ran
        Assert.Equal(("Planning", 1L), (Row(a[2]).Status, Row(a[2]).Serial)); // the other window's status; not written by the batch
        Assert.Equal("Unarchived 1 of 3 projects. Could not unarchive “A2”. 1 project was not attempted.", area.Archived.Notice);
        Assert.Equal(new[] { "“A1”: unarchived to F", "“A2”: could not be unarchived", "“A3”: not attempted" }, area.Archived.BulkDetails!.ToArray());
        AssertNoArchivedClaim(area);
        Assert.Contains(log.Lines, l => l == "ERROR Bulk unarchive failed.");
        Assert.DoesNotContain(log.Lines, l => l.Contains("A1", StringComparison.Ordinal) || l.Contains("A2", StringComparison.Ordinal) || l.Contains("A3", StringComparison.Ordinal));
        Assert.False(area.IsLifecycleBusy);
        Assert.DoesNotContain(area.Archived.Entries, e => e.NodeId == a[2]); // the list reload after the batch shows A3 gone (unarchived elsewhere)
    });

    [Fact]
    public Task BU10_CancelNeverUnarchives() => InContext(async () =>
    {
        var w = await MakeWorld();
        _dialogs.OnBulkUnarchive = vm =>
        {
            vm.SelectAllCommand.Execute(null);
            return false; // Cancel, Esc, or Enter on the default
        };

        await Run(w.Area);
        await Settle(w.Area);

        Assert.Single(_dialogs.BulkUnarchiveDialogs);
        Assert.Equal(0, _treeStore.Calls("unarchiveBound"));
        Assert.True(w.Area.IsUndoAvailable);
        AssertBannerKept(w.Area, w.Banner);
    });

    [Fact]
    public Task BU11_NoArchivedProjectsLeft_NoDialog_ANotice_AndOnlyFoldersDisableTheCommand() => InContext(async () =>
    {
        var f = await Folder("F");
        var x = await Folder("X");
        var a1 = await Project("A1", f);
        await Archive(a1);
        await MarkFolder(x, FolderLifecycleKind.Archive);
        var area = await Area();
        await OpenArchived(area);
        Assert.True(area.Archived.UnarchiveProjectsCommand.CanExecute(null));
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(a1, ProjectStatus.Completed)).Outcome); // the list is stale

        await Run(area);
        await Settle(area);

        Assert.Empty(_dialogs.BulkUnarchiveDialogs);
        Assert.Equal(WorkspaceAreaViewModel.NoArchivedProjectsToUnarchive, area.Archived.Notice);
        Assert.Equal(0, _treeStore.Calls("unarchiveBound"));

        area.Archived.ReloadCommand.Execute(null);
        await area.Archived.CurrentLoad;
        Assert.Equal([x], area.Archived.Entries.Select(e => e.NodeId));
        Assert.False(area.Archived.UnarchiveProjectsCommand.CanExecute(null)); // only an archived Folder is listed
    });

    [Fact]
    public Task BU12_WhileAnotherLifecycleWriteRuns_TheCommandIsDisabled_NoDialog_NoCall() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        var area = w.Area;
        var gate = _treeStore.Hold("archiveProject");
        area.SelectedNode = Node(Node(area.Roots, "F").Children, "S");
        var pending = area.ArchiveProjectCommand.ExecuteAsync();
        gate.WaitEntered();

        Assert.False(area.Archived.UnarchiveProjectsCommand.CanExecute(null));
        await Run(area);
        Assert.Empty(_dialogs.BulkUnarchiveDialogs);
        Assert.Equal(0, _treeStore.Calls("bulkUnarchiveCandidates"));

        gate.Release();
        await pending;
        await Settle(area);
        Assert.True(area.Archived.UnarchiveProjectsCommand.CanExecute(null));
    });

    [Fact]
    public Task BU13_MoreThan500_500RowsAndANotice_SelectAllMakesExactly500CallsInOrder() => InContext(async () =>
    {
        var h = await Folder("H");
        for (var i = 0; i < 501; i++)
            await Archive(await Project($"B{i:D3}", h));
        var area = await Area();
        await OpenArchived(area);
        BulkUnarchiveViewModel? shown = null;
        _dialogs.OnBulkUnarchive = vm =>
        {
            shown = vm;
            vm.SelectAllCommand.Execute(null);
            return true;
        };

        await Run(area);
        await Settle(area);

        Assert.Equal(500, shown!.Rows.Count);
        Assert.NotNull(shown.TruncatedText);
        var calls = _treeStore.UnarchiveBoundCalls.ToList();
        Assert.Equal(500, calls.Count);
        Assert.Equal(shown.Rows.Select(r => new BulkUnarchiveTarget(r.NodeId, r.ArchiveSerial)), calls.Select(c => c.Target));
        var last = Assert.Single(area.Archived.Entries); // the 501st is still archived ...
        Assert.Equal("B500", last.Name);
        Assert.DoesNotContain(calls, c => c.Target.ProjectId == last.NodeId); // ... and was never called
    });

    [Fact]
    public Task BU14_TheCandidateReadFails_NoDialog_ANotice_NothingChanged() => InContext(async () =>
    {
        var w = await MakeWorld(phase19Banner: false);
        var read = _treeStore.Hold("bulkUnarchiveCandidates");
        var run = Run(w.Area);
        read.WaitEntered();
        read.Fail(new IOException("scripted read failure"));
        await run;
        await Settle(w.Area);

        Assert.Empty(_dialogs.BulkUnarchiveDialogs);
        Assert.Equal(WorkspaceAreaViewModel.BulkUnarchiveListFailed, w.Area.Archived.Notice);
        Assert.Equal(0, _treeStore.Calls("unarchiveBound"));
        Assert.False(w.Area.IsLifecycleBusy);
    });

    // ---- 5.6 Regression -------------------------------------------------------------------------------------------------------

    [Fact]
    public Task R3_RuleL_AnOlderLazyLoadOfTheParent_NeitherDuplicatesNorHidesTheUnarchivedRow() => InContext(async () =>
    {
        var w = await MakeWorld(expandF: false, phase19Banner: false);
        var area = w.Area;
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(w.F));
        var f = Node(area.Roots, "F");
        f.IsExpanded = true;
        var expanding = area.EnsureChildrenLoadedAsync(f);
        load.WaitEntered(); // snapshot: S only (A1 still archived)
        Confirm(ProjectStatus.Active, ["A1"]);

        var run = Run(area); // A1's committed result waits for F's load in flight, then merges by Id
        Assert.True(_context.PumpUntil(() => _treeStore.Calls("unarchiveBound") == 1, ModalTimeout));
        load.Release();
        Assert.True(await expanding);
        await run;
        await Settle(area);

        OnceUnder(area, w.A[0], w.F);
        Assert.Equal(0, area.ConfirmationsStarted);
        Assert.Equal(0, _treeStore.Calls("nodeState"));
    });

    [Fact]
    public Task R4_SearchHitsAreUnmarkedAfterTheBulkUnarchive() => InContext(async () =>
    {
        var alpha = await Project("Alpha");
        await Archive(alpha);
        var area = await SearchArea();
        var search = area.Search!;
        search.SearchText = "alpha";
        await search.CurrentSearch;
        Assert.Contains(search.Results, r => r.Hit.ProjectNodeId == alpha);
        Assert.All(search.Results, r => Assert.True(r.IsArchived));
        await OpenArchived(area);
        Confirm(ProjectStatus.Active, ["Alpha"]);

        await Run(area);
        await Settle(area);

        Assert.Equal("Active", Row(alpha).Status);
        Assert.All(search.Results, r => Assert.False(r.IsArchived));
    });
}
