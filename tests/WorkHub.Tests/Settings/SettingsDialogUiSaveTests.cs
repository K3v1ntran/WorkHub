using System.Text.Json.Nodes;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Settings;

/// <summary>
/// Settings toolbar fix (Phase 21 D10, independent checkpoint): the Settings dialog's Save sends the Repository draft only when
/// the user edited the Repository section in the dialog. Otherwise it sends one <see cref="SettingsEdit"/> holding only the
/// toolbar keys and autosave the user changed, so values another WorkHub saved after the dialog opened (Repository, other
/// toolbars, autosave, unknown members) are kept. Every case runs through <see cref="SettingsViewModel.SaveCommand"/> with two
/// <see cref="SettingsService"/> instances on the same test settings.json; dialog A's store records each edit it commits.
/// </summary>
public sealed class SettingsDialogUiSaveTests : IDisposable
{
    private static readonly WorkspaceToolbar[] All = [WorkspaceToolbar.Tree, WorkspaceToolbar.Selection, WorkspaceToolbar.Content];

    private const string InitialFile = """
        {
          "settingsVersion": 2,
          "futureTopLevel": { "keep": [1, 2, 3] },
          "repository": {
            "svn": { "host": "http://svn.example.test/svn/", "systemPrefix": "A_", "oraclePrefix": "B_", "releasePrefix": "R_", "tortoiseProcPath": "", "futureSvnField": "keep-me" },
            "git": { "host": "http://git.example.test/root/", "systemPrefix": "G_", "oraclePrefix": "H_" },
            "futureRepositorySection": { "nested": { "deep": true } }
          },
          "healthCheck": { "timeoutSeconds": 7 },
          "ui": {
            "lastPrimaryTab": "Workspace",
            "window": { "left": 10, "top": 20, "width": 900, "height": 600, "isMaximized": false, "futureWindowFlag": 1 },
            "workspace": { "showTreeToolbar": true, "showSelectionToolbar": true, "showContentToolbar": true, "futureWorkspaceFlag": "keep" },
            "notes": { "autosave": false, "x-notes": "keep" },
            "futureUiSection": 42
          }
        }
        """;

    private readonly TempWorkHubRoot _root = new();
    private bool _initialized;

    public void Dispose() => _root.Dispose();

