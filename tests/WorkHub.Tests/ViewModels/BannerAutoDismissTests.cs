using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>A hand-fired banner clock: every scheduled callback is kept (also after it was cancelled) so a stale one can be fired.</summary>
internal sealed class TestBannerClock : IBannerClock
{
    public List<Entry> Scheduled { get; } = [];

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var entry = new Entry(delay, callback);
        Scheduled.Add(entry);
        return entry;
    }

    /// <summary>Runs the callback even if it was cancelled: the schedule's own checks must ignore a stale one.</summary>
    public void Fire(int index) => Scheduled[index].Callback();

    public sealed class Entry(TimeSpan delay, Action callback) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;

        public Action Callback { get; } = callback;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}

/// <summary>
/// Phase 26 acceptance for the Workspace banner (proposal Revision 2 §3.2, §3.3, §5.1: AD1–AD16b, AD18). Window A is the Workspace area
/// on a gated tree store; window B is a second service on the same SQLite test database. The banner clock is fired by hand and the
/// view's state (visible, pointer over, focus within) is reported through the view model's hook exactly as the window does. Gates
/// only, no sleeps. Fake names only.
/// <para>Every body runs on a <see cref="SingleThreadContext"/>; teardown follows <see cref="ContextTeardown"/>.</para>
/// </summary>
public sealed class BannerAutoDismissTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedWorkspaceStore _treeStore;
    private readonly GatedProjectContentStore _contentStore;
    private readonly WorkspaceService _other;
    private readonly List<WorkspaceAreaViewModel> _areas = [];
    private readonly SingleThreadContext _context = new();
    private readonly TestBannerClock _clock = new();

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(30);

    public BannerAutoDismissTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _treeStore = new GatedWorkspaceStore(new SqliteWorkspaceStore(_connections));
        _contentStore = new GatedProjectContentStore(new SqliteProjectContentStore(_connections));
        _other = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose() =>
        ContextTeardown.Run(_context, ReleaseGates, _areas.Select((area, i) => (ContextTeardown.TrackedSet)(() => Tracked(area, $"area{i}."))).ToList(),
            _root.Dispose, _root.Paths.Root, TeardownBound);

    private Task InContext(Func<Task> body) => _context.RunAsync(body, BodyTimeout);

    private void ReleaseGates()
    {
        _treeStore.ReleaseAll();
        _contentStore.ReleaseAll();
    }

    private static Task Settle(WorkspaceAreaViewModel area) => TrackedTasks.SettleAsync(() => Tracked(area, ""));

    private static IReadOnlyList<(string Name, Task Task)> Tracked(WorkspaceAreaViewModel area, string prefix)
    {
        var tasks = new List<(string Name, Task Task)>
        {
            ("CurrentDetailsLoad", area.CurrentDetailsLoad), ("CurrentReveal", area.CurrentReveal), ("CurrentFolderInfoLoad", area.CurrentFolderInfoLoad),
            ("CurrentLifecycleOperation", area.CurrentLifecycleOperation), ("CurrentReconciliation", area.CurrentReconciliation), ("CurrentDrop", area.CurrentDrop),
            ("Trash.CurrentLoad", area.Trash.CurrentLoad), ("Trash.CurrentCount", area.Trash.CurrentCount), ("Trash.CurrentRestore", area.Trash.CurrentRestore),
            ("Archived.CurrentLoad", area.Archived.CurrentLoad), ("Archived.CurrentCount", area.Archived.CurrentCount), ("Archived.CurrentUnarchive", area.Archived.CurrentUnarchive),
        };
        if (area.SelectedContent is { } content)
            tasks.AddRange([("Content.CurrentLoad", content.CurrentLoad), ("Content.CurrentTrashOperation", content.CurrentTrashOperation)]);
        return tasks.Select(t => (prefix + t.Name, t.Task)).ToList();
    }

    /// <summary>Window A with the banner visible (the view's report), the pointer and focus outside it.</summary>
    private async Task<WorkspaceAreaViewModel> Area(bool bannerVisible = true)
    {
        var content = new ProjectContentService(_contentStore, new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance, content, new ScriptedLinkChecker(),
            new WorkspaceSearchService(new SqliteWorkspaceSearchStore(_connections, WorkspaceSearchStoreOptions.Default)), searchDelayMs: 0)
        {
            BannerClock = _clock,
        };
        _areas.Add(area);
        area.IsTabSelected = true;
        area.ReportBannerView(visible: bannerVisible, pointerOver: false, focusWithin: false);
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        return area;
    }

    private async Task<long> Folder(string name, long? parent = null) => (await _other.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null) =>
        (await _other.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;

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

    private static Task<WorkspaceDropOutcome> Drop(WorkspaceAreaViewModel area, long source, long? target) =>
        area.MoveByDropAsync(area.BeginDragSession(source) ?? throw new InvalidOperationException("not draggable"), source, target);

    /// <summary>No timer is pending and none was ever scheduled since <paramref name="before"/> entries.</summary>
    private void AssertNoTimer(WorkspaceAreaViewModel area, int before)
    {
        Assert.False(area.BannerAutoDismiss.IsArmed);
        Assert.Equal(before, _clock.Scheduled.Count);
    }

    private void AssertFiringEverythingChangesNothing(WorkspaceAreaViewModel area)
    {
        var text = area.BannerMessage;
        for (var i = 0; i < _clock.Scheduled.Count; i++)
            _clock.Fire(i);
        Assert.Equal(text, area.BannerMessage);
    }

    // ---- AD1, AD2: a reviewed success result hides after exactly 5 s ----------------------------------------------------------

    [Fact]
    public Task AD1_AD2_DropResult_OneFiveSecondTimer_ShownUntilItFires_ThenCleared() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await Area();

        Assert.Equal(WorkspaceDropOutcome.Moved, await Drop(area, p, f));

        Assert.Equal("Moved “P” to “F”.", area.BannerMessage);
        var timer = Assert.Single(_clock.Scheduled);
        Assert.Equal(TimeSpan.FromSeconds(5), timer.Delay);
        Assert.Equal(BannerAutoDismiss.Delay, timer.Delay);
        Assert.Equal(BannerKind.AutoDismiss, area.BannerAutoDismiss.Kind);
        Assert.True(area.BannerAutoDismiss.IsArmed);
        _clock.Fire(0);
        Assert.Null(area.BannerMessage);
        Assert.False(area.BannerAutoDismiss.IsArmed);
        await Settle(area);
    });

    [Fact]
    public Task AD2_APersistentMessage_IsNeverArmed() => InContext(async () =>
    {
        var f = await Folder("F");
        var dup = await Project("Dup");
        var area = await Area();
        await Project("Dup", f);                                                    // window B: the destination has the name now

        Assert.Equal(WorkspaceDropOutcome.Refused, await Drop(area, dup, f));       // passes the pre-check, the store refuses

        Assert.StartsWith("The destination already contains", area.BannerMessage);
        Assert.Equal(BannerKind.Persistent, area.BannerAutoDismiss.Kind);
        AssertNoTimer(area, 0);
        await Settle(area);
    });

    // ---- AD3–AD5: a new message replaces the schedule; stale callbacks never hide a newer one --------------------------------------

    [Fact]
    public Task AD3_ANewResultBeforeTheFirstExpires_HasItsOwnFiveSeconds() => InContext(async () =>
    {
        var f = await Folder("F");
        var g = await Folder("G");
        var p = await Project("P");
        var q = await Project("Q");
        var area = await Area();

        await Drop(area, p, f);
        await Drop(area, q, g);

        Assert.Equal(2, _clock.Scheduled.Count);
        Assert.True(_clock.Scheduled[0].Disposed);
        _clock.Fire(0);                                                             // the first result's timer, late
        Assert.Equal("Moved “Q” to “G”.", area.BannerMessage);
        _clock.Fire(1);
        Assert.Null(area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task AD4_AnErrorReplacesAResult_TheResultsLateTimerLeavesTheError() => InContext(async () =>
    {
        var f = await Folder("F");
        var g = await Folder("G");
        var p = await Project("P");
        var dup = await Project("Dup");
        var area = await Area();
        await Project("Dup", g);

        await Drop(area, p, f);                                                     // auto
        await Drop(area, dup, g);                                                   // refused: Persistent

        Assert.StartsWith("The destination already contains", area.BannerMessage);
        Assert.Single(_clock.Scheduled);                                            // nothing scheduled for the error
        _clock.Fire(0);
        Assert.StartsWith("The destination already contains", area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task AD5_TheSameTextAgain_IsANewTurn_TheFirstTimerCannotHideIt() => InContext(async () =>
    {
        var a = await Folder("A");
        var b = await Folder("B");
        await Folder("E", a);
        await Folder("E", b);
        var area = await Area();
        var aNode = await Expand(area, Node(area.Roots, "A"));
        var bNode = await Expand(area, Node(area.Roots, "B"));

        area.SelectedNode = Node(aNode.Children, "E");
        await area.RemoveFolderCommand.ExecuteAsync();
        Assert.Equal("Removed the empty folder “E”.", area.BannerMessage);
        var firstTurn = area.BannerAutoDismiss.Turn;
        area.SelectedNode = Node(bNode.Children, "E");
        await area.RemoveFolderCommand.ExecuteAsync();

        Assert.Equal("Removed the empty folder “E”.", area.BannerMessage);         // the same text …
        Assert.True(area.BannerAutoDismiss.Turn > firstTurn);                       // … but a new turn
        Assert.Equal(2, _clock.Scheduled.Count);
        _clock.Fire(0);
        Assert.Equal("Removed the empty folder “E”.", area.BannerMessage);
        _clock.Fire(1);
        Assert.Null(area.BannerMessage);
        await Settle(area);
    });

    // ---- AD6–AD8: visibility, Dismiss -------------------------------------------------------------------------------------------

    [Fact]
    public Task AD6_LeaveTheTabAndComeBack_ANewArm_TheStaleArmOfTheSameMessageIsIgnored() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await Area();
        await Drop(area, p, f);
        var arm1 = area.BannerAutoDismiss.Arm;

        area.IsTabSelected = false;                                                 // hidden: cancel, no timer while hidden
        Assert.True(_clock.Scheduled[0].Disposed);
        Assert.False(area.BannerAutoDismiss.IsArmed);
        Assert.Single(_clock.Scheduled);
        area.IsTabSelected = true;                                                  // shown again: a fresh 5 s
        Assert.Equal(2, _clock.Scheduled.Count);
        Assert.Equal(BannerAutoDismiss.Delay, _clock.Scheduled[1].Delay);
        Assert.True(area.BannerAutoDismiss.Arm > arm1);

        _clock.Fire(0);                                                             // arm 1, on purpose, after arm 2 exists
        Assert.Equal("Moved “P” to “F”.", area.BannerMessage);
        _clock.Fire(1);
        Assert.Null(area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task AD7_Dismiss_CancelsAtOnce_ALateTimerChangesNothing() => InContext(async () =>
    {
        var f = await Folder("F");
        var g = await Folder("G");
        var p = await Project("P");
        var q = await Project("Q");
        var area = await Area();
        await Drop(area, p, f);

        area.DismissBannerCommand.Execute(null);
        Assert.Null(area.BannerMessage);
        Assert.True(_clock.Scheduled[0].Disposed);
        _clock.Fire(0);
        Assert.Null(area.BannerMessage);

        await Drop(area, q, g);                                                     // a later message is unaffected by the old timer
        _clock.Fire(0);
        Assert.Equal("Moved “Q” to “G”.", area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task AD8_AResultWhileTheBannerIsHidden_NoTimerUntilItIsShown() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await Area(bannerVisible: false);

        await Drop(area, p, f);
        AssertNoTimer(area, 0);
        area.ReportBannerView(visible: true, pointerOver: false, focusWithin: false);

        var timer = Assert.Single(_clock.Scheduled);
        Assert.Equal(BannerAutoDismiss.Delay, timer.Delay);
        _clock.Fire(0);
        Assert.Null(area.BannerMessage);
        await Settle(area);
    });

    // ---- AD9–AD12: Undo, Details and progress keep the banner -------------------------------------------------------------------

    [Fact]
    public Task AD9_MoveToTrashWithUndo_IsNeverArmed() => InContext(async () =>
    {
        await Project("P");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");

        await area.TrashProjectCommand.ExecuteAsync();

        Assert.Equal("Moved “P” to the Trash.", area.BannerMessage);
        Assert.True(area.IsUndoAvailable);
        AssertNoTimer(area, 0);
        AssertFiringEverythingChangesNothing(area);
        Assert.True(area.IsUndoAvailable);
        await Settle(area);
    });

    [Theory]
    [InlineData("project")]
    [InlineData("folderArchive")]
    [InlineData("folderTrash")]
    public Task AD10_ArchiveAndFolderLifecycleWithUndo_AreNeverArmed(string what) => InContext(async () =>
    {
        await Project("P");
        await Folder("F");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, what == "project" ? "P" : "F");

        await (what switch
        {
            "project" => area.ArchiveProjectCommand.ExecuteAsync(),
            "folderArchive" => area.ArchiveFolderCommand.ExecuteAsync(),
            _ => area.TrashFolderCommand.ExecuteAsync(),
        });

        Assert.True(area.IsUndoAvailable);
        AssertNoTimer(area, 0);
        await Settle(area);
    });

    [Fact]
    public Task AD11_BulkArchiveSummaryWithDetails_AndTheBulkUndoSummary_AreNeverArmed() => InContext(async () =>
    {
        var f = await Folder("F");
        foreach (var name in new[] { "A", "B" })
            await Project(name, f);
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "F");
        _dialogs.OnBulkArchive = vm =>                                              // Active projects are not pre-ticked (as in
        {                                                                           // BulkArchiveViewModelTests.World): tick all, confirm
            vm.SelectAllCommand.Execute(null);
            return true;
        };

        await area.ArchiveProjectsInFolderCommand.ExecuteAsync();
        Assert.True(area.IsBulkDetailsAvailable);
        Assert.True(area.IsUndoAvailable);
        AssertNoTimer(area, 0);

        await area.UndoCommand.ExecuteAsync();                                      // the Bulk Undo summary carries Details too
        Assert.True(area.IsBulkDetailsAvailable);
        AssertNoTimer(area, 0);
        AssertFiringEverythingChangesNothing(area);
        await Settle(area);
    });

    [Fact]
    public Task AD12_ProgressWhileTheStoreIsHeld_NoTimer_ThenTheResultDecides() => InContext(async () =>
    {
        await Project("P");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        var gate = _treeStore.Hold("trashProject");

        var trash = area.TrashProjectCommand.ExecuteAsync();
        gate.WaitEntered();
        Assert.Equal("Moving “P” to the Trash…", area.BannerMessage);
        AssertNoTimer(area, 0);
        gate.Release();
        await trash;

        Assert.Equal("Moved “P” to the Trash.", area.BannerMessage);               // a result with Undo: still Persistent
        AssertNoTimer(area, 0);
        await Settle(area);
    });

    // ---- AD13, AD14: errors, refusals, notices and the partial failure stay -----------------------------------------------------

    [Fact]
    public Task AD13_RefusalsAndNotices_ArePersistent() => InContext(async () =>
    {
        var f = await Folder("F");
        await Project("Inside", f);
        var dup = await Project("Dup");
        var g = await Folder("G");
        var area = await Area();
        await Project("Dup", g);

        await Drop(area, dup, g);                                                   // typed refusal (DuplicateName)
        Assert.StartsWith("The destination already contains", area.BannerMessage);
        AssertNoTimer(area, 0);

        area.SelectedNode = Node(area.Roots, "F");
        await area.RemoveFolderCommand.ExecuteAsync();                              // not empty: a notice, nothing removed
        Assert.NotNull(area.BannerMessage);
        Assert.DoesNotContain("Removed the empty folder", area.BannerMessage, StringComparison.Ordinal);
        AssertNoTimer(area, 0);
        await Settle(area);
    });

    [Fact]
    public Task AD14_DropWithTheTreeNotUpdated_IsPersistent() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await Area();
        var children = _treeStore.Hold(GatedWorkspaceStore.Children(f));

        var drop = Drop(area, p, f);
        children.WaitEntered();
        children.Fail(new IOException("disk read failed (test)"));

        Assert.Equal(WorkspaceDropOutcome.MovedTreeNotUpdated, await drop);
        Assert.Equal("Moved “P” to “F”. The Workspace tree could not be updated.", area.BannerMessage);
        AssertNoTimer(area, 0);
        await Settle(area);
    });

    // ---- AD15, AD15a–e: Undo results choose AutoDismiss from the typed outcome ----------------------------------------------------

    [Fact]
    public Task AD15_UndoArchiveSucceeds_HidesAfterFiveSeconds() => InContext(async () =>
    {
        await Project("P");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        await area.ArchiveProjectCommand.ExecuteAsync();

        await area.UndoCommand.ExecuteAsync();

        Assert.StartsWith("Restored “P” to", area.BannerMessage);
        Assert.False(area.IsUndoAvailable);
        var timer = Assert.Single(_clock.Scheduled);
        Assert.Equal(BannerAutoDismiss.Delay, timer.Delay);
        _clock.Fire(0);
        Assert.Null(area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task AD15a_UndoProjectTrashRestored_HidesAfterFiveSeconds_SelectionRuleUnchanged() => InContext(async () =>
    {
        var p = await Project("P");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "P");
        await area.TrashProjectCommand.ExecuteAsync();

        await area.UndoCommand.ExecuteAsync();

        Assert.Equal("Restored “P” to Workspace (top level).", area.BannerMessage);
        Assert.Equal(p, area.SelectedNode!.Id);
        var timer = Assert.Single(_clock.Scheduled);
        Assert.Equal(BannerAutoDismiss.Delay, timer.Delay);
        _clock.Fire(0);
        Assert.Null(area.BannerMessage);
        await Settle(area);
    });

    [Theory]
    [InlineData("NotTrashed")]
    [InlineData("NameConflict")]
    [InlineData("ParentTrashed")]
    public Task AD15b_UndoProjectTrashRefused_BannerKept_NoTimer(string outcome) => InContext(async () =>
    {
        var g = await Folder("G");
        var p = outcome == "ParentTrashed" ? await Project("P", g) : await Project("P");
        var area = await Area();
        area.SelectedNode = outcome == "ParentTrashed" ? Node((await Expand(area, Node(area.Roots, "G"))).Children, "P") : Node(area.Roots, "P");
        await area.TrashProjectCommand.ExecuteAsync();
        switch (outcome)
        {
            case "NotTrashed":
                await _other.RestoreProjectAsync(p);                                // window B restored it first
                break;
            case "NameConflict":
                await Project("P");                                                 // window B: a new “P” took its place
                break;
            default:
                await OtherMarks(g, FolderLifecycleKind.Trash);                     // window B trashed its folder
                break;
        }

        await area.UndoCommand.ExecuteAsync();

        Assert.NotNull(area.BannerMessage);
        Assert.DoesNotContain("Restored “P” to", area.BannerMessage, StringComparison.Ordinal);
        AssertNoTimer(area, 0);
        AssertFiringEverythingChangesNothing(area);
        await Settle(area);
    });

    [Theory]
    [InlineData(FolderLifecycleKind.Archive, "Unarchived folder “F” to Workspace (top level).")]
    [InlineData(FolderLifecycleKind.Trash, "Restored folder “F” to Workspace (top level).")]
    public Task AD15c_UndoFolderCleared_HidesAfterFiveSeconds(FolderLifecycleKind kind, string expected) => InContext(async () =>
    {
        await Folder("F");
        var area = await Area();
        area.SelectedNode = Node(area.Roots, "F");
        await (kind == FolderLifecycleKind.Archive ? area.ArchiveFolderCommand.ExecuteAsync() : area.TrashFolderCommand.ExecuteAsync());

        await area.UndoCommand.ExecuteAsync();

        Assert.Equal(expected, area.BannerMessage);
        var timer = Assert.Single(_clock.Scheduled);
        Assert.Equal(BannerAutoDismiss.Delay, timer.Delay);
        _clock.Fire(0);
        Assert.Null(area.BannerMessage);
        await Settle(area);
    });

    [Theory]
    [InlineData("MarkedAgain")]
    [InlineData("ParentArchived")]
    public Task AD15d_UndoFolderRefused_BannerKept_NoTimer(string outcome) => InContext(async () =>
    {
        var g = await Folder("G");
        var f = outcome == "ParentArchived" ? await Folder("F", g) : await Folder("F");
        var area = await Area();
        area.SelectedNode = outcome == "ParentArchived" ? Node((await Expand(area, Node(area.Roots, "G"))).Children, "F") : Node(area.Roots, "F");
        await area.ArchiveFolderCommand.ExecuteAsync();
        if (outcome == "MarkedAgain")
        {
            await _other.ClearFolderAsync(f, FolderLifecycleKind.Archive);          // window B unarchived it …
            await OtherMarks(f, FolderLifecycleKind.Archive);                       // … and archived it again (a new event)
        }
        else
        {
            await OtherMarks(g, FolderLifecycleKind.Archive);                       // window B archived its parent
        }

        await area.UndoCommand.ExecuteAsync();

        Assert.NotNull(area.BannerMessage);
        Assert.DoesNotContain("Unarchived folder “F” to", area.BannerMessage, StringComparison.Ordinal);
        AssertNoTimer(area, 0);
        AssertFiringEverythingChangesNothing(area);
        await Settle(area);
    });

    /// <summary>
    /// A panel Restore / Unarchive while a Workspace AutoDismiss result is counting: the panel keeps its own notice, and the banner,
    /// its text and its ORIGINAL timer are untouched (not cancelled, no new timer); that timer still hides the banner.
    /// </summary>
    [Theory]
    [InlineData("RestoreProjectFromTrash", "Restored “T” to Workspace (top level).")]
    [InlineData("RestoreFolderFromTrash", "Restored folder “T” to Workspace (top level).")]
    [InlineData("UnarchiveFolderFromArchived", "Unarchived folder “T” to Workspace (top level).")]
    public Task AD15e_PanelRestoreOrUnarchive_KeepsItsNotice_TheRunningBannerTimerIsUntouched(string variant, string panelNotice) => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P");
        switch (variant)                                                            // window B prepares the panel entry "T"
        {
            case "RestoreProjectFromTrash":
                await _other.TrashProjectAsync(await Project("T"));
                break;
            case "RestoreFolderFromTrash":
                await OtherMarks(await Folder("T"), FolderLifecycleKind.Trash);
                break;
            default:
                await OtherMarks(await Folder("T"), FolderLifecycleKind.Archive);
                break;
        }

        var area = await Area();
        await Drop(area, p, f);                                                     // a valid AutoDismiss banner first
        Assert.Equal("Moved “P” to “F”.", area.BannerMessage);
        var original = Assert.Single(_clock.Scheduled);
        Assert.Equal(BannerAutoDismiss.Delay, original.Delay);
        Assert.False(original.Disposed);
        Assert.True(area.BannerAutoDismiss.IsArmed);

        string? Notice()
        {
            return variant == "UnarchiveFolderFromArchived" ? area.Archived.Notice : area.Trash.Notice;
        }

        if (variant == "UnarchiveFolderFromArchived")
        {
            await area.OpenArchivedCommand.ExecuteAsync();
            await area.Archived.CurrentLoad;
            area.Archived.SelectedEntry = area.Archived.Entries.Single(e => e.Name == "T");
            await area.Archived.UnarchiveCommand.ExecuteAsync();
        }
        else
        {
            await area.OpenTrashCommand.ExecuteAsync();
            await area.Trash.CurrentLoad;
            area.Trash.SelectedEntry = area.Trash.Entries.Single(e => e.Name == "T");
            await area.Trash.RestoreCommand.ExecuteAsync();
        }

        Assert.Equal(panelNotice, Notice());                                       // the panel's notice, as today
        Assert.Equal("Moved “P” to “F”.", area.BannerMessage);                     // the banner text is kept
        Assert.False(original.Disposed);                                            // the original timer was not cancelled …
        Assert.Single(_clock.Scheduled);                                            // … and no new timer was scheduled
        Assert.True(area.BannerAutoDismiss.IsArmed);

        _clock.Fire(0);                                                             // the original timer's own callback
        Assert.Null(area.BannerMessage);
        Assert.Equal(panelNotice, Notice());                                       // the panel notice stays
        await Settle(area);
    });

    // ---- AD16, AD16b: attention holds the countdown ---------------------------------------------------------------------------

    [Fact]
    public Task AD16_PointerOverTheBanner_Holds_AFreshArmAfterLeave_TheStaleArmIsIgnored() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await Area();
        await Drop(area, p, f);

        area.ReportBannerView(visible: true, pointerOver: true, focusWithin: false);   // at "4 s": the pointer enters
        Assert.True(_clock.Scheduled[0].Disposed);
        Assert.False(area.BannerAutoDismiss.IsArmed);
        Assert.Single(_clock.Scheduled);
        area.ReportBannerView(visible: true, pointerOver: false, focusWithin: false);  // leaves: a fresh 5 s
        Assert.Equal(2, _clock.Scheduled.Count);
        Assert.Equal(BannerAutoDismiss.Delay, _clock.Scheduled[1].Delay);

        _clock.Fire(0);                                                             // arm 1, on purpose, after arm 2 exists
        Assert.Equal("Moved “P” to “F”.", area.BannerMessage);
        _clock.Fire(1);
        Assert.Null(area.BannerMessage);
        await Settle(area);
    });

    [Fact]
    public Task AD16b_FocusStaysOnDismiss_PointerInAndOut_NoTimerUntilFocusLeavesToo() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await Area();
        await Drop(area, p, f);

        area.ReportBannerView(visible: true, pointerOver: false, focusWithin: true);   // Tab to the banner's Dismiss button
        area.ReportBannerView(visible: true, pointerOver: true, focusWithin: true);    // the pointer enters …
        area.ReportBannerView(visible: true, pointerOver: false, focusWithin: true);   // … and leaves; focus stays
        Assert.False(area.BannerAutoDismiss.IsArmed);
        Assert.Single(_clock.Scheduled);
        _clock.Fire(0);                                                             // the old arm
        Assert.Equal("Moved “P” to “F”.", area.BannerMessage);

        area.ReportBannerView(visible: true, pointerOver: false, focusWithin: false);  // focus leaves too: one fresh 5 s
        Assert.Equal(2, _clock.Scheduled.Count);
        Assert.Equal(BannerAutoDismiss.Delay, _clock.Scheduled[1].Delay);
        _clock.Fire(1);
        Assert.Null(area.BannerMessage);
        await Settle(area);
    });

    // ---- AD18: window closed ---------------------------------------------------------------------------------------------------

    [Fact]
    public Task AD18_WindowClosedWithAPendingTimer_NothingHappens() => InContext(async () =>
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await Area();
        await Drop(area, p, f);

        area.ShutdownBanners();

        Assert.True(_clock.Scheduled[0].Disposed);
        Assert.False(area.BannerAutoDismiss.IsArmed);
        _clock.Fire(0);
        Assert.Equal("Moved “P” to “F”.", area.BannerMessage);
        area.ReportBannerView(visible: true, pointerOver: false, focusWithin: false);
        Assert.Single(_clock.Scheduled);                                            // never armed again
        await Settle(area);
    });
}

