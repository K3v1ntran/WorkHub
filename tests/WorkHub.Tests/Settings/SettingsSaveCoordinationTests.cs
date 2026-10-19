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
/// Phase 13A settings save coordination (§6.1 S1-S8): one save gate per instance, commit-time section merge, preservation of
/// unknown members, the cross-process lock, Current = the document actually saved, and failure behaviour.
/// </summary>
public sealed class SettingsSaveCoordinationTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string File_ => _root.Paths.SettingsFile;

    private (SettingsService Service, JsonSettingsStore Store) NewInstance(TimeSpan? lockTimeout = null)
    {
        var store = new JsonSettingsStore(File_, lockTimeout: lockTimeout);
        var service = new SettingsService(store, _ => true); // repository validation is not under test here
        service.Initialize(store.LoadOrCreate());
        return (service, store);
    }

    private static JsonObject Parse(string path) =>
        JsonNode.Parse(File.ReadAllText(path), new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject();

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(File_, json);
    }

    private const string FileWithUnknownMembers = """
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
            "window": { "left": 10, "top": 20, "width": 900, "height": 600, "isMaximized": false },
            "workspace": { "showSelectionToolbar": true, "futureWorkspaceFlag": "keep" },
            "futureUiSection": 42
          }
        }
        """;

    private static void AssertEqualExcept(JsonObject before, JsonObject after, params string[] changedPaths)
    {
        foreach (var path in changedPaths)
        {
            Remove(before, path);
            Remove(after, path);
        }

        Assert.True(JsonNode.DeepEquals(before, after), $"Unrelated settings changed.\nBefore: {before.ToJsonString()}\nAfter:  {after.ToJsonString()}");
    }

    private static void Remove(JsonObject root, string path)
    {
        var parts = path.Split('.');
        var node = root;
        foreach (var part in parts[..^1])
            node = (JsonObject)node[part]!;
        node.Remove(parts[^1]);
    }

    // ---- Unknown members (A13 and the review clarification) ----------------------------------------------------------

    [Fact]
    public async Task UiSave_KeepsEveryOtherMember_IncludingUnknownOnesInsideTheEditedSection()
    {
        WriteFile(FileWithUnknownMembers);
        var (service, _) = NewInstance();
        var before = Parse(File_);

        var result = await service.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Selection, false);

        Assert.True(result.Saved, result.Error);
        var after = Parse(File_);
        Assert.False(after["ui"]!["workspace"]!["showSelectionToolbar"]!.GetValue<bool>());
        Assert.Equal("keep", after["ui"]!["workspace"]!["futureWorkspaceFlag"]!.GetValue<string>()); // inside the edited object
        AssertEqualExcept(before, after, "ui.workspace.showSelectionToolbar");
        Assert.False(service.Current.Ui.Workspace.ShowSelectionToolbar);
    }

    [Fact]
    public async Task RepositorySave_ReplacesTheTypedValues_AndKeepsUnknownMembersInsideRepository()
    {
        WriteFile(FileWithUnknownMembers);
        var (service, _) = NewInstance();
        var before = Parse(File_);
        var draft = service.CreateRepositoryDraft();
        draft.Svn.Host = "http://svn2.example.test/svn/";

        var result = await service.SaveRepositoryAsync(draft, RepositoryUsage.None);

        Assert.True(result.Saved, result.Error);
        var after = Parse(File_);
        Assert.Equal("http://svn2.example.test/svn/", after["repository"]!["svn"]!["host"]!.GetValue<string>());
        Assert.Equal("keep-me", after["repository"]!["svn"]!["futureSvnField"]!.GetValue<string>());
        Assert.True(after["repository"]!["futureRepositorySection"]!["nested"]!["deep"]!.GetValue<bool>());
        AssertEqualExcept(before, after, "repository.svn.host");
        Assert.Equal("http://svn2.example.test/svn/", service.Current.Repository.Svn.Host);
    }

    // ---- Another process saved meanwhile (A14) ------------------------------------------------------------------------

    [Fact]
    public async Task StaleCurrent_AfterAnotherProcessSaves_KeepsTheirChange_AndCurrentIsTheSavedDocument()
    {
        var (first, _) = NewInstance();
        var (second, _) = NewInstance(); // both loaded the same file; second's Current becomes stale below
        var draft = first.CreateRepositoryDraft();
        draft.Git.Host = "http://other-process.example.test/root/";
        Assert.True((await first.SaveRepositoryAsync(draft, RepositoryUsage.None)).Saved);

        var result = await second.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Selection, false);

        Assert.True(result.Saved, result.Error);
        var saved = new JsonSettingsStore(File_).LoadOrCreate().Settings;
        Assert.Equal("http://other-process.example.test/root/", saved.Repository.Git.Host); // not overwritten from the stale snapshot
        Assert.False(saved.Ui.Workspace.ShowSelectionToolbar);
        // Current is built from the merged document that was written, including the other process's section.
        Assert.Equal("http://other-process.example.test/root/", second.Current.Repository.Git.Host);
        Assert.False(second.Current.Ui.Workspace.ShowSelectionToolbar);
    }

    // ---- Overlapping saves in one instance (A11) ----------------------------------------------------------------------

    private sealed class GatedStore(ISettingsStore inner) : ISettingsStore
    {
        private int _active;

        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim ReleaseFirst { get; } = new(false);

        public int MaxConcurrent { get; private set; }

        public int Calls { get; private set; }

        public SettingsLoadResult LoadOrCreate() => inner.LoadOrCreate();

        public void Save(AppSettings settings) => inner.Save(settings);

        public SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback)
        {
            var now = Interlocked.Increment(ref _active);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            try
            {
                if (++Calls == 1)
                {
                    FirstEntered.TrySetResult();
                    Assert.True(ReleaseFirst.Wait(TimeSpan.FromSeconds(10)));
                }

                return inner.Update(edit, fallback);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    [Fact]
    public async Task OverlappingRepositoryAndUiSaves_AreSerialized_AndBothArePersisted()
    {
        var inner = new JsonSettingsStore(File_);
        var gated = new GatedStore(inner);
        var service = new SettingsService(gated, _ => true);
        service.Initialize(gated.LoadOrCreate());
        var draft = service.CreateRepositoryDraft();
        draft.Svn.Host = "http://overlap.example.test/svn/";

        var repositorySave = service.SaveRepositoryAsync(draft, RepositoryUsage.None);
        await gated.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the repository save is inside the store
        var uiSave = service.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Selection, false);
        await Task.Delay(100);
        Assert.Equal(1, gated.Calls); // the UI save waits at the gate
        gated.ReleaseFirst.Set();

        Assert.True((await repositorySave).Saved);
        Assert.True((await uiSave).Saved);
        Assert.Equal(1, gated.MaxConcurrent);
        var saved = new JsonSettingsStore(File_).LoadOrCreate().Settings;
        Assert.Equal("http://overlap.example.test/svn/", saved.Repository.Svn.Host);
        Assert.False(saved.Ui.Workspace.ShowSelectionToolbar);
        Assert.Equal("http://overlap.example.test/svn/", service.Current.Repository.Svn.Host);
        Assert.False(service.Current.Ui.Workspace.ShowSelectionToolbar);
    }

    // ---- The dialog's unchanged field (A12) ---------------------------------------------------------------------------

    private SettingsViewModel NewDialog(SettingsService service)
    {
        _root.CreateInitializer().Initialize();
        return new SettingsViewModel(service, new SystemService(new SqliteSystemStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile))),
            _root.CreateLegacyImportService(), new FakeDialogs(), () => null);
    }

    [Fact]
    public async Task DialogWithAnUnchangedToggle_DoesNotOverwriteAToolbarValueSavedAfterItOpened()
    {
        var (service, _) = NewInstance();
        var dialog = NewDialog(service);
        Assert.True(dialog.ShowSelectionToolbar);
        Assert.True((await service.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Selection, false)).Saved); // the strip menu, after the dialog opened

        dialog.GitHost = "http://dialog.example.test/root/";
        await dialog.SaveCommand.ExecuteAsync();

        var saved = new JsonSettingsStore(File_).LoadOrCreate().Settings;
        Assert.Equal("http://dialog.example.test/root/", saved.Repository.Git.Host);
        Assert.False(saved.Ui.Workspace.ShowSelectionToolbar); // unchanged in the dialog → not written
    }

    [Fact]
    public async Task DialogWithAChangedToggle_SavesTheDialogValue()
    {
        var (service, _) = NewInstance();
        var dialog = NewDialog(service);

        dialog.ShowSelectionToolbar = false;
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        Assert.False(new JsonSettingsStore(File_).LoadOrCreate().Settings.Ui.Workspace.ShowSelectionToolbar);
        Assert.False(service.Current.Ui.Workspace.ShowSelectionToolbar);
    }

    // ---- Lock timeout and persistence failure (A14 / A15) -------------------------------------------------------------

    private string[] TempFiles() => Directory.GetFiles(_root.Paths.ConfigDirectory, "settings.json.*.tmp");

    [Fact]
    public async Task LockHeldByAnotherProcess_FailsAfterTheTimeout_AndChangesNothing()
    {
        var (service, store) = NewInstance(TimeSpan.FromMilliseconds(300));
        var before = File.ReadAllBytes(File_);
        using (new FileStream(store.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await service.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Selection, false);

            Assert.False(result.Saved);
            Assert.Contains("another WorkHub window", result.Error);
        }

        Assert.True(service.Current.Ui.Workspace.ShowSelectionToolbar); // Current unchanged
        Assert.Equal(before, File.ReadAllBytes(File_));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task WriteFailure_IsReported_CurrentIsUnchanged_AndOnlyItsOwnTempFileIsRemoved()
    {
        var (service, _) = NewInstance();
        var before = File.ReadAllBytes(File_);
        var foreignTemp = Path.Combine(_root.Paths.ConfigDirectory, "settings.json.0123456789abcdef0123456789abcdef.tmp");
        File.WriteAllText(foreignTemp, "another operation's temp file");
        File.SetAttributes(File_, FileAttributes.ReadOnly); // the replace step fails
        try
        {
            var result = await service.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Selection, false);

            Assert.False(result.Saved);
            Assert.StartsWith("Settings could not be saved", result.Error);
        }
        finally
        {
            File.SetAttributes(File_, FileAttributes.Normal);
        }

        Assert.True(service.Current.Ui.Workspace.ShowSelectionToolbar);
        Assert.Equal(before, File.ReadAllBytes(File_));
        Assert.Equal([foreignTemp], TempFiles()); // ours removed; the foreign one kept
    }

    // ---- Unreadable file (A15) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task UnreadableFile_IsNeverReplacedByTheQuickToolbarSave()
    {
        WriteFile("{ not json");
        var (service, _) = NewInstance();

        var result = await service.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Selection, false);

        Assert.False(result.Saved);
        Assert.Contains("could not be read", result.Error);
        Assert.Equal("{ not json", File.ReadAllText(File_));
        Assert.Empty(Directory.GetFiles(_root.Paths.ConfigDirectory, "settings.json.invalid-*"));
    }

    [Fact]
    public async Task UnreadableFile_TheExplicitDialogSave_ReplacesItAndKeepsACopy()
    {
        WriteFile("{ not json");
        var (service, _) = NewInstance();
        var draft = service.CreateRepositoryDraft();
        draft.Svn.Host = "http://recovered.example.test/svn/";

        var result = await service.SaveRepositoryAsync(draft, RepositoryUsage.None);

        Assert.True(result.Saved, result.Error);
        Assert.Equal("http://recovered.example.test/svn/", new JsonSettingsStore(File_).LoadOrCreate().Settings.Repository.Svn.Host);
        var copy = Assert.Single(Directory.GetFiles(_root.Paths.ConfigDirectory, "settings.json.invalid-*"));
        Assert.Equal("{ not json", File.ReadAllText(copy));
    }

    [Fact]
    public async Task WrongTypes_CountAsUnreadable_ForBothSaveKinds()
    {
        WriteFile("""{ "settingsVersion": 2, "repository": { "svn": 5 } }""");
        var (service, _) = NewInstance();

        Assert.False((await service.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Selection, false)).Saved); // quick save refused

        var dialog = await service.SaveRepositoryAsync(service.CreateRepositoryDraft(), RepositoryUsage.None);
        Assert.True(dialog.Saved, dialog.Error);
        Assert.Single(Directory.GetFiles(_root.Paths.ConfigDirectory, "settings.json.invalid-*")); // the hand-edited file is kept
    }

    // ---- Main-window preference (A15 / A16) ---------------------------------------------------------------------------

    [Fact]
    public async Task PreferenceViewModel_ShowsOnlyTheSavedValue_AndReportsFailures()
    {
        var (service, store) = NewInstance(TimeSpan.FromMilliseconds(200));
        string? reported = null;
        var preferences = new WorkspacePreferencesViewModel(service, message => reported = message);
        using (new FileStream(store.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await preferences.SetToolbarAsync(WorkspaceToolbar.Selection, false);

        Assert.True(preferences.ShowSelectionToolbar); // not toggled: the save failed
        Assert.StartsWith("Couldn't save the toolbar setting.", reported);

        reported = null;
        await preferences.SetToolbarAsync(WorkspaceToolbar.Selection, false);
        Assert.False(preferences.ShowSelectionToolbar);
        Assert.Null(reported);
    }

    [Fact]
    public async Task ChangedRaisedOffTheUiThread_IsMarshalledToTheViewModelsContext()
    {
        var (service, _) = NewInstance();
        var context = new SingleThreadContext();
        WorkspacePreferencesViewModel? preferences = null;
        var notifiedOnContext = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.RunAsync(() =>
        {
            preferences = new WorkspacePreferencesViewModel(service, _ => { });
            preferences.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WorkspacePreferencesViewModel.ShowSelectionToolbar))
                    notifiedOnContext.TrySetResult(context.IsCurrentThread);
            };
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(10));

        Assert.True((await Task.Run(() => service.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Selection, false))).Saved); // completes on a pool thread

        Assert.True(await notifiedOnContext.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(context.Stop(TimeSpan.FromSeconds(5)));
    }
}
