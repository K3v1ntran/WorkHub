using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 14 correction: the ordering of "Trash (n)" between the post-purge count reconciliation, content-listing loads and the
/// count changes of committed writes (Move to Trash, Restore). Whichever count read or committed write was ISSUED last decides
/// the shown count: an older reconciliation never overwrites a newer write or listing, an older listing never overwrites a newer
/// reconciliation, and a write whose base count is not exact ends with a fresh authoritative read.
/// <para>Gated store reads take their snapshot when called and deliver it on release; the purge is held at its own checkpoints.
/// Every body runs on a <see cref="SingleThreadContext"/>; teardown follows <see cref="ContextTeardown"/>.</para>
/// </summary>
public sealed class ProjectTrashCountOrderingTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TeardownBound = TimeSpan.FromSeconds(30);

    private readonly PurgeWorld _w = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SingleThreadContext _context = new();
    private readonly GatedProjectContentStore _store;
    private readonly List<ProjectContentViewModel> _views = [];
    private readonly List<PurgeGate> _purgeGates = [];
    private readonly List<Task> _extra = [];

    public ProjectTrashCountOrderingTests() => _store = new GatedProjectContentStore(new SqliteProjectContentStore(_w.Connections));

    public void Dispose() =>
        ContextTeardown.Run(_context, ReleaseGates, [Tracked], _w.Dispose, _w.Root.Paths.Root, TeardownBound);

    private void ReleaseGates()
    {
        _store.ReleaseAll();
        foreach (var gate in _purgeGates)
            gate.Release.Set();
    }

    private IReadOnlyList<(string Name, Task Task)> Tracked()
    {
        var tasks = new List<(string Name, Task Task)>();
        for (var i = 0; i < _views.Count; i++)
        {
            var v = _views[i];
            tasks.AddRange([($"v{i}.CurrentLoad", v.CurrentLoad), ($"v{i}.CurrentTrashOperation", v.CurrentTrashOperation), ($"v{i}.Trash.CurrentLoad", v.Trash.CurrentLoad),
                ($"v{i}.Trash.CurrentRestore", v.Trash.CurrentRestore), ($"v{i}.Trash.CurrentPurge", v.Trash.CurrentPurge), ($"v{i}.CountReconciliation", v.Trash.CurrentCountReconciliation)]);
        }

        tasks.AddRange(_extra.Select((t, i) => ($"extra{i}", t)));
        return tasks;
    }

    private Task InContext(Func<Task> body) => _context.RunAsync(body, BodyTimeout);

    private sealed class PurgeGate
    {
        public SemaphoreSlim Reached { get; } = new(0);

        public ManualResetEventSlim Release { get; } = new(false);

        public Action<string> At(string point) => reached =>
        {
            if (reached != point)
                return;
            Reached.Release();
            Release.Wait(Wait);
        };
    }

    /// <summary>A content service over the gated store whose purge is held at <paramref name="point"/> (null: not held).</summary>
    private ProjectContentService Service(string? point, out PurgeGate gate)
    {
        gate = new PurgeGate();
        _purgeGates.Add(gate);
        var purge = _w.NewPurge();
        if (point is not null)
            purge.OnCheckpoint = gate.At(point);
        return new ProjectContentService(_store, new ProcessProjectLinkLauncher(_w.Starter, _ => true, _ => true), purge);
    }

    private async Task<ProjectContentViewModel> View(ProjectContentService content, long project, string name)
    {
        var vm = new ProjectContentViewModel(content, new ScriptedLinkChecker(), _dialogs, NullAppLog.Instance, new LinkCheckCache(), project, name);
        _views.Add(vm);
        await vm.ActivateAsync();
        return vm;
    }

    private static async Task OpenTrash(ProjectContentViewModel vm)
    {
        await vm.OpenTrashCommand.ExecuteAsync();
        await vm.Trash.CurrentLoad;
    }

    private static ProjectContentItemViewModel Row(ProjectContentViewModel vm, ProjectContentItemKind kind, long id) => vm.Items.Single(i => i.Kind == kind && i.Id == id);

    private async Task<int> DbCount(long project) => (await _w.Other.ListTrashAsync(project)).Count;

    /// <summary>Starts a purge of E_L1 held at "published", then goes Back (a fresh listing that reads 9 before the commit).</summary>
    private async Task<(ProjectContentViewModel Vm, Task Running, PurgeGate Gate)> PurgeHeldThenBack(bool holdListing = false)
    {
        var content = Service("published", out var gate);
        var vm = await View(content, _w.P, "Project P");
        await OpenTrash(vm);
        vm.Trash.UpdateSelection([vm.Trash.Entries.Single(e => e.Id == _w.E_L1)]);
        var running = vm.Trash.DeletePermanentlyCommand.ExecuteAsync();
        _extra.Add(running);
        Assert.True(await gate.Reached.WaitAsync(Wait), "the purge reached publication");

        var listing = holdListing ? _store.Hold(GatedProjectContentStore.List(null)) : null;
        var back = vm.CloseTrashCommand.ExecuteAsync(); // Back: a navigation
        _extra.Add(back);
        if (listing is not null)
            listing.WaitEntered(); // the listing has read its snapshot (9) and is held
        else
        {
            await vm.CurrentLoad;
            Assert.Equal("Trash (9)", vm.TrashButtonText);
        }

        return (vm, running, gate);
    }

    // ---- 1. An older reconciliation never overwrites a newer committed write -------------------------------------------------

    [Fact]
    public Task O1_AHeldReconciliation_ReleasedAfterANewerMoveToTrash_DoesNotOverwriteIt() => InContext(async () =>
    {
        await _w.BuildAsync();
        var (vm, running, gate) = await PurgeHeldThenBack();
        var reconciliation = _store.Hold("listTrash");
        gate.Release.Set();
        await running;
        reconciliation.WaitEntered(); // the post-purge reconciliation has read 8 and is held
        var olderRead = vm.Trash.CurrentCountReconciliation;
        _extra.Add(olderRead);

        vm.SelectedItem = Row(vm, ProjectContentItemKind.Link, _w.LK);
        await vm.MoveToTrashCommand.ExecuteAsync(); // a newer committed write: the Trash now holds 9
        await vm.CurrentTrashOperation;
        Assert.True(vm.HasUndo); // the write's own Undo is unchanged
        reconciliation.Release(); // the older read (8) arrives last
        await olderRead;
        await vm.Trash.CurrentCountReconciliation;

        Assert.Equal(9, await DbCount(_w.P));
        Assert.Equal("Trash (9)", vm.TrashButtonText);
        Assert.True(vm.HasUndo);
        Assert.Null(vm.BannerMessage);
        Assert.Null(vm.Trash.Notice);
        Assert.False(vm.IsWriteBusy);
    });

    [Fact]
    public Task O1_TheSameRace_InTheUsualOrder_TheReconciliationThenTheWrite_EndsAtTheTrueCount() => InContext(async () =>
    {
        await _w.BuildAsync();
        var (vm, running, gate) = await PurgeHeldThenBack();
        gate.Release.Set();
        await running;
        await vm.Trash.CurrentCountReconciliation;
        Assert.Equal("Trash (8)", vm.TrashButtonText);

        vm.SelectedItem = Row(vm, ProjectContentItemKind.Link, _w.LK);
        await vm.MoveToTrashCommand.ExecuteAsync();
        await vm.CurrentTrashOperation;
        await vm.Trash.CurrentCountReconciliation;

        Assert.Equal("Trash (9)", vm.TrashButtonText);
        Assert.Equal(9, await DbCount(_w.P));
        Assert.True(vm.HasUndo);
    });

    [Fact]
    public Task O1_ARestoreThatCommitsAfterBack_IsCounted_TheListingNeverKeepsThePreRestoreCount() => InContext(async () =>
    {
        await _w.BuildAsync();
        var vm = await View(Service(null, out _), _w.P, "Project P");
        await OpenTrash(vm);
        vm.Trash.SelectedEntry = vm.Trash.Entries.Single(e => e.Id == _w.E_L1);
        var restore = _store.Hold("restore");
        var restoring = vm.Trash.RestoreCommand.ExecuteAsync();
        _extra.Add(restoring);
        restore.WaitEntered();

        await vm.CloseTrashCommand.ExecuteAsync(); // Back while the Restore is pending: the listing reads 9
        await vm.CurrentLoad;
        Assert.Equal("Trash (9)", vm.TrashButtonText);
        var selection = vm.SelectedItem = Row(vm, ProjectContentItemKind.Note, _w.NK);
        restore.Release(); // the Restore commits after the listing was read
        await restoring;
        await vm.Trash.CurrentRestore;
        await vm.Trash.CurrentCountReconciliation;

        Assert.Equal(8, await DbCount(_w.P));
        Assert.Equal("Trash (8)", vm.TrashButtonText);
        Assert.Same(selection, vm.SelectedItem);
        Assert.Null(vm.BannerMessage);
        Assert.Null(vm.Trash.Notice);
        Assert.False(vm.IsWriteBusy);
    });

    // ---- 2. An older listing never overwrites a newer reconciliation ----------------------------------------------------------

    [Fact]
    public Task O2_AListingReadBeforeTheCommit_DeliveredAfterTheReconciliation_DoesNotOverwriteTheCount() => InContext(async () =>
    {
        await _w.BuildAsync();
        var (vm, running, gate) = await PurgeHeldThenBack(holdListing: true); // the listing read 9 and is held
        gate.Release.Set();
        await running;
        await vm.Trash.CurrentCountReconciliation; // post-purge: 8
        Assert.Equal("Trash (8)", vm.TrashButtonText);

        _store.ReleaseAll(); // the pre-purge listing arrives last
        await vm.CurrentLoad;

        Assert.Equal(8, await DbCount(_w.P));
        Assert.Equal("Trash (8)", vm.TrashButtonText);
        Assert.Contains(vm.Items, i => i.Kind == ProjectContentItemKind.Folder && i.Id == _w.FK); // the listing itself is shown
        Assert.Null(vm.LoadError);
        Assert.Null(vm.Trash.Notice);
    });

    [Fact]
    public Task O2_ReverseOrder_TheListingFirst_ThenTheReconciliation_EndsAtTheTrueCount() => InContext(async () =>
    {
        await _w.BuildAsync();
        var (vm, running, gate) = await PurgeHeldThenBack(holdListing: true);
        _store.ReleaseAll();
        await vm.CurrentLoad;
        Assert.Equal("Trash (9)", vm.TrashButtonText);
        gate.Release.Set();
        await running;
        await vm.Trash.CurrentCountReconciliation;

        Assert.Equal("Trash (8)", vm.TrashButtonText);
    });

    // ---- 3. Navigation, writes elsewhere in the Project, deactivation ---------------------------------------------------------

    [Fact]
    public Task O3_NavigatingIntoAFolderAndWriting_WhileTheReconciliationIsHeld_KeepsTheNewerCountAndTheSelection() => InContext(async () =>
    {
        await _w.BuildAsync();
        var (vm, running, gate) = await PurgeHeldThenBack();
        var reconciliation = _store.Hold("listTrash");
        gate.Release.Set();
        await running;
        reconciliation.WaitEntered(); // read 8, held
        var olderRead = vm.Trash.CurrentCountReconciliation;
        _extra.Add(olderRead);

        await vm.NavigateAsync(_w.FK); // a newer listing: 8
        Assert.Equal("Trash (8)", vm.TrashButtonText);
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Link, _w.FKLink);
        await vm.MoveToTrashCommand.ExecuteAsync(); // 9
        await vm.CurrentTrashOperation;
        var selection = vm.SelectedItem = Row(vm, ProjectContentItemKind.Note, _w.X);
        reconciliation.Release();
        await olderRead;
        await vm.Trash.CurrentCountReconciliation;

        Assert.Equal("Trash (9)", vm.TrashButtonText);
        Assert.Same(selection, vm.SelectedItem); // an unrelated selection is never replaced
        Assert.Equal(_w.FK, vm.CurrentFolderId);
        Assert.True(vm.HasUndo);

        await vm.GoUpCommand.ExecuteAsync();
        await vm.CurrentLoad;
        Assert.Equal("Trash (9)", vm.TrashButtonText);
        Assert.Equal(9, await DbCount(_w.P));
    });

    [Fact]
    public Task O3_DeactivatedWhileTheReconciliationIsHeld_TheOldViewKeepsItsCount_TheNextVisitsShowTheirOwnTruth() => InContext(async () =>
    {
        await _w.BuildAsync();
        var (vm, running, gate) = await PurgeHeldThenBack();
        var reconciliation = _store.Hold("listTrash");
        gate.Release.Set();
        await running;
        reconciliation.WaitEntered();
        var olderRead = vm.Trash.CurrentCountReconciliation;
        _extra.Add(olderRead);

        vm.Deactivate(); // another Project selected
        reconciliation.Release();
        await olderRead;
        Assert.Equal("Trash (9)", vm.TrashButtonText); // a deactivated view applies nothing
        Assert.Null(vm.Trash.Notice);
        Assert.Null(vm.BannerMessage);

        var q = await View(Service(null, out _), _w.Q, "Project Q");
        Assert.Equal("Trash (2)", q.TrashButtonText);
        Assert.Null(q.BannerMessage);
        await OpenTrash(q);
        Assert.Null(q.Trash.Notice); // no purge notice leaks into another Project

        var again = await View(Service(null, out _), _w.P, "Project P"); // back to P: a new visit
        Assert.Equal("Trash (8)", again.TrashButtonText);
        Assert.Null(again.BannerMessage);
        Assert.Equal(8, await DbCount(_w.P));
    });

    [Fact]
    public Task O3_DeactivatedBeforeTheCommit_NoReconciliationRuns_TheDatabaseResultStands() => InContext(async () =>
    {
        await _w.BuildAsync();
        var (vm, running, gate) = await PurgeHeldThenBack();
        vm.Deactivate();
        gate.Release.Set();
        await running;
        await vm.Trash.CurrentCountReconciliation;

        Assert.Equal("Trash (9)", vm.TrashButtonText);
        Assert.Null(vm.Trash.Notice);
        Assert.Equal(8, await DbCount(_w.P));
        Assert.False(vm.IsWriteBusy);
    });
}
