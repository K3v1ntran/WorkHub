using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// The right-pane content view: listing lifecycle, dialogs with pending writes, moves with a stale observation, and
/// manual checks whose results are accepted only while their token still matches (revision 3 §6.5–6.6, §8).
/// </summary>
public sealed class ProjectContentViewModelTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedProjectContentStore _store;
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _seed;
    private readonly WorkspaceService _workspace;
    private readonly ScriptedLinkChecker _checker = new();
    private readonly RecordingProcessStarter _starter = new();
    private readonly long _alpha;

    public ProjectContentViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var launcher = new ProcessProjectLinkLauncher(_starter, path => !path.Contains("gone", StringComparison.Ordinal), path => !path.Contains("gone", StringComparison.Ordinal));
        _store = new GatedProjectContentStore(new SqliteProjectContentStore(_connections));
        _content = new ProjectContentService(_store, launcher);
        _seed = new ProjectContentService(new SqliteProjectContentStore(_connections), launcher);
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(_connections));
        _alpha = _workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active)).Result.Value!.Id;
    }

    public void Dispose() => _root.Dispose();

    private async Task<ProjectContentViewModel> Opened(LinkCheckCache? cache = null)
    {
        var vm = new ProjectContentViewModel(_content, _checker, _dialogs, NullAppLog.Instance, cache ?? new LinkCheckCache(), _alpha, "Alpha");
        await vm.ActivateAsync();
        return vm;
    }

    private async Task<ProjectFolder> SeedFolder(string name, long? parent = null) => (await _seed.CreateFolderAsync(_alpha, parent, name)).Value!;

    private async Task<ProjectLink> SeedLink(string name, long? folder = null, ProjectLinkType type = ProjectLinkType.Folder, string? target = null) =>
        (await _seed.CreateLinkAsync(_alpha, folder, new ProjectLinkDraft(name, type, target ?? $@"C:\Work\{name}"))).Value!;

    private static ProjectContentItemViewModel Row(ProjectContentViewModel vm, string name) => vm.Items.Single(i => i.Name == name);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not reached.");
            await Task.Delay(5);
        }
    }

    // ---- Integration with the Workspace area -----------------------------------------------------------------------

    [Fact]
    public async Task SelectingAProject_ShowsItsContent_TheMainTreeStaysFolderAndProjectOnly()
    {
        var clients = (await _workspace.CreateFolderAsync(null, "Clients")).Value!;
        await SeedFolder("Docs");
        await SeedLink("Out");
        var area = new WorkspaceAreaViewModel(_workspace, _dialogs, NullAppLog.Instance, _content, _checker);
        area.IsTabSelected = true;
        await area.RootLoad;

        Assert.Equal(["Clients", "Alpha"], area.Roots.Select(n => n.Name));
        Assert.All(area.Roots, n => Assert.True(n.IsFolder || n.IsProject));

        var project = area.Roots.Single(n => n.Name == "Alpha");
        area.SelectedNode = project;
        var content = area.SelectedContent!;
        await content.CurrentLoad;
        Assert.Equal(["Docs", "Out"], content.Items.Select(i => i.Name));
        Assert.False(project.HasChildren); // ProjectFolders never become tree children
        Assert.Empty(project.Children);

        area.SelectedNode = area.Roots.Single(n => n.Name == "Clients");
        Assert.Null(area.SelectedContent);
        Assert.Equal(clients.Id, area.SelectedNode!.Id);
    }

    // ---- Listing lifecycle ------------------------------------------------------------------------------------------

    [Fact]
    public async Task StaleListing_AndItsFailure_NeverReplaceANewerContainer()
    {
        var docs = await SeedFolder("Docs");
        await SeedLink("Inside", docs.Id);
        await SeedLink("AtRoot");
        var vm = await Opened();

        // Enter Docs, held; go back to the root before Docs answers.
        var gate = _store.Hold(GatedProjectContentStore.List(docs.Id));
        vm.SelectedItem = Row(vm, "Docs");
        var entering = vm.OpenCommand.ExecuteAsync();
        gate.WaitEntered();
        await vm.GoUpCommand.ExecuteAsync();
        Assert.Equal(["Docs", "AtRoot"], vm.Items.Select(i => i.Name));

        gate.Release();
        await entering;
        Assert.Null(vm.CurrentFolderId);
        Assert.Equal(["Docs", "AtRoot"], vm.Items.Select(i => i.Name));

        // A stale FAILURE is dropped too.
        var failing = _store.Hold(GatedProjectContentStore.List(docs.Id));
        vm.SelectedItem = Row(vm, "Docs");
        var again = vm.OpenCommand.ExecuteAsync();
        failing.WaitEntered();
        await vm.GoUpCommand.ExecuteAsync();
        failing.Fail(new IOException("database is locked"));
        await again;
        Assert.Null(vm.LoadError);
        Assert.True(vm.IsReady);
    }

    [Fact]
    public async Task LoadFailure_ShowsError_ThenRetryLoads()
    {
        await SeedLink("Out");
        var gate = _store.Hold(GatedProjectContentStore.List(null));
        var vm = new ProjectContentViewModel(_content, _checker, _dialogs, NullAppLog.Instance, new LinkCheckCache(), _alpha, "Alpha");
        var load = vm.ActivateAsync();
        gate.WaitEntered();
        Assert.True(vm.IsLoading);
        gate.Fail(new IOException("database is locked"));
        await load;

        Assert.Equal("Content could not be loaded. Nothing was changed.", vm.LoadError);
        Assert.False(vm.NewFolderCommand.CanExecute(null));
        await vm.RetryCommand.ExecuteAsync();
        Assert.Null(vm.LoadError);
        Assert.Equal(["Out"], vm.Items.Select(i => i.Name));
        Assert.True(vm.NewFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task EmptyContainer_AndMissingProject_States()
    {
        var vm = await Opened();
        Assert.True(vm.IsEmpty);
        Assert.Equal(["Alpha"], vm.Breadcrumb.Select(c => c.Name));

        var gone = new ProjectContentViewModel(_content, _checker, _dialogs, NullAppLog.Instance, new LinkCheckCache(), 9999, "Gone");
        await gone.ActivateAsync();
        Assert.Equal("This project no longer exists.", gone.ContainerMessage);
        Assert.False(gone.IsEmpty);
        Assert.False(gone.NewLinkCommand.CanExecute(null));
    }

    // ---- Dialogs ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NewFolder_DialogStaysOpenWhilePending_ThenTheRowAppears()
    {
        var vm = await Opened();
        var gate = _store.Hold("createFolder");
        _dialogs.OnProjectFolderNameEditor = editor =>
        {
            var closes = new List<bool>();
            editor.CloseRequested += (_, ok) => closes.Add(ok);
            editor.Name = "  Specs ";
            var pending = editor.SaveCommand.ExecuteAsync();
            gate.WaitEntered();
            Assert.False(editor.CanClose);
            Assert.False(editor.CancelCommand.CanExecute(null));
            Assert.False(editor.SaveCommand.CanExecute(null)); // no double submission
            editor.CancelCommand.Execute(null);
            Assert.Empty(closes);
            gate.Release();
            pending.GetAwaiter().GetResult();
            return Assert.Single(closes);
        };

        await vm.NewFolderCommand.ExecuteAsync();

        Assert.Equal(["Specs"], vm.Items.Select(i => i.Name));
        Assert.Same(vm.Items[0], vm.SelectedItem);
    }

    [Fact]
    public async Task FailedWrites_KeepTheDialogOpen_WithTheEnteredValues()
    {
        await SeedFolder("Docs");
        var vm = await Opened();
        ProjectLinkEditorViewModel? seen = null;
        _dialogs.OnProjectLinkEditor = editor =>
        {
            seen = editor;
            editor.Name = "Spec";
            editor.LinkType = ProjectLinkType.File;
            editor.Target = @"relative\spec.docx";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.NotNull(editor.TargetError);
            Assert.True(editor.CanClose);

            var gate = _store.Hold("createLink");
            editor.Target = @"C:\Docs\spec.docx";
            var pending = editor.SaveCommand.ExecuteAsync();
            gate.WaitEntered();
            gate.Fail(new IOException("database is locked"));
            pending.GetAwaiter().GetResult();
            Assert.Contains("database is locked", editor.GeneralError);
            return false; // the user cancels after the failure
        };

        await vm.NewLinkCommand.ExecuteAsync();

        Assert.Equal("Spec", seen!.Name);
        Assert.Equal(@"C:\Docs\spec.docx", seen.Target);
        Assert.Equal(ProjectLinkType.File, seen.LinkType);
        Assert.Equal(["Docs"], vm.Items.Select(i => i.Name)); // nothing was added
        var folder = _dialogs.OnProjectFolderNameEditor;
        _dialogs.OnProjectFolderNameEditor = editor =>
        {
            editor.Name = "docs";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.Contains("already exists", editor.Error);
            Assert.Equal("docs", editor.Name);
            return false;
        };
        await vm.NewFolderCommand.ExecuteAsync();
        Assert.Single(vm.Items);
        _dialogs.OnProjectFolderNameEditor = folder;
    }

    // ---- Moves ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Move_RemovesTheRowFromThisContainer()
    {
        var archive = await SeedFolder("Archive");
        await SeedLink("Out");
        var vm = await Opened();
        _dialogs.OnProjectContentMove = editor =>
        {
            Assert.Equal(["Alpha (project root)", "Archive"], editor.Destinations.Select(d => d.Path));
            Assert.Null(editor.SelectedDestination!.FolderId); // preselects where the item is
            editor.SelectedDestination = editor.Destinations.Single(d => d.FolderId == archive.Id);
            editor.MoveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return editor.Moved;
        };

        vm.SelectedItem = Row(vm, "Out");
        await vm.MoveCommand.ExecuteAsync();

        Assert.Equal(["Archive"], vm.Items.Select(i => i.Name));
        Assert.Equal(["Out"], (await _seed.ListAsync(_alpha, archive.Id)).Listing!.Links.Select(l => l.Name));
    }

    [Fact]
    public async Task TwoWindows_StaleMoveDialog_ReportsMovedMeanwhile_DisablesMove_AndTheViewReloads()
    {
        var p = await SeedFolder("P");
        var q = await SeedFolder("Q");
        var r = await SeedFolder("R");
        await SeedLink("X", p.Id);
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "P");
        await vm.OpenCommand.ExecuteAsync();
        var x = Row(vm, "X");
        vm.SelectedItem = x;

        _dialogs.OnProjectContentMove = editor =>
        {
            // Window B moves X from P to Q and commits while A's dialog is open.
            Assert.Equal(ContentMoveOutcome.Moved, _seed.MoveLinkAsync(x.Id, p.Id, q.Id).Result.Outcome);

            editor.SelectedDestination = editor.Destinations.Single(d => d.FolderId == r.Id);
            editor.MoveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.True(editor.IsStale);
            Assert.Contains("moved meanwhile", editor.Error);
            Assert.False(editor.MoveCommand.CanExecute(null));
            editor.SelectedDestination = editor.Destinations.Single(d => d.FolderId == q.Id);
            Assert.False(editor.MoveCommand.CanExecute(null)); // stays disabled for this dialog
            Assert.True(editor.CanClose);
            return false;
        };

        await vm.MoveCommand.ExecuteAsync();

        Assert.Empty(vm.Items); // reloaded: X is now in Q, not here
        Assert.Equal(["X"], (await _seed.ListAsync(_alpha, q.Id)).Listing!.Links.Select(l => l.Name));
        Assert.Empty((await _seed.ListAsync(_alpha, r.Id)).Listing!.Links);
    }

    [Fact]
    public async Task LinkMovedElsewhereWhileItsEditorIsOpen_SaveKeepsTheMove_AndTheRowLeavesThisListing()
    {
        var destination = await SeedFolder("Destination");
        var link = await SeedLink("Out", target: @"C:\Work\Out");
        var cache = new LinkCheckCache();
        var vm = await Opened(cache);
        var row = Row(vm, "Out");
        vm.SelectedItem = row;
        var check = vm.CheckCommand.ExecuteAsync(); // a check is in flight for the same link
        await WaitUntil(() => _checker.Requests.Count == 1);

        _dialogs.OnProjectLinkEditor = editor =>
        {
            // Another window/process (an independent service and store) moves the link while this editor is open.
            Assert.Equal(ContentMoveOutcome.Moved, _seed.MoveLinkAsync(link.Id, null, destination.Id).Result.Outcome);
            editor.Name = "Out (edited)";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.Null(editor.GeneralError);
            return editor.SavedLink is not null;
        };
        await vm.EditCommand.ExecuteAsync();

        // The edit succeeded without touching the container: the other process's destination stands.
        var stored = (await _seed.ListAsync(_alpha, destination.Id)).Listing!.Links.Single();
        Assert.Equal("Out (edited)", stored.Name);
        Assert.Equal(destination.Id, stored.FolderId);
        Assert.Empty((await _seed.ListAsync(_alpha, null)).Listing!.Links);

        // The root listing no longer shows it, and the selection is valid.
        Assert.Equal(["Destination"], vm.Items.Select(i => i.Name));
        Assert.Null(vm.SelectedItem);
        Assert.False(vm.EditCommand.CanExecute(null));

        // The late check result neither reintroduces nor updates the removed row, and is not remembered.
        _checker.Requests[0].Completion.SetResult(LinkHealthStatus.Missing);
        await check.WaitAsync(Wait);
        await Task.Delay(50);
        Assert.Equal(["Destination"], vm.Items.Select(i => i.Name));
        Assert.NotEqual(LinkHealthStatus.Missing, row.Status);
        Assert.False(vm.StopCheckingCommand.CanExecute(null));

        vm.SelectedItem = Row(vm, "Destination");
        await vm.OpenCommand.ExecuteAsync();
        var moved = Row(vm, "Out (edited)");
        Assert.Equal(LinkHealthStatus.NotChecked, moved.Status);
    }

    [Fact]
    public async Task FolderMovedElsewhereWhileItsRenameIsOpen_SaveKeepsTheMove_AndTheRowLeavesThisListing()
    {
        var destination = await SeedFolder("Destination");
        var docs = await SeedFolder("Docs");
        await SeedLink("Stays");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Docs");

        _dialogs.OnProjectFolderNameEditor = editor =>
        {
            Assert.Equal(ContentMoveOutcome.Moved, _seed.MoveFolderAsync(docs.Id, null, destination.Id).Result.Outcome);
            editor.Name = "Docs (renamed)";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.Null(editor.Error);
            return editor.SavedFolder is not null;
        };
        await vm.EditCommand.ExecuteAsync();

        var stored = (await _seed.ListAsync(_alpha, destination.Id)).Listing!.Folders.Single();
        Assert.Equal("Docs (renamed)", stored.Name);
        Assert.Equal(destination.Id, stored.ParentFolderId);
        Assert.Equal(["Destination"], (await _seed.ListAsync(_alpha, null)).Listing!.Folders.Select(f => f.Name));

        Assert.Equal(["Destination", "Stays"], vm.Items.Select(i => i.Name));
        Assert.Null(vm.SelectedItem);
        Assert.False(vm.MoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task EditsInTheSameContainer_StillUpdateTheRowInPlace()
    {
        await SeedFolder("Alpha docs");
        await SeedLink("Beta");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Alpha docs");
        _dialogs.OnProjectFolderNameEditor = editor =>
        {
            editor.Name = "Zeta docs";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return true;
        };
        await vm.EditCommand.ExecuteAsync();

        Assert.Equal(["Zeta docs", "Beta"], vm.Items.Select(i => i.Name));
        Assert.Same(Row(vm, "Zeta docs"), vm.SelectedItem);
    }

    // ---- Checks -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckAll_IsExclusive_AndStopRevertsRowsAndDiscardsLateResults()
    {
        await SeedLink("A");
        await SeedLink("B");
        await SeedLink("Web", type: ProjectLinkType.Web, target: "https://example.test/");
        var vm = await Opened();

        var run = vm.CheckAllCommand.ExecuteAsync();
        await WaitUntil(() => _checker.Requests.Count == 3);
        Assert.True(vm.IsCheckingAll);
        Assert.False(vm.CheckAllCommand.CanExecute(null)); // no overlapping Check all
        Assert.All(vm.Items, i => Assert.True(i.IsChecking));
        Assert.True(vm.StopCheckingCommand.CanExecute(null));

        vm.StopCheckingCommand.Execute(null);
        await run.WaitAsync(Wait);

        Assert.False(vm.IsCheckingAll);
        Assert.All(_checker.Requests, r => Assert.True(r.Token.IsCancellationRequested)); // queued requests cancelled
        Assert.All(vm.Items, i => Assert.Equal(LinkHealthStatus.NotChecked, i.Status));  // reverted
        _checker.Requests[0].Completion.TrySetResult(LinkHealthStatus.Missing);            // late result
        await Task.Delay(50);
        Assert.Equal(LinkHealthStatus.NotChecked, Row(vm, "A").Status);
        Assert.True(vm.CheckAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckAll_AppliesMatchingResults_WithTheirTime()
    {
        await SeedLink("A");
        await SeedLink("B");
        var vm = await Opened();

        var run = vm.CheckAllCommand.ExecuteAsync();
        await WaitUntil(() => _checker.Requests.Count == 2);
        _checker.Requests.Single(r => r.Target.EndsWith("A")).Completion.SetResult(LinkHealthStatus.Found);
        _checker.Requests.Single(r => r.Target.EndsWith("B")).Completion.SetResult(LinkHealthStatus.TimedOut);
        await run.WaitAsync(Wait);

        Assert.Equal(LinkHealthStatus.Found, Row(vm, "A").Status);
        Assert.Equal(LinkHealthStatus.TimedOut, Row(vm, "B").Status);
        Assert.True(Row(vm, "B").IsStatusUnverified);
        Assert.False(Row(vm, "B").IsStatusProblem); // a timeout is never presented as Missing
        Assert.NotNull(Row(vm, "A").CheckedAt);
        Assert.StartsWith("Found · ", Row(vm, "A").StatusText);
        Assert.False(vm.IsCheckingAll);
    }

    [Fact]
    public async Task NavigationDuringACheck_CancelsIt_AndTheLateResultTouchesNothing()
    {
        var docs = await SeedFolder("Docs");
        await SeedLink("Same", docs.Id, target: @"C:\Work\Out");
        await SeedLink("Out", target: @"C:\Work\Out");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Out");
        var check = vm.CheckCommand.ExecuteAsync();
        await WaitUntil(() => _checker.Requests.Count == 1);

        vm.SelectedItem = Row(vm, "Docs");
        await vm.OpenCommand.ExecuteAsync();
        await check.WaitAsync(Wait);

        Assert.True(_checker.Requests[0].Token.IsCancellationRequested);
        _checker.Requests[0].Completion.TrySetResult(LinkHealthStatus.Missing);
        await Task.Delay(50);
        Assert.Equal(LinkHealthStatus.NotChecked, Row(vm, "Same").Status); // same target, different container: untouched
        Assert.Null(vm.BannerMessage);
    }

    [Fact]
    public async Task EditingTheTargetDuringACheck_ResetsTheStatus_AndRejectsTheOldResult()
    {
        await SeedLink("Out", target: @"C:\Work\Out");
        var vm = await Opened();
        var row = Row(vm, "Out");
        vm.SelectedItem = row;
        var check = vm.CheckCommand.ExecuteAsync();
        await WaitUntil(() => _checker.Requests.Count == 1);

        _dialogs.OnProjectLinkEditor = editor =>
        {
            editor.Target = @"C:\Work\Elsewhere";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return editor.SavedLink is not null;
        };
        await vm.EditCommand.ExecuteAsync();
        Assert.Equal(LinkHealthStatus.NotChecked, row.Status);

        _checker.Requests[0].Completion.SetResult(LinkHealthStatus.Found); // result for the OLD target
        await check.WaitAsync(Wait);
        Assert.Equal(LinkHealthStatus.NotChecked, row.Status);
        Assert.Equal(@"C:\Work\Elsewhere", row.Target);
    }

    [Fact]
    public async Task NameOnlyEdit_KeepsTheAcceptedStatus_TypeChangeResetsIt()
    {
        await SeedLink("Out");
        var vm = await Opened();
        var row = Row(vm, "Out");
        vm.SelectedItem = row;
        var check = vm.CheckCommand.ExecuteAsync();
        await WaitUntil(() => _checker.Requests.Count == 1);
        _checker.Requests[0].Completion.SetResult(LinkHealthStatus.Found);
        await check.WaitAsync(Wait);

        _dialogs.OnProjectLinkEditor = editor =>
        {
            editor.Name = "Build output";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return true;
        };
        await vm.EditCommand.ExecuteAsync();
        Assert.Equal("Build output", row.Name);
        Assert.Equal(LinkHealthStatus.Found, row.Status);

        _dialogs.OnProjectLinkEditor = editor =>
        {
            editor.LinkType = ProjectLinkType.File;
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return true;
        };
        await vm.EditCommand.ExecuteAsync();
        Assert.Equal(LinkHealthStatus.NotChecked, row.Status);
    }

    [Fact]
    public async Task AcceptedResults_AreRememberedForTheSession_OnlyWhileTypeAndTargetMatch()
    {
        var docs = await SeedFolder("Docs");
        var link = await SeedLink("Out");
        var cache = new LinkCheckCache();
        var vm = await Opened(cache);
        vm.SelectedItem = Row(vm, "Out");
        var check = vm.CheckCommand.ExecuteAsync();
        await WaitUntil(() => _checker.Requests.Count == 1);
        _checker.Requests[0].Completion.SetResult(LinkHealthStatus.Missing);
        await check.WaitAsync(Wait);

        vm.SelectedItem = Row(vm, "Docs");
        await vm.OpenCommand.ExecuteAsync();
        await vm.GoUpCommand.ExecuteAsync();
        Assert.Equal(LinkHealthStatus.Missing, Row(vm, "Out").Status);

        // Another view of the same Project in this session sees it too; a changed target does not.
        Assert.Equal(LinkHealthStatus.Missing, Row(await Opened(cache), "Out").Status);
        await _seed.UpdateLinkAsync(link.Id, new ProjectLinkDraft("Out", ProjectLinkType.Folder, @"C:\Work\New"));
        Assert.Equal(LinkHealthStatus.NotChecked, Row(await Opened(cache), "Out").Status);
        Assert.True(docs.Id > 0);
    }

    [Fact]
    public async Task OpenLink_FailureShowsABanner_AndNothingStarts()
    {
        await SeedLink("gone folder", target: @"C:\Work\gone");
        await SeedLink("Site", type: ProjectLinkType.Web, target: "https://example.test/x");
        var vm = await Opened();

        vm.SelectedItem = Row(vm, "Site");
        await vm.OpenCommand.ExecuteAsync();
        Assert.Null(vm.BannerMessage);
        Assert.Equal("https://example.test/x", Assert.Single(_starter.Started).FileName);

        vm.SelectedItem = Row(vm, "gone folder");
        await vm.OpenCommand.ExecuteAsync();
        Assert.Equal("Folder could not be found.", vm.BannerMessage);
        Assert.Single(_starter.Started);
    }
}
