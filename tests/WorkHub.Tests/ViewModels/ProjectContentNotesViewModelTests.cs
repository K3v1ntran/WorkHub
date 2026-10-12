using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.Services;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Notes in the right-pane content view: listing, read-only preview, opening the editor, and the fresh-read reconciliation
/// after every editor close (Phase 5B §5.3 revision 2). A second, independent service plays the other window.
/// </summary>
public sealed class ProjectContentNotesViewModelTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedProjectContentStore _store;
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _other;
    private readonly ScriptedLinkChecker _checker = new();
    private readonly long _alpha;

    public ProjectContentNotesViewModelTests()
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

    private async Task<ProjectContentViewModel> Opened(LinkCheckCache? cache = null)
    {
        var vm = new ProjectContentViewModel(_content, _checker, _dialogs, NullAppLog.Instance, cache ?? new LinkCheckCache(), _alpha, "Alpha");
        await vm.ActivateAsync();
        return vm;
    }

    private async Task<ProjectNote> SeedNote(string title, string content = "text", long? folder = null) =>
        (await _other.CreateNoteAsync(_alpha, folder, new ProjectNoteDraft(title, content))).Note!;

    private async Task<ProjectFolder> SeedFolder(string name) => (await _other.CreateFolderAsync(_alpha, null, name)).Value!;

    private static ProjectContentItemViewModel Row(ProjectContentViewModel vm, string name) => vm.Items.Single(i => i.Name == name);

    private static List<string> Names(ProjectContentViewModel vm) => vm.Items.Select(i => i.Name).ToList();

    private static async Task Select(ProjectContentViewModel vm, string name)
    {
        vm.SelectedItem = Row(vm, name);
        await vm.CurrentPreviewLoad;
    }

    /// <summary>Plays the editor: <paramref name="play"/> acts as the user, then the editor is closed through the gate.</summary>
    private void PlayEditor(Action<NoteEditorViewModel> play, UnsavedChangesChoice onPrompt = UnsavedChangesChoice.Discard)
    {
        _dialogs.OnUnsavedChanges = _ => onPrompt;
        _dialogs.OnNoteEditor = editor =>
        {
            play(editor);
            editor.RequestClose();
            editor.CurrentClose.GetAwaiter().GetResult();
            Assert.True(editor.IsCloseApproved);
            return editor.SavedAny;
        };
    }

    // ---- Listing, commands, preview ---------------------------------------------------------------------------------

    [Fact]
    public async Task Notes_ListAfterFoldersAndLinks_AndLinkChecksIgnoreThem()
    {
        await SeedFolder("Specs");
        await _other.CreateLinkAsync(_alpha, null, new ProjectLinkDraft("Out", ProjectLinkType.Folder, @"C:\Work\Out"));
        await SeedNote("Zeta");
        await SeedNote("Agenda");
        var vm = await Opened();

        Assert.Equal(["Specs", "Out", "Agenda", "Zeta"], Names(vm));
        Assert.Equal("Note", Row(vm, "Agenda").KindText);
        Assert.StartsWith("Updated ", Row(vm, "Agenda").DetailText);
        Assert.Equal(string.Empty, Row(vm, "Agenda").StatusText);

        vm.SelectedItem = Row(vm, "Agenda");
        Assert.False(vm.CheckCommand.CanExecute(null));
        var run = vm.CheckAllCommand.ExecuteAsync();
        await Task.Delay(50);
        Assert.Single(_checker.Requests); // only the link
        vm.StopCheckingCommand.Execute(null);
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task SelectingANote_ShowsItsPreview_OtherRowsHideIt()
    {
        await SeedFolder("Specs");
        await SeedNote("Agenda", "line 1\r\n\tline 2  ");
        var vm = await Opened();

        await Select(vm, "Agenda");
        Assert.True(vm.IsPreviewVisible);
        Assert.Equal("line 1\r\n\tline 2  ", vm.PreviewNote!.Content);
        Assert.StartsWith("Updated ", vm.PreviewUpdatedText);

        await Select(vm, "Specs");
        Assert.False(vm.IsPreviewVisible);
        Assert.Null(vm.PreviewNote);
    }

    [Fact]
    public async Task StalePreviewResultsAndErrors_AreDropped()
    {
        var a = await SeedNote("A", "a text");
        var b = await SeedNote("B", "b text");
        var vm = await Opened();

        var gate = _store.Hold(GatedProjectContentStore.Note(a.Id));
        vm.SelectedItem = Row(vm, "A");
        var staleLoad = vm.CurrentPreviewLoad;
        gate.WaitEntered();
        await Select(vm, "B");
        gate.Release();
        await staleLoad;
        Assert.Equal("b text", vm.PreviewNote!.Content);

        var failing = _store.Hold(GatedProjectContentStore.Note(a.Id));
        vm.SelectedItem = Row(vm, "A");
        var staleFailure = vm.CurrentPreviewLoad;
        failing.WaitEntered();
        await Select(vm, "B");
        failing.Fail(new IOException("database is locked"));
        await staleFailure;
        Assert.Null(vm.PreviewMessage);
        Assert.Equal("b text", vm.PreviewNote!.Content);
        Assert.True(b.Id > 0);
    }

    [Fact]
    public async Task OpenEnterAndEdit_OpenTheEditorOnAFreshRead()
    {
        var note = await SeedNote("Agenda", "old");
        var vm = await Opened();
        await Select(vm, "Agenda");
        await _other.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Agenda", "newer")); // after the preview was read
        NoteEditorViewModel? seen = null;
        PlayEditor(editor => seen = editor);

        await vm.OpenCommand.ExecuteAsync(); // Enter and double-click run this command

        Assert.Equal("newer", seen!.Content);
        seen = null;
        await vm.EditCommand.ExecuteAsync(); // F2
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task NewNote_SavedInTheCurrentFolder_IsInsertedAndSelected()
    {
        await SeedNote("Zeta");
        var vm = await Opened();
        PlayEditor(editor =>
        {
            editor.Title = "Agenda";
            editor.Content = "first";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
        });

        await vm.NewNoteCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        Assert.Equal(["Agenda", "Zeta"], Names(vm));
        Assert.Same(Row(vm, "Agenda"), vm.SelectedItem);
        Assert.Equal("first", vm.PreviewNote!.Content);
    }

    [Fact]
    public async Task NewNote_ClosedWithoutSaving_ReadsNothing_AndChangesNothing()
    {
        await SeedNote("Zeta");
        var vm = await Opened();
        var gate = _store.Hold("getNotes"); // would block if a reconciliation read happened
        PlayEditor(editor => editor.Title = "typed but discarded");

        await vm.NewNoteCommand.ExecuteAsync();
        await vm.CurrentReconciliation.WaitAsync(Wait);

        Assert.False(gate.Entered.IsSet);
        Assert.Equal(["Zeta"], Names(vm));
    }

    // ---- Fresh-read reconciliation ----------------------------------------------------------------------------------

    [Fact]
    public async Task Save_ThenExternalMove_ThenCleanClose_RemovesTheRow()
    {
        var x = await SeedFolder("X");
        var y = await SeedFolder("Y");
        var note = await SeedNote("Agenda", folder: x.Id);
        var vm = await Opened();
        await vm.NavigateAsync(x.Id);
        await Select(vm, "Agenda");
        PlayEditor(editor =>
        {
            editor.Content = "edited";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();       // A saves in X
            _other.MoveNoteAsync(note.Id, x.Id, y.Id).GetAwaiter().GetResult(); // the other window moves it to Y
        });                                                                  // A closes cleanly

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        Assert.Empty(vm.Items); // not re-added from A's saved row
        Assert.Null(vm.SelectedItem);
        Assert.False(vm.IsPreviewVisible);
        Assert.Null(vm.PreviewNote);
        Assert.Equal(["Agenda"], (await _other.ListAsync(_alpha, y.Id)).Listing!.Notes.Select(n => n.Title));
    }

    [Fact]
    public async Task Open_ThenExternalMove_ThenCloseWithoutSave_StillReconciles()
    {
        var x = await SeedFolder("X");
        var y = await SeedFolder("Y");
        var note = await SeedNote("Agenda", folder: x.Id);
        var vm = await Opened();
        await vm.NavigateAsync(x.Id);
        await Select(vm, "Agenda");
        var dialogResult = true;
        _dialogs.OnNoteEditor = editor =>
        {
            _other.MoveNoteAsync(note.Id, x.Id, y.Id).GetAwaiter().GetResult();
            editor.RequestClose();
            editor.CurrentClose.GetAwaiter().GetResult();
            dialogResult = editor.SavedAny;
            return dialogResult;
        };

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        Assert.False(dialogResult);   // nothing was persisted …
        Assert.Empty(vm.Items);       // … and the row still left the listing
        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public async Task Save_ThenExternalContentEdit_ThenCleanClose_ShowsTheNewerRevision()
    {
        var note = await SeedNote("Agenda", "v1");
        var vm = await Opened();
        await Select(vm, "Agenda");
        PlayEditor(editor =>
        {
            editor.Content = "A's v2";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();                                 // revision 2
            _other.SaveNoteAsync(note.Id, 2, new ProjectNoteDraft("Agenda (theirs)", "their v3")).GetAwaiter().GetResult(); // revision 3
        });

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        var row = Row(vm, "Agenda (theirs)");
        Assert.Equal(3, row.Note!.Revision);
        Assert.Same(row, vm.SelectedItem);
        Assert.Equal("their v3", vm.PreviewNote!.Content);
        Assert.Equal(3, vm.PreviewNote.Revision);
    }

    [Fact]
    public async Task Conflict_ThenSaveAsNew_ReconcilesBothIdsFromFreshReads()
    {
        var note = await SeedNote("Agenda", "v1");
        var vm = await Opened();
        await Select(vm, "Agenda");
        PlayEditor(editor =>
        {
            _other.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Agenda", "theirs")).GetAwaiter().GetResult();
            editor.Title = "Agenda (mine)";
            editor.Content = "mine";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();      // Conflict
            Assert.True(editor.CanRecover);
            editor.SaveAsNewCommand.ExecuteAsync().GetAwaiter().GetResult(); // next to the original
            Assert.Equal(2, editor.TouchedNoteIds.Count);
        });

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        Assert.Equal(["Agenda", "Agenda (mine)"], Names(vm));
        Assert.Equal(2, Row(vm, "Agenda").Note!.Revision);          // the other window's version, read fresh
        Assert.Same(Row(vm, "Agenda (mine)"), vm.SelectedItem);    // the created note is selected
        Assert.Equal("mine", vm.PreviewNote!.Content);
    }

    // ---- Editor location after external moves (§6.2) ----------------------------------------------------------------

    /// <summary>Archive › Destination, so the label proves the full logical path, not just the folder name.</summary>
    private async Task<ProjectFolder> SeedDestination()
    {
        var archive = await SeedFolder("Archive");
        return (await _other.CreateFolderAsync(_alpha, archive.Id, "Destination")).Value!;
    }

    [Fact]
    public async Task ExternalMoveBeforeOpen_TheEditorShowsTheNotesActualLocation()
    {
        var destination = await SeedDestination();
        var note = await SeedNote("Agenda");
        var vm = await Opened();                  // the listing still shows the note at the Project root
        await Select(vm, "Agenda");
        Assert.True((await _other.MoveNoteAsync(note.Id, null, destination.Id)).Moved);
        string? location = null;
        PlayEditor(editor => location = editor.LocationText);

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        Assert.Equal("Alpha", vm.LocationPath);
        Assert.Equal("Location: Alpha › Archive › Destination", location);
        Assert.DoesNotContain("Agenda", Names(vm)); // reconciliation removes the moved-away row
    }

    [Fact]
    public async Task ExternalMoveWhileEditing_ThenSave_TheLocationReflectsTheSavedRow()
    {
        var destination = await SeedDestination();
        var note = await SeedNote("Agenda", "v1");
        var vm = await Opened();
        await Select(vm, "Agenda");
        PlayEditor(editor =>
        {
            Assert.Equal("Location: Alpha", editor.LocationText);
            Assert.True(_other.MoveNoteAsync(note.Id, null, destination.Id).GetAwaiter().GetResult().Moved); // Revision unchanged
            editor.Content = "v2";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();

            Assert.Null(editor.GeneralError);
            Assert.StartsWith("Saved ", editor.StatusText);
            Assert.Equal("Location: Alpha › Archive › Destination", editor.LocationText);
        });

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        var stored = (await _other.GetNoteAsync(note.Id))!;
        Assert.Equal(destination.Id, stored.FolderId);  // Save never writes the container
        Assert.Equal(("v2", 2L), (stored.Content, stored.Revision));
        Assert.DoesNotContain("Agenda", Names(vm));
    }

    [Fact]
    public async Task ExternalMoveAndEdit_Conflict_ThenSaveAsNew_FolderLabelAndConfirmationAgree()
    {
        var destination = await SeedDestination();
        var note = await SeedNote("Agenda", "v1");
        var vm = await Opened();
        await Select(vm, "Agenda");
        NoteEditorViewModel? played = null;
        PlayEditor(editor =>
        {
            played = editor;
            Assert.True(_other.MoveNoteAsync(note.Id, null, destination.Id).GetAwaiter().GetResult().Moved);
            _other.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Agenda", "theirs")).GetAwaiter().GetResult();
            editor.Content = "mine";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();      // Conflict
            Assert.True(editor.CanRecover);
            Assert.Equal("Location: Alpha", editor.LocationText);             // nothing persisted yet: unchanged

            editor.SaveAsNewCommand.ExecuteAsync().GetAwaiter().GetResult(); // next to the moved original
        });

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        var created = (await _other.GetNoteAsync(played!.NoteId!.Value))!;
        Assert.NotEqual(note.Id, created.Id);
        Assert.Equal(destination.Id, created.FolderId);
        Assert.Equal("Location: Alpha › Archive › Destination", played.LocationText);
        Assert.Equal("Saved as a new note in Alpha › Archive › Destination. The original note was not changed.", played.StatusText);
        Assert.Equal("theirs", (await _other.GetNoteAsync(note.Id))!.Content);
        Assert.DoesNotContain(vm.Items, i => i.IsNote); // neither note is in the root listing any more
    }

    [Fact]
    public async Task LocationLookupFailsAtOpen_TheEditorStillOpens_WithTheUnavailableFallback()
    {
        await SeedNote("Agenda");
        var vm = await Opened();
        await Select(vm, "Agenda");
        _store.Hold("containerPath").Fail(new IOException("database is locked"));
        string? location = null;
        PlayEditor(editor => location = editor.LocationText);

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        Assert.Equal(NoteEditorViewModel.LocationUnavailable, location); // never the listing's possibly stale breadcrumb
    }

    [Fact]
    public async Task NoteRemovedExternally_IsRemovedFromTheListing_AndItsPreviewCleared()
    {
        var note = await SeedNote("Agenda");
        await SeedNote("Other");
        var vm = await Opened();
        await Select(vm, "Agenda");
        PlayEditor(_ =>
        {
            using var connection = _root.Open();
            Db.Exec(connection, "DELETE FROM ProjectNotes WHERE Id = $id;", ("$id", note.Id));
        });

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        Assert.Equal(["Other"], Names(vm));
        Assert.Null(vm.SelectedItem);
        Assert.Null(vm.PreviewNote);
    }

    [Fact]
    public async Task NavigationOrDeactivationDuringTheReconciliationRead_DiscardsTheLateResultAndError()
    {
        var x = await SeedFolder("X");
        var note = await SeedNote("Agenda");
        await SeedNote("Inside", folder: x.Id);
        var vm = await Opened();
        await Select(vm, "Agenda");
        var gate = _store.Hold("getNotes");
        PlayEditor(editor =>
        {
            editor.Content = "edited";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
        });

        var open = vm.OpenCommand.ExecuteAsync();
        gate.WaitEntered();
        await vm.NavigateAsync(x.Id); // the user moved on while the read was pending
        gate.Release();
        await open.WaitAsync(Wait);
        await vm.CurrentReconciliation;
        Assert.Equal(["Inside"], Names(vm));

        // A late FAILURE for an obsolete view is discarded too (deactivation this time).
        await vm.GoUpCommand.ExecuteAsync();
        await Select(vm, "Agenda");
        var failing = _store.Hold("getNotes");
        var again = vm.OpenCommand.ExecuteAsync();
        failing.WaitEntered();
        vm.Deactivate();
        failing.Fail(new IOException("database is locked"));
        await again.WaitAsync(Wait);
        await vm.CurrentReconciliation;
        Assert.Null(vm.BannerMessage);
        Assert.True(note.Id > 0);
    }

    [Fact]
    public async Task AnOlderPreviewRequest_CannotOverwriteTheRefreshedPreview()
    {
        var note = await SeedNote("Agenda", "v1");
        var vm = await Opened();
        var gate = _store.Hold(GatedProjectContentStore.Note(note.Id));
        vm.SelectedItem = Row(vm, "Agenda");       // preview request #1 reads "v1" and is held
        var olderPreview = vm.CurrentPreviewLoad;
        gate.WaitEntered();
        _dialogs.OnNoteEditor = editor =>
        {
            editor.Content = "v2";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            editor.RequestClose();
            editor.CurrentClose.GetAwaiter().GetResult();
            return true;
        };

        // The editor reads the note itself (GetNoteAsync for this id is no longer gated: the gate was consumed).
        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;
        Assert.Equal("v2", vm.PreviewNote!.Content);

        gate.Release();             // the older request finally delivers "v1"
        await olderPreview;
        Assert.Equal("v2", vm.PreviewNote!.Content);
        Assert.False(vm.IsPreviewLoading);
    }

    [Fact]
    public async Task ReconciliationReadFailure_PreservesRowsSelectionAndPreview_AndIsNotNotFound()
    {
        var x = await SeedFolder("X");
        var y = await SeedFolder("Y");
        var note = await SeedNote("Agenda", "v1", folder: x.Id);
        var vm = await Opened();
        await vm.NavigateAsync(x.Id);
        await Select(vm, "Agenda");
        var previewBefore = vm.PreviewNote;
        var gate = _store.Hold("getNotes");
        PlayEditor(_ => _other.MoveNoteAsync(note.Id, x.Id, y.Id).GetAwaiter().GetResult());

        var open = vm.OpenCommand.ExecuteAsync();
        gate.WaitEntered();
        gate.Fail(new IOException("database is locked"));
        await open.WaitAsync(Wait);
        await vm.CurrentReconciliation;

        Assert.Equal(["Agenda"], Names(vm)); // not removed, although it really moved: a failed read proves nothing
        Assert.Same(Row(vm, "Agenda"), vm.SelectedItem);
        Assert.Same(previewBefore, vm.PreviewNote);
        Assert.Equal("The list could not be refreshed after editing. It may be out of date; open the folder again to refresh.", vm.BannerMessage);

        await vm.NavigateAsync(x.Id); // opening the folder again shows the persisted state
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task InPlaceReconciliation_LeavesARunningLinkCheckUnaffected()
    {
        await _other.CreateLinkAsync(_alpha, null, new ProjectLinkDraft("Out", ProjectLinkType.Folder, @"C:\Work\Out"));
        await SeedNote("Agenda");
        var vm = await Opened();
        var generation = vm.Generation;
        vm.SelectedItem = Row(vm, "Out");
        var check = vm.CheckCommand.ExecuteAsync();
        await Task.Delay(50);
        var request = Assert.Single(_checker.Requests);

        await Select(vm, "Agenda");
        PlayEditor(editor =>
        {
            editor.Content = "edited";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
        });
        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        Assert.Equal(generation, vm.Generation);
        Assert.False(request.Token.IsCancellationRequested);
        Assert.True(Row(vm, "Out").IsChecking);
        request.Completion.SetResult(LinkHealthStatus.Found);
        await check.WaitAsync(Wait);
        Assert.Equal(LinkHealthStatus.Found, Row(vm, "Out").Status);
    }

    [Fact]
    public async Task MoveNote_UsesTheObservedContainer_AndRemovesTheRow()
    {
        var x = await SeedFolder("X");
        await SeedNote("Agenda");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Agenda");
        _dialogs.OnProjectContentMove = editor =>
        {
            editor.SelectedDestination = editor.Destinations.Single(d => d.FolderId == x.Id);
            editor.MoveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return editor.Moved;
        };

        await vm.MoveCommand.ExecuteAsync();

        Assert.Equal(["X"], Names(vm));
        var moved = Assert.Single((await _other.ListAsync(_alpha, x.Id)).Listing!.Notes);
        Assert.Equal(1, moved.Revision);
    }
}
