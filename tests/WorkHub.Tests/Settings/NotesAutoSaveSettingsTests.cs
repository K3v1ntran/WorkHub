using System.Text.Json.Nodes;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Settings;

/// <summary>
/// Phase 18: "Save notes automatically" (<c>ui.notes.autosave</c>, default off), saved through the existing settings merge:
/// only that member changes, every other value is kept, including unknown members (A1a, A1b). Formatting may change.
/// </summary>
public sealed class NotesAutoSaveSettingsTests : IDisposable
{
    private const string Toolbars = "\"workspace\": { \"showTreeToolbar\": false, \"showSelectionToolbar\": true, \"showContentToolbar\": false }";
    private const string Window = "\"window\": { \"left\": 10, \"top\": 20, \"width\": 900, \"height\": 600, \"isMaximized\": false, \"futureWindowFlag\": 1 }";

    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string File_ => _root.Paths.SettingsFile;

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(File_, json);
    }

    private JsonObject Read() => JsonNode.Parse(File.ReadAllText(File_))!.AsObject();

    private SettingsService NewService()
    {
        var store = new JsonSettingsStore(File_);
        var service = new SettingsService(store, _ => true);
        service.Initialize(store.LoadOrCreate());
        return service;
    }

    private static string FileWith(string? notes) => $$"""
        {
          "settingsVersion": {{AppSettings.CurrentSettingsVersion}},
          "repository": { "svn": { "host": "http://svn.example.test/svn/", "futureSvnFlag": [1, 2] }, "git": { "host": "http://git.example.test/" } },
          "healthCheck": { "timeoutSeconds": 7 },
          "ui": {
            "lastPrimaryTab": "Workspace",
            {{Window}},
            {{Toolbars}},
            {{(notes is null ? string.Empty : notes + ",")}}
            "x-ui": 7
          },
          "x-root": { "a": [1, "b", null], "nested": { "deep": true } }
        }
        """;

    private static JsonObject Without(JsonObject document, bool dropNotesObject)
    {
        var copy = JsonNode.Parse(document.ToJsonString())!.AsObject();
        var notes = copy["ui"]!["notes"]!.AsObject();
        notes.Remove("autosave");
        if (dropNotesObject)
        {
            Assert.Empty(notes); // created by this save to hold only autosave
            copy["ui"]!.AsObject().Remove("notes");
        }

        return copy;
    }

    [Fact]
    public async Task A1a_ExistingUiNotes_IsKeptWithItsUnknownMember_AndEveryOtherValueIsUnchanged()
    {
        WriteFile(FileWith("\"notes\": { \"x-notes\": \"keep\" }"));
        var before = Read();
        var service = NewService();
        Assert.False(service.Current.Ui.Notes.AutoSave);

        foreach (var value in new[] { true, false })
        {
            var result = await service.SaveNotesAutoSaveAsync(value);

            Assert.True(result.Saved, result.Error);
            var after = Read();
            Assert.Equal(value, after["ui"]!["notes"]!["autosave"]!.GetValue<bool>());
            Assert.Equal("keep", after["ui"]!["notes"]!["x-notes"]!.GetValue<string>());
            var expected = JsonNode.Parse(before.ToJsonString())!.AsObject();
            Assert.True(JsonNode.DeepEquals(expected, Without(after, dropNotesObject: false)),
                $"Unrelated settings changed.\nBefore: {expected.ToJsonString()}\nAfter:  {Without(after, false).ToJsonString()}");
            Assert.Equal(value, service.Current.Ui.Notes.AutoSave);
            Assert.Equal(value, new JsonSettingsStore(File_).LoadOrCreate().Settings.Ui.Notes.AutoSave);
        }
    }

    [Fact]
    public async Task A1b_MissingUiNotes_ReadsOff_AndTheSaveCreatesItWithOnlyAutosave_KeepingEverythingElse()
    {
        WriteFile(FileWith(notes: null));
        var before = Read();
        var service = NewService();
        Assert.False(service.Current.Ui.Notes.AutoSave);       // missing = off

        var result = await service.SaveNotesAutoSaveAsync(true);

        Assert.True(result.Saved, result.Error);
        var after = Read();
        var notes = after["ui"]!["notes"]!.AsObject();
        Assert.Equal(["autosave"], notes.Select(p => p.Key));   // exactly one member
        Assert.True(notes["autosave"]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(before, Without(after, dropNotesObject: true)),
            $"Unrelated settings changed.\nBefore: {before.ToJsonString()}\nAfter:  {Without(after, true).ToJsonString()}");
        Assert.True(service.Current.Ui.Notes.AutoSave);
    }

    private bool _initialized;

    private SettingsViewModel NewDialog(SettingsService service)
    {
        if (!_initialized)
        {
            _root.CreateInitializer().Initialize();
            _initialized = true;
        }

        return new SettingsViewModel(service, new SystemService(new SqliteSystemStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile))),
            _root.CreateLegacyImportService(), new FakeDialogs(), () => null);
    }

    /// <summary>
    /// Regression (Phase 18 review): the dialog's Save, when only "Save notes automatically" changed, must not send the
    /// Repository draft taken when the dialog opened, so Repository values another WorkHub saved meanwhile survive.
    /// </summary>
    [Fact]
    public async Task SettingsDialog_OnlyAutosaveChanged_KeepsRepositoryValuesSavedByAnotherInstanceAfterTheDialogOpened()
    {
        WriteFile(FileWith("\"notes\": { \"x-notes\": \"keep\" }"));
        var first = NewService();
        var dialog = NewDialog(first);                                        // 1. opened with the initial Repository
        var closed = 0;
        dialog.CloseRequested += (_, saved) => closed += saved ? 1 : 0;
        Assert.Equal("http://svn.example.test/svn/", dialog.SvnHost);

        var second = NewService();                                            // 2. another instance saves a new Repository
        var newer = second.CreateRepositoryDraft();
        newer.Svn.Host = "http://svn-new.example.test/svn/";
        newer.Svn.SystemPrefix = "NEW_doc_";
        newer.Git.Host = "http://git-new.example.test/";
        var saved = await second.SaveRepositoryAsync(newer, RepositoryUsage.None);
        Assert.True(saved.Saved, saved.Error);
        Assert.Equal("http://svn.example.test/svn/", dialog.SvnHost);        // the dialog's draft is unchanged
        var before = Read();                                                  // the file right before SaveCommand

        dialog.SaveNotesAutomatically = true;                                 // 3. only autosave changes
        Assert.True(dialog.ChangesOnlyNotesAutoSave);
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);                                        // 4. read back from disk
        Assert.Equal(1, closed);
        var after = Read();
        Assert.True(after["ui"]!["notes"]!["autosave"]!.GetValue<bool>());
        Assert.Equal("http://svn-new.example.test/svn/", after["repository"]!["svn"]!["host"]!.GetValue<string>());
        Assert.Equal("NEW_doc_", after["repository"]!["svn"]!["systemPrefix"]!.GetValue<string>());
        Assert.Equal("http://git-new.example.test/", after["repository"]!["git"]!["host"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(before, Without(after, dropNotesObject: false)),
            $"Something besides ui.notes.autosave changed.\nBefore: {before.ToJsonString()}\nAfter:  {Without(after, false).ToJsonString()}");
        Assert.Equal("http://svn-new.example.test/svn/", first.Current.Repository.Svn.Host); // Current = what was written
    }

    [Fact]
    public async Task SettingsDialog_ARealRepositoryEdit_StillUsesTheRepositorySaveWithItsValidation()
    {
        WriteFile(FileWith(notes: null));
        var service = NewService();
        var dialog = NewDialog(service);

        dialog.SvnHost = "http://svn-edited.example.test/svn/";
        dialog.SaveNotesAutomatically = true;
        Assert.False(dialog.ChangesOnlyNotesAutoSave);
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        var after = Read();
        Assert.Equal("http://svn-edited.example.test/svn/", after["repository"]!["svn"]!["host"]!.GetValue<string>());
        Assert.True(after["ui"]!["notes"]!["autosave"]!.GetValue<bool>());

        var invalid = NewDialog(NewService());                                // an error still blocks the Save
        invalid.SvnHost = "not a url";
        await invalid.SaveCommand.ExecuteAsync();
        Assert.NotNull(invalid.SaveError);
        Assert.Equal("http://svn-edited.example.test/svn/", Read()["repository"]!["svn"]!["host"]!.GetValue<string>());
    }

    [Fact]
    public async Task SettingsDialog_WritesTheValueOnlyWhenChanged_AndANewFileDefaultsToOff()
    {
        var service = NewService();                              // no file yet: defaults
        Assert.False(service.Current.Ui.Notes.AutoSave);
        _root.CreateInitializer().Initialize();
        var dialog = new SettingsViewModel(service, new SystemService(new SqliteSystemStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile))),
            _root.CreateLegacyImportService(), new FakeDialogs(), () => null);
        Assert.False(dialog.SaveNotesAutomatically);
        Assert.Null(dialog.ChangedNotesAutoSave);

        dialog.SaveNotesAutomatically = true;
        await dialog.SaveCommand.ExecuteAsync();

        Assert.Null(dialog.SaveError);
        Assert.True(Read()["ui"]!["notes"]!["autosave"]!.GetValue<bool>());
        Assert.True(service.Current.Ui.Notes.AutoSave);
    }
}
