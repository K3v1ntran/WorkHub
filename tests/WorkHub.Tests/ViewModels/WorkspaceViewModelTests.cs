using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Workspace tab view models against a real isolated database, with a gated store to control when reads and writes
/// complete. Covers lazy loading, targeted updates after successful writes, and the stale-result rules.
/// </summary>
public sealed class WorkspaceViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly GatedWorkspaceStore _store;
    private readonly WorkspaceService _seed;
    private readonly WorkspaceService _workspace;

    public WorkspaceViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        var real = new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile));
        _seed = new WorkspaceService(real);
        _store = new GatedWorkspaceStore(real);
        _workspace = new WorkspaceService(_store);
    }

    public void Dispose() => _root.Dispose();

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private WorkspaceAreaViewModel CreateArea() => new(_workspace, _dialogs, NullAppLog.Instance);

    private async Task<WorkspaceAreaViewModel> ActivatedArea()
    {
        var area = CreateArea();
        area.IsTabSelected = true;
        await area.RootLoad;
        Assert.True(area.IsActivated);
        return area;
    }

    private async Task<long> SeedFolder(string name, long? parent = null) => (await _seed.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> SeedProject(string name, long? parent = null, ProjectStatus status = ProjectStatus.Active) =>
        (await _seed.CreateProjectAsync(parent, new ProjectDraft(name, status))).Value!.Id;

    private static List<string> Names(IEnumerable<WorkspaceNodeViewModel> nodes) =>
        nodes.Where(n => !n.IsPlaceholder).Select(n => n.Name).ToList();

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) =>
        nodes.Single(n => n.Name == name);

    private static async Task Expand(WorkspaceAreaViewModel area, WorkspaceNodeViewModel folder)
    {
        folder.IsExpanded = true;
        Assert.True(await area.EnsureChildrenLoadedAsync(folder));
    }

    private void NameDialogSaves(string name) => _dialogs.OnWorkspaceNameEditor = editor =>
    {
        var closed = false;
        editor.CloseRequested += (_, ok) => closed = ok;
        editor.Name = name;
        editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
        return closed;
    };

    private void ProjectDialogSaves(Action<ProjectEditorViewModel> fill) => _dialogs.OnProjectEditor = editor =>
    {
        var closed = false;
        editor.CloseRequested += (_, ok) => closed = ok;
        fill(editor);
        editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
        return closed;
    };

    private void MoveDialogChooses(string destinationPath, Action<WorkspaceMoveViewModel>? inspect = null) => _dialogs.OnWorkspaceMove = editor =>
    {
        var closed = false;
        editor.CloseRequested += (_, ok) => closed = ok;
        editor.SelectedDestination = editor.Destinations.Single(d => d.Path == destinationPath);
        editor.MoveCommand.ExecuteAsync().GetAwaiter().GetResult();
        inspect?.Invoke(editor);
        return closed;
    };

    // ---- Lazy loading ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task NothingLoadsUntilTheTabIsShown_ThenOnlyRoots()
    {
        var clients = await SeedFolder("Clients");
        await SeedFolder("東京", clients);
        await SeedProject("Alpha");

        var area = CreateArea();
        Assert.Equal(0, _store.Calls(GatedWorkspaceStore.Children(null)));

        area.IsTabSelected = true;
        await area.RootLoad;

        Assert.Equal(["Clients", "Alpha"], Names(area.Roots));
        Assert.Equal(1, _store.Calls(GatedWorkspaceStore.Children(null)));
        Assert.Equal(0, _store.Calls(GatedWorkspaceStore.Children(clients)));
        var folder = Node(area.Roots, "Clients");
        Assert.True(Assert.Single(folder.Children).IsPlaceholder); // expander before loading
        Assert.Empty(Node(area.Roots, "Alpha").Children);
    }

    [Fact]
    public async Task ExpandingLoadsOnlyThatFolder_Once()
    {
        var a = await SeedFolder("A");
        var b = await SeedFolder("B");
        await SeedProject("A-1", a);
        await SeedFolder("B-1", b);
        var area = await ActivatedArea();

        await Expand(area, Node(area.Roots, "A"));
        Node(area.Roots, "A").IsExpanded = false;
        await Expand(area, Node(area.Roots, "A"));

        Assert.Equal(["A-1"], Names(Node(area.Roots, "A").Children));
        Assert.Equal(1, _store.Calls(GatedWorkspaceStore.Children(a)));
        Assert.Equal(0, _store.Calls(GatedWorkspaceStore.Children(b)));
    }

    [Fact]
    public async Task Depth_FollowsNesting_IncludingPlaceholders_AndUpdatesAfterMove()
    {
        var a = await SeedFolder("A");
        var b = await SeedFolder("B", a);
        await SeedFolder("C", b);
        await SeedFolder("Other");
        var area = await ActivatedArea();
        var folderA = Node(area.Roots, "A");
        await Expand(area, folderA);
        var folderB = Node(folderA.Children, "B");

        Assert.Equal((0, 1), (folderA.Depth, folderB.Depth));
        Assert.Equal(2, Assert.Single(folderB.Children).Depth); // placeholder row is indented like a real child
        await Expand(area, folderB);
        var folderC = Node(folderB.Children, "C");
        Assert.Equal(2, folderC.Depth);

        var changed = new List<string?>();
        folderC.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        area.SelectedNode = folderB;
        MoveDialogChooses("Workspace (top level)");
        await area.MoveCommand.ExecuteAsync();

        Assert.Equal((0, 1), (folderB.Depth, folderC.Depth)); // the moved subtree re-indents
        Assert.Contains(nameof(WorkspaceNodeViewModel.Depth), changed);
    }

    [Fact]
    public async Task EmptyWorkspace_ShowsEmptyState_AndNoTrashNode()
    {
        var area = await ActivatedArea();

        Assert.True(area.IsEmpty);
        Assert.Empty(area.Roots);
    }

    [Fact]
    public void DeferredActions_AreNotExposed()
    {
        var members = typeof(WorkspaceAreaViewModel).GetProperties().Select(p => p.Name).ToList();

        // Phase 8 adds Project Trash and empty-folder removal (Delete = Del key); Archive and Tags stay deferred.
        Assert.DoesNotContain(members, m => m.Contains("Archive") || m.Contains("Tag") || m.Contains("Link") || m.Contains("Note") || m.Contains("Purge"));
        Assert.DoesNotContain(members, m => m.Contains("Delete") && m != nameof(WorkspaceAreaViewModel.DeleteCommand));
        Assert.DoesNotContain(ProjectEditorViewModel.ForCreate(_workspace, null, null).Statuses, s => s.Status == ProjectStatus.Archived);
    }

    // ---- Create, rename, edit, move (happy paths) --------------------------------------------------------------------

    [Fact]
    public async Task NewFolder_AtRoot_AppearsSortedAndSelected()
    {
        await SeedFolder("Beta");
        await SeedProject("Zeta");
        var area = await ActivatedArea();
        NameDialogSaves("  アルファ  ");

        await area.NewFolderCommand.ExecuteAsync();

        Assert.Equal(["Beta", "アルファ", "Zeta"], Names(area.Roots));
        Assert.Equal("アルファ", area.SelectedNode!.Name);
        Assert.True(area.SelectedNode.IsFolder);
    }

    [Fact]
    public async Task NewProject_InSelectedUnloadedFolder_LoadsExpandsAndSelectsIt_WithDetails()
    {
        var clients = await SeedFolder("Clients");
        await SeedProject("Existing", clients);
        var area = await ActivatedArea();
        area.SelectedNode = Node(area.Roots, "Clients");
        ProjectDialogSaves(e =>
        {
            Assert.Equal("Location: Clients", e.LocationText);
            Assert.Equal(ProjectStatus.Active, e.Status.Status); // default
            e.Name = "販売管理システム";
            e.Owner = "佐藤";
        });

        await area.NewProjectCommand.ExecuteAsync();
        await area.CurrentDetailsLoad;

        var folder = Node(area.Roots, "Clients");
        Assert.True(folder.IsExpanded);
        Assert.Equal(["Existing", "販売管理システム"], Names(folder.Children));
        Assert.Equal("販売管理システム", area.SelectedNode!.Name);
        Assert.Equal(("販売管理システム", "佐藤", ProjectStatus.Active), (area.SelectedProject!.Name, area.SelectedProject.Owner, area.SelectedProject.Status));
        Assert.Equal(1, _store.Calls(GatedWorkspaceStore.Children(clients)));
    }

    [Fact]
    public async Task NewItem_WhenAProjectIsSelected_GoesBesideIt()
    {
        var folder = await SeedFolder("F");
        await SeedProject("P", folder);
        var area = await ActivatedArea();
        await Expand(area, Node(area.Roots, "F"));
        area.SelectedNode = Node(Node(area.Roots, "F").Children, "P");
        await area.CurrentDetailsLoad; // no UI thread in tests: let the selection settle first
        NameDialogSaves("Sub");

        await area.NewFolderCommand.ExecuteAsync();

        Assert.Equal(["Sub", "P"], Names(Node(area.Roots, "F").Children));
    }

    [Fact]
    public async Task Rename_ResortsSiblings_KeepsSelection_AndRefreshesProjectDetails()
    {
        await SeedFolder("Folder");
        await SeedProject("Alpha");
        await SeedProject("Beta");
        var area = await ActivatedArea();
        area.SelectedNode = Node(area.Roots, "Alpha");
        await area.CurrentDetailsLoad;
        NameDialogSaves("Gamma");

        await area.RenameCommand.ExecuteAsync();
        await area.CurrentDetailsLoad;

        Assert.Equal(["Folder", "Beta", "Gamma"], Names(area.Roots));
        Assert.Equal("Gamma", area.SelectedNode!.Name);
        Assert.Equal("Gamma", area.SelectedProject!.Name);
    }

    [Fact]
    public async Task EditProject_ShowsSavedDetails_AndRenamesTheNode()
    {
        var id = await SeedProject("Alpha");
        var area = await ActivatedArea();
        area.SelectedNode = Node(area.Roots, "Alpha");
        await area.CurrentDetailsLoad;
        ProjectDialogSaves(e =>
        {
            Assert.True(e.IsEdit);
            e.Name = "Alpha 2";
            e.Status = e.Statuses.Single(s => s.Status == ProjectStatus.OnHold);
            e.Description = "メモ";
            e.ProjectManager = "鈴木";
        });

        await area.EditProjectCommand.ExecuteAsync();

        Assert.Equal(("Alpha 2", ProjectStatus.OnHold, "メモ", "鈴木"),
            (area.SelectedProject!.Name, area.SelectedProject.Status, area.SelectedProject.Description, area.SelectedProject.ProjectManager));
        Assert.Equal("On Hold", area.SelectedProjectStatus);
        Assert.Equal("Alpha 2", Node(area.Roots, "Alpha 2").Name);
        Assert.Equal(area.SelectedProject, await _seed.GetProjectAsync(id));
    }

    [Fact]
    public async Task Move_FromFolderToRootAndIntoAnotherFolder_UpdatesBothParentsOnce()
    {
        var a = await SeedFolder("A");
        await SeedFolder("B");
        await SeedProject("P", a);
        var area = await ActivatedArea();
        await Expand(area, Node(area.Roots, "A"));
        await Expand(area, Node(area.Roots, "B"));
        area.SelectedNode = Node(Node(area.Roots, "A").Children, "P");
        await area.CurrentDetailsLoad; // no UI thread in tests: let the selection settle first

        MoveDialogChooses("Workspace (top level)");
        await area.MoveCommand.ExecuteAsync();
        Assert.Empty(Names(Node(area.Roots, "A").Children));
        Assert.Equal(["A", "B", "P"], Names(area.Roots));

        MoveDialogChooses("B");
        await area.MoveCommand.ExecuteAsync();
        Assert.Equal(["A", "B"], Names(area.Roots));
        Assert.Equal(["P"], Names(Node(area.Roots, "B").Children));
        Assert.Equal("P", area.SelectedNode!.Name);
    }

    [Fact]
    public async Task MoveDialog_DoesNotOfferTheNodeOrItsSubtree_AndPreselectsTheCurrentParent()
    {
        var a = await SeedFolder("A");
        var b = await SeedFolder("B", a);
        await SeedFolder("C", b);
        await SeedFolder("D");
        var area = await ActivatedArea();
        await Expand(area, Node(area.Roots, "A"));
        area.SelectedNode = Node(Node(area.Roots, "A").Children, "B");
        WorkspaceMoveViewModel? shown = null;
        _dialogs.OnWorkspaceMove = editor => { shown = editor; return false; };

        await area.MoveCommand.ExecuteAsync();

        Assert.Equal(["Workspace (top level)", "A", "D"], shown!.Destinations.Select(d => d.Path));
        Assert.Equal("A", shown.SelectedDestination!.Path);
    }

    // ---- Failed writes leave the tree unchanged -------------------------------------------------------------------

    [Fact]
    public async Task DuplicateName_KeepsTheDialogOpen_AndTheTreeUnchanged()
    {
        await SeedFolder("Docs");
        var area = await ActivatedArea();
        WorkspaceNameEditorViewModel? editor = null;
        _dialogs.OnWorkspaceNameEditor = e =>
        {
            editor = e;
            e.Name = "ＤＯＣＳ";
            e.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return false; // the dialog stayed open with the error; the user cancels
        };

        await area.NewFolderCommand.ExecuteAsync();

        Assert.Contains("already exists here", editor!.Error);
        Assert.Equal(["Docs"], Names(area.Roots));
    }

    [Fact]
    public async Task MoveIntoOwnSubfolder_ShowsTheErrorInTheDialog_AndChangesNothing()
    {
        var a = await SeedFolder("A");
        await SeedFolder("B", a);
        var area = await ActivatedArea();
        await Expand(area, Node(area.Roots, "A"));
        var folderA = Node(area.Roots, "A");
        var folderB = Node(folderA.Children, "B");
        area.SelectedNode = folderA;
        string? error = null;
        _dialogs.OnWorkspaceMove = editor =>
        {
            // The dialog never offers A's own subtree; force B anyway to prove the move is still rejected.
            Assert.DoesNotContain(editor.Destinations, d => d.FolderId == folderB.Id);
            var forced = new WorkspaceMoveViewModel(_workspace, folderA.Id, "A", null, [new WorkspaceFolderPath(folderB.Id, "A / B")]);
            forced.SelectedDestination = forced.Destinations.Single(d => d.FolderId == folderB.Id);
            forced.MoveCommand.ExecuteAsync().GetAwaiter().GetResult();
            error = forced.Error;
            return false;
        };

        await area.MoveCommand.ExecuteAsync();

        Assert.Equal("A folder cannot be moved into itself or into one of its own subfolders.", error);
        Assert.Equal(["A"], Names(area.Roots));
        Assert.Equal(["B"], Names(Node(area.Roots, "A").Children));
    }

    [Fact]
    public async Task MoveToConflictingName_ShowsTheErrorInTheDialog_AndChangesNothing()
    {
        var a = await SeedFolder("A");
        var b = await SeedFolder("B");
        await SeedProject("Report", a);
        await SeedFolder("REPORT", b);
        var area = await ActivatedArea();
        await Expand(area, Node(area.Roots, "A"));
        area.SelectedNode = Node(Node(area.Roots, "A").Children, "Report");
        await area.CurrentDetailsLoad; // no UI thread in tests: let the selection settle first
        string? error = null;
        MoveDialogChooses("B", editor => error = editor.Error);

        await area.MoveCommand.ExecuteAsync();

        Assert.Equal("The destination already contains a folder or project named 'Report'.", error);
        Assert.Equal(["Report"], Names(Node(area.Roots, "A").Children));
        Assert.Equal("Report", area.SelectedNode!.Name);
    }

    [Fact]
    public async Task S10_CreateWhereTheStoreFails_LeavesNoGhostNode()
    {
        await SeedFolder("F");
        var area = await ActivatedArea();
        area.SelectedNode = Node(area.Roots, "F");
        _store.Hold("createFolder").Fail(new IOException("disk full"));
        WorkspaceNameEditorViewModel? editor = null;
        _dialogs.OnWorkspaceNameEditor = e =>
        {
            editor = e;
            e.Name = "X";
            e.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return false;
        };

        await area.NewFolderCommand.ExecuteAsync();

        Assert.Equal("Could not save: disk full", editor!.Error);
        Assert.Equal(["F"], Names(area.Roots));
        Assert.Empty(Node(area.Roots, "F").Children); // no ghost child, not even an expander
        Assert.Equal(0, _store.Calls(GatedWorkspaceStore.Children(Node(area.Roots, "F").Id)));
        using var connection = _root.Open();
        Assert.Equal(1, Db.Count(connection, "WorkspaceNodes"));
    }

    // ---- Stale results (gated store) -------------------------------------------------------------------------------

    [Fact]
    public async Task S1_OlderDetailsLoad_DoesNotReplaceANewerSelection()
    {
        var a = await SeedProject("A");
        await SeedProject("B");
        var area = await ActivatedArea();
        var gate = _store.Hold($"details:{a}");

        area.SelectedNode = Node(area.Roots, "A");
        var loadA = area.CurrentDetailsLoad;
        gate.WaitEntered();
        area.SelectedNode = Node(area.Roots, "B");
        await area.CurrentDetailsLoad;
        gate.Release();
        await loadA;

        Assert.Equal("B", area.SelectedProject!.Name);
        Assert.Null(area.DetailsMessage);
        Assert.False(area.IsLoadingDetails);
    }

    [Fact]
    public async Task S2_OlderDetailsLoad_DoesNotAppearForASelectedFolder()
    {
        var a = await SeedProject("A");
        await SeedFolder("F");
        var area = await ActivatedArea();
        var gate = _store.Hold($"details:{a}");

        area.SelectedNode = Node(area.Roots, "A");
        var loadA = area.CurrentDetailsLoad;
        gate.WaitEntered();
        area.SelectedNode = Node(area.Roots, "F");
        gate.Release();
        await loadA;

        Assert.Null(area.SelectedProject);
        Assert.True(area.IsFolderSelected);
        Assert.False(area.IsLoadingDetails);
    }

    [Fact]
    public async Task S3_OlderDetailsLoad_DoesNotAppearAfterSelectionIsCleared()
    {
        var a = await SeedProject("A");
        var area = await ActivatedArea();
        var gate = _store.Hold($"details:{a}");

        area.SelectedNode = Node(area.Roots, "A");
        var loadA = area.CurrentDetailsLoad;
        gate.WaitEntered();
        area.SelectedNode = null;
        gate.Release();
        await loadA;

        Assert.Null(area.SelectedProject);
        Assert.Null(area.DetailsMessage);
        Assert.False(area.IsLoadingDetails);
    }

    [Fact]
    public async Task S4_OlderDetailsLoad_DoesNotOverwriteSavedState()
    {
        var a = await SeedProject("A");
        var area = await ActivatedArea();
        var gate = _store.Hold($"details:{a}");

        area.SelectedNode = Node(area.Roots, "A"); // this load read the old state and is held
        var staleLoad = area.CurrentDetailsLoad;
        gate.WaitEntered();
        ProjectDialogSaves(e =>
        {
            e.Status = e.Statuses.Single(s => s.Status == ProjectStatus.Completed);
            e.Description = "saved";
        });
        await area.EditProjectCommand.ExecuteAsync(); // Edit reads current state itself, then saves
        gate.Release();
        await staleLoad;

        Assert.Equal((ProjectStatus.Completed, "saved"), (area.SelectedProject!.Status, area.SelectedProject.Description));
        Assert.False(area.IsLoadingDetails);
    }

    [Fact]
    public async Task S5_StaleDetailsFailure_DoesNotAppearAgainstANewerSelection()
    {
        var a = await SeedProject("A");
        await SeedProject("B");
        var area = await ActivatedArea();
        var gate = _store.Hold($"details:{a}");

        area.SelectedNode = Node(area.Roots, "A");
        var loadA = area.CurrentDetailsLoad;
        gate.WaitEntered();
        area.SelectedNode = Node(area.Roots, "B");
        await area.CurrentDetailsLoad;
        gate.Fail(new IOException("disk error"));
        await loadA;

        Assert.Equal("B", area.SelectedProject!.Name);
        Assert.Null(area.DetailsMessage);
        Assert.Null(area.BannerMessage);
    }

    [Fact]
    public async Task S6_ExpandingAgainWhileLoading_ReusesTheLoad()
    {
        var f = await SeedFolder("F");
        await SeedProject("P", f);
        var area = await ActivatedArea();
        var folder = Node(area.Roots, "F");
        var gate = _store.Hold(GatedWorkspaceStore.Children(f));

        folder.IsExpanded = true;
        gate.WaitEntered();
        folder.IsExpanded = false;
        folder.IsExpanded = true;
        var second = area.EnsureChildrenLoadedAsync(folder);
        gate.Release();
        Assert.True(await second);

        Assert.Equal(1, _store.Calls(GatedWorkspaceStore.Children(f)));
        Assert.Equal(["P"], Names(folder.Children));
    }

    [Fact]
    public async Task S7_CreateWhileTheFolderIsLoading_AppearsExactlyOnce()
    {
        var f = await SeedFolder("F");
        await SeedProject("Existing", f);
        var area = await ActivatedArea();
        var folder = Node(area.Roots, "F");
        var gate = _store.Hold(GatedWorkspaceStore.Children(f));
        folder.IsExpanded = true; // the listing is read now (without X) and delivered later
        gate.WaitEntered();
        area.SelectedNode = folder;
        NameDialogSaves("X");

        var create = area.NewFolderCommand.ExecuteAsync(); // committed; waits for the pending load before merging
        gate.Release();
        await create;

        Assert.Equal(["X", "Existing"], Names(folder.Children));
        Assert.Equal("X", area.SelectedNode!.Name);
        Assert.Equal(1, _store.Calls(GatedWorkspaceStore.Children(f)));
    }

    [Fact]
    public async Task S8_RenameWhileTheFolderIsLoading_ChildrenStillAttachToIt()
    {
        var f = await SeedFolder("F");
        await SeedProject("P", f);
        var area = await ActivatedArea();
        var folder = Node(area.Roots, "F");
        var gate = _store.Hold(GatedWorkspaceStore.Children(f));
        folder.IsExpanded = true;
        gate.WaitEntered();
        area.SelectedNode = folder;
        NameDialogSaves("Renamed");

        await area.RenameCommand.ExecuteAsync();
        var load = area.EnsureChildrenLoadedAsync(folder);
        gate.Release();
        await load;

        Assert.Same(folder, Node(area.Roots, "Renamed"));
        Assert.Equal(["P"], Names(folder.Children));
        Assert.Same(folder, Node(folder.Children, "P").Parent);
    }

    [Fact]
    public async Task S9_MoveIntoAFolderWhileItIsLoading_AppearsOnceAndLeavesTheOldParent()
    {
        var a = await SeedFolder("A");
        var t = await SeedFolder("T");
        await SeedProject("N", a);
        await SeedProject("Other", t);
        var area = await ActivatedArea();
        await Expand(area, Node(area.Roots, "A"));
        var target = Node(area.Roots, "T");
        var gate = _store.Hold(GatedWorkspaceStore.Children(t));
        target.IsExpanded = true; // T's listing (without N) is read now, delivered later
        gate.WaitEntered();
        area.SelectedNode = Node(Node(area.Roots, "A").Children, "N");
        await area.CurrentDetailsLoad; // no UI thread in tests: let the selection settle first
        MoveDialogChooses("T");

        var move = area.MoveCommand.ExecuteAsync();
        gate.Release();
        await move;

        Assert.Equal(["N", "Other"], Names(target.Children));
        Assert.Empty(Names(Node(area.Roots, "A").Children));
        Assert.Equal("N", area.SelectedNode!.Name);
        Assert.Same(target, area.SelectedNode.Parent);
    }

    [Fact]
    public async Task S12_SwitchingTabsWhileTheRootLoadIsPending_LoadsOnce()
    {
        await SeedFolder("F");
        var area = CreateArea();
        var gate = _store.Hold(GatedWorkspaceStore.Children(null));

        area.IsTabSelected = true;
        gate.WaitEntered();
        area.IsTabSelected = false;
        area.IsTabSelected = true;
        area.IsTabSelected = false;
        area.IsTabSelected = true;
        gate.Release();
        await area.RootLoad;

        Assert.Equal(1, _store.Calls(GatedWorkspaceStore.Children(null)));
        Assert.Equal(["F"], Names(area.Roots));
    }

    [Fact]
    public async Task S13_FailedRootLoad_CanBeRetried()
    {
        await SeedFolder("F");
        var area = CreateArea();
        _store.Hold(GatedWorkspaceStore.Children(null)).Fail(new IOException("locked"));

        area.IsTabSelected = true;
        await area.RootLoad;

        Assert.False(area.IsActivated);
        Assert.Equal("Workspace could not be loaded. Nothing was changed.", area.RootLoadError);
        Assert.True(area.RetryLoadCommand.CanExecute(null));
        Assert.False(area.NewFolderCommand.CanExecute(null));

        area.IsTabSelected = false;
        area.IsTabSelected = true; // switching tabs does not retry by itself
        Assert.Equal(1, _store.Calls(GatedWorkspaceStore.Children(null)));

        await area.RetryLoadCommand.ExecuteAsync();

        Assert.True(area.IsActivated);
        Assert.Null(area.RootLoadError);
        Assert.Equal(["F"], Names(area.Roots));
        Assert.Equal(2, _store.Calls(GatedWorkspaceStore.Children(null)));
    }

    [Fact]
    public async Task S14_ProjectRemovedUnderneath_ShowsNoLongerExists_OnlyForTheCurrentSelection()
    {
        var a = await SeedProject("A");
        var b = await SeedProject("B");
        var area = await ActivatedArea();
        using (var connection = _root.Open())
        {
            Db.DeleteProjectBypassingGuards(connection, a); // only possible outside WorkHub since V007
            Db.DeleteProjectBypassingGuards(connection, b);
        }

        area.SelectedNode = Node(area.Roots, "A");
        await area.CurrentDetailsLoad;
        Assert.Equal("This project no longer exists.", area.DetailsMessage);

        var gate = _store.Hold($"details:{b}");
        area.SelectedNode = Node(area.Roots, "B");
        var loadB = area.CurrentDetailsLoad;
        gate.WaitEntered();
        area.SelectedNode = null;
        gate.Release();
        await loadB;

        Assert.Null(area.DetailsMessage);
    }
}
