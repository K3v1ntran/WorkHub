using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Move to Trash, Undo and the Trash view in the content pane: confirmation and actual counts, the single pending-write
/// slot, and delayed completions whose results may change only the view they started in (Phase 7 proposal revision 2 §7,
/// §10.3). Held store calls ("countTrash", "trash", "restore") model slow operations.
/// </summary>
public sealed class ProjectTrashViewModelTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedProjectContentStore _store;
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _other;
    private readonly ScriptedLinkChecker _checker = new();
    private readonly RecordingProcessStarter _starter = new();
    private readonly long _alpha;
    private readonly long _beta;
    private readonly List<ProjectContentViewModel> _views = [];

    public ProjectTrashViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var launcher = new ProcessProjectLinkLauncher(_starter, _ => true, _ => true);
        _store = new GatedProjectContentStore(new SqliteProjectContentStore(_connections));
        _content = new ProjectContentService(_store, launcher);
        _other = new ProjectContentService(new SqliteProjectContentStore(_connections), launcher);
        var workspace = new WorkspaceService(new SqliteWorkspaceStore(_connections));
        _alpha = workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active)).Result.Value!.Id;
        _beta = workspace.CreateProjectAsync(null, new ProjectDraft("Beta", ProjectStatus.Active)).Result.Value!.Id;
    }

    /// <summary>Pending view-model work finishes before the database folder is deleted.</summary>
    public void Dispose()
    {
        var pending = _views.SelectMany(v => new[] { v.CurrentLoad, v.CurrentTrashOperation, v.CurrentPreviewLoad, v.CurrentReconciliation, v.Trash.CurrentLoad, v.Trash.CurrentRestore });
        Task.WhenAll(pending).ContinueWith(_ => { }).Wait(Wait);
        _root.Dispose();
    }

    private async Task<ProjectContentViewModel> Opened(long? project = null)
    {
        var vm = new ProjectContentViewModel(_content, _checker, _dialogs, NullAppLog.Instance, new LinkCheckCache(), project ?? _alpha, project is null || project == _alpha ? "Alpha" : "Beta");
        _views.Add(vm);
        await vm.ActivateAsync();
        return vm;
    }

    private async Task<ProjectFolder> Folder(string name, long? parent = null) => (await _other.CreateFolderAsync(_alpha, parent, name)).Value!;

    private async Task<ProjectLink> Link(string name, long? folder = null) =>
        (await _other.CreateLinkAsync(_alpha, folder, new ProjectLinkDraft(name, ProjectLinkType.Folder, $@"C:\Work\{name}"))).Value!;

    private async Task<ProjectNote> Note(string title, long? folder = null, string content = "v1") =>
        (await _other.CreateNoteAsync(_alpha, folder, new ProjectNoteDraft(title, content))).Note!;

    private static ProjectContentItemViewModel Row(ProjectContentViewModel vm, string name) => vm.Items.Single(i => i.Name == name);

    private static List<string> Names(ProjectContentViewModel vm) => vm.Items.Select(i => i.Name).ToList();

    private async Task<long> TrashEntries()
    {
        var entries = await _other.ListTrashAsync(_alpha);
        return entries.Count;
    }

    private async Task<bool> IsTrashed(ContentKind kind, long id) => kind switch
    {
        ContentKind.Link => (await _other.GetLinkAsync(id))!.IsTrashed,
        ContentKind.Note => (await _other.GetNoteAsync(id))!.IsTrashed,
        _ => (await _other.ListAsync(_alpha, id)).Problem == ContentContainerProblem.FolderTrashed,
    };

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

    // ---- Confirmation, actual count, Undo --------------------------------------------------------------------------------

    [Fact]
    public async Task MoveFolderToTrash_ConfirmsWithTheSnapshot_ShowsTheActualCount_AndUndoPutsTheRowBack()
    {
        var drafts = await Folder("Drafts");
        await Note("One", drafts.Id);
        await Link("Zeta");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Drafts");
        Assert.Equal("Trash", vm.TrashButtonText);

        // While the confirmation is open, another window adds an item to the folder.
        _dialogs.OnConfirm = _ => _other.CreateLinkAsync(_alpha, drafts.Id, new ProjectLinkDraft("Added", ProjectLinkType.Web, "https://a.test/")).GetAwaiter().GetResult();
        await vm.MoveToTrashCommand.ExecuteAsync();

        var (title, message, confirm) = Assert.Single(_dialogs.Confirmations);
        Assert.Equal(("Move to Trash", "Move to Trash"), (title, confirm));
        Assert.Equal("Move folder “Drafts” and its contents to the Trash? It currently contains 1 item; whatever it contains when the move happens goes with it. Files on disk are not touched.", message);
        Assert.Equal("Moved “Drafts” and 2 items inside it to the Trash.", vm.UndoMessage); // the transaction's count
        Assert.Equal(["Zeta"], Names(vm));
        Assert.Null(vm.SelectedItem);
        Assert.Equal("Trash (1)", vm.TrashButtonText);
        Assert.True(await IsTrashed(ContentKind.Folder, drafts.Id));

        await vm.UndoCommand.ExecuteAsync();

        Assert.False(vm.HasUndo);
        Assert.Equal(["Drafts", "Zeta"], Names(vm)); // inserted once, in order, from the committed row
        Assert.Same(Row(vm, "Drafts"), vm.SelectedItem);
        Assert.Equal(ProjectContentItemKind.Folder, vm.SelectedItem!.Kind);
        Assert.Null(vm.BannerMessage);
        Assert.Equal("Trash", vm.TrashButtonText);
        Assert.Equal(0, await TrashEntries());
        Assert.Equal(["Added", "One"], (await _other.ListAsync(_alpha, drafts.Id)).Listing!.Links.Select(l => l.Name).Concat((await _other.ListAsync(_alpha, drafts.Id)).Listing!.Notes.Select(n => n.Title)));
    }

    [Fact]
    public async Task CancellingTheConfirmation_WritesNothing_AndKeepsTheCurrentUndo()
    {
        await Link("First");
        await Link("Second");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "First");
        await vm.MoveToTrashCommand.ExecuteAsync();
        Assert.True(vm.HasUndo);

        _dialogs.ConfirmAnswer = false;
        vm.SelectedItem = Row(vm, "Second");
        await vm.MoveToTrashCommand.ExecuteAsync();

        Assert.Equal("Move link “Second” to the Trash? Its target is not touched.", _dialogs.Confirmations[^1].Message);
        Assert.Equal(["Second"], Names(vm));
        Assert.Equal(1, _store.TrashCalls);
        Assert.True(vm.HasUndo); // no write started, so the earlier Undo stays
        Assert.Equal("Moved “First” to the Trash.", vm.UndoMessage);
    }

    [Fact]
    public async Task UndoForARootThatWasMovedBeforeItWasTrashed_RestoresThere_WithoutInsertingARow()
    {
        var destination = await Folder("Destination");
        var link = await Link("Roamer");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Roamer");
        Assert.True((await _other.MoveLinkAsync(link.Id, null, destination.Id)).Moved); // the listing is stale now

        await vm.MoveToTrashCommand.ExecuteAsync(); // identity-based: trashed in its current container
        Assert.Equal(["Destination"], Names(vm));
        await vm.UndoCommand.ExecuteAsync();

        Assert.Equal("Restored to Alpha › Destination.", vm.BannerMessage);
        Assert.Equal(["Destination"], Names(vm));
        Assert.False(await IsTrashed(ContentKind.Link, link.Id));
    }

    [Fact]
    public async Task UndoKeepsOtherRowsLinkChecksRunning()
    {
        await Link("Checked");
        await Note("Doomed");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Checked");
        var check = vm.CheckCommand.ExecuteAsync();
        await WaitUntil(() => _checker.Requests.Count == 1);

        vm.SelectedItem = Row(vm, "Doomed");
        await vm.MoveToTrashCommand.ExecuteAsync();
        await vm.UndoCommand.ExecuteAsync();
        Assert.Equal(["Checked", "Doomed"], Names(vm));

        _checker.Requests[0].Completion.SetResult(LinkHealthStatus.Found);
        await check.WaitAsync(Wait);
        Assert.Equal(LinkHealthStatus.Found, Row(vm, "Checked").Status); // the check was neither cancelled nor dropped
        Assert.False(_checker.Requests[0].Token.IsCancellationRequested);
    }

    // ---- Delayed completions (§10.3) --------------------------------------------------------------------------------------

    [Fact]
    public async Task TrashHeld_NavigateToAnotherContainer_Release_ItemTrashed_NothingChangesInTheNewContainer()
    {
        var other = await Folder("Other");
        await Link("Inside", other.Id);
        var link = await Link("Doomed");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Doomed");
        var gate = _store.Hold("trash");

        var trash = vm.MoveToTrashCommand.ExecuteAsync();
        gate.WaitEntered();
        vm.SelectedItem = Row(vm, "Other");
        await vm.OpenCommand.ExecuteAsync(); // navigation stays allowed while the write is pending
        Assert.True(vm.IsWriteBusy);
        gate.Release();
        await trash.WaitAsync(Wait);

        Assert.True(await IsTrashed(ContentKind.Link, link.Id));
        Assert.Equal(["Inside"], Names(vm));
        Assert.False(vm.HasUndo);
        Assert.Null(vm.BannerMessage);
        Assert.Equal(other.Id, vm.CurrentFolderId);
        Assert.False(vm.IsWriteBusy);

        await vm.GoUpCommand.ExecuteAsync(); // a later visit is a fresh load without the item
        Assert.Equal(["Other"], Names(vm));
        Assert.Equal("Trash (1)", vm.TrashButtonText);
    }

    [Fact]
    public async Task TrashHeld_AnotherProjectSelected_Release_ItemTrashed_NeitherViewChanges()
    {
        var link = await Link("Doomed");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Doomed");
        var gate = _store.Hold("trash");

        var trash = vm.MoveToTrashCommand.ExecuteAsync();
        gate.WaitEntered();
        vm.Deactivate(); // another Project selected: this view is deactivated, the next view is a new instance
        var beta = await Opened(_beta);
        gate.Release();
        await trash.WaitAsync(Wait);

        Assert.True(await IsTrashed(ContentKind.Link, link.Id));
        Assert.Equal(["Doomed"], Names(vm)); // the deactivated view is left as it was
        Assert.False(vm.HasUndo);
        Assert.Null(vm.BannerMessage);
        Assert.Empty(beta.Items);
        Assert.False(beta.HasUndo);
        Assert.Null(beta.BannerMessage);
    }

    [Fact]
    public async Task TrashHeld_AwayAndBackToTheSameContainer_TheLateResultChangesNothing_ActingOnTheStaleRowReportsTrash()
    {
        var docs = await Folder("Docs");
        var link = await Link("Doomed");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Doomed");
        var gate = _store.Hold("trash");

        var trash = vm.MoveToTrashCommand.ExecuteAsync();
        gate.WaitEntered();
        vm.SelectedItem = Row(vm, "Docs");
        await vm.OpenCommand.ExecuteAsync();
        await vm.GoUpCommand.ExecuteAsync(); // back to the root: a NEW visit, listed before the write commits
        Assert.Equal(["Docs", "Doomed"], Names(vm));
        gate.Release();
        await trash.WaitAsync(Wait);

        Assert.True(await IsTrashed(ContentKind.Link, link.Id));
        Assert.Equal(["Docs", "Doomed"], Names(vm)); // not removed by the late result
        Assert.False(vm.HasUndo);
        Assert.Null(vm.BannerMessage);

        vm.SelectedItem = Row(vm, "Doomed");
        await vm.MoveToTrashCommand.ExecuteAsync();
        Assert.Equal("This item is in the Trash.", vm.BannerMessage);
        Assert.Equal(["Docs"], Names(vm));
        Assert.Single(_dialogs.Confirmations); // no second confirmation for a trashed item
        Assert.Equal(1, _store.TrashCalls);
    }

    [Fact]
    public async Task CountHeld_NavigateAway_Release_NoDialog_NothingWritten()
    {
        var docs = await Folder("Docs");
        var link = await Link("Doomed");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Doomed");
        var gate = _store.Hold("countTrash");

        var trash = vm.MoveToTrashCommand.ExecuteAsync();
        gate.WaitEntered();
        vm.SelectedItem = Row(vm, "Docs");
        await vm.OpenCommand.ExecuteAsync();
        gate.Release();
        await trash.WaitAsync(Wait);

        Assert.Empty(_dialogs.Confirmations);
        Assert.Equal(0, _store.TrashCalls);
        Assert.False(await IsTrashed(ContentKind.Link, link.Id));
        Assert.Null(vm.BannerMessage);
        Assert.Equal(docs.Id, vm.CurrentFolderId);
    }

    [Fact]
    public async Task CountDone_ButTheSelectionChanged_NoDialog()
    {
        await Link("A");
        await Link("B");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "A");
        var gate = _store.Hold("countTrash");

        var trash = vm.MoveToTrashCommand.ExecuteAsync();
        gate.WaitEntered();
        vm.SelectedItem = Row(vm, "B");
        gate.Release();
        await trash.WaitAsync(Wait);

        Assert.Empty(_dialogs.Confirmations);
        Assert.Equal(0, _store.TrashCalls);
    }

    [Fact]
    public async Task RestoreHeld_TrashViewClosed_Release_EntryRestored_TheViewsAreNotModifiedByTheResult()
    {
        var link = await Link("Doomed");
        await _other.TrashAsync(new ContentRef(ContentKind.Link, link.Id));
        var vm = await Opened();
        await vm.OpenTrashCommand.ExecuteAsync();
        Assert.True(vm.IsTrashOpen);
        var entry = Assert.Single(vm.Trash.Entries);
        Assert.Equal(("Doomed", "Link", "from Alpha", "1 item"), (entry.Name, entry.KindText, entry.FromText, entry.ItemsText));
        vm.Trash.SelectedEntry = entry;
        var gate = _store.Hold("restore");

        var restore = vm.Trash.RestoreCommand.ExecuteAsync();
        gate.WaitEntered();
        await vm.CloseTrashCommand.ExecuteAsync(); // Back: a fresh listing, loaded before the restore commits
        Assert.False(vm.IsTrashOpen);
        Assert.Empty(vm.Items);
        gate.Release();
        await restore.WaitAsync(Wait);

        Assert.False(await IsTrashed(ContentKind.Link, link.Id));
        Assert.Empty(vm.Items); // the listing is never modified by a Restore from the Trash view
        Assert.Empty(vm.Trash.Entries);
        Assert.Null(vm.Trash.Notice);
        Assert.Null(vm.BannerMessage);

        await vm.RetryCommand.ExecuteAsync(); // no-op guard: Retry is only for load errors
        await vm.OpenTrashCommand.ExecuteAsync();
        await vm.CloseTrashCommand.ExecuteAsync(); // the next listing load shows the item
        Assert.Equal(["Doomed"], Names(vm));
    }

    [Fact]
    public async Task RestoreFromTheTrashView_UpdatesThatView_RefusalsKeepTheEntry()
    {
        var drafts = await Folder("Drafts");
        var note = await Note("Agenda", drafts.Id);
        await _other.TrashAsync(new ContentRef(ContentKind.Note, note.Id));
        await _other.TrashAsync(new ContentRef(ContentKind.Folder, drafts.Id));
        var vm = await Opened();
        await vm.OpenTrashCommand.ExecuteAsync();
        Assert.Equal(["Drafts", "Agenda"], vm.Trash.Entries.Select(e => e.Name));
        Assert.Equal("from Alpha › Drafts (in the Trash)", vm.Trash.Entries[1].FromText);

        vm.Trash.SelectedEntry = vm.Trash.Entries[1];
        await vm.Trash.RestoreCommand.ExecuteAsync();
        Assert.Equal("The folder it came from is in the Trash. Restore “Drafts” first.", vm.Trash.Notice);
        Assert.Equal(2, vm.Trash.Entries.Count);

        vm.Trash.SelectedEntry = vm.Trash.Entries[0];
        await vm.Trash.RestoreCommand.ExecuteAsync();
        Assert.Equal("Restored “Drafts” to Alpha.", vm.Trash.Notice);
        Assert.Equal(["Agenda"], vm.Trash.Entries.Select(e => e.Name));
        Assert.Equal("Trash — Alpha (1)", vm.Trash.Title);
    }

    [Fact]
    public async Task UndoHeld_NavigateToAnotherContainer_Release_EntryRestored_NoInsertionNoSelectionNoMessage()
    {
        var docs = await Folder("Docs");
        await Link("InDocs", docs.Id);
        var link = await Link("Doomed");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Doomed");
        await vm.MoveToTrashCommand.ExecuteAsync();
        var gate = _store.Hold("restore");

        var undo = vm.UndoCommand.ExecuteAsync();
        gate.WaitEntered();
        vm.SelectedItem = Row(vm, "Docs");
        await vm.OpenCommand.ExecuteAsync();
        var selected = vm.SelectedItem;
        gate.Release();
        await undo.WaitAsync(Wait);

        Assert.False(await IsTrashed(ContentKind.Link, link.Id));
        Assert.Equal(["InDocs"], Names(vm));
        Assert.Same(selected, vm.SelectedItem);
        Assert.Null(vm.BannerMessage);
        Assert.False(vm.HasUndo);
    }

    [Fact]
    public async Task ANewTrashStarting_ClearsTheOlderUndo_AndItsFailureDoesNotBringItBack()
    {
        await Link("First");
        await Link("Second");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "First");
        await vm.MoveToTrashCommand.ExecuteAsync();
        Assert.Equal("Moved “First” to the Trash.", vm.UndoMessage);

        vm.SelectedItem = Row(vm, "Second");
        var gate = _store.Hold("trash");
        var second = vm.MoveToTrashCommand.ExecuteAsync();
        gate.WaitEntered();
        Assert.False(vm.HasUndo); // B1 withdrawn when T2 started
        Assert.False(vm.UndoCommand.CanExecute(null));
        gate.Fail(new IOException("disk"));
        await second.WaitAsync(Wait);

        Assert.False(vm.HasUndo);
        Assert.Equal("Could not move to the Trash. Nothing was changed.", vm.BannerMessage);
        Assert.Equal(["Second"], Names(vm)); // the failed item stays
        Assert.Equal(1, await TrashEntries()); // First is still in the Trash
    }

    [Fact]
    public async Task AnIneligibleOlderTrash_NeverShowsABanner_OnlyTheNewerOneDoes()
    {
        var docs = await Folder("Docs");
        await Link("T1");
        await Link("T2");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "T1");
        var gate = _store.Hold("trash");

        var first = vm.MoveToTrashCommand.ExecuteAsync();
        gate.WaitEntered();
        vm.SelectedItem = Row(vm, "Docs");
        await vm.OpenCommand.ExecuteAsync();
        await vm.GoUpCommand.ExecuteAsync();
        vm.SelectedItem = Row(vm, "T2");
        Assert.False(vm.MoveToTrashCommand.CanExecute(null)); // T2 can start only after T1 released the slot
        gate.Release();
        await first.WaitAsync(Wait);
        Assert.False(vm.HasUndo);

        await vm.MoveToTrashCommand.ExecuteAsync();

        Assert.Equal("Moved “T2” to the Trash.", vm.UndoMessage);
        Assert.Equal(2, await TrashEntries());
        _ = docs;
    }

    [Fact]
    public async Task RepeatedInvocationsWhileBusy_IssueOneStoreCall()
    {
        await Link("Doomed");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Doomed");
        var gate = _store.Hold("trash");

        var trash = vm.MoveToTrashCommand.ExecuteAsync();
        gate.WaitEntered();
        Assert.False(vm.MoveToTrashCommand.CanExecute(null));
        vm.MoveToTrashCommand.Execute(null);              // click / Del
        await vm.MoveToTrashCommand.ExecuteAsync();       // direct invocation
        gate.Release();
        await trash.WaitAsync(Wait);
        Assert.Equal(1, _store.TrashCalls);
        Assert.Single(_dialogs.Confirmations);

        var restoreGate = _store.Hold("restore");
        var undo = vm.UndoCommand.ExecuteAsync();
        restoreGate.WaitEntered();
        Assert.False(vm.UndoCommand.CanExecute(null));
        await vm.UndoCommand.ExecuteAsync();
        vm.UndoCommand.Execute(null);
        restoreGate.Release();
        await undo.WaitAsync(Wait);
        Assert.Equal(1, _store.RestoreCalls);
        Assert.Equal(["Doomed"], Names(vm)); // inserted once

        // Restore in the Trash view is guarded by the same slot.
        vm.SelectedItem = Row(vm, "Doomed");
        await vm.MoveToTrashCommand.ExecuteAsync();
        await vm.OpenTrashCommand.ExecuteAsync();
        vm.Trash.SelectedEntry = vm.Trash.Entries.Single();
        var heldRestore = _store.Hold("restore");
        var restore = vm.Trash.RestoreCommand.ExecuteAsync();
        heldRestore.WaitEntered();
        Assert.False(vm.Trash.RestoreCommand.CanExecute(null));
        await vm.Trash.RestoreCommand.ExecuteAsync();
        heldRestore.Release();
        await restore.WaitAsync(Wait);
        Assert.Equal(2, _store.RestoreCalls);
        Assert.Empty(vm.Trash.Entries);
    }

    // ---- Existing flows against items trashed elsewhere -----------------------------------------------------------------

    [Fact]
    public async Task ATrashedFolder_ShowsItsMessage_AndUpReturnsToTheRoot()
    {
        var docs = await Folder("Docs");
        var vm = await Opened();
        await _other.TrashAsync(new ContentRef(ContentKind.Folder, docs.Id));
        vm.SelectedItem = Row(vm, "Docs");

        await vm.OpenCommand.ExecuteAsync();

        Assert.Equal("This folder is in the Trash.", vm.ContainerMessage);
        Assert.True(vm.IsFolderMissing);
        await vm.GoUpCommand.ExecuteAsync();
        Assert.Empty(vm.Items);
        Assert.Null(vm.ContainerMessage);
    }

    [Fact]
    public async Task OpeningOrCheckingALinkTrashedElsewhere_RemovesTheRow_AndNeverLaunchesOrChecks()
    {
        var opened = await Link("Opened");
        var checkedLink = await Link("Checked");
        var vm = await Opened();
        await _other.TrashAsync(new ContentRef(ContentKind.Link, opened.Id));
        await _other.TrashAsync(new ContentRef(ContentKind.Link, checkedLink.Id));

        vm.SelectedItem = Row(vm, "Opened");
        await vm.OpenCommand.ExecuteAsync();
        Assert.Equal("This link is in the Trash.", vm.BannerMessage);
        Assert.Empty(_starter.Started);

        vm.SelectedItem = Row(vm, "Checked");
        await vm.CheckCommand.ExecuteAsync();
        Assert.Equal("This link is in the Trash.", vm.BannerMessage);
        Assert.Empty(_checker.Requests);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task PreviewAndEditorOpen_OfANoteTrashedElsewhere_SayItIsInTheTrash()
    {
        await Note("Previewed");
        await Note("Opened");
        var vm = await Opened();
        foreach (var row in vm.Items.ToList())
            await _other.TrashAsync(row.Ref);

        vm.SelectedItem = Row(vm, "Previewed");
        await vm.CurrentPreviewLoad;
        Assert.Equal("This note is in the Trash.", vm.PreviewMessage);
        Assert.Null(vm.PreviewNote);

        _dialogs.OnNoteEditor = _ => throw new InvalidOperationException("the editor must not open");
        vm.SelectedItem = Row(vm, "Opened");
        await vm.OpenCommand.ExecuteAsync();
        Assert.Equal("This note is in the Trash.", vm.BannerMessage);
        Assert.DoesNotContain("Opened", Names(vm));
    }

    [Fact]
    public async Task EditorSave_OnANoteTrashedWhileOpen_KeepsTheDraft_OffersRecovery_AndAfterRestoreSavesWithoutConflict()
    {
        var specs = await Folder("Specs");
        var note = await Note("Kickoff", specs.Id);
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Specs");
        await vm.OpenCommand.ExecuteAsync();
        vm.SelectedItem = Row(vm, "Kickoff");
        long? created = null;
        _dialogs.OnNoteEditor = editor =>
        {
            var entry = _other.TrashAsync(new ContentRef(ContentKind.Note, note.Id)).GetAwaiter().GetResult().Entry!;
            editor.Content = "my draft";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.Equal("This note is in the Trash. Your text is unchanged here.", editor.GeneralError);
            Assert.Equal("my draft", editor.Content);
            Assert.True(editor.CopyDraftCommand.CanExecute(null));
            editor.CopyDraftCommand.Execute(null);
            Assert.Contains("my draft", _dialogs.Clipboard);

            _other.RestoreAsync(entry.Id).GetAwaiter().GetResult();
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult(); // Revision unchanged by Trash/Restore
            Assert.Null(editor.GeneralError);
            Assert.False(editor.IsDirty);

            _other.TrashAsync(new ContentRef(ContentKind.Note, note.Id)).GetAwaiter().GetResult();
            editor.Content = "second draft";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            editor.SaveAsNewCommand.ExecuteAsync().GetAwaiter().GetResult(); // its folder is active
            created = editor.NoteId;
            Assert.StartsWith("Saved as a new note in Alpha › Specs.", editor.StatusText);
            editor.RequestClose();
            return editor.SavedAny;
        };

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        Assert.Equal(2, (await _other.GetNoteAsync(note.Id))!.Revision);
        Assert.True((await _other.GetNoteAsync(note.Id))!.IsTrashed);
        Assert.Equal("second draft", (await _other.GetNoteAsync(created!.Value))!.Content);
        // Reconciliation treats the trashed original as absent and shows the new note.
        Assert.Equal(["Kickoff"], Names(vm));
        Assert.Equal(created, vm.Items.Single().Id);
    }

    [Fact]
    public async Task EditorSaveAsNew_IsRefused_WhenTheNotesFolderIsInTheTrash()
    {
        var specs = await Folder("Specs");
        await Note("Kickoff", specs.Id);
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Specs");
        await vm.OpenCommand.ExecuteAsync();
        vm.SelectedItem = Row(vm, "Kickoff");
        _dialogs.OnNoteEditor = editor =>
        {
            _other.TrashAsync(new ContentRef(ContentKind.Folder, specs.Id)).GetAwaiter().GetResult();
            editor.Content = "draft";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            editor.SaveAsNewCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.Equal("The note's location is in the Trash. Copy your text instead.", editor.GeneralError);
            Assert.Equal("draft", editor.Content);
            Assert.True(editor.CopyDraftCommand.CanExecute(null));
            _dialogs.OnUnsavedChanges = _ => Presentation.Services.UnsavedChangesChoice.Discard;
            editor.RequestClose();
            return editor.SavedAny;
        };

        await vm.OpenCommand.ExecuteAsync();
        await vm.CurrentReconciliation;

        Assert.Empty(vm.Items);
        Assert.Single(await _other.ListTrashAsync(_alpha));
    }

    [Fact]
    public async Task ConfirmationAndBannerTexts_ForEveryKindAndCount()
    {
        Assert.Equal("Move note “N” to the Trash?", ProjectContentViewModel.ConfirmationText(ContentKind.Note, "N", 1));
        Assert.Contains("It is currently empty;", ProjectContentViewModel.ConfirmationText(ContentKind.Folder, "F", 1));
        Assert.Contains("It currently contains 4 items;", ProjectContentViewModel.ConfirmationText(ContentKind.Folder, "F", 5));
        Assert.Equal("Moved “F” to the Trash.", ProjectContentViewModel.TrashedText(ContentKind.Folder, "F", 1));
        Assert.Equal("Moved “F” and 1 item inside it to the Trash.", ProjectContentViewModel.TrashedText(ContentKind.Folder, "F", 2));
        Assert.Equal("Moved “L” to the Trash.", ProjectContentViewModel.TrashedText(ContentKind.Link, "L", 1));
        await Task.CompletedTask;
    }

    // ---- A refused Rename / Edit / Move of a row trashed meanwhile (§8): the source row is reconciled after the dialog closes

    private async Task RestoreOnlyEntry() => await _other.RestoreAsync((await _other.ListTrashAsync(_alpha)).Single().Id);

    [Fact]
    public async Task FolderTrashedWhileRenameIsOpen_SaveRefusedKeepsTheName_AfterClosingTheRowLeaves_OtherChecksContinue()
    {
        var folder = await Folder("Original");
        await Link("Checked");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Checked");
        var check = vm.CheckCommand.ExecuteAsync();
        await WaitUntil(() => _checker.Requests.Count == 1);
        var checkedRow = Row(vm, "Checked");
        vm.SelectedItem = Row(vm, "Original");

        _dialogs.OnProjectFolderNameEditor = editor =>
        {
            var closed = false;
            editor.CloseRequested += (_, _) => closed = true;
            _other.TrashAsync(new ContentRef(ContentKind.Folder, folder.Id)).GetAwaiter().GetResult();
            editor.Name = "Changed";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.Equal("This item is in the Trash.", editor.Error);
            Assert.Equal("Changed", editor.Name); // the dialog stays open with the entered name
            Assert.False(closed);
            Assert.True(editor.CanClose);
            return false;
        };
        await vm.EditCommand.ExecuteAsync();

        Assert.Equal(["Checked"], Names(vm));
        Assert.Null(vm.SelectedItem);
        Assert.Equal("This item is in the Trash.", vm.BannerMessage);
        Assert.Same(checkedRow, Row(vm, "Checked")); // unrelated row identity kept
        Assert.Equal("Original", (await _other.ListTrashAsync(_alpha)).Single().RootName); // nothing written

        _checker.Requests[0].Completion.SetResult(LinkHealthStatus.Found);
        await check.WaitAsync(Wait);
        Assert.Equal(LinkHealthStatus.Found, checkedRow.Status);
        Assert.False(_checker.Requests[0].Token.IsCancellationRequested);
    }

    [Fact]
    public async Task LinkTrashedWhileEditIsOpen_SaveRefusedKeepsTheValues_AfterClosingTheRowLeaves()
    {
        var link = await Link("Portal");
        await Note("Stays");
        var vm = await Opened();
        var stays = Row(vm, "Stays");
        vm.SelectedItem = Row(vm, "Portal");

        _dialogs.OnProjectLinkEditor = editor =>
        {
            var closed = false;
            editor.CloseRequested += (_, _) => closed = true;
            _other.TrashAsync(new ContentRef(ContentKind.Link, link.Id)).GetAwaiter().GetResult();
            editor.Name = "Changed link";
            editor.LinkType = ProjectLinkType.Web;
            editor.Target = "https://changed.test/";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.Equal("This item is in the Trash.", editor.GeneralError);
            Assert.Equal(("Changed link", ProjectLinkType.Web, "https://changed.test/"), (editor.Name, editor.LinkType, editor.Target));
            Assert.False(closed);
            return false;
        };
        await vm.EditCommand.ExecuteAsync();

        Assert.Equal(["Stays"], Names(vm));
        Assert.Same(stays, vm.Items.Single());
        Assert.Null(vm.SelectedItem);
        Assert.Equal("This item is in the Trash.", vm.BannerMessage);
        var stored = (await _other.GetLinkAsync(link.Id))!;
        Assert.True(stored.IsTrashed);
        Assert.Equal("Portal", stored.Name); // nothing written
    }

    [Theory]
    [InlineData(ContentKind.Folder)]
    [InlineData(ContentKind.Link)]
    [InlineData(ContentKind.Note)]
    public async Task ItemTrashedWhileMoveIsOpen_MoveRefused_AfterClosingTheSourceRowLeaves(ContentKind kind)
    {
        var destination = await Folder("Destination");
        var id = kind switch
        {
            ContentKind.Folder => (await Folder("Source")).Id,
            ContentKind.Link => (await Link("Source")).Id,
            _ => (await Note("Source")).Id,
        };
        var vm = await Opened();
        var kept = Row(vm, "Destination");
        vm.SelectedItem = Row(vm, "Source");
        await vm.CurrentPreviewLoad;
        if (kind == ContentKind.Note)
            Assert.NotNull(vm.PreviewNote);

        _dialogs.OnProjectContentMove = editor =>
        {
            var closed = false;
            editor.CloseRequested += (_, _) => closed = true;
            _other.TrashAsync(new ContentRef(kind, id)).GetAwaiter().GetResult();
            editor.SelectedDestination = editor.Destinations.Single(d => d.FolderId == destination.Id);
            editor.MoveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.Equal("This item is in the Trash.", editor.Error);
            Assert.Equal(destination.Id, editor.SelectedDestination!.FolderId);
            Assert.False(editor.IsStale);
            Assert.False(closed);
            return false;
        };
        await vm.MoveCommand.ExecuteAsync();

        Assert.Equal(["Destination"], Names(vm));
        Assert.Same(kept, vm.Items.Single());
        Assert.Null(vm.SelectedItem);
        Assert.Null(vm.PreviewNote);
        Assert.Equal("This item is in the Trash.", vm.BannerMessage);
        Assert.True(await IsTrashed(kind, id));
        var target = (await _other.ListAsync(_alpha, destination.Id)).Listing!;
        Assert.Empty(target.Folders.Cast<object>().Concat(target.Links).Concat(target.Notes)); // nothing moved
    }

    [Fact]
    public async Task RestoredBeforeTheFreshRead_TheRowStaysSelected_NoBanner()
    {
        var folder = await Folder("Original");
        var vm = await Opened();
        var row = Row(vm, "Original");
        vm.SelectedItem = row;

        _dialogs.OnProjectFolderNameEditor = editor =>
        {
            _other.TrashAsync(new ContentRef(ContentKind.Folder, folder.Id)).GetAwaiter().GetResult();
            editor.Name = "Changed";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.Equal("This item is in the Trash.", editor.Error);
            RestoreOnlyEntry().GetAwaiter().GetResult(); // another window restores it before the dialog closes
            return false;
        };
        await vm.EditCommand.ExecuteAsync();

        Assert.Same(row, vm.SelectedItem); // the newer state wins over the earlier Trash observation
        Assert.Equal(["Original"], Names(vm));
        Assert.Null(vm.BannerMessage);
    }

    [Fact]
    public async Task RestoredAndMovedElsewhereBeforeTheFreshRead_TheRowLeavesWithoutATrashBanner()
    {
        var destination = await Folder("Destination");
        var note = await Note("Source");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Source");

        _dialogs.OnProjectContentMove = editor =>
        {
            _other.TrashAsync(new ContentRef(ContentKind.Note, note.Id)).GetAwaiter().GetResult();
            editor.MoveCommand.ExecuteAsync().GetAwaiter().GetResult();
            Assert.Equal("This item is in the Trash.", editor.Error);
            RestoreOnlyEntry().GetAwaiter().GetResult();
            Assert.Equal(ContentMoveOutcome.Moved, _other.MoveNoteAsync(note.Id, null, destination.Id).GetAwaiter().GetResult().Outcome);
            return false;
        };
        await vm.MoveCommand.ExecuteAsync();

        Assert.Equal(["Destination"], Names(vm)); // it is active, but no longer in this container
        Assert.Null(vm.SelectedItem);
        Assert.Null(vm.BannerMessage);
    }

    [Fact]
    public async Task FreshReadHeld_RestoredAndTheSameContainerRevisited_TheLateTrashObservationChangesNothing()
    {
        var folder = await Folder("Original");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Original");
        var gate = _store.Hold("contentState");

        _dialogs.OnProjectFolderNameEditor = editor =>
        {
            _other.TrashAsync(new ContentRef(ContentKind.Folder, folder.Id)).GetAwaiter().GetResult();
            editor.Name = "Changed";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return false;
        };
        var edit = vm.EditCommand.ExecuteAsync();
        gate.WaitEntered(); // the held read observed the folder in the Trash

        await RestoreOnlyEntry();
        await vm.NavigateAsync(null); // a later visit to the same container: a fresh load lists the restored folder
        var revisited = Row(vm, "Original");
        vm.SelectedItem = revisited;
        gate.Release();
        await edit.WaitAsync(Wait);

        Assert.Same(revisited, vm.SelectedItem);
        Assert.Equal(["Original"], Names(vm));
        Assert.Null(vm.BannerMessage);
    }

    [Fact]
    public async Task FreshReadHeld_NavigatedAway_TheLateResultChangesNothingInTheNewContainer()
    {
        var other = await Folder("Other");
        await Link("Inside", other.Id);
        var link = await Link("Doomed");
        var vm = await Opened();
        vm.SelectedItem = Row(vm, "Doomed");
        var gate = _store.Hold("contentState");

        _dialogs.OnProjectLinkEditor = editor =>
        {
            _other.TrashAsync(new ContentRef(ContentKind.Link, link.Id)).GetAwaiter().GetResult();
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return false;
        };
        var edit = vm.EditCommand.ExecuteAsync();
        gate.WaitEntered();

        await vm.NavigateAsync(other.Id);
        var inside = Row(vm, "Inside");
        vm.SelectedItem = inside;
        gate.Release();
        await edit.WaitAsync(Wait);

        Assert.Equal(["Inside"], Names(vm));
        Assert.Same(inside, vm.SelectedItem);
        Assert.Null(vm.BannerMessage);
    }

    [Fact]
    public async Task FreshReadHeld_ViewDeactivated_TheLateResultLeavesItAsItWas()
    {
        var note = await Note("Doomed");
        var vm = await Opened();
        var row = Row(vm, "Doomed");
        vm.SelectedItem = row;
        var gate = _store.Hold("contentState");

        _dialogs.OnProjectContentMove = editor =>
        {
            _other.TrashAsync(new ContentRef(ContentKind.Note, note.Id)).GetAwaiter().GetResult();
            editor.MoveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return false;
        };
        var move = vm.MoveCommand.ExecuteAsync();
        gate.WaitEntered();

        vm.Deactivate();
        gate.Release();
        await move.WaitAsync(Wait);

        Assert.Equal(["Doomed"], Names(vm));
        Assert.Null(vm.BannerMessage);
    }

    [Fact]
    public async Task CancelledWithoutSubmitting_NoFreshReadAndNothingChanges()
    {
        await Folder("Original");
        var vm = await Opened();
        var row = Row(vm, "Original");
        vm.SelectedItem = row;
        var gate = _store.Hold("contentState");

        _dialogs.OnProjectFolderNameEditor = _ => false;
        await vm.EditCommand.ExecuteAsync();

        Assert.False(gate.Entered.IsSet);
        Assert.Same(row, vm.SelectedItem);
        Assert.Null(vm.BannerMessage);
    }
}
