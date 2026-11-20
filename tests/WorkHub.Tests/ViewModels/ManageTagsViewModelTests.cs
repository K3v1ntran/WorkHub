using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 21 "Manage tags…" view model and its Workspace entry (proposal Revision 2 + R2a §3.1, §4.1 TR7, §4.2 TR9-TR14 VM level).
/// Window A = the dialog on service A (a gated store: held reads take their snapshot when called and deliver on release; every
/// apply call is recorded); window B = service B on its own store, same real SQLite test database. Gates only, no sleeps.
/// </summary>
public sealed class ManageTagsViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly GatedWorkspaceStore _gated;
    private readonly WorkspaceService _a;
    private readonly WorkspaceService _b;
    private readonly FakeDialogs _dialogs = new();
    private readonly List<WorkspaceAreaViewModel> _areas = [];
    private readonly SingleThreadContext _context = new();
    private long _p1, _p2, _p3;
    private int _slotTaken, _slotReleased;

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ModalTimeout = TimeSpan.FromSeconds(10);
    private static readonly string ClientA = ProjectTagName.Key("clientA");

    public ManageTagsViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _gated = new GatedWorkspaceStore(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _a = new WorkspaceService(_gated);
        _b = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose() =>
        ContextTeardown.Run(_context, _gated.ReleaseAll,
            _areas.Select((area, i) => (ContextTeardown.TrackedSet)(() => [($"area{i}.CurrentDetailsLoad", area.CurrentDetailsLoad)])).ToList(),
            _root.Dispose, _root.Paths.Root, TimeSpan.FromSeconds(30));

    private async Task SeedAsync()
    {
        _p1 = (await _b.CreateProjectAsync(null, new ProjectDraft("P1", ProjectStatus.Active, null, null, null, ["clientA"]))).Value!.Id;
        _p2 = (await _b.CreateProjectAsync(null, new ProjectDraft("P2", ProjectStatus.Active, null, null, null, ["clientA", "Ops"]))).Value!.Id;
        _p3 = (await _b.CreateProjectAsync(null, new ProjectDraft("P3", ProjectStatus.Active))).Value!.Id;
    }

    private ManageTagsViewModel Dialog(Func<bool>? tryBegin = null) =>
        new(_a, tryBegin ?? (() => { _slotTaken++; return true; }), () => _slotReleased++, NullAppLog.Instance);

    private static async Task<ManageTagsViewModel> Select(ManageTagsViewModel vm, string key)
    {
        await vm.LoadAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.NameKey == key);
        return vm;
    }

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private int Applies => _gated.ApplyTagChangeCalls.Count;

    [Fact]
    public async Task TR1_VM_TheListShowsSpellingAndCounts_TheFilterNarrowsIt()
    {
        await SeedAsync();
        var vm = Dialog();
        await vm.LoadAsync();
        Assert.Equal(["clientA", "Ops"], vm.Rows.Select(r => r.Spelling));
        Assert.Equal("2 projects", vm.Rows[0].CountText);
        vm.Filter = "op";
        Assert.Equal(["Ops"], vm.FilteredRows.Select(r => r.Spelling));
    }

    [Fact]
    public async Task Preview_IsRequiredBeforeConfirm_AndAnyInputChangeDiscardsIt()
    {
        await SeedAsync();
        var vm = await Select(Dialog(), ClientA);
        vm.StartRenameCommand.Execute(null);
        Assert.Equal("clientA", vm.NewName);
        vm.NewName = "ClientX";
        Assert.False(vm.CanConfirm);                                      // no preview yet

        await vm.PreviewCommand.ExecuteAsync();
        Assert.True(vm.CanConfirm);
        Assert.Equal("Rename “clientA” to “ClientX” in 2 projects." + ManageTagsViewModel.CannotUndo, vm.PreviewText);

        vm.NewName = "ClientY";                                           // the input changed: the preview is discarded
        Assert.Null(vm.Preview);
        Assert.False(vm.CanConfirm);
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(0, Applies);
    }

    [Fact]
    public async Task ALatePreviewOfAnOlderInput_IsDropped()
    {
        await SeedAsync();
        var vm = await Select(Dialog(), ClientA);
        vm.StartRenameCommand.Execute(null);
        vm.NewName = "ClientX";
        var gate = _gated.Hold("prepareTagChange");
        var preview = vm.PreviewCommand.ExecuteAsync();
        gate.WaitEntered();
        vm.NewName = "ClientZ";                                           // the user keeps typing while the preview is read
        gate.Release();
        await preview;

        Assert.Null(vm.Preview);                                          // the "ClientX" plan never enables Confirm for "ClientZ"
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task TR9_VM_Stale_ShowsTheFreshPreview_OneApplyPerClick_NeverReplayed_ANewClickApplies()
    {
        await SeedAsync();
        var vm = await Select(Dialog(), ClientA);
        vm.StartRenameCommand.Execute(null);
        vm.NewName = "ClientX";
        await vm.PreviewCommand.ExecuteAsync();
        var old = vm.Preview!;
        Assert.True((await _b.UpdateProjectAsync(_p3, new ProjectDraft("P3", ProjectStatus.Active, null, null, null, ["clientA"]))).Succeeded); // B
        var afterB = Data();

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(1, Applies);
        Assert.Equal(ManageTagsViewModel.StaleMessage, vm.Message);
        Assert.NotEqual(old.Token, vm.Preview!.Token);
        Assert.Equal(3, vm.Preview.Replaced);
        Assert.True(vm.CanConfirm);                                       // a NEW click is needed
        Assert.Equal(afterB, Data());
        Assert.False(vm.ChangedAny);

        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(2, Applies);
        Assert.True(vm.ChangedAny);
        Assert.StartsWith("Renamed “clientA” to “ClientX” in 3 projects.", vm.Message);
        Assert.Equal((2, 2), (_slotTaken, _slotReleased));
    }

    [Fact]
    public async Task TR10_VM_TheLastEligibleRowGone_StaleWithANotConfirmablePreview()
    {
        await SeedAsync();
        var vm = await Select(Dialog(), ProjectTagName.Key("Ops"));
        vm.StartRenameCommand.Execute(null);
        vm.NewName = "Operations";
        await vm.PreviewCommand.ExecuteAsync();
        Assert.True((await _b.UpdateProjectAsync(_p2, new ProjectDraft("P2", ProjectStatus.Active, null, null, null, ["clientA"]))).Succeeded);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(ManageTagsViewModel.StaleMessage, vm.Message);
        Assert.False(vm.Preview!.IsConfirmable);
        Assert.Equal(ManageTagsViewModel.NothingToRenameMessage, vm.PreviewText);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task TR11_VM_AnInvalidName_NoStoreCall()
    {
        await SeedAsync();
        var vm = await Select(Dialog(), ClientA);
        vm.StartRenameCommand.Execute(null);
        foreach (var name in new[] { new string('x', 41), "bad\u0007" })
        {
            vm.NewName = name;
            await vm.PreviewCommand.ExecuteAsync();
            Assert.NotNull(vm.NameError);
            Assert.Null(vm.Preview);
        }

        Assert.Equal(0, _gated.Calls("prepareTagChange"));
        Assert.Equal(0, Applies);
    }

    [Fact]
    public async Task TR13_CloseAndCancelChange_NeverWrite()
    {
        await SeedAsync();
        var before = Data();
        var vm = await Select(Dialog(), ClientA);
        var closed = new List<bool>();
        vm.CloseRequested += (_, changed) => closed.Add(changed);
        vm.StartMergeCommand.Execute(null);
        vm.MergeTarget = vm.MergeTargets.Single();
        await vm.PreviewCommand.ExecuteAsync();
        vm.CancelChangeCommand.Execute(null);
        Assert.Null(vm.Preview);
        vm.CloseCommand.Execute(null);

        Assert.Equal([false], closed);
        Assert.Equal(0, Applies);
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task TR14_VM_TheSlotIsTaken_ConfirmWritesNothing()
    {
        await SeedAsync();
        var vm = await Select(Dialog(() => false), ClientA);
        vm.StartRenameCommand.Execute(null);
        vm.NewName = "ClientX";
        await vm.PreviewCommand.ExecuteAsync();

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(ManageTagsViewModel.AnotherChangeRunning, vm.Message);
        Assert.Equal(0, Applies);
        Assert.Equal(0, _slotReleased);
    }

    [Fact]
    public async Task Merge_UsesTheTargetsDisplayedSpelling_AndReportsBothCounts()
    {
        await SeedAsync();
        var vm = await Select(Dialog(), ProjectTagName.Key("Ops"));
        vm.StartMergeCommand.Execute(null);
        vm.MergeTarget = vm.MergeTargets.Single(r => r.NameKey == ClientA);
        await vm.PreviewCommand.ExecuteAsync();
        Assert.Equal(TagChangeOperation.Merge, vm.Preview!.Operation);
        Assert.Equal((0, 1), (vm.Preview.Replaced, vm.Preview.Merged));

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.True(vm.ChangedAny);
        Assert.Equal(["clientA"], (await _b.GetProjectAsync(_p2))!.Tags.Select(t => t.Name));
        Assert.Equal(["clientA"], vm.Rows.Select(r => r.Spelling));       // the list was read again
    }

    // ---- the Workspace entry (area) ------------------------------------------------------------------------------------------------

    private Task InContext(Func<Task> body) => _context.RunAsync(body, BodyTimeout);

    private async Task<WorkspaceAreaViewModel> Area()
    {
        var area = new WorkspaceAreaViewModel(_a, _dialogs, NullAppLog.Instance);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        return area;
    }

    [Fact]
    public Task TR7_AfterAChange_TheSelectedProjectsDetailsReload_TreeAndSelectionUnchanged() => InContext(async () =>
    {
        await SeedAsync();
        var area = await Area();
        var node = area.Roots.Single(n => !n.IsPlaceholder && n.Name == "P1");
        area.SelectedNode = node;
        await area.CurrentDetailsLoad;
        Assert.Equal(["clientA"], area.SelectedProject!.Tags.Select(t => t.Name));
        var roots = area.Roots.Select(r => r.Name).ToList();
        _dialogs.OnManageTags = vm =>
        {
            _dialogs.WaitModal(vm.LoadAsync(), ModalTimeout);
            vm.SelectedRow = vm.Rows.Single(r => r.NameKey == ClientA);
            vm.StartRenameCommand.Execute(null);
            vm.NewName = "Client X";
            _dialogs.WaitModal(vm.PreviewCommand.ExecuteAsync(), ModalTimeout);
            _dialogs.WaitModal(vm.ConfirmCommand.ExecuteAsync(), ModalTimeout);
            Assert.True(vm.ChangedAny);
            return vm.ChangedAny;
        };

        await area.ManageTagsCommand.ExecuteAsync();

        Assert.Equal(["Client X"], area.SelectedProject!.Tags.Select(t => t.Name));
        Assert.Same(node, area.SelectedNode);
        Assert.Equal(roots, area.Roots.Select(r => r.Name));
        Assert.False(area.IsLifecycleBusy);
    });

    [Fact]
    public Task TR14_TheEntryIsDisabledWhileAnotherLifecycleWriteHoldsTheSlot_AConfirmationAfterThatWritesNothing() => InContext(async () =>
    {
        await SeedAsync();
        var area = await Area();
        Assert.True(area.ManageTagsCommand.CanExecute(null));
        var release = area.HoldLifecycleSlotForTests();
        Assert.False(area.ManageTagsCommand.CanExecute(null));
        release();

        _dialogs.OnManageTags = vm =>
        {
            _dialogs.WaitModal(vm.LoadAsync(), ModalTimeout);
            vm.SelectedRow = vm.Rows.Single(r => r.NameKey == ClientA);
            vm.StartRenameCommand.Execute(null);
            vm.NewName = "ClientX";
            _dialogs.WaitModal(vm.PreviewCommand.ExecuteAsync(), ModalTimeout);
            var hold = area.HoldLifecycleSlotForTests();                     // another lifecycle write takes the slot meanwhile
            _dialogs.WaitModal(vm.ConfirmCommand.ExecuteAsync(), ModalTimeout);
            hold();
            Assert.Equal(ManageTagsViewModel.AnotherChangeRunning, vm.Message);
            return vm.ChangedAny;
        };

        await area.ManageTagsCommand.ExecuteAsync();

        Assert.Equal(0, Applies);
        Assert.Equal(["clientA"], (await _b.GetProjectAsync(_p1))!.Tags.Select(t => t.Name));
    });
}
