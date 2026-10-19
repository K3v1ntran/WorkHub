using System.Text.Json.Nodes;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Settings;

/// <summary>
/// Phase 13A (clarified): the tree, selection and content toolbars have independent preferences
/// (<c>ui.workspace.showTreeToolbar</c>, <c>showSelectionToolbar</c>, <c>showContentToolbar</c>), each saved as its own field
/// patch through the coordinated settings update.
/// </summary>
public sealed class WorkspaceToolbarPreferencesTests : IDisposable
{
    private static readonly WorkspaceToolbar[] All = [WorkspaceToolbar.Tree, WorkspaceToolbar.Selection, WorkspaceToolbar.Content];

    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string File_ => _root.Paths.SettingsFile;

    private SettingsService NewService()
    {
        var store = new JsonSettingsStore(File_);
        var service = new SettingsService(store, _ => true);
        service.Initialize(store.LoadOrCreate());
        return service;
    }

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(File_, json);
    }

    private JsonObject Saved() => JsonNode.Parse(File.ReadAllText(File_))!.AsObject();

    private static string PropertyOf(WorkspaceToolbar toolbar) => toolbar switch
    {
        WorkspaceToolbar.Tree => "showTreeToolbar",
        WorkspaceToolbar.Selection => "showSelectionToolbar",
        _ => "showContentToolbar",
    };

    private SettingsViewModel NewDialog(SettingsService service)
    {
        _root.CreateInitializer().Initialize();
        return new SettingsViewModel(service, new SystemService(new SqliteSystemStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile))),
            _root.CreateLegacyImportService(), new FakeDialogs(), () => null);
    }

    // ---- Defaults and compatibility ------------------------------------------------------------------------------------

    [Fact]
    public void MissingProperties_DefaultToShown_ForAllThreeToolbars()
    {
        WriteFile("""{ "settingsVersion": 2, "ui": { "lastPrimaryTab": "Workspace" } }""");

        var workspace = NewService().Current.Ui.Workspace;

        Assert.All(All, toolbar => Assert.True(workspace.IsShown(toolbar)));
        Assert.True(new AppSettings().Ui.Workspace.ShowTreeToolbar && new AppSettings().Ui.Workspace.ShowContentToolbar);
    }

    [Fact]
    public void ExistingShowSelectionToolbarFalse_StillHidesOnlyTheSelectionToolbar()
    {
        // A file saved by the earlier 13A build: only the selection property exists. It keeps its meaning and is not
        // reinterpreted for the tree or content toolbars.
        WriteFile("""{ "settingsVersion": 2, "ui": { "workspace": { "showSelectionToolbar": false } } }""");

        var workspace = NewService().Current.Ui.Workspace;

        Assert.False(workspace.ShowSelectionToolbar);
        Assert.True(workspace.ShowTreeToolbar);
        Assert.True(workspace.ShowContentToolbar);
    }

    // ---- Independent field patches -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WorkspaceToolbar.Tree)]
    [InlineData(WorkspaceToolbar.Selection)]
    [InlineData(WorkspaceToolbar.Content)]
    public async Task SavingOneToolbar_WritesOnlyItsProperty_AndKeepsUnknownWorkspaceMembers(WorkspaceToolbar toolbar)
    {
        WriteFile("""{ "settingsVersion": 2, "ui": { "workspace": { "futureWorkspaceFlag": "keep" } } }""");
        var service = NewService();

        var result = await service.SaveWorkspaceToolbarAsync(toolbar, false);

        Assert.True(result.Saved, result.Error);
        var workspace = Saved()["ui"]!["workspace"]!.AsObject();
        Assert.Equal(["futureWorkspaceFlag", PropertyOf(toolbar)], workspace.Select(p => p.Key).Order());
        Assert.False(workspace[PropertyOf(toolbar)]!.GetValue<bool>());
        Assert.All(All, other => Assert.Equal(other != toolbar, service.Current.Ui.Workspace.IsShown(other)));
    }

    [Fact]
    public async Task SavingOneToolbar_NeverOverwritesAnotherToolbarSavedByAnotherInstance()
    {
        var first = NewService();
        var second = NewService(); // its Current still says every toolbar is shown
        Assert.True((await first.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Tree, false)).Saved);

        Assert.True((await second.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Content, false)).Saved);

        var saved = new JsonSettingsStore(File_).LoadOrCreate().Settings.Ui.Workspace;
        Assert.False(saved.ShowTreeToolbar); // the other instance's value survives the stale snapshot
        Assert.True(saved.ShowSelectionToolbar);
        Assert.False(saved.ShowContentToolbar);
        Assert.False(second.Current.Ui.Workspace.ShowTreeToolbar); // Current = the merged document that was written
    }

    [Fact]
    public async Task Dialog_SavesOnlyTheTogglesChangedInIt()
    {
        var service = NewService();
        var dialog = NewDialog(service);
        Assert.True(dialog.ShowTreeToolbar && dialog.ShowSelectionToolbar && dialog.ShowContentToolbar);
        Assert.True((await service.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Tree, false)).Saved); // a menu, after the dialog opened

        dialog.ShowContentToolbar = false;
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        var workspace = Saved()["ui"]!["workspace"]!.AsObject();
        Assert.False(workspace["showTreeToolbar"]!.GetValue<bool>()); // unchanged in the dialog → not written back
        Assert.False(workspace["showContentToolbar"]!.GetValue<bool>());
        Assert.True(workspace["showSelectionToolbar"]!.GetValue<bool>()); // unchanged
    }

    // ---- All hidden: access remains and Settings restores ---------------------------------------------------------------

    [Fact]
    public async Task AllHidden_IsRestoredFromTheSettingsDialog()
    {
        var service = NewService();
        foreach (var toolbar in All)
            Assert.True((await service.SaveWorkspaceToolbarAsync(toolbar, false)).Saved);

        var dialog = NewDialog(service);
        Assert.False(dialog.ShowTreeToolbar || dialog.ShowSelectionToolbar || dialog.ShowContentToolbar);
        dialog.ShowTreeToolbar = dialog.ShowSelectionToolbar = dialog.ShowContentToolbar = true;
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        Assert.All(All, toolbar => Assert.True(service.Current.Ui.Workspace.IsShown(toolbar)));
        Assert.All(All, toolbar => Assert.True(new JsonSettingsStore(File_).LoadOrCreate().Settings.Ui.Workspace.IsShown(toolbar)));
    }

    [Fact]
    public async Task QuickSave_OfAnyToolbar_RefusesAnUnreadableFile_AndChangesNothing()
    {
        WriteFile("{ not json");
        var service = NewService();
        var before = File.ReadAllText(File_);

        foreach (var toolbar in All)
        {
            var result = await service.SaveWorkspaceToolbarAsync(toolbar, false);
            Assert.False(result.Saved);
            Assert.NotNull(result.Error);
        }

        Assert.Equal(before, File.ReadAllText(File_));
        Assert.All(All, toolbar => Assert.True(service.Current.Ui.Workspace.IsShown(toolbar)));
    }
}
