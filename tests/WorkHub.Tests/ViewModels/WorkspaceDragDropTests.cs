using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 25 R1 acceptance (proposal Revision 2 + R2a §2.1, §4.1: DD1–DD9, DD8a–f, DH1–DH5). Window A is the Workspace area on a
/// gated tree store (every store call counted; "move", "children:{id}" and "nodeState" can be held or failed); window B is a second
/// service on the same SQLite test database. A drop is driven through the view model's drag-session API, exactly as the window's
/// handlers call it; the hover-expand clock is a test clock fired by hand. Gates only, no sleeps. Fake names only.
/// </summary>
public sealed class WorkspaceDragDropTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedWorkspaceStore _treeStore;
    private readonly WorkspaceService _other;
    private readonly List<WorkspaceAreaViewModel> _areas = [];
    private readonly TestHoverClock _clock = new();

    public WorkspaceDragDropTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _treeStore = new GatedWorkspaceStore(new SqliteWorkspaceStore(_connections));
        _other = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose()
    {
        _treeStore.ReleaseAll();
        foreach (var area in _areas)
        {
            Task.WhenAll(area.CurrentDetailsLoad, area.CurrentDrop, area.CurrentReconciliation, area.CurrentLifecycleOperation)
                .ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(10));
        }

        _root.Dispose();
    }

    private async Task<WorkspaceAreaViewModel> AreaAsync()
    {
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance,
            search: new WorkspaceSearchService(new SqliteWorkspaceSearchStore(_connections, WorkspaceSearchStoreOptions.Default)), searchDelayMs: 0)
        {
            HoverClock = _clock,
        };
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        _treeStore.ResetCalls();
        return area;
    }

    private async Task<long> Folder(string name, long? parent = null) => (await _other.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null) =>
        (await _other.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) => nodes.Single(n => !n.IsPlaceholder && n.Name == name);

    private static List<string> Names(IEnumerable<WorkspaceNodeViewModel> nodes) => nodes.Where(n => !n.IsPlaceholder).Select(n => n.Name).ToList();

    private static async Task Expand(WorkspaceAreaViewModel area, WorkspaceNodeViewModel folder)
    {
        folder.IsExpanded = true;
        Assert.True(await area.EnsureChildrenLoadedAsync(folder));
    }

    private int Moves => _treeStore.Calls("move");

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private long? ParentInDatabase(long nodeId)
    {
        using var connection = _root.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ParentId FROM WorkspaceNodes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", nodeId);
        return command.ExecuteScalar() is long parent ? parent : null;
    }

    private async Task MarkFolder(long folder, FolderLifecycleKind kind)
    {
        var token = (await _other.PrepareFolderLifecycleAsync(folder, kind)).Confirmation!.Token;
        Assert.Equal(FolderLifecycleOutcome.Committed, (await _other.MarkFolderAsync(folder, kind, token)).Outcome);
    }

    private static int Session(WorkspaceAreaViewModel area, long sourceId) =>
        area.BeginDragSession(sourceId) ?? throw new InvalidOperationException("the node could not be dragged");

    // ---- DD1, DD2: accepted drops ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DD1_DropProjectOnFolder_MovesIt_ExpandsTheFolder_SelectsIt_ResultBanner()
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await AreaAsync();
        var s = Session(area, p);

        Assert.True(area.CanDrop(s, p, f));
        Assert.Equal(WorkspaceDropOutcome.Moved, await area.MoveByDropAsync(s, p, f));

        Assert.Equal(1, Moves);
        Assert.Equal(f, ParentInDatabase(p));
        var folder = Node(area.Roots, "F");
        Assert.True(folder.IsExpanded);
        Assert.Equal(["P"], Names(folder.Children));
        Assert.Equal(p, area.SelectedNode!.Id);
        Assert.Equal("Moved “P” to “F”.", area.BannerMessage);
        Assert.False(area.IsLifecycleBusy);
    }

    [Fact]
    public async Task DD2_DropFolderOnTheRootZone_MovesItToTheTopLevel()
    {
        var b = await Folder("B");
        var a = await Folder("A", b);
        var area = await AreaAsync();
        await Expand(area, Node(area.Roots, "B"));
        var s = Session(area, a);

        Assert.True(area.CanDrop(s, a, null));
        Assert.Equal(WorkspaceDropOutcome.Moved, await area.MoveByDropAsync(s, a, null));

        Assert.Equal(1, Moves);
        Assert.Null(ParentInDatabase(a));
        Assert.Equal(["A", "B"], Names(area.Roots));                      // tree order
        Assert.Empty(Names(Node(area.Roots, "B").Children));
        Assert.Equal("Moved “A” to the top level.", area.BannerMessage);
    }

    // ---- DD3, DD5: the pre-check refuses without a store call --------------------------------------------------------------------

    [Fact]
    public async Task DD3_OnItselfOrALoadedDescendant_PreCheckRefuses_NoStoreCall()
    {
        var a = await Folder("A");
        var c = await Folder("C", a);
        var area = await AreaAsync();
        await Expand(area, Node(area.Roots, "A"));
        var s = Session(area, a);
        var banner = area.BannerMessage;

        Assert.False(area.CanDrop(s, a, a));
        Assert.False(area.CanDrop(s, a, c));
        Assert.Equal(WorkspaceDropOutcome.Rejected, await area.MoveByDropAsync(s, a, a));
        Assert.Equal(WorkspaceDropOutcome.Rejected, await area.MoveByDropAsync(s, a, c));

        Assert.Equal(0, Moves);
        Assert.Equal(banner, area.BannerMessage);
        Assert.False(area.IsLifecycleBusy);
    }

    [Fact]
    public async Task DD5_OnItsCurrentParent_OrTopLevelAlreadyThere_Refused_NoWrite_NoBanner()
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var top = await Project("Top");
        var area = await AreaAsync();
        await Expand(area, Node(area.Roots, "F"));

        var s = Session(area, p);
        Assert.False(area.CanDrop(s, p, f));
        Assert.Equal(WorkspaceDropOutcome.Rejected, await area.MoveByDropAsync(s, p, f));
        area.EndDragSession(s);
        var s2 = Session(area, top);
        Assert.False(area.CanDrop(s2, top, null));
        Assert.Equal(WorkspaceDropOutcome.Rejected, await area.MoveByDropAsync(s2, top, null));

        Assert.Equal(0, Moves);
        Assert.Null(area.BannerMessage);
    }

    [Fact]
    public async Task DD3_AProjectRowIsNotATarget_AndAnotherSessionIsRejected()
    {
        var p = await Project("P");
        var q = await Project("Q");
        var f = await Folder("F");
        var area = await AreaAsync();
        var s = Session(area, p);

        Assert.False(area.CanDrop(s, p, q));                               // D2: a Project row is not a target
        Assert.False(area.CanDrop(s + 1, p, f));                            // not the current session
        Assert.False(area.CanDrop(null, p, f));
        area.EndDragSession(s);
        Assert.False(area.CanDrop(s, p, f));                                // the session ended
        Assert.Equal(WorkspaceDropOutcome.Rejected, await area.MoveByDropAsync(s, p, f));
        Assert.Equal(0, Moves);
    }

    // ---- DD4: the loaded tree is stale; the store returns MoveCycle; the target is reconciled ------------------------------------

    [Fact]
    public async Task DD4_StaleTreeAllowsTheDrop_StoreRefusesWithMoveCycle_NothingWritten_TargetReconciled()
    {
        var sId = await Folder("S");
        var tId = await Folder("T");
        var area = await AreaAsync();
        await Expand(area, Node(area.Roots, "S"));                          // S loaded and expanded (empty)
        Assert.True((await _other.MoveAsync(tId, sId)).Value);              // window B: T below S; A's tree still shows T at the top
        Assert.Equal(["S", "T"], Names(area.Roots));
        var before = Data();
        var session = Session(area, sId);

        Assert.True(area.CanDrop(session, sId, tId));                       // A's pre-check allows (T is not a loaded descendant of S)
        Assert.Equal(WorkspaceDropOutcome.Refused, await area.MoveByDropAsync(session, sId, tId));

        Assert.Equal(1, Moves);
        Assert.Equal(before, Data());                                       // nothing written by the drop
        Assert.Equal("A folder cannot be moved into itself or into one of its own subfolders.", area.BannerMessage);
        Assert.Equal(["S"], Names(area.Roots));                             // T left its stale place …
        Assert.Equal(["T"], Names(Node(area.Roots, "S").Children));         // … and is placed where it is now
        Assert.Null(ParentInDatabase(sId));                                 // S stays where it was
        Assert.False(area.IsLifecycleBusy);
    }

    // ---- DD6, DD7: typed refusals -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DD6_NameClashAtTheDestination_RefusalBanner_NothingMoved()
    {
        var f = await Folder("F");
        var alpha = await Project("Alpha");
        var area = await AreaAsync();
        await Project("Alpha", f);                                          // window B, after A loaded
        var s = Session(area, alpha);

        Assert.Equal(WorkspaceDropOutcome.Refused, await area.MoveByDropAsync(s, alpha, f));

        Assert.StartsWith("The destination already contains", area.BannerMessage);
        Assert.Null(ParentInDatabase(alpha));
        Assert.Contains("Alpha", Names(area.Roots));
        Assert.False(area.IsLifecycleBusy);
    }

    [Fact]
    public async Task DD7_ArchivedSource_AndArchivedTarget_TypedRefusal_AndTheMoveToReconciliation()
    {
        var f = await Folder("F");
        var g = await Folder("G");
        var p = await Project("P");
        var q = await Project("Q");
        var area = await AreaAsync();
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(p)).Outcome); // window B archives the source
        await MarkFolder(g, FolderLifecycleKind.Archive);                                         // … and the other target

        var s = Session(area, p);
        Assert.Equal(WorkspaceDropOutcome.Refused, await area.MoveByDropAsync(s, p, f));
        // The refusal is shown first; the reconcile that follows (as after Move to…) may replace it with its own archived notice.
        Assert.NotNull(area.BannerMessage);
        Assert.DoesNotContain("P", Names(area.Roots));                     // the source was reconciled out of the tree
        area.EndDragSession(s);

        var s2 = Session(area, q);
        Assert.Equal(WorkspaceDropOutcome.Refused, await area.MoveByDropAsync(s2, q, g));
        Assert.DoesNotContain("G", Names(area.Roots));                     // the destination was reconciled out of the tree
        Assert.Null(ParentInDatabase(q));
        Assert.Equal(2, Moves);
        Assert.False(area.IsLifecycleBusy);
    }

    // ---- DD8a–f: the write slot and the separated outcomes -------------------------------------------------------------------------

    [Fact]
    public async Task DD8a_SlotHeldByAnotherWrite_DropRejected_BannerAndUndoUnchanged()
    {
        var f = await Folder("F");
        var p = await Project("P");
        await Project("P2");
        var area = await AreaAsync();
        area.SelectedNode = Node(area.Roots, "P2");
        await area.TrashProjectCommand.ExecuteAsync();
        await area.CurrentLifecycleOperation;
        Assert.True(area.IsUndoAvailable);
        var banner = area.BannerMessage;
        var s = Session(area, p);
        var release = area.HoldLifecycleSlotForTests();
        try
        {
            Assert.Equal(WorkspaceDropOutcome.Rejected, await area.MoveByDropAsync(s, p, f));
            Assert.Equal(0, Moves);
            Assert.Equal(banner, area.BannerMessage);
            Assert.True(area.IsUndoAvailable);                              // a rejected drop never withdraws Undo
        }
        finally
        {
            release();
        }
    }

    [Fact]
    public async Task DD8b_DD8c_OneMoveWhileHeld_TheSecondDropIsRejected_SlotReleasedAfterTheApply()
    {
        var f = await Folder("F");
        var p = await Project("P");
        var q = await Project("Q");
        var area = await AreaAsync();
        var s = Session(area, p);
        var gate = _treeStore.Hold("move");

        var first = area.MoveByDropAsync(s, p, f);
        gate.WaitEntered();
        Assert.True(area.IsLifecycleBusy);                                  // taken before the first await
        var banner = area.BannerMessage;
        var second = area.MoveByDropAsync(s, q, f);
        Assert.True(second.IsCompleted);
        Assert.Equal(WorkspaceDropOutcome.Rejected, await second);          // rejected before the store
        Assert.Equal(1, Moves);
        Assert.Equal(banner, area.BannerMessage);

        gate.Release();                                                     // DD8c
        Assert.Equal(WorkspaceDropOutcome.Moved, await first);
        Assert.False(area.IsLifecycleBusy);
        Assert.Equal(["P"], Names(Node(area.Roots, "F").Children));
        Assert.Equal(WorkspaceDropOutcome.Moved, await area.MoveByDropAsync(s, q, f)); // a new drop is accepted
        Assert.Equal(2, Moves);
    }

    [Fact]
    public async Task DD8d_StoreRefuses_RefusalBannerKept_EvenWhenTheReconcileFails_SlotHeldUntilItEnds()
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await AreaAsync();
        await MarkFolder(f, FolderLifecycleKind.Trash);                     // window B: the destination is no longer live
        var expected = (await _other.MoveAsync(p, f)).Error;                // the same refusal, written by nobody
        var before = Data();
        var s = Session(area, p);
        var reconcile = _treeStore.Hold("nodeState");

        var drop = area.MoveByDropAsync(s, p, f);
        reconcile.WaitEntered();                                            // the reconcile read after the refusal
        Assert.True(area.IsLifecycleBusy);                                  // still held during the reconcile
        Assert.Equal(expected, area.BannerMessage);
        reconcile.Fail(new IOException("disk read failed (test)"));

        Assert.Equal(WorkspaceDropOutcome.Refused, await drop);
        Assert.Equal(expected, area.BannerMessage);                         // the refusal stays the banner
        Assert.Equal(before, Data());
        Assert.Equal(1, Moves);
        Assert.False(area.IsLifecycleBusy);
    }

    [Fact]
    public async Task DD8e_WriteFailsBeforeCommit_NothingWasChanged_NoTreeUpdate_SlotReleased()
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await AreaAsync();
        var before = Data();
        var s = Session(area, p);
        var gate = _treeStore.Hold("move");

        var drop = area.MoveByDropAsync(s, p, f);
        gate.WaitEntered();
        gate.Fail(new IOException("disk write failed (test)"));            // the store call throws; nothing commits

        Assert.Equal(WorkspaceDropOutcome.WriteFailed, await drop);
        Assert.Equal(before, Data());
        Assert.Null(ParentInDatabase(p));
        Assert.Equal(1, Moves);
        Assert.Equal("Could not move “P”. Nothing was changed.", area.BannerMessage);
        Assert.Contains("P", Names(area.Roots));                           // no tree update
        Assert.Equal(0, _treeStore.Calls(GatedWorkspaceStore.Children(f))); // no destination load
        Assert.Equal(0, _treeStore.Calls("nodeState"));                    // no reconcile
        Assert.False(area.IsLifecycleBusy);
    }

    [Fact]
    public async Task DD8f_WriteCommits_TreeUpdateFails_MovedButTreeNotUpdated_NoSecondMove_SlotReleasedAfter()
    {
        var f = await Folder("F");                                          // collapsed, not loaded
        var p = await Project("P");
        var area = await AreaAsync();
        var s = Session(area, p);
        var children = _treeStore.Hold(GatedWorkspaceStore.Children(f));   // the move itself is NOT held: it commits

        var drop = area.MoveByDropAsync(s, p, f);
        children.WaitEntered();                                             // ApplyMovedAsync is loading F's children

        Assert.Equal(f, ParentInDatabase(p));                               // committed before the tree update
        Assert.True(area.IsLifecycleBusy);                                  // the slot is still held
        Assert.Null(area.BannerMessage);                                    // no banner yet
        children.Fail(new IOException("disk read failed (test)"));

        Assert.Equal(WorkspaceDropOutcome.MovedTreeNotUpdated, await drop);
        Assert.Equal(1, Moves);                                             // never moved again
        Assert.Equal(f, ParentInDatabase(p));
        Assert.Equal("Moved “P” to “F”. The Workspace tree could not be updated.", area.BannerMessage);
        Assert.DoesNotContain("Nothing was changed", area.BannerMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("P", Names(area.Roots));                     // not shown at its old place
        Assert.Null(area.CachedNode(p));
        Assert.False(area.IsLifecycleBusy);
        Assert.True(area.NewFolderCommand.CanExecute(null));                // CanWrite() again
    }

    [Fact]
    public async Task DD8f_TopLevelForm_OfTheTreeNotUpdatedBanner()
    {
        Assert.Equal("Moved “P” to the top level. The Workspace tree could not be updated.",
            WorkspaceAreaViewModel.MovedText("P", null) + WorkspaceAreaViewModel.TreeNotUpdatedNote);
        await Task.CompletedTask;
    }

    // ---- DD9: search replaces the tree ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task DD9_WhileSearchReplacesTheTree_NothingIsDraggable_AndADropIsRejected()
    {
        var f = await Folder("F");
        var p = await Project("P");
        var area = await AreaAsync();
        var s = Session(area, p);

        area.Search!.SearchText = "P";

        Assert.False(area.CanStartDrag(Node(area.Roots, "P")));
        Assert.Null(area.BeginDragSession(p));
        Assert.False(area.CanDrop(s, p, f));
        Assert.Equal(WorkspaceDropOutcome.Rejected, await area.MoveByDropAsync(s, p, f));
        Assert.Equal(0, Moves);
    }

    // ---- DH1–DH5: hover-expand (D5(a)) ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task DH1_HoverOnACollapsedFolder_ExpandsItWhenTheClockFires()
    {
        await Folder("F1");
        var p = await Project("P");
        var area = await AreaAsync();
        var f1 = Node(area.Roots, "F1");
        var s = Session(area, p);

        area.DragHoverEnter(s, f1.Id);
        area.DragHoverEnter(s, f1.Id);                                      // the same pair keeps the pending timer
        Assert.Single(_clock.Scheduled);
        Assert.Equal(WorkspaceAreaViewModel.HoverExpandDelay, _clock.Scheduled[0].Delay);
        _clock.Fire(0);

        Assert.True(f1.IsExpanded);
        await area.EnsureChildrenLoadedAsync(f1);
    }

    [Fact]
    public async Task DH2_LeavingForAnotherFolderBeforeExpiry_CancelsTheFirst()
    {
        await Folder("F1");
        await Folder("F2");
        var p = await Project("P");
        var area = await AreaAsync();
        var (f1, f2) = (Node(area.Roots, "F1"), Node(area.Roots, "F2"));
        var s = Session(area, p);

        area.DragHoverEnter(s, f1.Id);
        area.DragHoverEnter(s, f2.Id);
        Assert.True(_clock.Scheduled[0].Disposed);
        _clock.Fire(0);                                                     // a late F1 timer
        Assert.False(f1.IsExpanded);
        _clock.Fire(1);
        Assert.True(f2.IsExpanded);
        await area.EnsureChildrenLoadedAsync(f2);
    }

    [Fact]
    public async Task DH3_DropCancelOrSearch_CancelThePendingHover()
    {
        await Folder("F1");
        await Folder("F2");
        await Folder("F3");
        var target = await Folder("T");
        var p = await Project("P");
        var area = await AreaAsync();

        var s1 = Session(area, p);                                          // drop
        area.DragHoverEnter(s1, Node(area.Roots, "F1").Id);
        Assert.Equal(WorkspaceDropOutcome.Moved, await area.MoveByDropAsync(s1, p, target));
        _clock.Fire(0);
        Assert.False(Node(area.Roots, "F1").IsExpanded);
        area.EndDragSession(s1);

        var t = Node(area.Roots, "T");
        var moved = Node(t.Children, "P");
        var s2 = Session(area, moved.Id);                                   // drag cancelled (session ends)
        area.DragHoverEnter(s2, Node(area.Roots, "F2").Id);
        area.EndDragSession(s2);
        _clock.Fire(1);
        Assert.False(Node(area.Roots, "F2").IsExpanded);

        var s3 = Session(area, moved.Id);                                   // search replaces the tree
        area.DragHoverEnter(s3, Node(area.Roots, "F3").Id);
        area.Search!.SearchText = "x";
        Assert.True(_clock.Scheduled[2].Disposed);
        _clock.Fire(2);
        Assert.False(Node(area.Roots, "F3").IsExpanded);
    }

    [Fact]
    public async Task DH4_ALateTimerOfAnEndedSession_NeverExpands_EvenWhenAnotherFolderReusesTheRow()
    {
        await Folder("F1");
        await Folder("F2");
        var p = await Project("P");
        var area = await AreaAsync();
        var (f1, f2) = (Node(area.Roots, "F1"), Node(area.Roots, "F2"));

        var s1 = Session(area, p);
        area.DragHoverEnter(s1, f1.Id);
        area.EndDragSession(s1);
        var s2 = Session(area, p);
        area.DragHoverEnter(s2, f2.Id);                                     // the row container may now show F2: keys are Ids
        _clock.Fire(0);                                                     // session 1's timer, late

        Assert.False(f1.IsExpanded);
        Assert.False(f2.IsExpanded);                                        // only its own timer may expand F2
        _clock.Fire(1);
        Assert.True(f2.IsExpanded);
        await area.EnsureChildrenLoadedAsync(f2);
    }

    [Fact]
    public async Task DH5_FolderTrashedAndReconciledBeforeExpiry_NothingExpands()
    {
        var f1Id = await Folder("F1");
        var p = await Project("P");
        var area = await AreaAsync();
        var f1 = Node(area.Roots, "F1");
        var s = Session(area, p);
        area.DragHoverEnter(s, f1Id);

        await MarkFolder(f1Id, FolderLifecycleKind.Trash);                  // window B
        await area.ReconcileNodeAsync(f1Id);                                // A learns it
        Assert.Null(area.CachedNode(f1Id));
        _clock.Fire(0);

        Assert.False(f1.IsExpanded);
        Assert.DoesNotContain("F1", Names(area.Roots));
    }

    /// <summary>A hand-fired clock: every scheduled callback is kept (also after it was cancelled) so a late fire can be tested.</summary>
    private sealed class TestHoverClock : IDragHoverClock
    {
        public List<Entry> Scheduled { get; } = [];

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var entry = new Entry(delay, callback);
            Scheduled.Add(entry);
            return entry;
        }

        /// <summary>Runs the callback even if it was cancelled: the view model's own checks must ignore a late one.</summary>
        public void Fire(int index) => Scheduled[index].Callback();

        public sealed class Entry(TimeSpan delay, Action callback) : IDisposable
        {
            public TimeSpan Delay { get; } = delay;

            public Action Callback { get; } = callback;

            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }
    }
}
