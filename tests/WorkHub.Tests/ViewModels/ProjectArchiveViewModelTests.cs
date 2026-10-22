using System.Collections.Specialized;
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
/// Project Archive in the Workspace area (Phase 12 proposal revision 3 §3, §6, §8, §11.3): timing and ownership of the
/// committed Archive, event-specific Undo (including the cross-window ABA), the Archived projects panel and its state-based
/// Unarchive, stale tree/reveal/panel results, typed archived-search navigation, and A3 (open editors and pending content
/// writes stay writable). Gated stores hold reads (snapshot when called, delivered on release) and writes (run on release);
/// a second set of services on the same database plays the other window.
/// <para>Every body runs on a <see cref="SingleThreadContext"/>; teardown follows <see cref="ContextTeardown"/>.</para>
/// </summary>
public sealed class ProjectArchiveViewModelTests : IDisposable
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

    public ProjectArchiveViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var real = new SqliteWorkspaceStore(_connections);
        _treeStore = new GatedWorkspaceStore(real);
        _other = new WorkspaceService(real);
        _contentStore = new GatedProjectContentStore(new SqliteProjectContentStore(_connections));
        _otherContent = new ProjectContentService(new SqliteProjectContentStore(_connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
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
            ("CurrentDetailsLoad", area.CurrentDetailsLoad), ("CurrentReveal", area.CurrentReveal),
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
        var content = new ProjectContentService(_contentStore, new ProcessProjectLinkLauncher(_starter, _ => true, _ => true));
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

    private async Task<long> Project(string name, long? parent = null, ProjectStatus status = ProjectStatus.Active) =>
        (await _other.CreateProjectAsync(parent, new ProjectDraft(name, status))).Value!.Id;

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

    private (string Status, long Serial) Row(long nodeId)
    {
        using var connection = _root.Open();
        var text = Db.ScalarString(connection, "SELECT Status || '|' || ArchiveSerial FROM Projects WHERE NodeId = $id;", ("$id", nodeId))!.Split('|');
        return (text[0], long.Parse(text[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private List<string> Data()
    {
        using var connection = _root.Open();
        return Persistence.ProjectContentMigrationTests.DataSnapshot(connection);
    }

    /// <summary>Selects the Project and archives it (not held); returns after the commit is applied.</summary>
    private static async Task ArchiveSelected(WorkspaceAreaViewModel area, WorkspaceNodeViewModel project)
    {
        area.SelectedNode = project;
        await area.ArchiveProjectCommand.ExecuteAsync();
    }

    private static Task<WorkspaceRevealOutcome> Reveal(WorkspaceAreaViewModel area, SearchResultKind kind, long id) => area.RevealAsync(new SearchItemRef(kind, id));

    // ---- Archive: timing, ownership, failure ------------------------------------------------------------------------------

    [Fact]
    public Task Archive_PendingChangesNothing_TheCommitRemovesItById_DeactivatesTheCurrentView_AndOffersUndo() => InContext(async () =>
    {
        var p = await Project("P", status: ProjectStatus.OnHold);
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        var gate = _treeStore.Hold("archiveProject");

        var operation = area.ArchiveProjectCommand.ExecuteAsync();
        gate.WaitEntered();

        // Pending: nothing in the UI changed; Workspace writes are disabled, content operations stay enabled.
        Assert.Equal("Archive project", _dialogs.Confirmations.Single().Title);
        Assert.Contains("open note editors stay open", _dialogs.Confirmations.Single().Message);
        Assert.Same(area.SelectedNode, Placements(area, p).Single());
        Assert.Same(view, area.SelectedContent);
        Assert.False(view.IsDeactivated);
        Assert.True(area.IsLifecycleBusy);
        Assert.Equal("Archiving “P”…", area.BannerMessage);
        Assert.False(area.NewFolderCommand.CanExecute(null));
        Assert.False(area.TrashProjectCommand.CanExecute(null));
        Assert.True(view.NewNoteCommand.CanExecute(null));

        gate.Release();
        await operation;

        Assert.Empty(Placements(area, p));
        Assert.Null(area.SelectedNode);
        Assert.Null(area.SelectedContent);
        Assert.True(view.IsDeactivated);
        Assert.Equal("Archived “P”.", area.BannerMessage);
        Assert.True(area.IsUndoAvailable);
        Assert.False(area.IsLifecycleBusy);
        Assert.Equal(("Archived", 1L), Row(p));
        await area.Archived.CurrentCount;
        Assert.Equal("Archived (1)", area.Archived.ButtonText);
        await Settle(area);
    });

    [Fact]
    public Task Archive_Ownership_PToQ_KeepsQ_AndPToQToP_DeactivatesOnlyTheCurrentPView() => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var c1 = area.SelectedContent!;
        var gate = _treeStore.Hold("archiveProject");
        var operation = area.ArchiveProjectCommand.ExecuteAsync();
        gate.WaitEntered();

        area.SelectedNode = Node(area.Roots, "Q"); // P's view C1 is deactivated by this selection change, as always
        var cq = area.SelectedContent!;
        Assert.True(c1.IsDeactivated);
        gate.Release();
        await operation;

        Assert.Equal("Q", area.SelectedNode!.Name);
        Assert.Same(cq, area.SelectedContent);
        Assert.False(cq.IsDeactivated);
        Assert.Empty(Placements(area, p));

        // Variant P → Q → P: the instance current at commit time belongs to P and is the one deactivated.
        var r = await Project("R");
        var area2 = await Area();
        area2.SelectedNode = Node(area2.Roots, "R");
        var r1 = area2.SelectedContent!;
        var gate2 = _treeStore.Hold("archiveProject");
        var operation2 = area2.ArchiveProjectCommand.ExecuteAsync();
        gate2.WaitEntered();
        area2.SelectedNode = Node(area2.Roots, "Q");
        var q2 = area2.SelectedContent!;
        area2.SelectedNode = Node(area2.Roots, "R");
        var r2 = area2.SelectedContent!;
        Assert.NotSame(r1, r2);
        gate2.Release();
        await operation2;

        Assert.True(r2.IsDeactivated);
        Assert.True(q2.IsDeactivated); // by the earlier selection change, not by the commit
        Assert.Null(area2.SelectedNode);
        Assert.Empty(Placements(area2, r));
        await Settle(area);
        await Settle(area2);
    });

    [Fact]
    public Task Archive_FailureOrDeclinedConfirmation_ChangeNothing() => InContext(async () =>
    {
        var p = await Project("P");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var view = area.SelectedContent!;

        _dialogs.ConfirmAnswer = false;
        await area.ArchiveProjectCommand.ExecuteAsync();
        Assert.Equal(0, _treeStore.Calls("archiveProject"));
        Assert.Same(view, area.SelectedContent);

        _dialogs.ConfirmAnswer = true;
        var gate = _treeStore.Hold("archiveProject");
        var operation = area.ArchiveProjectCommand.ExecuteAsync();
        gate.WaitEntered();
        gate.Fail(new InvalidOperationException("disk"));
        await operation;

        Assert.Equal("Could not archive “P”. Nothing was changed.", area.BannerMessage);
        Assert.Same(area.SelectedNode, Placements(area, p).Single());
        Assert.Same(view, area.SelectedContent);
        Assert.False(view.IsDeactivated);
        Assert.False(area.IsUndoAvailable);
        Assert.False(area.IsLifecycleBusy);
        Assert.Equal(("Active", 0L), Row(p));
        await Settle(area);
    });

    // ---- Undo (event-specific) -----------------------------------------------------------------------------------------

    [Fact]
    public Task Undo_RestoresTheExactPreviousStatus_PlacesItOnce_AndSelectsItUnderTheVisitRules() => InContext(async () =>
    {
        var clients = await Folder("Clients");
        var p = await Project("P", clients, ProjectStatus.OnHold);
        var area = await Area();
        await Expand(area, Node(area.Roots, "Clients"));
        await ArchiveSelected(area, Node(Node(area.Roots, "Clients").Children, "P"));
        Assert.True(area.UndoCommand.CanExecute(null));

        await area.UndoCommand.ExecuteAsync();

        Assert.Equal(("OnHold", 1L), Row(p));
        Assert.Single(Placements(area, p));
        Assert.Equal(p, area.SelectedNode!.Id); // the visit is current and nothing was selected since the click
        Assert.Equal("Restored “P” to Clients.", area.BannerMessage);
        Assert.False(area.IsUndoAvailable);
        await Settle(area);
    });

    [Fact]
    public Task Undo_IsWithdrawnByAnotherWorkspaceWrite_AndByLeavingTheTab() => InContext(async () =>
    {
        await Project("P");
        await Project("Q");
        var area = await Area();
        await ArchiveSelected(area, Node(area.Roots, "P"));
        Assert.True(area.IsUndoAvailable);
        area.SelectedNode = Node(area.Roots, "Q"); // a selection alone keeps it
        Assert.True(area.IsUndoAvailable);
        await area.NewFolderCommand.ExecuteAsync(); // the dialog is declined, but the write started
        Assert.False(area.IsUndoAvailable);

        await ArchiveSelected(area, Node(area.Roots, "Q"));
        Assert.True(area.IsUndoAvailable);
        area.IsTabSelected = false;
        area.IsTabSelected = true;
        Assert.False(area.IsUndoAvailable);
        Assert.False(area.UndoCommand.CanExecute(null));
        await Settle(area);
    });

    [Fact]
    public Task Undo_AfterAnotherWindowUnarchived_IsNotArchived_WritesNothing_AndLeavesTheTree() => InContext(async () =>
    {
        var p = await Project("P");
        var area = await Area();
        await ArchiveSelected(area, Node(area.Roots, "P"));
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(p, ProjectStatus.Completed)).Outcome);
        var before = Data();

        await area.UndoCommand.ExecuteAsync();

        Assert.Equal(before, Data());
        Assert.Equal("“P” is no longer archived.", area.BannerMessage);
        Assert.False(area.IsUndoAvailable);
        Assert.Empty(Placements(area, p)); // the tree is not changed by a refused Undo
        Assert.Null(area.SelectedNode);
        await Settle(area);
    });

    [Fact]
    public Task Undo_AfterAnotherWindowUnarchivedAndArchivedAgain_IsArchivedAgain_WritesNothing() => InContext(async () =>
    {
        var p = await Project("P");
        var area = await Area();
        await ArchiveSelected(area, Node(area.Roots, "P"));          // window A: serial 1
        await _other.UnarchiveProjectAsync(p, ProjectStatus.Completed); // window B
        await _other.ArchiveProjectAsync(p);                           // window B: serial 2
        var before = Data();

        await area.UndoCommand.ExecuteAsync();

        Assert.Equal(before, Data());
        Assert.Equal(("Archived", 2L), Row(p));
        Assert.Equal("“P” was archived again elsewhere. Undo is no longer available; use Archived to unarchive it.", area.BannerMessage);
        Assert.False(area.IsUndoAvailable);
        Assert.Empty(Placements(area, p));
        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        Assert.Equal([p], area.Archived.Entries.Select(e => e.NodeId));
        await Settle(area);
    });

    [Fact]
    public Task NoUndoIsOffered_ForAnAlreadyArchivedRefusal_OrForAPanelUnarchive() => InContext(async () =>
    {
        var p = await Project("P");
        var area = await Area();
        await _other.ArchiveProjectAsync(p); // another window archived it; this tree still shows it
        await ArchiveSelected(area, Node(area.Roots, "P"));

        Assert.Equal("This project is already archived.", area.BannerMessage);
        Assert.False(area.IsUndoAvailable);
        Assert.Empty(Placements(area, p));

        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        area.Archived.SelectedEntry = area.Archived.Entries.Single();
        await area.Archived.UnarchiveCommand.ExecuteAsync();
        Assert.False(area.IsUndoAvailable);
        await Settle(area);
    });

    // ---- Archived projects panel and Unarchive -----------------------------------------------------------------------------

    [Fact]
    public Task PanelUnarchive_AsksForTheStatus_PlacesItOnce_AndNeverChangesTheSelection() => InContext(async () =>
    {
        var clients = await Folder("Clients");
        var p = await Project("P", clients);
        await Project("Q");
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        await Expand(area, Node(area.Roots, "Clients"));
        area.SelectedNode = Node(area.Roots, "Q");
        var view = area.SelectedContent!;
        Assert.Empty(Placements(area, p));

        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        var row = Assert.Single(area.Archived.Entries);
        Assert.Equal(("P", "in Clients"), (row.Name, row.LocationText));
        Assert.Equal("Archived (1)", area.Archived.ButtonText);
        area.Archived.SelectedEntry = row;
        _dialogs.OnUnarchive = dialog =>
        {
            Assert.Equal("Completed", dialog.Status.DisplayName); // A8: preselected
            Assert.Equal(["Planning", "Active", "Maintenance", "On Hold", "Completed"], dialog.Statuses.Select(s => s.DisplayName));
            dialog.Status = dialog.Statuses.Single(s => s.Status == ProjectStatus.Maintenance);
            return true;
        };
        await area.Archived.UnarchiveCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;

        Assert.Equal(("Maintenance", 1L), Row(p));
        Assert.Single(Placements(area, p));
        Assert.Equal("Q", area.SelectedNode!.Name);
        Assert.Same(view, area.SelectedContent);
        Assert.Equal("Unarchived “P” to Clients.", area.Archived.Notice);
        Assert.Empty(area.Archived.Entries);
        area.CloseArchivedCommand.Execute(null);
        Assert.False(area.IsArchivedOpen);
        Assert.Same(view, area.SelectedContent);
        await Settle(area);
    });

    [Fact]
    public Task PanelUnarchive_IntoAnUnloadedParent_SetsItsExpander_AndCancelledDialogsWriteNothing() => InContext(async () =>
    {
        var clients = await Folder("Clients");
        var p = await Project("P", clients);
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        var folder = Node(area.Roots, "Clients");
        Assert.False(folder.HasChildren);

        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        area.Archived.SelectedEntry = area.Archived.Entries.Single();
        _dialogs.OnUnarchive = _ => false;
        await area.Archived.UnarchiveCommand.ExecuteAsync();
        Assert.Equal(0, _treeStore.Calls("unarchiveProject"));
        Assert.Equal(("Archived", 1L), Row(p)); // nothing written: still the other window's archive event (serial 1)
        _dialogs.OnUnarchive = _ => true;
        area.Archived.SelectedEntry = area.Archived.Entries.Single();
        await area.Archived.UnarchiveCommand.ExecuteAsync();

        Assert.Equal("Completed", Row(p).Status);
        Assert.Empty(Placements(area, p)); // its parent is not loaded here: it appears with that folder's load
        Assert.True(folder.HasChildren);
        await Expand(area, folder);
        Assert.Single(Placements(area, p));
        await Settle(area);
    });

    [Fact]
    public Task PanelUnarchive_RefusedBecauseUnarchivedElsewhere_RemovesTheRow_AndWritesNothing() => InContext(async () =>
    {
        var p = await Project("P");
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        area.Archived.SelectedEntry = area.Archived.Entries.Single();
        await _other.UnarchiveProjectAsync(p, ProjectStatus.Planning);
        var before = Data();

        await area.Archived.UnarchiveCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;

        Assert.Equal(before, Data());
        Assert.Equal("“P” is no longer archived.", area.Archived.Notice);
        Assert.Empty(area.Archived.Entries);
        Assert.False(area.IsLifecycleBusy);
        await Settle(area);
    });

    [Fact]
    public Task Unarchive_WhileTheParentsLoadIsInFlight_PlacesTheProjectExactlyOnce() => InContext(async () =>
    {
        var clients = await Folder("Clients");
        var p = await Project("P", clients);
        await Project("Other", clients);
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        var folder = Node(area.Roots, "Clients");
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(clients)); // its snapshot (without P) is taken when called
        folder.IsExpanded = true;
        load.WaitEntered();

        area.Archived.SelectedEntry = area.Archived.Entries.Single();
        var unarchive = area.Archived.UnarchiveCommand.ExecuteAsync();
        load.Release();
        await unarchive;
        await area.EnsureChildrenLoadedAsync(folder);

        Assert.Single(Placements(area, p));
        Assert.Equal(["Other", "P"], Names(folder.Children));
        await Settle(area);
    });

    // ---- Stale tree and reveal results ------------------------------------------------------------------------------------

    [Fact]
    public Task ARevealResolvedDuringAPendingArchive_IsResolvedAgain_AndNavigatesToThePanel() => InContext(async () =>
    {
        var p = await Project("P");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var archiveGate = _treeStore.HoldCommitted("archiveProject"); // the write commits now; its result is applied on release
        var operation = area.ArchiveProjectCommand.ExecuteAsync();
        archiveGate.WaitEntered();
        var resolveGate = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, p);
        resolveGate.WaitEntered();
        archiveGate.Release();
        await operation; // the completion is applied while the resolve's (pre- or post-commit) answer is held
        resolveGate.Release();

        Assert.Equal(WorkspaceRevealOutcome.Archived, await reveal);
        Assert.Equal(2, SearchStore.Resolves); // the stale answer was read again once
        Assert.True(area.IsArchivedOpen);
        Assert.Equal(p, area.Archived.SelectedEntry!.NodeId);
        Assert.Empty(Placements(area, p));
        await Settle(area);
    });

    [Fact]
    public Task AnotherWindowsArchive_IsReconciledFromDetails_ContentListing_RefusedWrites_AndTheEditCommand() => InContext(async () =>
    {
        var a = await Project("A");
        var b = await Project("B");
        var c = await Project("C");
        var d = await Project("D");
        var area = await Area();
        foreach (var id in new[] { a, b, c, d })
            await _other.ArchiveProjectAsync(id);

        // Details load of a newly selected Project.
        area.SelectedNode = Node(area.Roots, "A");
        await area.CurrentDetailsLoad;
        await Settle(area);
        Assert.Empty(Placements(area, a));
        Assert.Equal("This project is archived.", area.BannerMessage);

        // Refused Rename (the dialog shows the refusal; the node is reconciled after it closes).
        area.SelectedNode = Node(area.Roots, "B");
        await area.CurrentDetailsLoad; // removes B as well: the details read is fresh
        Assert.Empty(Placements(area, b));
        _dialogs.OnWorkspaceNameEditor = editor =>
        {
            editor.Name = "Renamed";
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10));
            Assert.Equal(WorkspaceService.ProjectIsArchived, editor.Error);
            return false;
        };
        var gate = _treeStore.Hold($"details:{c}");
        area.SelectedNode = Node(area.Roots, "C"); // details held: the tree still shows C
        await area.RenameCommand.ExecuteAsync();
        await area.CurrentReconciliation;
        Assert.Empty(Placements(area, c));
        gate.Release();

        // Edit Project never opens the editor on an archived Project: its own fresh read finds it archived first.
        var editorOpened = false;
        _dialogs.OnProjectEditor = _ => editorOpened = true;
        var dGate = _treeStore.Hold($"details:{d}");
        area.SelectedNode = Node(area.Roots, "D"); // the details load is held; the tree still shows D
        await area.EditProjectCommand.ExecuteAsync();
        Assert.Empty(Placements(area, d));
        Assert.False(editorOpened);
        Assert.Equal("This project is archived.", area.BannerMessage);
        dGate.Release();
        await Settle(area);
    });

    [Fact]
    public Task AContentListingThatSaysArchived_TakesTheProjectOutOfTheTree() => InContext(async () =>
    {
        var p = await Project("P");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        await area.CurrentDetailsLoad;
        await _other.ArchiveProjectAsync(p);

        await view.NavigateAsync(null); // any later listing of the view (here a navigation to its root)
        await area.CurrentReconciliation;

        Assert.Empty(Placements(area, p));
        Assert.True(view.IsDeactivated);
        Assert.Equal("This project is archived.", area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task MoveToTrash_OfAProjectArchivedElsewhere_IsRefused_AndReconciled() => InContext(async () =>
    {
        var p = await Project("P");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        await area.CurrentDetailsLoad;
        await _other.ArchiveProjectAsync(p);
        var before = Data();

        await area.TrashProjectCommand.ExecuteAsync();

        Assert.Equal(before, Data());
        Assert.Equal("This project is archived. Unarchive it first.", area.BannerMessage);
        Assert.Empty(Placements(area, p));
        Assert.False(area.IsUndoAvailable);
        await Settle(area);
    });

    [Fact]
    public Task RestoringAnArchivedAndTrashedProject_DoesNotPlaceIt_AndPointsToTheArchivedPanel() => InContext(async () =>
    {
        var p = await Project("Legacy");
        using (var connection = _root.Open())
        {
            Db.Exec(connection, "UPDATE Projects SET Status = 'Archived', ArchiveSerial = 1 WHERE NodeId = $id;", ("$id", p));
            Db.Exec(connection, "UPDATE WorkspaceNodes SET TrashedAt = '2026-01-01T00:00:00.000Z' WHERE Id = $id;", ("$id", p)); // an earlier build
        }

        var area = await Area();
        await area.OpenTrashCommand.ExecuteAsync();
        await area.Trash.CurrentLoad;
        var entry = area.Trash.Entries.Single();
        Assert.EndsWith("· archived", entry.FromText);
        area.Trash.SelectedEntry = entry;
        await area.Trash.RestoreCommand.ExecuteAsync();
        await area.Archived.CurrentCount;

        Assert.Equal("Restored “Legacy”. It is archived: find it under Archived.", area.Trash.Notice);
        Assert.Empty(Placements(area, p));
        Assert.Equal("Archived (1)", area.Archived.ButtonText);
        await Settle(area);
    });

    [Fact]
    public Task RemoveFolder_HoldingAnArchivedProject_ExplainsWhatHoldsIt() => InContext(async () =>
    {
        var clients = await Folder("Clients");
        var p = await Project("P", clients);
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "Clients");

        await area.RemoveFolderCommand.ExecuteAsync();

        Assert.Equal("“Clients” still holds 1 archived item. Unarchive it and move it elsewhere first, or move the folder to the Trash.", area.BannerMessage);
        Assert.Empty(_dialogs.Confirmations);
        Assert.Single(Placements(area, clients));
        await Settle(area);
    });

    // ---- Typed archived-search navigation (§8.4) --------------------------------------------------------------------------

    [Fact]
    public Task Navigation_SelectsTheRowByNodeId_AmongArchivedProjectsWithTheSameName() => InContext(async () =>
    {
        var x = await Folder("X");
        var y = await Folder("Y");
        var first = await Project("Alpha", x);
        var second = await Project("Alpha", y);
        await _other.ArchiveProjectAsync(first);
        await _other.ArchiveProjectAsync(second);
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "X");

        Assert.Equal(WorkspaceRevealOutcome.Archived, await Reveal(area, SearchResultKind.Project, second));

        Assert.True(area.IsArchivedOpen);
        Assert.Equal(second, area.Archived.SelectedEntry!.NodeId);
        Assert.Equal("in Y", area.Archived.SelectedEntry.LocationText);
        Assert.Equal("X", area.SelectedNode!.Name); // the tree selection is untouched
        await Settle(area);
    });

    [Fact]
    public Task Navigation_FromAContentHit_SelectsTheOwningProject_AndTheSearchKeepsTheMarkedHit() => InContext(async () =>
    {
        var p = await Project("Alpha portal");
        await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Alpha minutes", "x"));
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        var search = area.Search!;
        search.SearchText = "alpha";
        await search.CurrentSearch;
        var noteHit = search.Results.Single(r => r.Item.Kind == SearchResultKind.Note);
        Assert.True(noteHit.IsArchived);
        Assert.StartsWith("Archived · ", noteHit.Location);

        search.SelectedResult = noteHit;
        search.ActivateSelectedCommand.Execute(null);
        await search.CurrentActivation;

        Assert.Equal(p, area.Archived.SelectedEntry!.NodeId);
        Assert.Equal("This item's project is archived — it is selected under Archived.", search.NoticeText);
        Assert.Equal(2, search.Results.Count); // both hits stay
        await Settle(area);
    });

    [Fact]
    public Task Navigation_NeverUsesAListAlreadyShown_AProjectUnarchivedMeanwhileIsNoLongerArchived() => InContext(async () =>
    {
        var p = await Project("P");
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        Assert.Single(area.Archived.Entries); // the panel shows P (cached)
        await _other.UnarchiveProjectAsync(p, ProjectStatus.Active);
        var resolve = SearchStore.Hold("resolve");
        using (var connection = _root.Open()) // the resolve's answer is taken while P is still archived
            Db.Exec(connection, "UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;", ("$id", p));
        var reveal = Reveal(area, SearchResultKind.Project, p);
        resolve.WaitEntered();
        await _other.UnarchiveProjectAsync(p, ProjectStatus.Active);
        resolve.Release();

        Assert.Equal(WorkspaceRevealOutcome.NoLongerArchived, await reveal);
        Assert.Null(area.Archived.SelectedEntry);
        Assert.Empty(area.Archived.Entries); // the fresh read, not the cached row
        await Settle(area);
    });

    [Fact]
    public Task Navigation_ProjectUnarchivedThenTrashed_IsNoLongerArchived_AndTheNextRevealRemovesTheHit() => InContext(async () =>
    {
        var p = await Project("Alpha");
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        var search = area.Search!;
        search.SearchText = "alpha";
        await search.CurrentSearch;
        var resolve = SearchStore.Hold("resolve");
        search.SelectedResult = search.Results.Single();
        search.ActivateSelectedCommand.Execute(null);
        resolve.WaitEntered();
        await _other.UnarchiveProjectAsync(p, ProjectStatus.Active);
        await _other.TrashProjectAsync(p);
        resolve.Release();
        await search.CurrentActivation;

        Assert.Equal("This item is no longer archived. Search again.", search.NoticeText);
        Assert.Single(search.Results);
        search.SelectedResult = search.Results.Single();
        search.ActivateSelectedCommand.Execute(null);
        await search.CurrentActivation;
        Assert.Equal("This project is in the Trash.", search.NoticeText);
        Assert.Empty(search.Results);
        await Settle(area);
    });

    [Fact]
    public Task Navigation_ALifecycleCompletionDuringThePanelRead_MeansOneReRead_AndTheSelectionComesFromIt() => InContext(async () =>
    {
        var p = await Project("P");
        var q = await Project("Q");
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        var read = _treeStore.Hold("listArchived");
        var reveal = Reveal(area, SearchResultKind.Project, p);
        read.WaitEntered();
        await _other.ArchiveProjectAsync(q);
        await area.ReconcileNodeAsync(q); // a stale-node reconciliation here: one lifecycle completion
        read.Release();

        Assert.Equal(WorkspaceRevealOutcome.Archived, await reveal);
        Assert.Equal(2, _treeStore.Calls("listArchived"));
        Assert.Equal([p, q], area.Archived.Entries.Select(e => e.NodeId));
        Assert.Equal(p, area.Archived.SelectedEntry!.NodeId);
        await Settle(area);
    });

    public static TheoryData<string> Supersessions() => ["new reveal", "manual tree selection", "tab leave", "Workspace write", "Retry"];

    [Theory]
    [MemberData(nameof(Supersessions))]
    public Task Navigation_SupersededDuringThePanelRead_SelectsNothing_AndTheLaterActionStands(string action) => InContext(async () =>
    {
        var p = await Project("P");
        await Project("Q");
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        var read = _treeStore.Hold("listArchived");
        var reveal = Reveal(area, SearchResultKind.Project, p);
        read.WaitEntered();

        switch (action)
        {
            case "new reveal":
                var other = Reveal(area, SearchResultKind.Project, Node(area.Roots, "Q").Id);
                read.Release();
                Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal);
                Assert.Equal(WorkspaceRevealOutcome.Selected, await other);
                Assert.Equal("Q", area.SelectedNode!.Name);
                break;
            case "manual tree selection":
                area.SelectedNode = Node(area.Roots, "Q");
                read.Release();
                Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal);
                Assert.Equal("Q", area.SelectedNode!.Name);
                Assert.False(area.IsArchivedOpen); // selecting a tree node closed the panel
                break;
            case "tab leave":
                area.IsTabSelected = false;
                read.Release();
                Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal);
                break;
            case "Workspace write":
                await area.NewFolderCommand.ExecuteAsync(); // declined dialog, but the write started
                read.Release();
                Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal);
                break;
            default:
                area.InvalidateReveal(); // what Retry does first (Retry itself is offered only after a failed root load)
                read.Release();
                Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal);
                break;
        }

        Assert.Null(area.Archived.SelectedEntry);
        await Settle(area);
    });

    [Fact]
    public Task Navigation_BackDuringTheRead_OrAPanelSelectionDuringTheResolve_Wins() => InContext(async () =>
    {
        var p = await Project("P");
        var q = await Project("Q");
        await _other.ArchiveProjectAsync(p);
        await _other.ArchiveProjectAsync(q);
        var area = await Area();

        var read = _treeStore.Hold("listArchived");
        var reveal = Reveal(area, SearchResultKind.Project, p);
        read.WaitEntered();
        area.CloseArchivedCommand.Execute(null); // Back
        read.Release();
        Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal);
        Assert.False(area.IsArchivedOpen); // never reopened

        // The panel shows its list; a reveal of P is still resolving when the user picks Q in that list.
        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        var resolve = SearchStore.Hold("resolve");
        var reveal2 = Reveal(area, SearchResultKind.Project, p);
        resolve.WaitEntered();
        area.Archived.SelectedEntry = area.Archived.Entries.Single(e => e.NodeId == q);
        resolve.Release();
        Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal2);
        Assert.Equal(q, area.Archived.SelectedEntry!.NodeId); // the user's later choice stands
        Assert.Equal(2, area.Archived.Entries.Count); // no navigation read replaced the list
        await Settle(area);
    });

    [Fact]
    public Task Navigation_ACompletionDuringTheResolve_ResolvesOnceMore() => InContext(async () =>
    {
        var p = await Project("P");
        var q = await Project("Q");
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        var resolve = SearchStore.Hold("resolve");
        var reveal = Reveal(area, SearchResultKind.Project, p);
        resolve.WaitEntered();
        await _other.ArchiveProjectAsync(q);
        await area.ReconcileNodeAsync(q); // one lifecycle completion while the resolve is held
        resolve.Release();

        Assert.Equal(WorkspaceRevealOutcome.Archived, await reveal);
        Assert.Equal(2, SearchStore.Resolves);
        await Settle(area);
    });

    [Fact]
    public Task Navigation_APanelReadFailure_IsFailed_AndSelectsNothing() => InContext(async () =>
    {
        var p = await Project("P");
        await _other.ArchiveProjectAsync(p);
        var area = await Area();
        var read = _treeStore.Hold("listArchived");
        var reveal = Reveal(area, SearchResultKind.Project, p);
        read.WaitEntered();
        read.Fail(new InvalidOperationException("locked"));

        Assert.Equal(WorkspaceRevealOutcome.Failed, await reveal);
        Assert.Equal("The Archived list could not be loaded. Nothing was changed.", area.Archived.LoadError);
        Assert.Null(area.Archived.SelectedEntry);
        await Settle(area);
    });

    // ---- A3 (user-selected): editors, drafts and pending content writes ------------------------------------------------------

    [Fact]
    public Task NoteEditor_ProjectArchivedElsewhere_KeepsTheDraft_AndSaveSucceeds() => InContext(async () =>
    {
        var p = await Project("P");
        var note = (await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Kickoff", "v1"))).Note!.Id;
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        view.SelectedItem = view.Items.Single();
        var checkedInside = false;
        _dialogs.OnNoteEditor = editor =>
        {
            editor.Content = "my draft";
            _dialogs.WaitModal(_other.ArchiveProjectAsync(p), TimeSpan.FromSeconds(10));
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10));
            Assert.Null(editor.GeneralError);
            Assert.Equal("my draft", editor.Content);
            checkedInside = true;
            editor.RequestClose();
            return editor.SavedAny;
        };

        await view.OpenCommand.ExecuteAsync();
        await Settle(area);

        Assert.True(checkedInside);
        Assert.Equal("my draft", (await _otherContent.GetNoteAsync(note))!.Content);
        Assert.Equal("Archived", Row(p).Status);
    });

    [Fact]
    public Task APendingContentWrite_CompletesAfterTheArchiveCommits_AndTheDeactivatedViewIgnoresItsResult() => InContext(async () =>
    {
        var p = await Project("P");
        await _otherContent.CreateFolderAsync(p, null, "Specs");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var view = area.SelectedContent!;
        await view.CurrentLoad;
        view.SelectedItem = view.Items.Single();
        var trashGate = _contentStore.Hold("trash");
        var contentTrash = view.MoveToTrashCommand.ExecuteAsync();
        trashGate.WaitEntered();

        await area.ArchiveProjectCommand.ExecuteAsync(); // D15 does not block content operations, and vice versa
        Assert.True(view.IsDeactivated);
        trashGate.Release();
        await contentTrash;

        Assert.Single(await _otherContent.ListTrashAsync(p)); // A3: the write committed
        Assert.False(view.HasUndo); // a late result is applied only while its view is eligible
        Assert.Equal("Archived", Row(p).Status);
        await Settle(area);
    });

    // ---- Search marking and thread affinity ---------------------------------------------------------------------------------

    [Fact]
    public Task SearchHits_AreReMarkedByArchiveAndUndoInThisWindow() => InContext(async () =>
    {
        var p = await Project("Alpha");
        await _otherContent.CreateNoteAsync(p, null, new ProjectNoteDraft("Alpha note", "x"));
        var area = await Area();
        var search = area.Search!;
        search.SearchText = "alpha";
        await search.CurrentSearch;
        Assert.All(search.Results, r => Assert.False(r.IsArchived));

        await ArchiveSelected(area, Node(area.Roots, "Alpha"));
        Assert.All(search.Results, r => Assert.True(r.IsArchived));
        await area.UndoCommand.ExecuteAsync();
        Assert.All(search.Results, r => Assert.False(r.IsArchived));
        await Settle(area);
    });

    [Fact]
    public Task EveryTreeAndPanelMutation_HappensOnTheContextThread() => InContext(async () =>
    {
        var thread = Environment.CurrentManagedThreadId;
        var threads = new List<int>();
        var area = await Area();
        await Project("P");
        await Project("Q");
        await _other.ArchiveProjectAsync(await Project("R"));
        NotifyCollectionChangedEventHandler record = (_, _) => threads.Add(Environment.CurrentManagedThreadId);
        area.Roots.CollectionChanged += record;
        area.Archived.Entries.CollectionChanged += record;
        await area.OpenArchivedCommand.ExecuteAsync(); // the roots loaded before P and Q existed: reload them through reveals
        await area.Archived.CurrentLoad;
        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, (await _other.ListChildrenAsync(null)).Nodes.Single(n => n.Name == "P").Id));

        await ArchiveSelected(area, Node(area.Roots, "P"));
        await area.UndoCommand.ExecuteAsync();
        await area.OpenArchivedCommand.ExecuteAsync();
        await area.Archived.CurrentLoad;
        area.Archived.SelectedEntry = area.Archived.Entries.Single();
        await area.Archived.UnarchiveCommand.ExecuteAsync();
        await Settle(area);

        Assert.NotEmpty(threads);
        Assert.All(threads, t => Assert.Equal(thread, t));
    });
}
