using System.Globalization;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 24 view model acceptance (proposal Revision 2 + R2a §3.1, §3.4, §4.3: TGV1–TGV7): the Projects with this tag list of Manage
/// tags. Window A = the dialog on service A (a gated store; every list read's key is recorded in
/// <see cref="GatedWorkspaceStore.TaggedProjectsCalls"/>); window B = service B (or a raw insert) on the same SQLite test database.
/// Every case runs on a <see cref="SingleThreadContext"/> (as the WPF dispatcher), with gates only — no sleeps.
/// <para>Fixture: <see cref="TaggedProjectsFixture"/> (§4.1). Key "clienta" → P1–P6 (4 eligible, 2 in the Trash).</para>
/// </summary>
public sealed class ManageTagsProjectsViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly GatedWorkspaceStore _gated;
    private readonly WorkspaceService _a;
    private readonly WorkspaceService _b;
    private readonly SingleThreadContext _context = new();
    private readonly List<(string Name, Task Task)> _tracked = [];
    private readonly List<ManageTagsViewModel> _dialogs = [];
    private TaggedProjectsFixture _x = null!;

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly string ClientA = TaggedProjectsFixture.ClientA;
    private static readonly string Infra = TaggedProjectsFixture.Infra;

    public ManageTagsProjectsViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _gated = new GatedWorkspaceStore(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _a = new WorkspaceService(_gated);
        _b = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose() =>
        ContextTeardown.Run(_context, _gated.ReleaseAll,
            [() => [.. _tracked, .. _dialogs.Select((d, i) => ($"dialog{i}.CurrentProjectsLoad", d.CurrentProjectsLoad))]],
            _root.Dispose, _root.Paths.Root, TimeSpan.FromSeconds(30));

    private Task InContext(Func<Task> body) => _context.RunAsync(body, BodyTimeout);

    private async Task<ManageTagsViewModel> OpenAsync()
    {
        _x = await TaggedProjectsFixture.BuildAsync(_root, _b);
        var vm = new ManageTagsViewModel(_a, () => true, () => { }, NullAppLog.Instance);
        _dialogs.Add(vm);
        await vm.LoadAsync();
        return vm;
    }

    /// <summary>Selects the tag row of <paramref name="key"/> and returns the list read it started (tracked for teardown).</summary>
    private Task Select(ManageTagsViewModel vm, string key)
    {
        vm.SelectedRow = vm.Rows.Single(r => r.NameKey == key);
        var load = vm.CurrentProjectsLoad;
        _tracked.Add(($"load{_tracked.Count}", load));
        return load;
    }

    private static List<string> Names(ManageTagsViewModel vm) => vm.TaggedProjects.Select(r => r.Name).ToList();

    private static TaggedProjectRowViewModel Row(ManageTagsViewModel vm, string name) => vm.TaggedProjects.Single(r => r.Name == name);

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    // ---- TGV1 -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public Task TGV1_SelectingATag_LoadsTheList_InTheStoreOrder_HeaderFromTheListResult() => InContext(async () =>
    {
        var vm = await OpenAsync();
        Assert.Empty(vm.TaggedProjects);
        Assert.Null(vm.TaggedProjectsHeader);

        await Select(vm, ClientA);

        Assert.Equal(["P1", "P3", "P4", "P2", "P5", "P6"], Names(vm));
        Assert.Equal("4 projects, 2 in the Trash keep it", vm.TaggedProjectsHeader);
        Assert.Null(vm.TaggedProjectsError);
        Assert.Equal(("Workspace", null, null), (Row(vm, "P1").PathText, Row(vm, "P1").StateText, Row(vm, "P1").SpellingText));
        Assert.Equal(("Workspace", "Archived"), (Row(vm, "P3").PathText, Row(vm, "P3").StateText));
        Assert.Equal(("A1", "Archived"), (Row(vm, "P4").PathText, Row(vm, "P4").StateText));
        Assert.Equal(("F1", null, "ClientA"), (Row(vm, "P2").PathText, Row(vm, "P2").StateText, Row(vm, "P2").SpellingText));
        Assert.Equal(("Workspace", "In the Trash"), (Row(vm, "P5").PathText, Row(vm, "P5").StateText));
        Assert.Equal(("F2", "In the Trash"), (Row(vm, "P6").PathText, Row(vm, "P6").StateText));
        Assert.Equal([ClientA], _gated.TaggedProjectsCalls);
    });

    // ---- TGV2a – TGV2c: request versions, never the key alone --------------------------------------------------------------------

    [Fact]
    public Task TGV2a_LateResultOfAnotherKey_IsDropped() => InContext(async () =>
    {
        var vm = await OpenAsync();
        var held = _gated.Hold("taggedProjects");
        var first = Select(vm, ClientA);
        held.WaitEntered();

        await Select(vm, Infra);                                            // its read completes and is shown
        Assert.Equal(["P1", "P7"], Names(vm));
        vm.SelectedTaggedProject = Row(vm, "P7");

        held.Release();
        await first;

        Assert.Equal(["P1", "P7"], Names(vm));
        Assert.Equal("2 projects", vm.TaggedProjectsHeader);
        Assert.Null(vm.TaggedProjectsError);
        Assert.Equal(_x.P7, vm.SelectedTaggedProject!.ProjectNodeId);
        Assert.True(vm.ShowInWorkspaceCommand.CanExecute(null));
    });

    [Fact]
    public Task TGV2b_SameKeyAgain_TheOldReadSucceedsLate_IsDroppedAlthoughItsKeyMatches() => InContext(async () =>
    {
        var vm = await OpenAsync();
        var held = _gated.Hold("taggedProjects");
        var read1 = Select(vm, ClientA);                                    // read #1 takes its snapshot now (4 eligible)
        held.WaitEntered();
        await Select(vm, Infra);                                            // read #2 shown

        TaggedProjectsFixture.InsertTag(_root, _x.P7, "clientA", 27);      // window B, before read #3 runs
        await Select(vm, ClientA);                                          // read #3 shown
        Assert.Contains("P7", Names(vm));
        Assert.Equal("5 projects, 2 in the Trash keep it", vm.TaggedProjectsHeader);
        vm.SelectedTaggedProject = Row(vm, "P7");

        held.Release();
        await read1;

        Assert.Equal(["P1", "P3", "P7", "P4", "P2", "P5", "P6"], Names(vm)); // still read #3
        Assert.Equal("5 projects, 2 in the Trash keep it", vm.TaggedProjectsHeader);
        Assert.Null(vm.TaggedProjectsError);
        Assert.Equal(_x.P7, vm.SelectedTaggedProject!.ProjectNodeId);
        Assert.True(vm.ShowInWorkspaceCommand.CanExecute(null));
        Assert.Equal([ClientA, Infra, ClientA], _gated.TaggedProjectsCalls);
    });

    [Fact]
    public Task TGV2c_SameKeyAgain_TheOldReadFaultsLate_ChangesNothingVisible() => InContext(async () =>
    {
        var vm = await OpenAsync();
        var held = _gated.Hold("taggedProjects");
        var read1 = Select(vm, ClientA);
        held.WaitEntered();
        await Select(vm, Infra);
        TaggedProjectsFixture.InsertTag(_root, _x.P7, "clientA", 27);
        await Select(vm, ClientA);                                          // read #3 shown
        vm.SelectedTaggedProject = Row(vm, "P1");
        var message = vm.Message;

        held.Fail(new IOException("disk read failed (test)"));
        await read1;                                                         // handled inside: never throws

        Assert.Equal(7, vm.TaggedProjects.Count);
        Assert.Equal("5 projects, 2 in the Trash keep it", vm.TaggedProjectsHeader);
        Assert.Null(vm.TaggedProjectsError);
        Assert.True(vm.ShowInWorkspaceCommand.CanExecute(null));
        Assert.Equal(message, vm.Message);
        Assert.False(vm.RefreshFailed);
    });

    // ---- TGV3 – TGV5 --------------------------------------------------------------------------------------------------------------

    [Fact]
    public Task TGV3_ShowInWorkspace_OnlyForOneRevealableRow_NotWhileWritingOrRefreshing() => InContext(async () =>
    {
        var vm = await OpenAsync();
        await Select(vm, ClientA);

        Assert.False(vm.ShowInWorkspaceCommand.CanExecute(null));          // none selected
        vm.SelectedTaggedProject = Row(vm, "P5");
        Assert.False(vm.ShowInWorkspaceCommand.CanExecute(null));          // in the Trash
        vm.SelectedTaggedProject = Row(vm, "P3");
        Assert.True(vm.ShowInWorkspaceCommand.CanExecute(null));           // archived: revealed under Archived
        vm.SelectedTaggedProject = Row(vm, "P1");
        Assert.True(vm.ShowInWorkspaceCommand.CanExecute(null));

        vm.StartRenameCommand.Execute(null);
        vm.NewName = "ClientX";
        await vm.PreviewCommand.ExecuteAsync();
        var apply = _gated.Hold("applyTagChange");
        var confirm = vm.ConfirmCommand.ExecuteAsync();
        apply.WaitEntered();
        Assert.True(vm.IsWriting);
        Assert.Equal(_x.P1, vm.SelectedTaggedProject!.ProjectNodeId);
        Assert.False(vm.ShowInWorkspaceCommand.CanExecute(null));          // while IsWriting

        var reread = _gated.Hold("tagsInUse");                              // armed while the apply is held
        apply.Release();
        reread.WaitEntered();
        Assert.True(vm.IsRefreshing);
        Assert.False(vm.ShowInWorkspaceCommand.CanExecute(null));          // while IsRefreshing
        reread.Release();
        await confirm;
        Assert.False(vm.IsRefreshing);
    });

    [Fact]
    public Task TGV4_ShowInWorkspace_ClosesWithTheTarget_ChangedAnyUnchanged_NoWrite() => InContext(async () =>
    {
        var vm = await OpenAsync();
        await Select(vm, ClientA);
        var before = Data();
        var closed = new List<bool>();
        vm.CloseRequested += (_, changed) => closed.Add(changed);

        vm.SelectedTaggedProject = Row(vm, "P2");
        vm.ShowInWorkspaceCommand.Execute(null);

        Assert.Equal([false], closed);
        Assert.Equal(_x.P2, vm.RevealTarget);
        Assert.False(vm.ChangedAny);
        Assert.Empty(_gated.ApplyTagChangeCalls);
        Assert.Empty(_gated.ApplyTagDeleteCalls);
        Assert.Equal(before, Data());
    });

    [Fact]
    public Task TGV5_RefreshAfterCommit_RereadsTheListWithANewVersion_ForTheStillSelectedKey() => InContext(async () =>
    {
        var vm = await OpenAsync();
        await Select(vm, ClientA);
        vm.StartRenameCommand.Execute(null);
        vm.NewName = "ClientX";
        await vm.PreviewCommand.ExecuteAsync();

        var apply = _gated.Hold("applyTagChange");                         // sequence S (Phase 23)
        var confirm = vm.ConfirmCommand.ExecuteAsync();
        apply.WaitEntered();
        var reread = _gated.Hold("tagsInUse");                              // both armed BEFORE the apply is released
        var list = _gated.Hold("taggedProjects");
        apply.Release();
        reread.WaitEntered();

        // While held: the Phase 23 refresh state (TP3) is unchanged and no list re-read has started yet.
        Assert.True(vm.ChangedAny);
        Assert.True(vm.IsRefreshing);
        Assert.False(vm.IsWriting);
        Assert.True(vm.CanClose);
        Assert.False(vm.CanConfirm);
        Assert.False(vm.StartRenameCommand.CanExecute(null));
        Assert.True(vm.CloseCommand.CanExecute(null));
        Assert.StartsWith("Renamed “clientA” to “ClientX” in 4 projects.", vm.Message);
        Assert.Equal([ClientA], _gated.TaggedProjectsCalls);
        var success = vm.Message;

        reread.Release();
        list.WaitEntered();                                                  // the new-version list read for the still-selected key
        Assert.Equal([ClientA, ClientA], _gated.TaggedProjectsCalls);
        Assert.True(vm.IsRefreshing);
        list.Release();
        await confirm;

        // P5 and P6 (in the Trash) keep "clientA": the row stays selected with 0 changeable projects.
        Assert.False(vm.IsRefreshing);
        Assert.False(vm.RefreshFailed);
        Assert.Equal(ClientA, vm.SelectedRow!.NameKey);
        Assert.Equal(["P5", "P6"], Names(vm));
        Assert.Equal("0 projects, 2 in the Trash keep it", vm.TaggedProjectsHeader);
        Assert.Equal(success, vm.Message);
    });

    [Fact]
    public Task TGV5_RefreshAfterCommit_AVanishedKey_ShowsEmptyRowsAndZeroProjects() => InContext(async () =>
    {
        var vm = await OpenAsync();
        await Select(vm, Infra);                                            // P1 and P7, both live: a delete removes the key entirely
        vm.StartDeleteCommand.Execute(null);
        await vm.PreviewCommand.ExecuteAsync();
        Assert.True(vm.CanConfirm);
        var list = _gated.Hold("taggedProjects");

        var confirm = vm.ConfirmCommand.ExecuteAsync();
        list.WaitEntered();                                                  // after the list re-read: the old key, new version
        Assert.True(vm.IsRefreshing);
        Assert.Null(vm.SelectedRow);
        list.Release();
        await confirm;

        Assert.Equal([Infra, Infra], _gated.TaggedProjectsCalls);
        Assert.False(vm.IsRefreshing);
        Assert.Empty(vm.TaggedProjects);
        Assert.Equal("0 projects", vm.TaggedProjectsHeader);
        Assert.False(vm.ShowInWorkspaceCommand.CanExecute(null));
    });

    // ---- TGV6, TGV7 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public Task TGV6_CurrentListReadFails_ErrorInTheListAreaOnly() => InContext(async () =>
    {
        var vm = await OpenAsync();
        _gated.Hold("taggedProjects").Fail(new IOException("disk read failed (test)"));

        await Select(vm, ClientA);

        Assert.Equal(ManageTagsViewModel.CouldNotListProjects, vm.TaggedProjectsError);
        Assert.Empty(vm.TaggedProjects);
        Assert.Null(vm.TaggedProjectsHeader);
        Assert.Null(vm.Message);
        Assert.False(vm.RefreshFailed);
        Assert.True(vm.StartRenameCommand.CanExecute(null));
        Assert.True(vm.StartMergeCommand.CanExecute(null));
        Assert.True(vm.StartDeleteCommand.CanExecute(null));
        Assert.False(vm.ShowInWorkspaceCommand.CanExecute(null));

        await Select(vm, Infra);                                            // a later read clears the error
        Assert.Null(vm.TaggedProjectsError);
        Assert.Equal(["P1", "P7"], Names(vm));
    });

    [Fact]
    public Task TGV7_HeaderComesFromTheListRead_NotFromTheOlderTagRow() => InContext(async () =>
    {
        var vm = await OpenAsync();
        var row = vm.Rows.Single(r => r.NameKey == ClientA);
        Assert.Equal(("4 projects", "+ 2 in the Trash"), (row.CountText, row.TrashText));

        TaggedProjectsFixture.InsertTag(_root, _x.P7, "clientA", 27);      // window B adds the key to P7 …
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _b.TrashProjectAsync(_x.P1)).Outcome); // … and trashes P1
        await Select(vm, ClientA);

        Assert.Equal("4 projects, 3 in the Trash keep it", vm.TaggedProjectsHeader);
        Assert.Equal(("4 projects", "+ 2 in the Trash"), (vm.SelectedRow!.CountText, vm.SelectedRow.TrashText)); // the row stays as read
    });

    [Fact]
    public Task TGV7_501Rows_HeaderSaysAndOneMore() => InContext(async () =>
    {
        var vm = await OpenAsync();
        for (var i = 1; i <= 501; i++)
            await _b.CreateProjectAsync(null, new ProjectDraft(string.Create(CultureInfo.InvariantCulture, $"B{i:000}"), ProjectStatus.Active, Tags: ["bulk"]));
        await vm.LoadAsync();

        await Select(vm, ProjectTagName.Key("bulk"));

        Assert.Equal(500, vm.TaggedProjects.Count);
        Assert.Equal("501 projects — and 1 more", vm.TaggedProjectsHeader);
        Assert.Equal("B500", vm.TaggedProjects[^1].Name);
    });
}
