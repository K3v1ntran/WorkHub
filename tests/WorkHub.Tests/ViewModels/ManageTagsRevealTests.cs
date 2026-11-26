using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 24 Workspace flow (proposal Revision 2 + R2a §3.5, §4.4: TGA1–TGA4): Show in Workspace in Manage tags closes the dialog and
/// the Workspace reveals the Project BY ID at its current place through the search resolve, then shows the §3.5 project banner.
/// Window A = the Workspace area on a gated tree store and a gated search store; window B = service B on the same SQLite test
/// database. The dialog is played by <see cref="FakeDialogs.OnManageTags"/> (no WPF window). Runs on a
/// <see cref="SingleThreadContext"/>; gates only, no sleeps.
/// <para>
/// Every case asserts: no extra store Apply (rename / merge / delete) from Show in Workspace, the data snapshot unchanged by the
/// reveal, and the banner exactly as §3.5. Fixture: <see cref="TaggedProjectsFixture"/> (§4.1).
/// </para>
/// </summary>
public sealed class ManageTagsRevealTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedWorkspaceStore _treeStore;
    private readonly WorkspaceService _b;
    private readonly SingleThreadContext _context = new();
    private readonly List<WorkspaceAreaViewModel> _areas = [];
    private GatedSearchStore _search = null!;
    private TaggedProjectsFixture _x = null!;

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ModalTimeout = TimeSpan.FromSeconds(10);
    private static readonly string ClientA = TaggedProjectsFixture.ClientA;

    public ManageTagsRevealTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _treeStore = new GatedWorkspaceStore(new SqliteWorkspaceStore(_connections));
        _b = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose() =>
        ContextTeardown.Run(_context, () => { _treeStore.ReleaseAll(); _search?.ReleaseAll(); },
            [
                .. _areas.Select((area, i) => (ContextTeardown.TrackedSet)(() =>
                    [($"area{i}.CurrentDetailsLoad", area.CurrentDetailsLoad), ($"area{i}.CurrentReveal", area.CurrentReveal)])),
                () => _dialogs.ManageTagsDialogs.Select((d, i) => ($"dialog{i}.CurrentProjectsLoad", d.CurrentProjectsLoad)).ToList(),
            ],
            _root.Dispose, _root.Paths.Root, TimeSpan.FromSeconds(30));

    private Task InContext(Func<Task> body) => _context.RunAsync(body, BodyTimeout);

    private async Task<WorkspaceAreaViewModel> AreaAsync()
    {
        _x = await TaggedProjectsFixture.BuildAsync(_root, _b);
        _search = new GatedSearchStore(new SqliteWorkspaceSearchStore(_connections, WorkspaceSearchStoreOptions.Default));
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance,
            search: new WorkspaceSearchService(_search), searchDelayMs: 0);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        return area;
    }

    private int Applies => _treeStore.ApplyTagChangeCalls.Count + _treeStore.ApplyTagDeleteCalls.Count;

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) => nodes.Single(n => !n.IsPlaceholder && n.Name == name);

    /// <summary>
    /// Plays the dialog: list loaded, <paramref name="key"/> selected, its Projects list loaded, then <paramref name="meanwhile"/>
    /// (window B's change after the list was read, or a committed change in the dialog), then Show in Workspace on
    /// <paramref name="project"/>. The data snapshot just before Show in Workspace is kept in <see cref="_beforeReveal"/>.
    /// </summary>
    private void PlayShowInWorkspace(string key, string project, Func<ManageTagsViewModel, Task>? meanwhile = null)
    {
        _dialogs.OnManageTags = vm =>
        {
            _dialogs.WaitModal(vm.LoadAsync(), ModalTimeout);
            vm.SelectedRow = vm.Rows.Single(r => r.NameKey == key);
            _dialogs.WaitModal(vm.CurrentProjectsLoad, ModalTimeout);
            if (meanwhile is not null)
                _dialogs.WaitModal(meanwhile(vm), ModalTimeout);
            vm.SelectedTaggedProject = vm.TaggedProjects.Single(r => r.Name == project);
            _beforeReveal = Data();
            _appliesBeforeReveal = Applies;
            Assert.True(vm.ShowInWorkspaceCommand.CanExecute(null));
            vm.ShowInWorkspaceCommand.Execute(null);
            return vm.ChangedAny;
        };
    }

    private List<string>? _beforeReveal;
    private int _appliesBeforeReveal;

    /// <summary>The common assertions of every case (§4.4) and the outcome / banner pair.</summary>
    private async Task AssertReveal(WorkspaceAreaViewModel area, WorkspaceRevealOutcome[] outcomes, string? banner)
    {
        Assert.Contains(await area.CurrentReveal, outcomes);
        Assert.Equal(banner, area.BannerMessage);
        Assert.Equal(_appliesBeforeReveal, Applies);
        Assert.Equal(_beforeReveal, Data());
    }

    // ---- TGA1, TGA2 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public Task TGA1_RevealLive_SelectsTheProjectInsideItsFolder_NoBanner() => InContext(async () =>
    {
        var area = await AreaAsync();
        PlayShowInWorkspace(ClientA, "P2");

        await area.ManageTagsCommand.ExecuteAsync();

        await AssertReveal(area, [WorkspaceRevealOutcome.Selected], null);
        Assert.Equal(_x.P2, area.SelectedNode!.Id);
        Assert.Equal(_x.F1, area.SelectedNode.Parent!.Id);
        Assert.Equal(0, Applies);
    });

    [Fact]
    public Task TGA2_RevealArchived_OpensArchivedWithTheProject_ThenWithTheArchivedFolder() => InContext(async () =>
    {
        var area = await AreaAsync();
        PlayShowInWorkspace(ClientA, "P3");
        await area.ManageTagsCommand.ExecuteAsync();

        await AssertReveal(area, [WorkspaceRevealOutcome.Archived], "The project is archived — it is selected under Archived.");
        Assert.True(area.IsArchivedOpen);
        Assert.Equal(_x.P3, area.Archived.SelectedEntry!.NodeId);

        PlayShowInWorkspace(ClientA, "P4");
        await area.ManageTagsCommand.ExecuteAsync();

        await AssertReveal(area, [WorkspaceRevealOutcome.InArchivedFolder], "The project is in an archived folder — that folder is selected under Archived.");
        Assert.True(area.IsArchivedOpen);
        Assert.Equal(_x.A1, area.Archived.SelectedEntry!.NodeId);
        Assert.True(area.Archived.SelectedEntry.IsFolder);
        Assert.Equal(0, Applies);
    });

    // ---- TGA3a – TGA3f: the Project changed after the list was read ----------------------------------------------------------------

    [Fact]
    public Task TGA3a_MovedMeanwhile_IsFoundAtItsNewPlace() => InContext(async () =>
    {
        var area = await AreaAsync();
        PlayShowInWorkspace(ClientA, "P2", async _ => Assert.True((await _b.MoveAsync(_x.P2, _x.F3)).Value)); // window B

        await area.ManageTagsCommand.ExecuteAsync();

        await AssertReveal(area, [WorkspaceRevealOutcome.Selected], null);
        Assert.Equal(_x.P2, area.SelectedNode!.Id);
        Assert.Equal(_x.F3, area.SelectedNode.Parent!.Id);
    });

    [Fact]
    public Task TGA3b_RemovedMeanwhile_NotFound_SelectionUnchanged() => InContext(async () =>
    {
        var area = await AreaAsync();
        area.SelectedNode = Node(area.Roots, "P1");
        await area.CurrentDetailsLoad;
        PlayShowInWorkspace(ClientA, "P2", _ =>
        {
            _search.ScriptResolve = _ => new RevealResolution(RevealResolveStatus.NotFound, null); // the Project is gone
            return Task.CompletedTask;
        });

        await area.ManageTagsCommand.ExecuteAsync();

        await AssertReveal(area, [WorkspaceRevealOutcome.NotFound], "The project no longer exists.");
        Assert.Equal(_x.P1, area.SelectedNode!.Id);
    });

    [Fact]
    public Task TGA3c_ProjectTrashedMeanwhile_IsInTheTrash() => InContext(async () =>
    {
        var area = await AreaAsync();
        PlayShowInWorkspace(ClientA, "P2", async _ => Assert.Equal(ProjectTrashOutcome.Trashed, (await _b.TrashProjectAsync(_x.P2)).Outcome));

        await area.ManageTagsCommand.ExecuteAsync();

        await AssertReveal(area, [WorkspaceRevealOutcome.Trashed, WorkspaceRevealOutcome.ProjectTrashed], "The project is in the Trash.");
    });

    [Fact]
    public Task TGA3d_FolderTrashedMeanwhile_ItsFolderIsInTheTrash() => InContext(async () =>
    {
        var area = await AreaAsync();
        PlayShowInWorkspace(ClientA, "P2", async _ =>
        {
            var token = (await _b.PrepareFolderLifecycleAsync(_x.F1, FolderLifecycleKind.Trash)).Confirmation!.Token;
            Assert.Equal(FolderLifecycleOutcome.Committed, (await _b.MarkFolderAsync(_x.F1, FolderLifecycleKind.Trash, token)).Outcome);
        });

        await area.ManageTagsCommand.ExecuteAsync();

        await AssertReveal(area, [WorkspaceRevealOutcome.FolderTrashed], "The project's folder is in the Trash.");
    });

    /// <summary>
    /// The location keeps changing for both bounded attempts: B moves P2 from F1 to F3 while resolve #1 is held, and the branch on
    /// each attempt's path (F1, then F3) is loaded locally while that resolve is held — so both attempts see a changed path.
    /// </summary>
    [Fact]
    public Task TGA3e_LocationKeepsChanging_WorkspaceChanged() => InContext(async () =>
    {
        var area = await AreaAsync();
        PlayShowInWorkspace(ClientA, "P2");
        var first = _search.Hold("resolve");

        var command = area.ManageTagsCommand.ExecuteAsync();
        first.WaitEntered();                                                 // resolve #1 took its snapshot: P2 in F1
        Assert.True((await _b.MoveAsync(_x.P2, _x.F3)).Value);              // window B, between the two attempts
        _beforeReveal = Data();                                              // the reveal itself must not change it
        var f1 = Node(area.Roots, "F1");
        f1.IsExpanded = true;
        Assert.True(await area.EnsureChildrenLoadedAsync(f1));
        var second = _search.Hold("resolve");
        first.Release();
        second.WaitEntered();                                                // resolve #2: P2 in F3
        var f3 = Node(area.Roots, "F3");
        f3.IsExpanded = true;
        Assert.True(await area.EnsureChildrenLoadedAsync(f3));
        second.Release();
        await command;

        await AssertReveal(area, [WorkspaceRevealOutcome.WorkspaceChanged], "The project was moved or changed meanwhile. Try Show in Workspace again.");
        Assert.Null(area.SelectedNode);
    });

    [Fact]
    public Task TGA3f_Busy_ShowsTheBusyBanner() => InContext(async () =>
    {
        var area = await AreaAsync();
        PlayShowInWorkspace(ClientA, "P2");
        _search.Hold("resolve").Fail(new WorkspaceSearchBusyException(TimeSpan.FromSeconds(30)));

        await area.ManageTagsCommand.ExecuteAsync();

        await AssertReveal(area, [WorkspaceRevealOutcome.Busy], "The database stayed busy (another program or window is writing). Try again.");
    });

    // ---- TGA4a, TGA4b: a committed change earlier in the same dialog -------------------------------------------------------------

    /// <summary>In the dialog: Rename "clientA" → "ClientX" committed (with its post-commit refresh), then the ClientX row selected.</summary>
    private static async Task RenameThenSelectClientX(ManageTagsViewModel vm)
    {
        vm.StartRenameCommand.Execute(null);
        vm.NewName = "ClientX";
        await vm.PreviewCommand.ExecuteAsync();
        await vm.ConfirmCommand.ExecuteAsync();
        Assert.True(vm.ChangedAny);
        vm.SelectedRow = vm.Rows.Single(r => r.NameKey == ProjectTagName.Key("ClientX"));
        await vm.CurrentProjectsLoad;
    }

    [Theory]
    [InlineData(false)] // TGA4a: the target is the selected project P1
    [InlineData(true)]  // TGA4b: the target is another project, P2
    public Task TGA4_CommittedChange_ReloadsTheSelectedDetailsOnce_BeforeTheRevealStarts(bool otherTarget) => InContext(async () =>
    {
        var area = await AreaAsync();
        area.SelectedNode = Node(area.Roots, "P1");
        await area.CurrentDetailsLoad;
        var p1Before = _treeStore.Calls($"details:{_x.P1}");
        var p2Before = _treeStore.Calls($"details:{_x.P2}");
        PlayShowInWorkspace(ClientA, otherTarget ? "P2" : "P1", RenameThenSelectClientX);
        var resolve = _search.Hold("resolve");

        var command = area.ManageTagsCommand.ExecuteAsync();
        resolve.WaitEntered();                                               // the reveal has started …

        // … only after the P1 reload (from ChangedAny) was issued exactly once and completed.
        Assert.Equal(p1Before + 1, _treeStore.Calls($"details:{_x.P1}"));
        Assert.True(area.CurrentDetailsLoad.IsCompleted);
        Assert.Equal(p2Before, _treeStore.Calls($"details:{_x.P2}"));
        // P1 carries clientA and Infra (§4.1): after the rename it has exactly ClientX and Infra (order-independent).
        Assert.Equal(["ClientX", "Infra"], area.SelectedProject!.Tags.Select(t => t.Name).Order(StringComparer.Ordinal));
        resolve.Release();
        await command;
        await area.CurrentDetailsLoad;

        await AssertReveal(area, [WorkspaceRevealOutcome.Selected], null);
        Assert.Single(_treeStore.ApplyTagChangeCalls);                      // the one rename
        if (otherTarget)
        {
            Assert.Equal(_x.P2, area.SelectedNode!.Id);
            Assert.Equal(p1Before + 1, _treeStore.Calls($"details:{_x.P1}"));
            Assert.Equal(p2Before + 1, _treeStore.Calls($"details:{_x.P2}")); // selection changed: exactly one load
        }
        else
        {
            Assert.Equal(_x.P1, area.SelectedNode!.Id);
            Assert.Equal(p1Before + 1, _treeStore.Calls($"details:{_x.P1}")); // same node: no further load
            Assert.Equal(p2Before, _treeStore.Calls($"details:{_x.P2}"));
        }
    });
}
