using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 23 (proposal Revision 2, D1–D8): Rename… / Merge into… follow the close and refresh contract of Delete everywhere…
/// (§3.1, §3.5, acceptance §4.1–§4.3 TP1–TP7). Window A = the dialog on service A (a gated store; every rename/merge apply is
/// recorded in <see cref="GatedWorkspaceStore.ApplyTagChangeCalls"/>, the store-side Apply counter); window B = service B on its own
/// store, same real SQLite test database. Gates only, no sleeps.
/// <para>
/// Gate sequence S (§4.1): (1) the initial list load completes; (2) Preview completes; (3) <c>Hold("applyTagChange")</c>, Confirm
/// without awaiting, <c>WaitEntered</c>; (4) write-phase assertions; (5) <c>Hold("tagsInUse")</c> armed WHILE the apply is held, so
/// it can only catch the post-commit re-read; (6) release the apply; (7) <c>WaitEntered</c> on the re-read, then refresh-phase
/// assertions; (8) release or fail the re-read, await Confirm, final assertions.
/// </para>
/// <para>
/// Scope: no WPF window here (the test project does not reference WorkHub.App). These prove the state the window's Closing handler
/// reads (<see cref="ManageTagsViewModel.CanClose"/>), the Close / Esc command and the result flow into the Workspace. The wiring is
/// TD-C6 / TP-C2 (source contracts); a real X / Alt+F4 is GUI GP1 / GP2 / GP3-R (not run here).
/// </para>
/// <para>
/// Fixture (§4.2): P1 live {clientA}; P2 live {clientA, Ops} (carries both the Merge source and target); P3 live {}; P4 directly in
/// the Workspace Trash {clientA}.
/// </para>
/// </summary>
public sealed class ManageTagsCloseRefreshTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly GatedWorkspaceStore _gated;
    private readonly WorkspaceService _a;
    private readonly WorkspaceService _b;
    private readonly FakeDialogs _dialogs = new();
    private readonly List<WorkspaceAreaViewModel> _areas = [];
    private readonly SingleThreadContext _context = new();
    private long _p1, _p2, _p3, _p4;
    private int _slotTaken, _slotReleased;

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ModalTimeout = TimeSpan.FromSeconds(10);
    private static readonly string ClientA = ProjectTagName.Key("clientA");
    private static readonly string ClientX = ProjectTagName.Key("ClientX");
    private static readonly string Ops = ProjectTagName.Key("Ops");

    private const string TrashKeeps = " 1 project in the Trash keeps “clientA”.";
    private const string Renamed = "Renamed “clientA” to “ClientX” in 2 projects." + TrashKeeps;
    private const string Merged = "Merged “clientA” into “Ops” in 2 projects (1 already had “Ops”)." + TrashKeeps;

    public ManageTagsCloseRefreshTests()
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
        async Task<long> Project(string name, params string[] tags) =>
            (await _b.CreateProjectAsync(null, new ProjectDraft(name, ProjectStatus.Active, null, null, null, tags))).Value!.Id;
        _p1 = await Project("P1", "clientA");
        _p2 = await Project("P2", "clientA", "Ops");
        _p3 = await Project("P3");
        _p4 = await Project("P4", "clientA");
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _b.TrashProjectAsync(_p4)).Outcome);
    }

    private ManageTagsViewModel Dialog() =>
        new(_a, () => { _slotTaken++; return true; }, () => _slotReleased++, NullAppLog.Instance);

    private int Applies => _gated.ApplyTagChangeCalls.Count;

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    /// <summary>The exact stored rows of a Project (raw <c>CreatedAt</c> text), ordered by key.</summary>
    private List<(string Key, string Name, string CreatedAt)> Rows(long project)
    {
        using var connection = _root.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NameKey, Name, CreatedAt FROM ProjectTags WHERE ProjectNodeId = $p ORDER BY NameKey;";
        command.Parameters.AddWithValue("$p", project);
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private sealed record Snapshot(
        List<(string Key, string Name, string CreatedAt)> P1,
        List<(string Key, string Name, string CreatedAt)> P2,
        List<(string Key, string Name, string CreatedAt)> P3,
        List<(string Key, string Name, string CreatedAt)> P4);

    private Snapshot Before() => new(Rows(_p1), Rows(_p2), Rows(_p3), Rows(_p4));

    private static string CreatedOf(List<(string Key, string Name, string CreatedAt)> rows, string key) => rows.Single(r => r.Key == key).CreatedAt;

    /// <summary>S1–S2: list loaded, clientA selected, Rename… "ClientX" or Merge into… "Ops", Preview completed (confirmable).</summary>
    private async Task<ManageTagsViewModel> Prepared(bool merge)
    {
        var vm = Dialog();
        await vm.LoadAsync();                                               // S1: the initial read is never gated
        vm.SelectedRow = vm.Rows.Single(r => r.NameKey == ClientA);
        if (merge)
        {
            vm.StartMergeCommand.Execute(null);
            vm.MergeTarget = vm.MergeTargets.Single(r => r.NameKey == Ops);
        }
        else
        {
            vm.StartRenameCommand.Execute(null);
            vm.NewName = "ClientX";
        }

        await vm.PreviewCommand.ExecuteAsync();                             // S2
        Assert.True(vm.CanConfirm);
        return vm;
    }

    /// <summary>On-disk result of a committed Rename (TP3-R / TP4-R) or Merge (TP3-M / TP4-M) against <paramref name="before"/>.</summary>
    private void AssertCommitted(bool merge, Snapshot before)
    {
        var newKey = merge ? Ops : ClientX;
        var newName = merge ? "Ops" : "ClientX";
        Assert.Equal<(string, string, string)>(new[] { (newKey, newName, CreatedOf(before.P1, ClientA)) }, Rows(_p1)); // replaced row keeps the source CreatedAt
        if (merge)
        {
            // P2 carried both tags: the source row is gone and the existing target row is untouched (Name and raw CreatedAt).
            Assert.Equal<(string, string, string)>(before.P2.Where(r => r.Key == Ops).ToList(), Rows(_p2));
        }
        else
        {
            Assert.Equal<(string, string, string)>(new[] { (ClientX, "ClientX", CreatedOf(before.P2, ClientA)), before.P2.Single(r => r.Key == Ops) }
                .OrderBy(r => r.Item1, StringComparer.Ordinal).ToList(), Rows(_p2));
        }

        Assert.Empty(Rows(_p3));
        Assert.Equal<(string, string, string)>(before.P4, Rows(_p4));                                 // in the Trash: keeps clientA
    }

    /// <summary>Refresh phase (re-read held) or after a failed re-read: Confirm and every new action locked; Close available.</summary>
    private static void AssertNewActionsLocked(ManageTagsViewModel vm)
    {
        Assert.False(vm.CanConfirm);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.False(vm.PreviewCommand.CanExecute(null));
        Assert.False(vm.StartRenameCommand.CanExecute(null));
        Assert.False(vm.StartMergeCommand.CanExecute(null));
        Assert.False(vm.StartDeleteCommand.CanExecute(null));
        Assert.False(vm.CancelChangeCommand.CanExecute(null));
        Assert.True(vm.CloseCommand.CanExecute(null));
        Assert.True(vm.CanClose);
    }

    // ---- TP1 / TP2: the write phase refuses every close path ------------------------------------------------------------------

    [Theory]
    [InlineData(false)] // TP1 Rename
    [InlineData(true)]  // TP2 Merge into
    public async Task TP1_TP2_WhileTheApplyWrites_EveryClosePathIsRefused_NothingCommittedYet(bool merge)
    {
        await SeedAsync();
        var vm = await Prepared(merge);
        var before = Data();
        var closed = new List<bool>();
        vm.CloseRequested += (_, changed) => closed.Add(changed);
        Assert.True(vm.CanClose);

        var apply = _gated.Hold("applyTagChange");                         // S3
        var confirm = vm.ConfirmCommand.ExecuteAsync();
        apply.WaitEntered();

        Assert.True(vm.IsWriting);                                          // S4
        Assert.False(vm.CanClose);                                          // the window's Closing cancels X / Alt+F4
        Assert.False(vm.CloseCommand.CanExecute(null));                     // Close button and Esc
        vm.CloseCommand.Execute(null);
        Assert.Empty(closed);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.False(vm.ChangedAny);
        Assert.Equal((1, 0), (_slotTaken, _slotReleased));                  // slot taken
        Assert.Equal(before, Data());                                       // the held write has not run

        apply.Release();
        await confirm;
        Assert.False(vm.IsWriting);
        Assert.True(vm.CanClose);
        Assert.Equal(1, Applies);
    }

    // ---- TP3-R / TP3-M: committed, the post-commit re-read held -----------------------------------------------------------------

    [Theory]
    [InlineData(false)] // TP3-R
    [InlineData(true)]  // TP3-M
    public async Task TP3_Committed_ReReadHeld_ChangedAny_SlotReleased_CloseAllowed_ConfirmLocked_OneApply(bool merge)
    {
        await SeedAsync();
        var vm = await Prepared(merge);
        var before = Before();
        var closed = new List<bool>();
        vm.CloseRequested += (_, changed) => closed.Add(changed);

        var apply = _gated.Hold("applyTagChange");                         // S3
        var confirm = vm.ConfirmCommand.ExecuteAsync();
        apply.WaitEntered();
        Assert.False(vm.CanClose);                                          // S4
        var reread = _gated.Hold("tagsInUse");                             // S5: armed while the apply is still held
        apply.Release();                                                    // S6
        reread.WaitEntered();                                               // S7

        // ---- refresh phase, re-read held
        Assert.Equal(1, Applies);
        AssertCommitted(merge, before);
        Assert.True(vm.ChangedAny);
        Assert.False(vm.IsWriting);
        Assert.True(vm.IsRefreshing);
        Assert.Null(vm.Preview);                                            // the used preview is invalid
        Assert.Null(vm.Mode);
        Assert.Equal((1, 1), (_slotTaken, _slotReleased));                  // slot released before the re-read
        Assert.Equal(merge ? Merged : Renamed, vm.Message);
        AssertNewActionsLocked(vm);
        var data = Data();

        vm.ConfirmCommand.Execute(null);                                    // a second confirmation attempt while the read is held
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(1, Applies);
        Assert.Equal(data, Data());

        // ---- S8: release the re-read
        reread.Release();
        await confirm;

        Assert.False(vm.IsRefreshing);
        Assert.False(vm.RefreshFailed);
        Assert.Equal(merge ? Merged : Renamed, vm.Message);
        Assert.Equal(1, Applies);
        Assert.Empty(closed);                                               // nothing closed by itself
        Assert.DoesNotContain(vm.Rows, r => r.NameKey == ClientA && r.ChangeableProjects > 0);
        Assert.Null(vm.Preview);
        Assert.False(vm.CanConfirm);                                        // Confirm needs a NEW valid preview
        Assert.True(vm.CloseCommand.CanExecute(null));
    }

    // ---- TP4-R / TP4-M: committed, the post-commit re-read fails ------------------------------------------------------------------

    [Theory]
    [InlineData(false)] // TP4-R
    [InlineData(true)]  // TP4-M
    public async Task TP4_Committed_ReReadFails_SuccessTextKeptWithNote_Locked_CloseAllowed_OneApply(bool merge)
    {
        await SeedAsync();
        var vm = await Prepared(merge);
        var before = Before();

        var apply = _gated.Hold("applyTagChange");                         // S3
        var confirm = vm.ConfirmCommand.ExecuteAsync();
        apply.WaitEntered();
        var reread = _gated.Hold("tagsInUse");                             // S5
        apply.Release();                                                    // S6
        reread.WaitEntered();                                               // S7
        Assert.True(vm.ChangedAny);
        Assert.True(vm.IsRefreshing);

        reread.Fail(new IOException("list read failed (test)"));            // S8: the post-commit re-read fails
        await confirm;

        var success = merge ? Merged : Renamed;
        Assert.Equal(success + ManageTagsViewModel.RefreshFailedNote, vm.Message);
        Assert.DoesNotContain("could not be listed", vm.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was changed", vm.Message, StringComparison.Ordinal);
        Assert.True(vm.RefreshFailed);
        Assert.False(vm.IsRefreshing);
        Assert.True(vm.ChangedAny);
        Assert.Equal((1, 1), (_slotTaken, _slotReleased));
        AssertNewActionsLocked(vm);
        AssertCommitted(merge, before);                                     // the change is kept on disk
        Assert.Equal(1, Applies);

        vm.ConfirmCommand.Execute(null);                                    // nothing re-applies afterwards
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.Equal(1, Applies);
    }

    // ---- TP5: a non-committed outcome (Stale) keeps the Phase 21 behaviour; close refused only while the Apply is in flight ---------
    // NothingToRename, Unchanged and InvalidName cannot come back from a Confirm in the dialog: Confirm needs a confirmable preview
    // whose token the store checks FIRST (any later change is Stale), and an invalid name is refused before any store call (TR11).
    // Their texts are covered by the Phase 21 tests and are not touched by Phase 23.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TP5_Stale_CloseRefusedOnlyWhileHeld_NothingWritten_Phase21TextAndFreshPreview(bool merge)
    {
        await SeedAsync();
        var vm = await Prepared(merge);
        Assert.True((await _b.UpdateProjectAsync(_p3, new ProjectDraft("P3", ProjectStatus.Active, null, null, null, ["clientA"]))).Succeeded); // B
        var afterB = Data();

        var apply = _gated.Hold("applyTagChange");
        var confirm = vm.ConfirmCommand.ExecuteAsync();
        apply.WaitEntered();
        Assert.False(vm.CanClose);
        apply.Release();
        await confirm;

        Assert.Equal(1, Applies);
        Assert.Equal(ManageTagsViewModel.StaleMessage, vm.Message);
        Assert.NotNull(vm.Preview);                                         // the fresh plan, for a new deliberate Confirm
        Assert.True(vm.CanConfirm);
        Assert.True(vm.CanClose);
        Assert.False(vm.ChangedAny);
        Assert.False(vm.IsRefreshing);
        Assert.False(vm.RefreshFailed);
        Assert.Equal(afterB, Data());
        Assert.Equal((1, 1), (_slotTaken, _slotReleased));
    }

    // ---- TP6: a write failure (control) -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TP6_Control_AWriteFailure_CouldNotRename_NothingWritten_CloseAllowedAfter_NotChanged()
    {
        await SeedAsync();
        var vm = await Prepared(merge: false);
        var before = Data();
        _gated.Hold("applyTagChange").Fail(new IOException("disk write failed (test)")); // thrown before the store call

        await vm.ConfirmCommand.ExecuteAsync();

        Assert.Equal(1, Applies);
        Assert.Equal(ManageTagsViewModel.CouldNotRename, vm.Message);
        Assert.Equal(before, Data());
        Assert.False(vm.ChangedAny);
        Assert.False(vm.IsWriting);
        Assert.True(vm.CanClose);
        Assert.False(vm.IsRefreshing);
        Assert.False(vm.RefreshFailed);
        Assert.Equal((1, 1), (_slotTaken, _slotReleased));
    }

    // ---- TP7: the Workspace flow, the dialog closed while the post-commit re-read is held ------------------------------------------

    private Task InContext(Func<Task> body) => _context.RunAsync(body, BodyTimeout);

    [Fact]
    public Task TP7_Workspace_DialogClosedDuringTheHeldReread_ReloadsTheProjectDetailsExactlyOnce_OneApply() => InContext(async () =>
    {
        await SeedAsync();
        var area = new WorkspaceAreaViewModel(_a, _dialogs, NullAppLog.Instance);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        area.SelectedNode = area.Roots.Single(n => !n.IsPlaceholder && n.Name == "P1");
        await area.CurrentDetailsLoad;
        Assert.Equal(["clientA"], area.SelectedProject!.Tags.Select(t => t.Name));
        var details = _gated.Calls($"details:{_p1}");

        StoreGate? reread = null;
        Task? confirm = null;
        ManageTagsViewModel? dialog = null;
        var closed = new List<bool>();
        _dialogs.OnManageTags = vm =>
        {
            dialog = vm;
            vm.CloseRequested += (_, changed) => closed.Add(changed);
            _dialogs.WaitModal(vm.LoadAsync(), ModalTimeout);               // S1
            vm.SelectedRow = vm.Rows.Single(r => r.NameKey == ClientA);
            vm.StartRenameCommand.Execute(null);
            vm.NewName = "ClientX";
            _dialogs.WaitModal(vm.PreviewCommand.ExecuteAsync(), ModalTimeout); // S2

            var apply = _gated.Hold("applyTagChange");                     // S3
            confirm = vm.ConfirmCommand.ExecuteAsync();
            apply.WaitEntered();
            Assert.False(vm.CanClose);                                      // S4: a close attempt now is cancelled
            Assert.False(vm.CloseCommand.CanExecute(null));
            Assert.True(area.IsLifecycleBusy);

            reread = _gated.Hold("tagsInUse");                             // S5: armed while the apply is held
            apply.Release();                                                // S6
            reread.WaitEntered();                                           // S7
            Assert.True(vm.ChangedAny);
            Assert.True(vm.CanClose);
            Assert.False(vm.CanConfirm);
            Assert.False(area.IsLifecycleBusy);                             // slot released before the re-read
            vm.CloseCommand.Execute(null);                                  // the user closes while the re-read is still held
            return vm.ChangedAny;                                           // ShowDialog returns; the Workspace reads ChangedAny
        };

        await area.ManageTagsCommand.ExecuteAsync();
        await area.CurrentDetailsLoad;

        Assert.Equal([true], closed);
        Assert.Equal(details + 1, _gated.Calls($"details:{_p1}"));         // exactly one details reload
        Assert.Equal(["ClientX"], area.SelectedProject!.Tags.Select(t => t.Name));
        Assert.Equal(1, Applies);

        reread!.Release();                                                  // S8: the closed dialog's re-read ends harmlessly
        await confirm!;
        Assert.False(dialog!.IsRefreshing);
        Assert.Equal(details + 1, _gated.Calls($"details:{_p1}"));         // still exactly one
        Assert.Equal(["ClientX"], area.SelectedProject!.Tags.Select(t => t.Name));
        Assert.Equal(1, Applies);
        Assert.False(area.IsLifecycleBusy);
    });
}