/// <summary>
/// Phase 26 acceptance for the content banner (proposal Revision 2 §5.1: AD17, AD19 and the content success result of §2). The content
/// view model runs on its own, as in the Phase 7 Trash tests; the banner clock is fired by hand.
/// </summary>
public sealed class ContentBannerAutoDismissTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _other;
    private readonly TestBannerClock _clock = new();
    private readonly long _alpha;
    private readonly List<ProjectContentViewModel> _views = [];

    public ContentBannerAutoDismissTests()
    {
        _root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var launcher = new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true);
        _content = new ProjectContentService(new SqliteProjectContentStore(connections), launcher);
        _other = new ProjectContentService(new SqliteProjectContentStore(connections), launcher);
        var workspace = new WorkspaceService(new SqliteWorkspaceStore(connections));
        _alpha = workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active)).Result.Value!.Id;
    }

    public void Dispose()
    {
        Task.WhenAll(_views.SelectMany(v => new[] { v.CurrentLoad, v.CurrentTrashOperation, v.CurrentPreviewLoad, v.CurrentReconciliation }))
            .ContinueWith(_ => { }).Wait(Wait);
        _root.Dispose();
    }

    private async Task<ProjectContentViewModel> Opened()
    {
        var vm = new ProjectContentViewModel(_content, new ScriptedLinkChecker(), _dialogs, NullAppLog.Instance, new LinkCheckCache(), _alpha, "Alpha")
        {
            BannerClock = _clock,
        };
        _views.Add(vm);
        vm.ReportBannerView(visible: true, pointerOver: false, focusWithin: false);
        await vm.ActivateAsync();
        return vm;
    }

    private async Task<ProjectFolder> Folder(string name) => (await _other.CreateFolderAsync(_alpha, null, name)).Value!;

    private async Task<ProjectLink> Link(string name) =>
        (await _other.CreateLinkAsync(_alpha, null, new ProjectLinkDraft(name, ProjectLinkType.Folder, $@"C:\Work\{name}"))).Value!;

    private static ProjectContentItemViewModel Row(ProjectContentViewModel vm, string name) => vm.Items.Single(i => i.Name == name);

    /// <summary>A link moved to another folder by window B before it is trashed here: its Undo restores it there ("Restored to …").</summary>
    private async Task<ProjectContentViewModel> RestoredElsewhere()
    {
        var destination = await Folder("Destination");
        var link = await Link("Roamer");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Roamer");
        Assert.True((await _other.MoveLinkAsync(link.Id, null, destination.Id)).Moved);
        await vm.MoveToTrashCommand.ExecuteAsync();
        Assert.True(vm.HasUndo);
        Assert.Empty(_clock.Scheduled);                                             // AD19: the Trash notice (Undo) never auto-hides
        await vm.UndoCommand.ExecuteAsync();
        Assert.Equal("Restored to Alpha › Destination.", vm.BannerMessage);
        return vm;
    }

    [Fact]
    public async Task ContentRestoredElsewhere_HidesAfterFiveSeconds()
    {
        var vm = await RestoredElsewhere();

        var timer = Assert.Single(_clock.Scheduled);
        Assert.Equal(BannerAutoDismiss.Delay, timer.Delay);
        _clock.Fire(0);
        Assert.Null(vm.BannerMessage);
    }

    [Fact]
    public async Task AD17_AnotherProjectSelected_TheOldContentTimerIsIgnored()
    {
        var vm = await RestoredElsewhere();

        vm.Deactivate();                                                            // another Project selected

        Assert.True(_clock.Scheduled[0].Disposed);
        Assert.False(vm.BannerAutoDismiss.IsArmed);
        _clock.Fire(0);
        Assert.Equal("Restored to Alpha › Destination.", vm.BannerMessage);        // the deactivated view is never touched again
        vm.ReportBannerView(visible: true, pointerOver: false, focusWithin: false);
        Assert.Single(_clock.Scheduled);
    }

    [Fact]
    public async Task AD19_ContentUndoRefused_TheErrorStays_NoTimer()
    {
        await Link("Gone");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Gone");
        await vm.MoveToTrashCommand.ExecuteAsync();
        Assert.True(vm.HasUndo);
        var entry = (await _other.ListTrashAsync(_alpha)).Single();
        Assert.Equal(RestoreOutcome.Restored, (await _other.RestoreAsync(entry.Id)).Outcome);    // window B restored it first

        await vm.UndoCommand.ExecuteAsync();

        Assert.NotNull(vm.BannerMessage);
        Assert.Empty(_clock.Scheduled);
        Assert.False(vm.BannerAutoDismiss.IsArmed);
    }
}
