using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Settings;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 13A view-model contracts: the Info drawer follows the selection (§5.4), the selection toolbar preference (§5.3) only
/// hides buttons (every command stays usable), the title-line location, and the technical status line (§5.1).
/// </summary>
public sealed class WorkspaceInfoAndToolbarTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly WorkspaceService _workspace;

    public WorkspaceInfoAndToolbarTests()
    {
        _root.CreateInitializer().Initialize();
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose() => _root.Dispose();

    private async Task<WorkspaceAreaViewModel> ActivatedArea(SettingsService? settings = null)
    {
        var area = new WorkspaceAreaViewModel(_workspace, _dialogs, NullAppLog.Instance, settings: settings);
        area.IsTabSelected = true;
        await area.RootLoad;
        return area;
    }

    private SettingsService Settings()
    {
        var store = new JsonSettingsStore(_root.Paths.SettingsFile);
        var service = new SettingsService(store, _ => true);
        service.Initialize(store.LoadOrCreate());
        return service;
    }

    private async Task<long> Folder(string name, long? parent = null) => (await _workspace.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null, string? owner = null) =>
        (await _workspace.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active, $"About {name}", owner, $"PM of {name}"))).Value!.Id;

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) => nodes.Single(n => n.Name == name);

    [Fact]
    public async Task Info_OpensOnlyWithASelection_AndFollowsTheSelectedProject()
    {
        await Project("Alpha", owner: "Owner A");
        await Project("Beta", owner: "Owner B");
        var area = await ActivatedArea();

        Assert.False(area.ToggleInfoCommand.CanExecute(null)); // nothing selected
        area.IsInfoOpen = true;
        Assert.False(area.IsInfoOpen);

        area.SelectedNode = Node(area.Roots, "Alpha");
        await area.CurrentDetailsLoad;
        area.ToggleInfoCommand.Execute(null);
        Assert.True(area.IsInfoOpen);
        Assert.Equal("Owner A", area.SelectedProject!.Owner);
        Assert.Equal("PM of Alpha", area.SelectedProject.ProjectManager);
        Assert.Equal("About Alpha", area.SelectedProject.Description);

        area.SelectedNode = Node(area.Roots, "Beta"); // follows the selection; stays open
        await area.CurrentDetailsLoad;
        Assert.True(area.IsInfoOpen);
        Assert.Equal("Owner B", area.SelectedProject!.Owner);

        area.CloseInfoCommand.Execute(null);
        Assert.False(area.IsInfoOpen);
    }

    [Fact]
    public async Task Info_StaysOpenThroughTheTreesTransientDeselect_ButClosesOnARealDeselect()
    {
        // GUI finding: a TreeView selection change deselects the old node (IsSelected=false → SelectedNode=null) before the
        // new one is selected. Info must survive that transient and still close when nothing stays selected.
        await Project("Alpha");
        await Project("Beta");
        var context = new SingleThreadContext();
        try
        {
            await context.RunAsync(async () =>
            {
                var area = await ActivatedArea(); // created on the context: the view model captures it
                var alpha = Node(area.Roots, "Alpha");
                var beta = Node(area.Roots, "Beta");
                alpha.IsSelected = true;
                area.IsInfoOpen = true;

                alpha.IsSelected = false; // the tree's order: old node first ...
                beta.IsSelected = true;   // ... then the new one, in the same UI work item
                await Task.Yield();       // let the deferred check run
                await Task.Delay(50);
                Assert.Same(beta, area.SelectedNode);
                Assert.True(area.IsInfoOpen);

                beta.IsSelected = false;  // a real deselect
                await Task.Delay(50);
                Assert.Null(area.SelectedNode);
                Assert.False(area.IsInfoOpen);
            }, TimeSpan.FromSeconds(20));
        }
        finally
        {
            Assert.True(context.Stop(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Info_ForAFolder_ShowsItsCounts_AndClosesWhenNothingIsSelected()
    {
        var clients = await Folder("Clients");
        await Project("Inside", clients);
        await Folder("Sub", clients);
        var area = await ActivatedArea();

        area.SelectedNode = Node(area.Roots, "Clients");
        area.IsInfoOpen = true;
        await area.CurrentFolderInfoLoad;
        Assert.Equal(2, area.SelectedFolderInfo!.ActiveChildren);

        area.SelectedNode = null;
        Assert.False(area.IsInfoOpen);
        Assert.Null(area.SelectedFolderInfo);
    }

    [Fact]
    public async Task Info_ClosesWhenTheTrashOrArchivedPanelOpens()
    {
        await Project("Alpha");
        var area = await ActivatedArea();
        area.SelectedNode = Node(area.Roots, "Alpha");
        area.IsInfoOpen = true;

        await area.OpenTrashCommand.ExecuteAsync();

        Assert.False(area.IsInfoOpen);
        Assert.False(area.ToggleInfoCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectedLocation_IsTheParentPath_OrWorkspaceAtTheTop()
    {
        var clients = await Folder("Clients");
        var tokyo = await Folder("Tokyo", clients);
        await Project("Alpha", tokyo);
        await Project("Top");
        var area = await ActivatedArea();

        area.SelectedNode = Node(area.Roots, "Top");
        Assert.Equal("Workspace", area.SelectedLocation);

        var clientsNode = Node(area.Roots, "Clients");
        clientsNode.IsExpanded = true;
        Assert.True(await area.EnsureChildrenLoadedAsync(clientsNode));
        var tokyoNode = Node(clientsNode.Children, "Tokyo");
        tokyoNode.IsExpanded = true;
        Assert.True(await area.EnsureChildrenLoadedAsync(tokyoNode));
        area.SelectedNode = Node(tokyoNode.Children, "Alpha");
        Assert.Equal("Clients / Tokyo", area.SelectedLocation);
    }

    [Fact]
    public async Task ToolbarPreference_DefaultsToShown_AndHidingItKeepsEveryActionCommandUsable()
    {
        await Project("Alpha");
        var withoutSettings = await ActivatedArea();
        Assert.True(withoutSettings.ShowSelectionToolbar);
        Assert.Null(withoutSettings.Preferences);

        var settings = Settings();
        var area = await ActivatedArea(settings);
        Assert.True(area.ShowSelectionToolbar);
        var notified = 0;
        area.PropertyChanged += (_, e) => notified += e.PropertyName == nameof(WorkspaceAreaViewModel.ShowSelectionToolbar) ? 1 : 0;

        await area.Preferences!.HideSelectionToolbarCommand.ExecuteAsync();

        Assert.False(area.ShowSelectionToolbar);
        Assert.True(notified > 0);
        Assert.False(new JsonSettingsStore(_root.Paths.SettingsFile).LoadOrCreate().Settings.Ui.Workspace.ShowSelectionToolbar); // persisted
        // Only buttons hide: More / the context menu / keys use the same commands, which stay executable.
        area.SelectedNode = Node(area.Roots, "Alpha");
        Assert.True(area.EditProjectCommand.CanExecute(null));
        Assert.True(area.MoveCommand.CanExecute(null));
        Assert.True(area.ArchiveProjectCommand.CanExecute(null));
        Assert.True(area.TrashProjectCommand.CanExecute(null));
        Assert.True(area.ToggleInfoCommand.CanExecute(null));

        // A new window (restart) reads the saved value.
        var restarted = await ActivatedArea(Settings());
        Assert.False(restarted.ShowSelectionToolbar);
        await restarted.Preferences!.ShowSelectionToolbarCommand.ExecuteAsync();
        Assert.True(restarted.ShowSelectionToolbar);
    }

    [Fact]
    public async Task EachToolbarMenuCommand_ChangesOnlyItsOwnToolbar()
    {
        var area = await ActivatedArea(Settings());
        var preferences = area.Preferences!;
        var notified = new List<string?>();
        area.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        await preferences.HideContentToolbarCommand.ExecuteAsync();
        Assert.Equal((true, true, false), (area.ShowTreeToolbar, area.ShowSelectionToolbar, area.ShowContentToolbar));

        await preferences.HideTreeToolbarCommand.ExecuteAsync();
        Assert.Equal((false, true, false), (area.ShowTreeToolbar, area.ShowSelectionToolbar, area.ShowContentToolbar));

        await preferences.ShowContentToolbarCommand.ExecuteAsync();
        Assert.Equal((false, true, true), (area.ShowTreeToolbar, area.ShowSelectionToolbar, area.ShowContentToolbar));
        Assert.Contains(nameof(WorkspaceAreaViewModel.ShowTreeToolbar), notified);
        Assert.Contains(nameof(WorkspaceAreaViewModel.ShowContentToolbar), notified);

        var saved = new JsonSettingsStore(_root.Paths.SettingsFile).LoadOrCreate().Settings.Ui.Workspace;
        Assert.Equal((false, true, true), (saved.ShowTreeToolbar, saved.ShowSelectionToolbar, saved.ShowContentToolbar));
    }

    [Fact]
    public async Task AllToolbarsHidden_KeepsEveryActionCommandUsable_AndEachToolbarRestorable()
    {
        await Project("Alpha");
        var area = await ActivatedArea(Settings());
        foreach (var toolbar in new[] { WorkspaceToolbar.Tree, WorkspaceToolbar.Selection, WorkspaceToolbar.Content })
            await area.Preferences!.SetToolbarAsync(toolbar, false);
        Assert.False(area.ShowTreeToolbar || area.ShowSelectionToolbar || area.ShowContentToolbar);

        // More (⋯), the menus and the keys use the same commands, which the toolbar switches never disable.
        Assert.True(area.NewFolderCommand.CanExecute(null));
        Assert.True(area.NewProjectCommand.CanExecute(null));
        area.SelectedNode = Node(area.Roots, "Alpha");
        Assert.True(area.RenameCommand.CanExecute(null));
        Assert.True(area.EditProjectCommand.CanExecute(null));
        Assert.True(area.MoveCommand.CanExecute(null));
        Assert.True(area.ArchiveProjectCommand.CanExecute(null));
        Assert.True(area.TrashProjectCommand.CanExecute(null));
        Assert.True(area.ToggleInfoCommand.CanExecute(null));
        Assert.True(area.OpenArchivedCommand.CanExecute(null));
        Assert.True(area.OpenTrashCommand.CanExecute(null));

        var preferences = area.Preferences!;
        Assert.True(preferences.ShowTreeToolbarCommand.CanExecute(null));
        Assert.True(preferences.ShowSelectionToolbarCommand.CanExecute(null));
        Assert.True(preferences.ShowContentToolbarCommand.CanExecute(null));
        Assert.False(preferences.HideTreeToolbarCommand.CanExecute(null));
    }

    [Fact]
    public async Task ToolbarPreference_LoadedAtStartup_AfterTheViewModelWasCreated_IsApplied()
    {
        // GUI finding: the main window's view models exist before startup loads settings.json. A saved "hidden" must still
        // take effect once the settings are initialized.
        var saved = Settings();
        Assert.True((await saved.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Selection, false)).Saved);

        var store = new JsonSettingsStore(_root.Paths.SettingsFile);
        var startup = new SettingsService(store, _ => true); // not initialized yet: defaults (toolbar shown)
        var area = await ActivatedArea(startup);
        Assert.True(area.ShowSelectionToolbar);

        startup.Initialize(store.LoadOrCreate()); // what MainViewModel.InitializeAsync does

        Assert.False(area.ShowSelectionToolbar);
    }

    [Theory]
    [InlineData(DailyBackupOutcome.Created, "workhub_daily_x.db", "Database schema v8 · Today's backup: workhub_daily_x.db (created now)")]
    [InlineData(DailyBackupOutcome.AlreadyDone, "workhub_daily_y.db", "Database schema v8 · Today's backup: workhub_daily_y.db")]
    [InlineData(DailyBackupOutcome.Failed, null, "Database schema v8 · No backup was made today (see the notice above the tabs).")]
    public void TechnicalStatus_DescribesSchemaAndTodaysBackup(DailyBackupOutcome outcome, string? file, string expected)
    {
        Assert.Equal(expected, MainViewModel.TechnicalStatusFor(8, new DailyBackupResult(outcome, FileName: file)));
        Assert.Equal("Database schema v8 · Daily backup did not run.", MainViewModel.TechnicalStatusFor(8, null));
    }
}
