using System.Text.Json.Nodes;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Settings;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Settings;

/// <summary>
/// Phase 16A settings merge and peek (proposal §4.5–4.6, acceptance S1–S7). Two <see cref="JsonSettingsStore"/> instances on
/// one temporary file stand in for two windows. Only test roots are used.
/// </summary>
public sealed class WindowStateSettingsTests : IDisposable
{
    private static SaveBudget FarFuture => SaveBudget.Unlimited;
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string File_ => _root.Paths.SettingsFile;

    private static readonly JsonNodeOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static JsonObject Parse(string path) => JsonNode.Parse(File.ReadAllText(path), CaseInsensitive)!.AsObject();

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(File_, json);
    }

    private string[] TempFiles() => Directory.GetFiles(_root.Paths.ConfigDirectory, "settings.json.*.tmp");

    private SettingsService Service(JsonSettingsStore store)
    {
        var service = new SettingsService(store, _ => true);
        service.Initialize(store.LoadOrCreate());
        return service;
    }

    private const string FileWithEverything = """
        {
          "settingsVersion": 2,
          "x": { "y": [1, 2] },
          "repository": {
            "svn": { "host": "http://svn.example.test/svn/", "systemPrefix": "A_", "oraclePrefix": "B_", "releasePrefix": "R_", "tortoiseProcPath": "" },
            "git": { "host": "http://git.example.test/root/", "systemPrefix": "G_", "oraclePrefix": "H_" }
          },
          "healthCheck": { "timeoutSeconds": 7 },
          "ui": {
            "lastPrimaryTab": "Repository",
            "window": { "left": 10, "top": 20, "width": 900, "height": 600, "isMaximized": false, "monitorHint": "keep-me" },
            "workspace": { "showTreeToolbar": false, "showSelectionToolbar": true, "showContentToolbar": false },
            "extra": { "z": true }
          }
        }
        """;

    private static void Strip(JsonObject root)
    {
        var window = root["ui"]!["window"]!.AsObject();
        foreach (var name in new[] { "left", "top", "width", "height", "isMaximized" })
            window.Remove(name);
        root["ui"]!.AsObject().Remove("lastPrimaryTab");
    }

    [Fact]
    public void S1_TheUiSave_ChangesOnlyTheWindowMembersAndTheTab_EverythingElseKeepsItsValue()
    {
        WriteFile(FileWithEverything);
        var before = Parse(File_);

        var result = new JsonSettingsStore(File_).Update(new SettingsEdit { Ui = UiStates.Bounds(-1800, 100, 1250, 820, maximized: true, tab: "Workspace") },
            AppSettings.CreateDefault(), FarFuture);

        Assert.Equal(SettingsUpdateStatus.Saved, result.Status);
        var after = Parse(File_);
        var window = after["ui"]!["window"]!;
        Assert.Equal(-1800, window["left"]!.GetValue<double>());
        Assert.Equal(100, window["top"]!.GetValue<double>());
        Assert.Equal(1250, window["width"]!.GetValue<double>());
        Assert.Equal(820, window["height"]!.GetValue<double>());
        Assert.True(window["isMaximized"]!.GetValue<bool>());
        Assert.Equal("Workspace", after["ui"]!["lastPrimaryTab"]!.GetValue<string>());
        Assert.Equal("keep-me", window["monitorHint"]!.GetValue<string>()); // unknown member inside ui.window
        Strip(before);
        Strip(after);
        Assert.True(JsonNode.DeepEquals(before, after), $"Unrelated settings changed.\nBefore: {before.ToJsonString()}\nAfter:  {after.ToJsonString()}");
        Assert.Equal(-1800, result.Settings!.Ui.Window.Left);
    }

    [Fact]
    public void S2_TheShellNeverReachedReady_TheTabStaysAsSaved_TheBoundsAreWritten()
    {
        WriteFile(FileWithEverything);

        new JsonSettingsStore(File_).Update(new SettingsEdit { Ui = UiStates.Bounds(300, 200, tab: null) }, AppSettings.CreateDefault(), FarFuture);

        var after = Parse(File_);
        Assert.Equal("Repository", after["ui"]!["lastPrimaryTab"]!.GetValue<string>());
        Assert.Equal(300, after["ui"]!["window"]!["left"]!.GetValue<double>());
    }

    [Fact]
    public void S3_AnUnreadableFile_IsRefused_ByteIdentical_NoTempFileLeft()
    {
        WriteFile("{ not json");
        var before = File.ReadAllBytes(File_);
        var service = Service(new JsonSettingsStore(File_));

        var result = service.SaveWindowState(UiStates.Bounds(1, 2), FarFuture);

        Assert.Equal(WindowStateSaveOutcome.FileUnreadable, result.Outcome);
        Assert.Equal(before, File.ReadAllBytes(File_));
        Assert.Empty(TempFiles());
        Assert.Empty(Directory.GetFiles(_root.Paths.ConfigDirectory, "settings.json.invalid-*")); // never replaced, no copy
    }

    [Fact]
    public void S4_TheLockIsHeldByAnotherHandle_LockTimeout_ByteIdentical_WithinTheTimeout()
    {
        WriteFile(FileWithEverything);
        var store = new JsonSettingsStore(File_, lockTimeout: TimeSpan.FromMilliseconds(300));
        var service = Service(store);
        var before = File.ReadAllBytes(File_);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        WindowStateSaveResult result;
        using (new FileStream(store.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            result = service.SaveWindowState(UiStates.Bounds(1, 2), SaveBudget.StartNow(TimeProvider.System, TimeSpan.FromSeconds(3)));

        Assert.Equal(WindowStateSaveOutcome.LockTimeout, result.Outcome); // the 300 ms ceiling, not the 3 s budget, ended it
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), watch.Elapsed.ToString());
        Assert.Equal(before, File.ReadAllBytes(File_));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task S5_AnotherWindowSavedRepositoryAndToolbar_ThenThisWindowCloses_BothAreKept()
    {
        var window1 = Service(new JsonSettingsStore(File_)); // loads first
        var window2 = Service(new JsonSettingsStore(File_));
        var draft = window2.CreateRepositoryDraft();
        draft.Git.Host = "http://window2.example.test/root/";
        Assert.True((await window2.SaveRepositoryAsync(draft, RepositoryUsage.None)).Saved);
        Assert.True((await window2.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Content, false)).Saved);

        var result = window1.SaveWindowState(UiStates.Bounds(-1800, 100, tab: "Workspace"), FarFuture);

        Assert.True(result.Saved, result.Error);
        var saved = new JsonSettingsStore(File_).LoadOrCreate().Settings;
        Assert.Equal("http://window2.example.test/root/", saved.Repository.Git.Host);
        Assert.False(saved.Ui.Workspace.ShowContentToolbar);
        Assert.Equal(-1800, saved.Ui.Window.Left);
        Assert.Equal("Workspace", saved.Ui.LastPrimaryTab);
        Assert.Equal("http://window2.example.test/root/", window1.Current.Repository.Git.Host); // Current = the saved document
    }

    [Fact]
    public void S6_ThePeek_CreatesAndChangesNothing()
    {
        // missing folder
        var missingRoot = Path.Combine(_root.Paths.Root, "no-such-folder", "Config", "settings.json");
        Assert.Null(new JsonSettingsStore(missingRoot).PeekUi());
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingRoot)));

        // missing file (folder exists)
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        Assert.Null(new JsonSettingsStore(File_).PeekUi());
        Assert.False(File.Exists(File_));
        Assert.False(File.Exists(File_ + ".lock"));

        // invalid JSON
        WriteFile("{ not json");
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(File_, stamp);
        Assert.Null(new JsonSettingsStore(File_).PeekUi());
        Assert.Equal("{ not json", File.ReadAllText(File_));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(File_));

        // read-only valid file
        WriteFile(FileWithEverything);
        File.SetLastWriteTimeUtc(File_, stamp);
        var length = new FileInfo(File_).Length;
        File.SetAttributes(File_, FileAttributes.ReadOnly);
        try
        {
            var ui = new JsonSettingsStore(File_).PeekUi();
            Assert.NotNull(ui);
            Assert.Equal(10, ui!.Window.Left);
            Assert.Equal("Repository", ui.LastPrimaryTab);
        }
        finally
        {
            File.SetAttributes(File_, FileAttributes.Normal);
        }

        Assert.Equal(length, new FileInfo(File_).Length);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(File_));
        Assert.False(File.Exists(File_ + ".lock"));
        Assert.Empty(TempFiles());
    }

    [Fact]
    public async Task S7_AVersion1File_IsUpgradedExactlyAsAToolbarSaveUpgradesIt()
    {
        const string v1 = """
            {
              "settingsVersion": 1,
              "repositories": {
                "svnSystem": { "baseUrl": "http://svn.example.test/svn/", "prefix": "A_" },
                "gitSystem": { "baseUrl": "http://git.example.test/root/", "prefix": "G_" },
                "tortoiseProcPath": ""
              }
            }
            """;
        WriteFile(v1);
        var viaUi = new JsonSettingsStore(File_).Update(new SettingsEdit { Ui = UiStates.Bounds(5, 6) }, AppSettings.CreateDefault(), FarFuture);
        var uiFile = Parse(File_);

        WriteFile(v1);
        var toolbar = Service(new JsonSettingsStore(File_));
        Assert.True((await toolbar.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Tree, true)).Saved);
        var toolbarFile = Parse(File_);

        Assert.Equal(SettingsUpdateStatus.Saved, viaUi.Status);
        Assert.Equal(2, uiFile["settingsVersion"]!.GetValue<int>());
        Assert.Null(uiFile["repositories"]);
        Assert.Equal("http://svn.example.test/svn/", uiFile["repository"]!["svn"]!["host"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(toolbarFile["repository"], uiFile["repository"]),
            $"{toolbarFile["repository"]!.ToJsonString()}\n{uiFile["repository"]!.ToJsonString()}");
    }

    [Fact]
    public void L8_ARelaunchedProcessPeeks_TheStateSavedBeforeItsStart()
    {
        var normalClose = Service(new JsonSettingsStore(File_));
        Assert.True(normalClose.SaveWindowState(UiStates.Bounds(-1500, 50, 1300, 900, tab: "Workspace"), FarFuture).Saved);

        // The Restore relaunch: the new process peeks before the relaunching process saves anything.
        var peek = new JsonSettingsStore(File_).PeekUi();

        Assert.NotNull(peek);
        Assert.Equal(-1500, peek!.Window.Left);
        Assert.Equal(50, peek.Window.Top);
        Assert.Equal(1300, peek.Window.Width);
        Assert.Equal(900, peek.Window.Height);
        Assert.Equal("Workspace", peek.LastPrimaryTab);
    }

    [Fact]
    public void L9_TwoSavers_TheLastSuccessfulSaveWins()
    {
        var a = Service(new JsonSettingsStore(File_));
        var b = Service(new JsonSettingsStore(File_, lockTimeout: TimeSpan.FromMilliseconds(200)));

        Assert.True(a.SaveWindowState(UiStates.Bounds(100, 100), FarFuture).Saved);
        Assert.True(b.SaveWindowState(UiStates.Bounds(900, 300), FarFuture).Saved);
        Assert.Equal(900, new JsonSettingsStore(File_).PeekUi()!.Window.Left); // B's state

        Assert.True(a.SaveWindowState(UiStates.Bounds(111, 111), FarFuture).Saved);
        WindowStateSaveResult late;
        using (new FileStream(File_ + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            late = b.SaveWindowState(UiStates.Bounds(999, 333), FarFuture);
        Assert.Equal(WindowStateSaveOutcome.LockTimeout, late.Outcome);
        Assert.Equal(111, new JsonSettingsStore(File_).PeekUi()!.Window.Left); // A's state: the last successful save
    }
}