    private string File_ => _root.Paths.SettingsFile;

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(File_, json);
    }

    private JsonObject Read() => JsonNode.Parse(File.ReadAllText(File_))!.AsObject();

    private (SettingsService Service, RecordingStore Store) NewInstance()
    {
        var store = new RecordingStore(new JsonSettingsStore(File_));
        var service = new SettingsService(store, _ => true); // the executable check is not under test here
        service.Initialize(store.LoadOrCreate());
        return (service, store);
    }

    private SettingsViewModel NewDialog(SettingsService service, List<bool> closeRequests)
    {
        if (!_initialized)
        {
            _root.CreateInitializer().Initialize();
            _initialized = true;
        }

        var dialog = new SettingsViewModel(service, new SystemService(new SqliteSystemStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile))),
            _root.CreateLegacyImportService(), new FakeDialogs(), () => null);
        dialog.CloseRequested += (_, saved) => closeRequests.Add(saved);
        return dialog;
    }

    private static string PropertyOf(WorkspaceToolbar toolbar) => toolbar switch
    {
        WorkspaceToolbar.Tree => "showTreeToolbar",
        WorkspaceToolbar.Selection => "showSelectionToolbar",
        _ => "showContentToolbar",
    };

    private static string PathOf(WorkspaceToolbar toolbar) => "ui.workspace." + PropertyOf(toolbar);

    private static void Set(SettingsViewModel dialog, WorkspaceToolbar toolbar, bool show)
    {
        switch (toolbar)
        {
            case WorkspaceToolbar.Tree: dialog.ShowTreeToolbar = show; break;
            case WorkspaceToolbar.Selection: dialog.ShowSelectionToolbar = show; break;
            default: dialog.ShowContentToolbar = show; break;
        }
    }

    private static bool? ToolbarIn(WorkspaceToolbarChanges changes, WorkspaceToolbar toolbar) => toolbar switch
    {
        WorkspaceToolbar.Tree => changes.Tree,
        WorkspaceToolbar.Selection => changes.Selection,
        _ => changes.Content,
    };

    private static bool Toolbar(JsonObject document, WorkspaceToolbar toolbar) =>
        document["ui"]!["workspace"]![PropertyOf(toolbar)]!.GetValue<bool>();

    /// <summary>The file before and after are equal by value, except at <paramref name="changedPaths"/>.</summary>
    private static void AssertEqualExcept(JsonObject before, JsonObject after, params string[] changedPaths)
    {
        var expected = JsonNode.Parse(before.ToJsonString())!.AsObject();
        var actual = JsonNode.Parse(after.ToJsonString())!.AsObject();
        foreach (var path in changedPaths)
        {
            Remove(expected, path);
            Remove(actual, path);
        }

        Assert.True(JsonNode.DeepEquals(expected, actual),
            $"Something besides {string.Join(", ", changedPaths)} changed.\nBefore: {expected.ToJsonString()}\nAfter:  {actual.ToJsonString()}");
    }

    private static void Remove(JsonObject root, string path)
    {
        var parts = path.Split('.');
        var node = root;
        foreach (var part in parts[..^1])
            node = (JsonObject)node[part]!;
        node.Remove(parts[^1]);
    }

    /// <summary>B (another WorkHub) saves a new SVN and Git Repository after A's dialog opened.</summary>
    private static async Task SaveNewerRepositoryAsync(SettingsService other)
    {
        var newer = other.CreateRepositoryDraft();
        newer.Svn.Host = "http://svn-new.example.test/svn/";
        newer.Svn.SystemPrefix = "NEW_doc_";
        newer.Git.Host = "http://git-new.example.test/";
        newer.Git.OraclePrefix = "NEW_ora_";
        var saved = await other.SaveRepositoryAsync(newer, RepositoryUsage.None);
        Assert.True(saved.Saved, saved.Error);
    }

    private static void AssertNewerRepository(JsonObject document)
    {
        Assert.Equal("http://svn-new.example.test/svn/", document["repository"]!["svn"]!["host"]!.GetValue<string>());
        Assert.Equal("NEW_doc_", document["repository"]!["svn"]!["systemPrefix"]!.GetValue<string>());
        Assert.Equal("http://git-new.example.test/", document["repository"]!["git"]!["host"]!.GetValue<string>());
        Assert.Equal("NEW_ora_", document["repository"]!["git"]!["oraclePrefix"]!.GetValue<string>());
        Assert.Equal("keep-me", document["repository"]!["svn"]!["futureSvnField"]!.GetValue<string>());
    }

    // ---- 1. Toolbar only, after another instance saved a new Repository ------------------------------------------------

    [Theory]
    [InlineData(WorkspaceToolbar.Tree)]
    [InlineData(WorkspaceToolbar.Selection)]
    [InlineData(WorkspaceToolbar.Content)]
    public async Task ToolbarOnly_KeepsTheRepositoryAnotherInstanceSavedAfterTheDialogOpened(WorkspaceToolbar toolbar)
    {
        WriteFile(InitialFile);
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);                                   // A opens with the initial Repository
        await SaveNewerRepositoryAsync(NewInstance().Service);               // B saves a new SVN / Git Repository
        var before = Read();

        Set(dialog, toolbar, false);                                         // A changes only this toolbar
        Assert.False(dialog.IsRepositoryEdited);
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        Assert.Equal([true], closed);
        var edit = Assert.Single(recorder.Edits);                            // one commit, no Repository draft
        Assert.Null(edit.Repository);
        Assert.Null(edit.NotesAutoSave);
        Assert.Null(edit.Ui);
        Assert.True(edit.AllowReplacingUnreadableFile);
        Assert.All(All, t => Assert.Equal(t == toolbar ? (bool?)false : null, ToolbarIn(edit.Toolbars!, t)));

        var after = Read();
        AssertNewerRepository(after);
        Assert.False(Toolbar(after, toolbar));
        AssertEqualExcept(before, after, PathOf(toolbar));
        Assert.Equal("http://svn-new.example.test/svn/", a.Current.Repository.Svn.Host); // Current = the document written
    }

    [Fact]
    public async Task TwoToolbars_SaveExactlyThoseTwoKeys_AndKeepTheNewerRepository()
    {
        WriteFile(InitialFile);
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);
        await SaveNewerRepositoryAsync(NewInstance().Service);
        var before = Read();

        dialog.ShowTreeToolbar = false;
        dialog.ShowContentToolbar = false;
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        Assert.Equal([true], closed);
        var edit = Assert.Single(recorder.Edits);
        Assert.Null(edit.Repository);
        Assert.Equal(new WorkspaceToolbarChanges { Tree = false, Content = false }, edit.Toolbars);
        var after = Read();
        AssertNewerRepository(after);
        AssertEqualExcept(before, after, PathOf(WorkspaceToolbar.Tree), PathOf(WorkspaceToolbar.Content));
    }

    // ---- 2. Toolbar and autosave, one commit ---------------------------------------------------------------------------

    [Theory]
    [InlineData(WorkspaceToolbar.Tree)]
    [InlineData(WorkspaceToolbar.Selection)]
    [InlineData(WorkspaceToolbar.Content)]
    public async Task ToolbarAndAutosave_AreSavedInOneCommit_AndTheNewerRepositoryIsKept(WorkspaceToolbar toolbar)
    {
        WriteFile(InitialFile);
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);
        await SaveNewerRepositoryAsync(NewInstance().Service);
        var before = Read();

        Set(dialog, toolbar, false);
        dialog.SaveNotesAutomatically = true;
        Assert.False(dialog.ChangesOnlyNotesAutoSave);
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        Assert.Equal([true], closed);
        var edit = Assert.Single(recorder.Edits);                            // both UI changes in the same SettingsEdit
        Assert.Null(edit.Repository);
        Assert.True(edit.NotesAutoSave);
        Assert.All(All, t => Assert.Equal(t == toolbar ? (bool?)false : null, ToolbarIn(edit.Toolbars!, t)));

        var after = Read();
        AssertNewerRepository(after);
        Assert.False(Toolbar(after, toolbar));
        Assert.True(after["ui"]!["notes"]!["autosave"]!.GetValue<bool>());
        Assert.Equal("keep", after["ui"]!["notes"]!["x-notes"]!.GetValue<string>());
        AssertEqualExcept(before, after, PathOf(toolbar), "ui.notes.autosave");
        Assert.True(a.Current.Ui.Notes.AutoSave);
        Assert.False(a.Current.Ui.Workspace.IsShown(toolbar));
    }

    // ---- 3. Keys A did not change keep what B saved ------------------------------------------------------------------

    [Theory]
    [InlineData(WorkspaceToolbar.Tree)]
    [InlineData(WorkspaceToolbar.Selection)]
    [InlineData(WorkspaceToolbar.Content)]
    public async Task OneToolbar_KeepsTheOtherToolbarsAndAutosaveAnotherInstanceSavedAfterTheDialogOpened(WorkspaceToolbar toolbar)
    {
        WriteFile(InitialFile);
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);
        Assert.All(All, t => Assert.True(a.Current.Ui.Workspace.IsShown(t)));
        Assert.False(dialog.SaveNotesAutomatically);

        var b = NewInstance().Service;                                       // B, after A opened: the other two toolbars and autosave
        foreach (var other in All.Where(t => t != toolbar))
            Assert.True((await b.SaveWorkspaceToolbarAsync(other, false)).Saved);
        Assert.True((await b.SaveNotesAutoSaveAsync(true)).Saved);
        var before = Read();
        Assert.All(All.Where(t => t != toolbar), t => Assert.False(Toolbar(before, t)));

        Set(dialog, toolbar, false);                                         // A changes only its toolbar
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        Assert.Equal([true], closed);
        var edit = Assert.Single(recorder.Edits);
        Assert.Null(edit.Repository);
        Assert.Null(edit.NotesAutoSave);                                     // A did not touch autosave
        Assert.All(All, t => Assert.Equal(t == toolbar ? (bool?)false : null, ToolbarIn(edit.Toolbars!, t)));

        var after = Read();
        Assert.All(All, t => Assert.False(Toolbar(after, t)));               // A's own + B's two
        Assert.True(after["ui"]!["notes"]!["autosave"]!.GetValue<bool>());   // B's autosave
        AssertEqualExcept(before, after, PathOf(toolbar));
        Assert.All(All, t => Assert.False(a.Current.Ui.Workspace.IsShown(t)));
        Assert.True(a.Current.Ui.Notes.AutoSave);
    }

    // ---- 4. Control: a real Repository edit keeps the Repository save and its validation ------------------------------

    [Fact]
    public async Task RepositoryEditWithToolbarAndAutosave_ValidIsSavedInOneCommit()
    {
        WriteFile(InitialFile);
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);
        var before = Read();

        dialog.SvnHost = "  http://svn-edited.example.test/svn/  ";          // trimmed by the Repository save
        dialog.ShowSelectionToolbar = false;
        dialog.SaveNotesAutomatically = true;
        Assert.True(dialog.IsRepositoryEdited);
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        Assert.Equal([true], closed);
        var edit = Assert.Single(recorder.Edits);
        Assert.Equal("http://svn-edited.example.test/svn/", edit.Repository!.Svn.Host);
        Assert.Equal(new WorkspaceToolbarChanges { Selection = false }, edit.Toolbars);
        Assert.True(edit.NotesAutoSave);

        var after = Read();
        Assert.Equal("http://svn-edited.example.test/svn/", after["repository"]!["svn"]!["host"]!.GetValue<string>());
        Assert.False(Toolbar(after, WorkspaceToolbar.Selection));
        Assert.True(after["ui"]!["notes"]!["autosave"]!.GetValue<bool>());
        AssertEqualExcept(before, after, "repository.svn.host", PathOf(WorkspaceToolbar.Selection), "ui.notes.autosave");
    }

    [Fact]
    public async Task RepositoryEditWithToolbarAndAutosave_InvalidIsRefused_AndNoUiChangeIsWritten()
    {
        WriteFile(InitialFile);
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);
        var before = File.ReadAllBytes(File_);

        dialog.SvnHost = "not a url";
        dialog.ShowTreeToolbar = false;
        dialog.SaveNotesAutomatically = true;
        await dialog.SaveCommand.ExecuteAsync();

        Assert.NotNull(dialog.SaveError);
        Assert.Contains(dialog.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Empty(closed);                                                // the dialog stays open
        Assert.Empty(recorder.Edits);                                        // refused before the store
        Assert.Equal(before, File.ReadAllBytes(File_));                      // nothing partial
        Assert.False(dialog.ShowTreeToolbar);                                // the draft is kept
        Assert.True(dialog.SaveNotesAutomatically);
        Assert.Equal("not a url", dialog.SvnHost);
        Assert.True(a.Current.Ui.Workspace.ShowTreeToolbar);
        Assert.False(a.Current.Ui.Notes.AutoSave);
    }

    [Fact]
    public async Task RepositoryEditedThenRestoredToTheOpenedValue_IsNotAnEdit_AndKeepsTheNewerRepository()
    {
        WriteFile(InitialFile);
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);
        await SaveNewerRepositoryAsync(NewInstance().Service);

        dialog.SvnHost = "http://typed.example.test/svn/";
        dialog.SvnHost = " http://svn.example.test/svn/ ";                    // back to the opened value (compared after trimming)
        dialog.ShowContentToolbar = false;
        Assert.False(dialog.IsRepositoryEdited);
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Equal([true], closed);
        Assert.Null(Assert.Single(recorder.Edits).Repository);
        AssertNewerRepository(Read());
    }

    // ---- 5. No change, unreadable file, Cancel ---------------------------------------------------------------------------

    [Fact]
    public async Task NoChange_SendsNoRepository_AndLeavesEveryValueAsOnDisk()
    {
        WriteFile(InitialFile);
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);
        await SaveNewerRepositoryAsync(NewInstance().Service);
        var before = Read();

        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        Assert.Equal([true], closed);
        var edit = Assert.Single(recorder.Edits);
        Assert.Null(edit.Repository);
        Assert.Null(edit.Toolbars);
        Assert.Null(edit.NotesAutoSave);
        AssertEqualExcept(before, Read());
    }

    [Fact]
    public async Task ToolbarOnly_ExplicitSave_StillReplacesAnUnreadableFile_KeepingACopy()
    {
        WriteFile("{ not json");
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);

        dialog.ShowTreeToolbar = false;
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        Assert.Equal([true], closed);
        Assert.True(Assert.Single(recorder.Edits).AllowReplacingUnreadableFile);
        Assert.Single(Directory.GetFiles(_root.Paths.ConfigDirectory, "settings.json.invalid-*"));
        Assert.False(new JsonSettingsStore(File_).LoadOrCreate().Settings.Ui.Workspace.ShowTreeToolbar);
    }

    [Fact]
    public async Task Cancel_WritesNothing()
    {
        WriteFile(InitialFile);
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);
        var before = File.ReadAllBytes(File_);

        dialog.ShowTreeToolbar = false;
        dialog.SaveNotesAutomatically = true;
        dialog.CancelCommand.Execute(null);
        await Task.Yield();

        Assert.Equal([false], closed);
        Assert.Empty(recorder.Edits);
        Assert.Equal(before, File.ReadAllBytes(File_));
    }

    [Fact]
    public async Task FailedUiSave_ShowsTheError_AndKeepsTheDialogOpen()
    {
        WriteFile(InitialFile);
        var (a, recorder) = NewInstance();
        var closed = new List<bool>();
        var dialog = NewDialog(a, closed);
        var before = File.ReadAllBytes(File_);
        recorder.Throw = new IOException("disk full (test)");

        dialog.ShowSelectionToolbar = false;
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Contains("disk full (test)", dialog.SaveError);
        Assert.Empty(closed);
        Assert.Equal(before, File.ReadAllBytes(File_));
        Assert.True(a.Current.Ui.Workspace.ShowSelectionToolbar);
    }

    /// <summary>Forwards to the real store and records every edit committed through it (the observation point for "one commit").</summary>
    private sealed class RecordingStore(JsonSettingsStore inner) : ISettingsStore
    {
        public List<SettingsEdit> Edits { get; } = [];

        public Exception? Throw { get; set; }

        public SettingsLoadResult LoadOrCreate() => inner.LoadOrCreate();

        public void Save(AppSettings settings) => inner.Save(settings);

        public SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback)
        {
            Edits.Add(edit);
            if (Throw is not null)
                throw Throw;
            return inner.Update(edit, fallback);
        }

        public SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback, SaveBudget budget)
        {
            Edits.Add(edit);
            if (Throw is not null)
                throw Throw;
            return inner.Update(edit, fallback, budget);
        }

        public UiSettings? PeekUi() => inner.PeekUi();
    }
}
