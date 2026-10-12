using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.Services;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Note editor lifecycle (Phase 5B §4): dirty tracking, explicit vs close-triggered Save, the Editing/Saving/Prompting/Closing
/// gate for every close path, draft preservation on every failure, and Conflict/NotFound recovery.
/// </summary>
public sealed class NoteEditorViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedProjectContentStore _store;
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _other;
    private readonly long _alpha;
    private int _closeRequests;

    public NoteEditorViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var launcher = new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true);
        _store = new GatedProjectContentStore(new SqliteProjectContentStore(_connections));
        _content = new ProjectContentService(_store, launcher);
        _other = new ProjectContentService(new SqliteProjectContentStore(_connections), launcher);
        _alpha = new WorkspaceService(new SqliteWorkspaceStore(_connections)).CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active)).Result.Value!.Id;
    }

    public void Dispose() => _root.Dispose();

    private async Task<NoteEditorViewModel> Editing(string title = "Kickoff", string content = "agenda", long? folder = null)
    {
        var note = (await _other.CreateNoteAsync(_alpha, folder, new ProjectNoteDraft(title, content))).Note!;
        return Track(NoteEditorViewModel.ForEdit(_content, _dialogs, note, "Alpha"));
    }

    private NoteEditorViewModel Track(NoteEditorViewModel editor)
    {
        editor.CloseRequested += (_, _) => _closeRequests++;
        return editor;
    }

    private async Task<ProjectNote> Stored(long id) => (await _other.GetNoteAsync(id))!;

    [Fact]
    public async Task DirtyTracking_IsOrdinal_AndTitleWhitespaceCounts()
    {
        var editor = await Editing();

        Assert.False(editor.IsDirty);
        Assert.False(editor.SaveCommand.CanExecute(null)); // clean existing note: nothing to save
        editor.Title = "Kickoff ";
        Assert.True(editor.IsDirty);
        Assert.EndsWith("•", editor.WindowTitle);
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.Title = "Kickoff";
        Assert.False(editor.IsDirty);
        editor.Content = "agenda\r\n";
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public async Task ExplicitSave_StaysOpen_UsesThePersistedValuesAsTheNewBaseline()
    {
        var editor = await Editing();
        editor.Title = "  Kickoff v2  ";
        editor.Content = "  spaced\ttext\r\n";

        await editor.SaveCommand.ExecuteAsync();

        Assert.Equal(0, _closeRequests);
        Assert.Equal(NoteEditorState.Editing, editor.State);
        Assert.Equal("Kickoff v2", editor.Title); // the normalized stored title
        Assert.Equal("  spaced\ttext\r\n", editor.Content);
        Assert.False(editor.IsDirty);
        Assert.StartsWith("Saved ", editor.StatusText);
        Assert.True(editor.SavedAny);
        var stored = await Stored(editor.NoteId!.Value);
        Assert.Equal((2L, "Kickoff v2", "  spaced\ttext\r\n"), (stored.Revision, stored.Title, stored.Content));

        editor.Content = "third";
        await editor.SaveCommand.ExecuteAsync(); // the next save uses the new revision
        Assert.Equal(3, (await Stored(editor.NoteId!.Value)).Revision);
    }

    [Fact]
    public async Task PendingSave_LocksInputsAndEveryClosePath_ThenTheSnapshotIsWhatGetsStored()
    {
        var editor = await Editing();
        editor.Content = "snapshot";
        var gate = _store.Hold("saveNote");

        var pending = editor.SaveCommand.ExecuteAsync();
        gate.WaitEntered();

        Assert.Equal(NoteEditorState.Saving, editor.State);
        Assert.True(editor.IsReadOnly);
        Assert.False(editor.SaveCommand.CanExecute(null));          // no double submission
        Assert.False(editor.RequestCloseCommand.CanExecute(null));  // Close / Esc disabled
        Assert.False(editor.IsCloseApproved);                       // X / Alt+F4 are refused by the window
        editor.RequestClose();                                      // a window close path while pending
        await editor.CurrentClose;
        Assert.Equal(0, _dialogs.UnsavedChangesPrompts);
        Assert.Equal(0, _closeRequests);

        gate.Release();
        await pending;
        Assert.Equal(NoteEditorState.Editing, editor.State);
        Assert.Equal("snapshot", (await Stored(editor.NoteId!.Value)).Content);
        Assert.Equal(0, _closeRequests);
    }

    [Fact]
    public async Task CleanClose_ClosesWithoutAPrompt()
    {
        var editor = await Editing();

        editor.RequestClose();
        await editor.CurrentClose;

        Assert.Equal(0, _dialogs.UnsavedChangesPrompts);
        Assert.Equal(1, _closeRequests);
        Assert.True(editor.IsCloseApproved);
        Assert.False(editor.SavedAny);
    }

    [Fact]
    public async Task DirtyClose_Save_ClosesOnlyAfterSuccess()
    {
        var editor = await Editing();
        editor.Content = "saved on close";
        _dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Save;

        editor.RequestClose();
        await editor.CurrentClose;

        Assert.Equal(1, _dialogs.UnsavedChangesPrompts);
        Assert.Equal(1, _closeRequests);
        Assert.Equal("saved on close", (await Stored(editor.NoteId!.Value)).Content);
    }

    [Fact]
    public async Task DirtyClose_SaveFails_StaysOpenWithTheDraft_AndDoesNotRePrompt()
    {
        var editor = await Editing();
        editor.Content = "my draft";
        _dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Save;
        var gate = _store.Hold("saveNote");

        editor.RequestClose();
        gate.WaitEntered();
        Assert.Equal(NoteEditorState.Saving, editor.State);
        gate.Fail(new IOException("database is locked")); // injected failure; a real SQLite lock: NoteSqliteLockTests
        await editor.CurrentClose;

        Assert.Equal(NoteEditorState.Editing, editor.State);
        Assert.Equal(0, _closeRequests);
        Assert.Equal(1, _dialogs.UnsavedChangesPrompts);
        Assert.Equal("my draft", editor.Content);
        Assert.True(editor.IsDirty);
        Assert.Contains("database is locked", editor.GeneralError);
        Assert.Equal("agenda", (await Stored(editor.NoteId!.Value)).Content);
    }

    [Fact]
    public async Task DirtyClose_Discard_ClosesWithoutWriting_Cancel_StaysOpen()
    {
        var editor = await Editing();
        editor.Content = "draft";

        _dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Cancel; // also what closing the prompt window means
        editor.RequestClose();
        await editor.CurrentClose;
        Assert.Equal(0, _closeRequests);
        Assert.Equal(NoteEditorState.Editing, editor.State);
        Assert.Equal("draft", editor.Content);

        _dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Discard;
        editor.RequestClose();
        await editor.CurrentClose;
        Assert.Equal(1, _closeRequests);
        Assert.Equal(2, _dialogs.UnsavedChangesPrompts);
        Assert.Equal(1, (await Stored(editor.NoteId!.Value)).Revision);
    }

    [Fact]
    public async Task CloseRequestsWhilePrompting_AreIgnored_SoPromptsNeverStack()
    {
        var editor = await Editing();
        editor.Content = "draft";
        _dialogs.OnUnsavedChanges = _ =>
        {
            // Alt+F4 / X / Esc again while the prompt is open.
            editor.RequestClose();
            editor.RequestCloseCommand.Execute(null);
            Assert.False(editor.RequestCloseCommand.CanExecute(null));
            return UnsavedChangesChoice.Cancel;
        };

        editor.RequestClose();
        await editor.CurrentClose;

        Assert.Equal(1, _dialogs.UnsavedChangesPrompts);
        Assert.Equal(0, _closeRequests);
        Assert.Equal(NoteEditorState.Editing, editor.State);
    }

    [Fact]
    public async Task ValidationFailure_KeepsTheDraft_WithAFieldError()
    {
        var editor = await Editing();
        editor.Title = "   ";
        editor.Content = "keep me";

        await editor.SaveCommand.ExecuteAsync();

        Assert.NotNull(editor.TitleError);
        Assert.Equal("   ", editor.Title);
        Assert.Equal("keep me", editor.Content);
        Assert.False(editor.CanRecover);
        Assert.Equal(1, (await Stored(editor.NoteId!.Value)).Revision);
    }

    [Fact]
    public async Task NewNote_FirstSaveCreates_AndIsTracked_ClosingWithoutSaveTracksNothing()
    {
        var unsaved = Track(NoteEditorViewModel.ForCreate(_content, _dialogs, _alpha, null, "Alpha"));
        Assert.True(unsaved.IsNew);
        Assert.True(unsaved.SaveCommand.CanExecute(null)); // a new note may be saved even when "clean"
        unsaved.RequestClose();
        await unsaved.CurrentClose;
        Assert.Empty(unsaved.TouchedNoteIds);

        var editor = NoteEditorViewModel.ForCreate(_content, _dialogs, _alpha, null, "Alpha");
        editor.Title = "Meeting";
        editor.Content = "notes";
        await editor.SaveCommand.ExecuteAsync();

        Assert.False(editor.IsNew);
        var id = editor.NoteId!.Value;
        Assert.Equal([id], editor.TouchedNoteIds);
        Assert.Contains(id, editor.CreatedNoteIds);
        Assert.Equal(1, (await Stored(id)).Revision);
    }

    [Fact]
    public async Task Conflict_OffersRecovery_CopyDraft_AndSaveAsNew_SwitchesToTheNewNote()
    {
        var editor = await Editing(folder: (await _other.CreateFolderAsync(_alpha, null, "Specs")).Value!.Id);
        var originalId = editor.NoteId!.Value;
        await _other.SaveNoteAsync(originalId, 1, new ProjectNoteDraft("Kickoff", "theirs"));
        editor.Content = "mine";

        await editor.SaveCommand.ExecuteAsync();

        Assert.Contains("changed elsewhere", editor.GeneralError);
        Assert.True(editor.CanRecover);
        Assert.Equal("mine", editor.Content);
        Assert.True(editor.IsDirty);

        editor.CopyDraftCommand.Execute(null);
        Assert.Equal("Kickoff\r\n\r\nmine", _dialogs.Clipboard);

        await editor.SaveAsNewCommand.ExecuteAsync();

        Assert.NotEqual(originalId, editor.NoteId);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanRecover);
        Assert.Equal([originalId, editor.NoteId!.Value], editor.TouchedNoteIds);
        Assert.Contains(editor.NoteId!.Value, editor.CreatedNoteIds);
        Assert.Equal("theirs", (await Stored(originalId)).Content);   // the original is untouched
        Assert.Equal("mine", (await Stored(editor.NoteId!.Value)).Content);
    }

    // ---- Location display after committed writes (§6.2) ----------------------------------------------------------

    [Fact]
    public async Task LocationLookupFailsAfterACommittedSave_TheSaveStillSucceeds_AndTheNewRevisionIsKept()
    {
        var editor = await Editing();
        editor.Content = "first";
        var lookup = _store.Hold("containerPath");

        var save = editor.SaveCommand.ExecuteAsync();
        lookup.WaitEntered();                       // the write has committed; only the display read is pending
        Assert.Equal(NoteEditorState.Saving, editor.State);
        lookup.Fail(new IOException("database is locked"));
        await save;

        Assert.Null(editor.GeneralError);           // not reported as a failed save
        Assert.False(editor.CanRecover);
        Assert.False(editor.IsDirty);
        Assert.Equal(NoteEditorState.Editing, editor.State);
        Assert.Equal(NoteEditorViewModel.LocationUnavailable, editor.LocationText); // truthful fallback, not the stale "Alpha"
        Assert.StartsWith("Saved ", editor.StatusText);
        Assert.EndsWith("The note's location could not be shown.", editor.StatusText);
        Assert.Equal(2, (await Stored(editor.NoteId!.Value)).Revision);

        // The editor holds revision 2: the next save is not a Conflict, and the location shows again.
        editor.Content = "second";
        await editor.SaveCommand.ExecuteAsync();

        Assert.Null(editor.GeneralError);
        Assert.Equal(3, (await Stored(editor.NoteId!.Value)).Revision);
        Assert.Equal("Location: Alpha", editor.LocationText);
        Assert.DoesNotContain("location", editor.StatusText);
    }

    [Fact]
    public async Task LocationLookupFailsAfterSaveAsNew_TheNewNoteIsStillAccepted()
    {
        var editor = await Editing();
        var originalId = editor.NoteId!.Value;
        await _other.SaveNoteAsync(originalId, 1, new ProjectNoteDraft("Kickoff", "theirs"));
        editor.Content = "mine";
        await editor.SaveCommand.ExecuteAsync();
        Assert.True(editor.CanRecover);
        _store.Hold("containerPath").Fail(new IOException("database is locked"));

        await editor.SaveAsNewCommand.ExecuteAsync();

        Assert.Null(editor.GeneralError);
        Assert.NotEqual(originalId, editor.NoteId);
        Assert.False(editor.IsDirty);
        Assert.Equal(NoteEditorViewModel.LocationUnavailable, editor.LocationText);
        Assert.Equal("Saved as a new note. Its location could not be shown. The original note was not changed.", editor.StatusText);
        Assert.Equal(1, (await Stored(editor.NoteId!.Value)).Revision);

        editor.Content = "mine, edited";           // the editor edits the new note at its real revision
        await editor.SaveCommand.ExecuteAsync();
        Assert.Null(editor.GeneralError);
        Assert.Equal("mine, edited", (await Stored(editor.NoteId!.Value)).Content);
    }

    [Fact]
    public async Task NotFound_SaveAsNewRefused_KeepsTheDraft_AndOffersCopy()
    {
        var specs = (await _other.CreateFolderAsync(_alpha, null, "Specs")).Value!;
        var editor = await Editing(folder: specs.Id);
        using (var connection = _root.Open())
        {
            Db.Exec(connection, "DELETE FROM ProjectNotes WHERE Id = $id;", ("$id", editor.NoteId!.Value));
            Db.Exec(connection, "DELETE FROM ProjectFolders WHERE Id = $id;", ("$id", specs.Id)); // last-known location gone too
        }

        editor.Content = "draft";
        await editor.SaveCommand.ExecuteAsync();
        Assert.Contains("no longer exists", editor.GeneralError);
        Assert.True(editor.CanRecover);

        await editor.SaveAsNewCommand.ExecuteAsync();

        Assert.Contains("Copy your text", editor.GeneralError);
        Assert.Equal("draft", editor.Content);
        Assert.True(editor.CopyDraftCommand.CanExecute(null));
        using var after = _root.Open();
        Assert.Equal(0, Db.Count(after, "ProjectNotes"));
    }
}
