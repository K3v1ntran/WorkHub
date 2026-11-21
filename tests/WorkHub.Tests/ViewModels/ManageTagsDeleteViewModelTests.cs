using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 22 "Delete everywhere…" in the Manage tags view model and its Workspace entry (proposal Revision 2 + R2a §3.1, §3.4,
/// §3.4a, §4.2 TD-VM1-TD-VM12). Window A = the dialog on service A (a gated store: held reads take their snapshot when called and
/// deliver on release; every delete apply is recorded in <see cref="GatedWorkspaceStore.ApplyTagDeleteCalls"/>, the store-side
/// Apply counter); window B = service B on its own store, same real SQLite test database. Gates only, no sleeps. Keys, focus and
/// Enter / Esc / Space are not provable here (no key input in a view model): see GUI GD5.
/// <para>
/// Fixture (§4): P1 live {clientA, Infra}; P2 live {ClientA}; P3 archived {clientA}; P4 directly in the Trash {clientA}; P5 live in
/// folder F, F trashed {clientA}; P6 live {Infra}.
/// </para>
/// </summary>
public sealed class ManageTagsDeleteViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly GatedWorkspaceStore _gated;
    private readonly WorkspaceService _a;
    private readonly WorkspaceService _b;
    private readonly FakeDialogs _dialogs = new();
    private readonly List<WorkspaceAreaViewModel> _areas = [];
    private readonly SingleThreadContext _context = new();
    private readonly long[] _p = new long[7];
    private long _f;
    private int _slotTaken, _slotReleased;

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ModalTimeout = TimeSpan.FromSeconds(10);
    private static readonly string ClientA = ProjectTagName.Key("clientA");
    private static readonly string Infra = ProjectTagName.Key("Infra");

    private const string Described = "Delete “clientA” from 3 projects. 2 projects in the Trash keep it." + ManageTagsViewModel.CannotUndoDelete;
    private const string Success = "Deleted “clientA” from 3 projects. 2 projects in the Trash keep it.";

    public ManageTagsDeleteViewModelTests()
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
        async Task<long> Project(string name, long? parent, params string[] tags) =>
            (await _b.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active, null, null, null, tags))).Value!.Id;
        _f = (await _b.CreateFolderAsync(null, "F")).Value!.Id;
        _p[1] = await Project("P1", null, "clientA", "Infra");
        _p[2] = await Project("P2", null, "ClientA");
        _p[3] = await Project("P3", null, "clientA");
        _p[4] = await Project("P4", null, "clientA");
        _p[5] = await Project("P5", _f, "clientA");
        _p[6] = await Project("P6", null, "Infra");
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _b.ArchiveProjectAsync(_p[3])).Outcome);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _b.TrashProjectAsync(_p[4])).Outcome);
        var token = (await _b.PrepareFolderLifecycleAsync(_f, FolderLifecycleKind.Trash)).Confirmation!.Token;
        Assert.Equal(FolderLifecycleOutcome.Committed, (await _b.MarkFolderAsync(_f, FolderLifecycleKind.Trash, token)).Outcome);
    }

    private ManageTagsViewModel Dialog(Func<bool>? tryBegin = null) =>
        new(_a, tryBegin ?? (() => { _slotTaken++; return true; }), () => _slotReleased++, NullAppLog.Instance);

    private static async Task<ManageTagsViewModel> Select(ManageTagsViewModel vm, string key)
    {
        await vm.LoadAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.NameKey == key);
        return vm;
    }

    /// <summary>Selects "clientA", opens Delete everywhere… and previews (Deleted 3 / Trashed 2).</summary>
    private async Task<ManageTagsViewModel> Previewed(Func<bool>? tryBegin = null)
    {
        var vm = await Select(Dialog(tryBegin), ClientA);
        vm.StartDeleteCommand.Execute(null);
        await vm.PreviewCommand.ExecuteAsync();
        Assert.Equal((3, 2), (vm.DeletePreview!.Deleted, vm.DeletePreview.Trashed));
        return vm;
    }

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private int Applies => _gated.ApplyTagDeleteCalls.Count;

    private async Task<string[]> StoredTags(long project) => (await _b.GetProjectAsync(project))!.Tags.Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();

    /// <summary>TD2 data: clientA gone from P1-P3, kept on P4 and P5, Infra untouched.</summary>
    private async Task AssertDeletedAsInTD2()
    {
        Assert.Equal(["Infra"], await StoredTags(_p[1]));
        Assert.Empty(await StoredTags(_p[2]));
        Assert.Empty(await StoredTags(_p[3]));
        Assert.Equal(["clientA"], await StoredTags(_p[4]));
        Assert.Equal(["clientA"], await StoredTags(_p[5]));
        Assert.Equal(["Infra"], await StoredTags(_p[6]));
    }

    private static void AssertNewActionsLocked(ManageTagsViewModel vm)
    {
        Assert.False(vm.PreviewCommand.CanExecute(null));
        Assert.False(vm.StartDeleteCommand.CanExecute(null));
        Assert.False(vm.StartRenameCommand.CanExecute(null));
        Assert.False(vm.StartMergeCommand.CanExecute(null));
        Assert.False(vm.CancelChangeCommand.CanExecute(null));
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.False(vm.CanConfirm);
        Assert.True(vm.CloseCommand.CanExecute(null));
    }

    // ---- TD-VM1 - TD-VM7 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TD_VM1_APreviewOfTheSelectedKeyIsRequired_AnotherRowOrModeDiscardsIt()
    {
        await SeedAsync();
        var vm = await Select(Dialog(), ClientA);
        Assert.True(vm.StartDeleteCommand.CanExecute(null));
        vm.StartDeleteCommand.Execute(null);
        Assert.True(vm.IsDeleteMode);
        Assert.False(vm.CanConfirm);                                       // no preview yet

        await vm.PreviewCommand.ExecuteAsync();
        Assert.True(vm.CanConfirm);
        Assert.Equal(Described, vm.DeletePreviewText);

        vm.StartRenameCommand.Execute(null);                               // another mode: the delete preview is discarded
        Assert.False(vm.IsDeleteMode);
        Assert.Null(vm.DeletePreview);

        vm.StartDeleteCommand.Execute(null);
        await vm.PreviewCommand.ExecuteAsync();
        vm.SelectedRow = vm.Rows.Single(r => r.NameKey == Infra);          // another row: discarded too
        Assert.False(vm.IsDeleteMode);
        Assert.Null(vm.DeletePreview);
        Assert.False(vm.CanConfirm);
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(0, Applies);
    }

    [Fact]
    public async Task TD_VM2_ALateDeletePreviewOfAnOlderSelection_IsDropped()
    {
        await SeedAsync();
        var vm = await Select(Dialog(), ClientA);
        vm.StartDeleteCommand.Execute(null);
        var gate = _gated.Hold("prepareTagDelete");
        var preview = vm.PreviewCommand.ExecuteAsync();
        gate.WaitEntered();
        vm.SelectedRow = vm.Rows.Single(r => r.NameKey == Infra);          // the user picks another tag while the preview is read
        gate.Release();
        await preview;

        Assert.Null(vm.DeletePreview);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task TD_VM3_Success_OneApply_SuccessText_PreviewInvalidated_ListReread_SlotReleased()
    {
        await SeedAsync();
        var vm = await Previewed();
        var reads = _gated.Calls("tagsInUse");

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(1, Applies);
        Assert.Equal(Success, vm.Message);
        Assert.Null(vm.DeletePreview);
        Assert.False(vm.IsDeleteMode);
        Assert.True(vm.ChangedAny);
        Assert.Equal(reads + 1, _gated.Calls("tagsInUse"));               // the list was read again
        var row = vm.Rows.Single(r => r.NameKey == ClientA);
        Assert.Equal((0, 2), (row.ChangeableProjects, row.TrashedProjects));
        Assert.False(vm.IsRefreshing);
        Assert.False(vm.RefreshFailed);
        Assert.Equal((1, 1), (_slotTaken, _slotReleased));
        await AssertDeletedAsInTD2();
    }

    [Fact]
    public async Task TD_VM4_Stale_ShowsTheFreshPreview_OneApplyUntilANewConfirmation()
    {
        await SeedAsync();
        var vm = await Previewed();
        Assert.True((await _b.UpdateProjectAsync(_p[6], new ProjectDraft("P6", ProjectStatus.Active, null, null, null, ["Infra", "clientA"]))).Succeeded);

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(1, Applies);
        Assert.Equal(ManageTagsViewModel.StaleMessage, vm.Message);
        Assert.Equal(4, vm.DeletePreview!.Deleted);                        // the fresh plan
        Assert.Equal(["Infra", "clientA"], await StoredTags(_p[6]));      // nothing written
        Assert.True(vm.CanConfirm);

        await vm.ConfirmCommand.ExecuteAsync();                             // a new deliberate confirmation

        Assert.Equal(2, Applies);
        Assert.Equal(["Infra"], await StoredTags(_p[6]));
    }

    [Fact]
    public async Task TD_VM5_TheSlotIsTaken_NoApply_NothingWritten()
    {
        await SeedAsync();
        var vm = await Previewed(() => false);
        var before = Data();

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(ManageTagsViewModel.AnotherChangeRunning, vm.Message);
        Assert.Equal(0, Applies);
        Assert.Equal(0, _slotReleased);
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task TD_VM6a_CommandLevel_AValidPreview_ExactlyOneApply()
    {
        await SeedAsync();
        var vm = await Previewed();

        await vm.ConfirmCommand.ExecuteAsync();                             // the command a click or Space on Confirm runs

        Assert.Equal(1, Applies);
        await vm.ConfirmCommand.ExecuteAsync();                             // a second command after the commit: the preview was used up
        Assert.Equal(1, Applies);
    }

    [Fact]
    public async Task TD_VM6b_CommandLevel_NoPreview_OrANotConfirmablePreview_NoApply()
    {
        await SeedAsync();
        var vm = await Select(Dialog(), ClientA);
        vm.StartDeleteCommand.Execute(null);                               // no preview
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(0, Applies);

        await vm.PreviewCommand.ExecuteAsync();
        foreach (var i in new[] { 1, 2, 3 })                                // B removes every eligible row after the preview
            Sql("DELETE FROM ProjectTags WHERE ProjectNodeId = $p AND NameKey = $k;", ("$p", _p[i]), ("$k", ClientA));

        await vm.ConfirmCommand.ExecuteAsync();                             // Stale: the fresh preview has nothing to delete
        Assert.Equal(1, Applies);
        Assert.False(vm.DeletePreview!.IsConfirmable);
        Assert.False(vm.ConfirmCommand.CanExecute(null));

        await vm.ConfirmCommand.ExecuteAsync();                             // a not-confirmable preview: no Apply
        Assert.Equal(1, Applies);
    }

    [Fact]
    public async Task TD_VM6c_CommandLevel_CloseWithAValidPreview_NoApply_AndConfirmIsDisabledWhileApplying()
    {
        await SeedAsync();
        var vm = await Previewed();
        var closed = new List<bool>();
        vm.CloseRequested += (_, changed) => closed.Add(changed);
        vm.CloseCommand.Execute(null);
        Assert.Equal([false], closed);
        Assert.Equal(0, Applies);

        var gate = _gated.Hold("applyTagDelete");
        var confirm = vm.ConfirmCommand.ExecuteAsync();
        gate.WaitEntered();
        Assert.False(vm.ConfirmCommand.CanExecute(null));                   // while applying
        Assert.False(vm.CloseCommand.CanExecute(null));                     // Esc / Close wait for the outcome
        gate.Release();
        await confirm;
        Assert.Equal(1, Applies);
    }

    [Fact]
    public async Task TD_VM7_ARowCarriedOnlyByProjectsInTheTrash_CannotStartADelete()
    {
        await SeedAsync();
        var key = ProjectTagName.Key("clientA");
        var preview = (await _b.PrepareTagDeleteAsync(key)).Value!;
        Assert.True((await _b.ApplyTagDeleteAsync(key, preview.Token)).Committed); // only P4 / P5 (Trash) keep it

        var vm = await Select(Dialog(), ClientA);

        Assert.Equal(0, vm.SelectedRow!.ChangeableProjects);
        Assert.False(vm.StartDeleteCommand.CanExecute(null));
    }

    // ---- TD-VM8 - TD-VM11: write failure vs refresh failure (Revision 2 point 1) ---------------------------------------------------

    [Fact]
    public async Task TD_VM8_Control_AWriteFailureBeforeCommit_SaysNothingWasChanged_ListReread_SlotReleased()
    {
        await SeedAsync();
        var vm = await Previewed();
        var before = Data();
        var reads = _gated.Calls("tagsInUse");
        _gated.Hold("applyTagDelete").Fail(new IOException("disk write failed (test)")); // throws before the store call: nothing written

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(1, Applies);
        Assert.Equal(ManageTagsViewModel.CouldNotDelete, vm.Message);
        Assert.Equal(reads + 1, _gated.Calls("tagsInUse"));
        Assert.Equal(before, Data());
        Assert.False(vm.ChangedAny);
        Assert.False(vm.RefreshFailed);
        Assert.Equal((1, 1), (_slotTaken, _slotReleased));
    }

    [Fact]
    public async Task TD_VM9_TheListRefreshFailsAfterCommit_DeletionKept_SuccessReported_OneApply_Locked_SlotReleased()
    {
        await SeedAsync();
        var vm = await Previewed();
        _gated.Hold("tagsInUse").Fail(new IOException("list read failed (test)")); // only the post-commit re-read

        await vm.ConfirmCommand.ExecuteAsync();

        await AssertDeletedAsInTD2();
        Assert.Equal(1, Applies);
        Assert.Equal(Success + ManageTagsViewModel.RefreshFailedNote, vm.Message);
        Assert.DoesNotContain("Nothing was changed", vm.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not delete", vm.Message, StringComparison.Ordinal);
        Assert.True(vm.RefreshFailed);
        Assert.False(vm.IsRefreshing);
        Assert.True(vm.ChangedAny);
        AssertNewActionsLocked(vm);
        Assert.Equal((1, 1), (_slotTaken, _slotReleased));

        vm.ConfirmCommand.Execute(null);                                    // nothing re-applies afterwards
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(1, Applies);
    }

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

    private async Task<WorkspaceAreaViewModel> AreaWithP1Selected()
    {
        var area = await Area();
        area.SelectedNode = area.Roots.Single(n => !n.IsPlaceholder && n.Name == "P1");
        await area.CurrentDetailsLoad;
        Assert.Contains("clientA", area.SelectedProject!.Tags.Select(t => t.Name));
        return area;
    }

    /// <summary>Plays the dialog: Delete everywhere… on clientA, Preview, Confirm; <paramref name="beforeConfirm"/> / <paramref name="afterConfirm"/> arm faults.</summary>
    private Func<ManageTagsViewModel, bool> PlayDelete(Action? beforeConfirm, Action? afterConfirm, List<ManageTagsViewModel> seen) => vm =>
    {
        seen.Add(vm);
        _dialogs.WaitModal(vm.LoadAsync(), ModalTimeout);
        vm.SelectedRow = vm.Rows.Single(r => r.NameKey == ClientA);
        vm.StartDeleteCommand.Execute(null);
        _dialogs.WaitModal(vm.PreviewCommand.ExecuteAsync(), ModalTimeout);
        beforeConfirm?.Invoke();
        _dialogs.WaitModal(vm.ConfirmCommand.ExecuteAsync(), ModalTimeout);
        afterConfirm?.Invoke();
        return vm.ChangedAny;
    };

    [Fact]
    public Task TD_VM10_TheDetailsReloadFailsAfterCommit_DeletionKept_DialogSuccessUnchanged_OneApply_SlotReleased() => InContext(async () =>
    {
        await SeedAsync();
        var area = await AreaWithP1Selected();
        var seen = new List<ManageTagsViewModel>();
        _dialogs.OnManageTags = PlayDelete(null, () => _gated.ScriptProject[_p[1]] = () => throw new IOException("details read failed (test)"), seen);

        await area.ManageTagsCommand.ExecuteAsync();
        await area.CurrentDetailsLoad;

        await AssertDeletedAsInTD2();
        Assert.Equal(1, Applies);
        var vm = Assert.Single(seen);
        Assert.Equal(Success, vm.Message);                                  // the dialog's result text unchanged
        Assert.True(vm.ChangedAny);
        Assert.Equal("Project details could not be loaded.", area.DetailsMessage); // the area's own details-load error, no rollback wording
        Assert.False(area.IsLifecycleBusy);                                 // slot released
        Assert.True(area.ManageTagsCommand.CanExecute(null));
    });

    [Fact]
    public Task TD_VM11_BothRefreshesFail_DeletionKept_OneApply_SlotReleased() => InContext(async () =>
    {
        await SeedAsync();
        var area = await AreaWithP1Selected();
        var seen = new List<ManageTagsViewModel>();
        _dialogs.OnManageTags = PlayDelete(
            () => _gated.Hold("tagsInUse").Fail(new IOException("list read failed (test)")),
            () => _gated.ScriptProject[_p[1]] = () => throw new IOException("details read failed (test)"),
            seen);

        await area.ManageTagsCommand.ExecuteAsync();
        await area.CurrentDetailsLoad;

        await AssertDeletedAsInTD2();
        Assert.Equal(1, Applies);
        var vm = Assert.Single(seen);
        Assert.Equal(Success + ManageTagsViewModel.RefreshFailedNote, vm.Message);
        Assert.True(vm.RefreshFailed);
        Assert.Equal("Project details could not be loaded.", area.DetailsMessage);
        Assert.False(area.IsLifecycleBusy);
        Assert.True(area.ManageTagsCommand.CanExecute(null));
    });

    // ---- TD-VM12 (R2a): the post-commit re-read held by a gate ---------------------------------------------------------------------

    [Fact]
    public async Task TD_VM12_WhileThePostCommitReadIsHeld_SlotReleased_PreviewInvalid_SuccessShown_EverythingLocked_StillOneApply()
    {
        await SeedAsync();
        var vm = await Previewed();
        var gate = _gated.Hold("tagsInUse");                               // armed after the initial load: catches only the re-read

        var confirm = vm.ConfirmCommand.ExecuteAsync();
        gate.WaitEntered();

        // ---- while held
        Assert.Equal(1, Applies);
        await AssertDeletedAsInTD2();                                       // committed (read through window B's connection)
        Assert.Equal((1, 1), (_slotTaken, _slotReleased));                  // slot released before the re-read
        Assert.Null(vm.DeletePreview);                                      // the used preview is invalid
        Assert.False(vm.IsDeleteMode);
        Assert.Equal(Success, vm.Message);
        Assert.True(vm.IsRefreshing);
        AssertNewActionsLocked(vm);
        var before = Data();

        vm.ConfirmCommand.Execute(null);                                    // try to confirm again while the read is held
        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(1, Applies);
        Assert.Equal(before, Data());
        Assert.Equal(Success, vm.Message);

        // ---- after release
        gate.Release();
        await confirm;

        Assert.False(vm.IsRefreshing);
        Assert.False(vm.RefreshFailed);
        Assert.Equal(Success, vm.Message);
        var clientA = vm.Rows.Single(r => r.NameKey == ClientA);            // the new data
        Assert.Equal((0, 2), (clientA.ChangeableProjects, clientA.TrashedProjects));
        Assert.Same(clientA, vm.SelectedRow);
        Assert.False(vm.StartDeleteCommand.CanExecute(null));               // no changeable Project left on this row
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.True(vm.CloseCommand.CanExecute(null));

        vm.SelectedRow = vm.Rows.Single(r => r.NameKey == Infra);           // a valid row: its actions are enabled again
        Assert.True(vm.StartDeleteCommand.CanExecute(null));
        Assert.True(vm.StartRenameCommand.CanExecute(null));
        vm.StartDeleteCommand.Execute(null);
        Assert.False(vm.CanConfirm);                                        // Confirm still needs a NEW valid preview
        await vm.PreviewCommand.ExecuteAsync();
        Assert.True(vm.CanConfirm);
        Assert.Equal(1, Applies);
    }

    // ---- TD-VM13 - TD-VM15 (review fix 1): no close while the delete Apply writes; close allowed during the post-commit re-read ----
    // Scope: the test project has no WPF window (it does not reference WorkHub.App), so these prove the view-model state the window's
    // Closing handler reads (CanClose), the Close / Esc command (CloseCommand) and the result flow into the Workspace. That
    // DialogService wires Closing to CanClose is TD-C6 (source contract). That a real title-bar X / Alt+F4 is refused is GUI GD8
    // (Phase22ContractTests), not provable here.

    [Fact]
    public async Task TD_VM13_WhileApplyWrites_CloseRefused_AfterCommit_DuringTheHeldReread_CloseAccepted_ChangedAny_OneApply()
    {
        await SeedAsync();
        var vm = await Previewed();
        var closed = new List<bool>();
        vm.CloseRequested += (_, changed) => closed.Add(changed);
        Assert.True(vm.CanClose);                                           // with a valid preview, before Confirm

        var apply = _gated.Hold("applyTagDelete");
        var confirm = vm.ConfirmCommand.ExecuteAsync();
        apply.WaitEntered();

        // ---- write phase: every close path refused
        Assert.True(vm.IsDeleteWriting);
        Assert.False(vm.CanClose);                                          // the window's Closing cancels X / Alt+F4
        Assert.False(vm.CloseCommand.CanExecute(null));                     // Close button and Esc
        vm.CloseCommand.Execute(null);
        Assert.Empty(closed);
        Assert.False(vm.ChangedAny);
        Assert.Equal((1, 0), (_slotTaken, _slotReleased));

        // ---- commit, then hold the list re-read
        var reread = _gated.Hold("tagsInUse");
        apply.Release();
        reread.WaitEntered();

        Assert.False(vm.IsDeleteWriting);
        Assert.True(vm.ChangedAny);                                         // set before closing became possible
        Assert.True(vm.CanClose);
        Assert.True(vm.IsRefreshing);
        Assert.Null(vm.DeletePreview);                                      // the used preview is invalid
        Assert.Equal((1, 1), (_slotTaken, _slotReleased));                  // slot released
        AssertNewActionsLocked(vm);                                         // Confirm locked, Close available
        await AssertDeletedAsInTD2();

        vm.CloseCommand.Execute(null);                                      // close during the re-read: accepted, reports the change
        Assert.Equal([true], closed);

        reread.Release();
        await confirm;
        Assert.Equal(1, Applies);
        Assert.Equal([true], closed);                                       // the re-read finishing closes nothing by itself
    }

    [Fact]
    public async Task TD_VM14_Control_RenameKeepsItsCloseBehaviour_WindowCloseNotRefusedWhileItsApplyRuns()
    {
        await SeedAsync();
        var vm = await Select(Dialog(), ClientA);
        vm.StartRenameCommand.Execute(null);
        vm.NewName = "ClientX";
        await vm.PreviewCommand.ExecuteAsync();
        Assert.True(vm.CanConfirm);

        var apply = _gated.Hold("applyTagChange");
        var confirm = vm.ConfirmCommand.ExecuteAsync();
        apply.WaitEntered();

        Assert.False(vm.IsDeleteWriting);
        Assert.True(vm.CanClose);                                           // Phase 21: the window guard never refuses Rename / Merge
        Assert.False(vm.CloseCommand.CanExecute(null));                     // Phase 21: Close / Esc wait for the outcome (unchanged)
        apply.Release();
        await confirm;
        Assert.True(vm.CanClose);
        Assert.Equal(0, Applies);
    }

    [Fact]
    public Task TD_VM15_Workspace_DialogClosedDuringThePostCommitReread_ReloadsTheProjectDetails_OneApply() => InContext(async () =>
    {
        await SeedAsync();
        var area = await AreaWithP1Selected();
        var details = _gated.Calls($"details:{_p[1]}");
        StoreGate? reread = null;
        Task? confirm = null;
        ManageTagsViewModel? dialog = null;
        var closed = new List<bool>();
        _dialogs.OnManageTags = vm =>
        {
            dialog = vm;
            vm.CloseRequested += (_, changed) => closed.Add(changed);
            _dialogs.WaitModal(vm.LoadAsync(), ModalTimeout);
            vm.SelectedRow = vm.Rows.Single(r => r.NameKey == ClientA);
            vm.StartDeleteCommand.Execute(null);
            _dialogs.WaitModal(vm.PreviewCommand.ExecuteAsync(), ModalTimeout);

            var apply = _gated.Hold("applyTagDelete");
            confirm = vm.ConfirmCommand.ExecuteAsync();
            apply.WaitEntered();
            Assert.False(vm.CanClose);                                      // a close attempt now is cancelled: the dialog stays
            Assert.False(vm.CloseCommand.CanExecute(null));
            Assert.True(area.IsLifecycleBusy);

            reread = _gated.Hold("tagsInUse");
            apply.Release();
            reread.WaitEntered();                                           // committed; the list re-read is held
            Assert.True(vm.CanClose);
            Assert.True(vm.ChangedAny);
            Assert.False(vm.CanConfirm);
            Assert.False(area.IsLifecycleBusy);                             // slot released before the re-read
            vm.CloseCommand.Execute(null);                                  // the user closes while the re-read is still held
            return vm.ChangedAny;                                           // ShowDialog returns; the Workspace reads ChangedAny
        };

        await area.ManageTagsCommand.ExecuteAsync();
        await area.CurrentDetailsLoad;

        Assert.Equal([true], closed);
        Assert.Equal(details + 1, _gated.Calls($"details:{_p[1]}"));       // the existing details reload ran
        Assert.Equal(["Infra"], area.SelectedProject!.Tags.Select(t => t.Name));
        Assert.Equal(1, Applies);

        reread!.Release();                                                  // the closed dialog's re-read finishes harmlessly
        await confirm!;
        Assert.False(dialog!.IsRefreshing);
        Assert.Equal(1, Applies);
        await AssertDeletedAsInTD2();
    });

    private void Sql(string text, params (string Name, object Value)[] args)
    {
        using var connection = _root.Open();
        using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
