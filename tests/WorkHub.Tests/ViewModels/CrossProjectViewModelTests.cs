using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 15 view models (acceptance U1–U7): the Copy/Move commands and the write slot, result ownership after navigation and
/// deactivation, the source listing and "Trash (n)", the absence of any cross-Project Undo, Open destination by the new identity,
/// note drafts of a moved note, and the dialog (counts, skipped items, paths, refusals, suggestion, Cancel). Real SQLite through a
/// gated store; the dialog is played by <see cref="FakeDialogs"/>.
/// </summary>
public sealed class CrossProjectViewModelTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly CrossWorld _w = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly GatedProjectContentStore _store;
    private readonly ProjectContentService _content;
    private readonly List<ProjectContentViewModel> _views = [];

    public CrossProjectViewModelTests()
    {
        _store = new GatedProjectContentStore(new SqliteProjectContentStore(_w.Connections));
        _content = CrossWorld.Service(_store);
    }

    public void Dispose()
    {
        _store.ReleaseAll();
        Task.WhenAll(_views.SelectMany(v => new[] { v.CurrentLoad, v.CurrentCrossProject, v.CurrentTrashOperation, v.CurrentCountReconciliation })).ContinueWith(_ => { }).Wait(Wait);
        _w.Dispose();
    }

    private async Task<ProjectContentViewModel> View(long project, string name)
    {
        var vm = new ProjectContentViewModel(_content, new ScriptedLinkChecker(), _dialogs, NullAppLog.Instance, new LinkCheckCache(), project, name);
        _views.Add(vm);
        await vm.ActivateAsync();
        return vm;
    }

    private static ProjectContentItemViewModel Row(ProjectContentViewModel vm, ProjectContentItemKind kind, long id) => vm.Items.Single(i => i.Kind == kind && i.Id == id);

    /// <summary>Plays the dialog: choose the Project, then the folder (null = root), optionally a name, and Confirm.</summary>
    private static Func<CrossProjectDialogViewModel, bool> Choose(long project, long? folder, string? name = null, bool confirm = true) => d =>
    {
        d.SelectedProject = d.Projects.Single(p => p.ProjectNodeId == project);
        d.CurrentLoad.GetAwaiter().GetResult();
        d.CurrentPreview.GetAwaiter().GetResult();
        if (folder is not null)
        {
            d.SelectedFolder = d.Folders.Single(f => f.FolderId == folder);
            d.CurrentPreview.GetAwaiter().GetResult();
        }

        if (name is not null)
        {
            d.RootName = name;
            d.CurrentPreview.GetAwaiter().GetResult();
        }

        if (!confirm)
            return false;
        Assert.True(d.CanConfirm, d.Message);
        d.ConfirmCommand.Execute(null);
        return true;
    };

    // ---- U3 / U4: the shown visit after Copy and Move; no Undo anywhere ------------------------------------------------------------

    [Fact]
    public async Task U3_U4_Move_RemovesTheRow_RaisesTrashCount_ShowsOpenDestination_AndOffersNoUndo()
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        var trashBefore = vm.TrashButtonText;
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Folder, _w.S);
        _dialogs.OnCrossProject = Choose(_w.B, _w.Inbox);

        await vm.MoveToProjectCommand.ExecuteAsync();
        await vm.CurrentCrossProject;

        Assert.Equal("Trash (3)", trashBefore);
        Assert.Equal("Trash (4)", vm.TrashButtonText);
        Assert.DoesNotContain(vm.Items, i => i.Kind == ProjectContentItemKind.Folder && i.Id == _w.S);
        Assert.Equal("Moved “Specs” (7 items) to Beta › Inbox. The original is in the Trash of Alpha and can be restored there.", vm.DestinationMessage);
        Assert.False(vm.HasUndo); // no cross-Project Undo
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Null(vm.BannerMessage);
        var root = vm.DestinationRoot!.Value;
        Assert.Equal(_w.B, _w.Scalar($"SELECT ProjectNodeId FROM ProjectFolders WHERE Id = {root.Id} AND Name = 'Specs';"));
        Assert.False(vm.IsWriteBusy);
    }

    [Fact]
    public async Task U3_Copy_LeavesTheSourceListingUnchanged()
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        var rows = vm.Items.Select(i => (i.Kind, i.Id)).ToList();
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Link, _w.LA);
        _dialogs.OnCrossProject = Choose(_w.C, _w.Incoming);

        await vm.CopyToProjectCommand.ExecuteAsync();

        Assert.Equal(rows, vm.Items.Select(i => (i.Kind, i.Id)).ToList());
        Assert.Equal("Trash (3)", vm.TrashButtonText);
        Assert.Equal("Copied “Build server” to Shelf › Gamma › Incoming.", vm.DestinationMessage);
        Assert.False(vm.HasUndo);
    }

    [Fact]
    public async Task U4_AnEarlierMoveToTrashUndo_IsWithdrawnWhenTheWriteStarts_AndNoUndoReplacesIt()
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Note, _w.NA2);
        await vm.MoveToTrashCommand.ExecuteAsync();
        Assert.True(vm.HasUndo);
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Link, _w.LA);
        _dialogs.OnCrossProject = Choose(_w.B, null);

        await vm.CopyToProjectCommand.ExecuteAsync();

        Assert.False(vm.HasUndo);
        Assert.True(vm.HasDestination);
    }

    [Fact]
    public async Task U7_CancelInTheDialog_WritesNothing_AndShowsNoNotice()
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Folder, _w.S);
        _dialogs.OnCrossProject = Choose(_w.B, _w.Inbox, confirm: false);
        var before = _w.Dump();

        await vm.MoveToProjectCommand.ExecuteAsync();

        Assert.Equal(before, _w.Dump());
        Assert.False(vm.HasDestination);
        Assert.Null(vm.BannerMessage);
        Assert.Equal(0, _store.CrossCalls);
    }

    // ---- U1: the write slot ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task U1_WhileTheCommitIsPending_TheCommandsAreDisabled_AndARepeatedInvocationIssuesNoSecondCall()
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Link, _w.LA);
        _dialogs.OnCrossProject = Choose(_w.B, null);
        var held = _store.Hold("cross");

        var running = vm.CopyToProjectCommand.ExecuteAsync();
        Assert.True(held.Entered.Wait(Wait));
        Assert.True(vm.IsWriteBusy);
        Assert.False(vm.CopyToProjectCommand.CanExecute(null));
        Assert.False(vm.MoveToProjectCommand.CanExecute(null));
        Assert.False(vm.MoveToTrashCommand.CanExecute(null));
        await vm.MoveToProjectCommand.ExecuteAsync(); // ignored
        held.Release();
        await running;
        await vm.CurrentCrossProject;

        Assert.Equal(1, _store.CrossCalls);
        Assert.Single(_dialogs.CrossProjectDialogs);
        Assert.False(vm.IsWriteBusy);
    }

    // ---- U2: result ownership after navigation and deactivation -------------------------------------------------------------------

    [Fact]
    public async Task U2_AMoveCommittedAfterTheUserNavigated_ShowsNothing_TheCountIsReRead_TheSelectionStays()
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Folder, _w.S);
        _dialogs.OnCrossProject = Choose(_w.B, _w.Inbox);
        var held = _store.Hold("cross");

        var running = vm.MoveToProjectCommand.ExecuteAsync();
        Assert.True(held.Entered.Wait(Wait));
        await vm.NavigateAsync(_w.K);
        var selection = vm.SelectedItem = Row(vm, ProjectContentItemKind.Note, _w.KN);
        held.Release();
        await running;
        await vm.CurrentCrossProject;
        await vm.CurrentCountReconciliation;

        Assert.False(vm.HasDestination);
        Assert.Null(vm.BannerMessage);
        Assert.Same(selection, vm.SelectedItem);
        Assert.Equal("Trash (4)", vm.TrashButtonText); // re-read: the source Trash gained the original
        Assert.Equal(1L, _w.Scalar($"SELECT count(*) FROM ProjectFolders WHERE ProjectNodeId = {_w.B} AND Name = 'Specs';"));
    }

    [Fact]
    public async Task U2_AMoveCommittedAfterAnotherProjectWasSelected_TheOldViewChangesNothing()
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Link, _w.LA);
        _dialogs.OnCrossProject = Choose(_w.B, null);
        var held = _store.Hold("cross");

        var running = vm.MoveToProjectCommand.ExecuteAsync();
        Assert.True(held.Entered.Wait(Wait));
        vm.Deactivate();
        held.Release();
        await running;
        await vm.CurrentCrossProject;

        Assert.False(vm.HasDestination);
        Assert.Null(vm.BannerMessage);
        Assert.Equal("Trash (3)", vm.TrashButtonText); // a deactivated view applies nothing
        Assert.Equal(1L, _w.Scalar($"SELECT count(*) FROM ProjectTrashEntries WHERE ProjectNodeId = {_w.A} AND RootKind = 'Link' AND RootId = {_w.LA};")); // the database result stands
    }

    // ---- U5: Open destination uses the new identity -------------------------------------------------------------------------------

    [Fact]
    public async Task U5_OpenDestination_AsksForTheNewRootByKindAndId_AndNavigationClearsTheNotice()
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        ContentRef? requested = null;
        vm.DestinationRequested = item => { requested = item; return Task.CompletedTask; };
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Note, _w.NA);
        _dialogs.OnCrossProject = Choose(_w.C, null);
        await vm.CopyToProjectCommand.ExecuteAsync();

        vm.OpenDestinationCommand.Execute(null);

        Assert.Equal(vm.DestinationRoot, requested);
        Assert.Equal(ContentKind.Note, requested!.Value.Kind);
        Assert.NotEqual(_w.NA, requested.Value.Id);
        Assert.Equal(_w.C, _w.Scalar($"SELECT ProjectNodeId FROM ProjectNotes WHERE Id = {requested.Value.Id};"));
        await vm.NavigateAsync(_w.K);
        Assert.False(vm.HasDestination); // the notice belongs to the visit that produced it
    }

    // ---- U6: a draft open on a moved note --------------------------------------------------------------------------------------------

    [Fact]
    public async Task U6_AnOpenEditorOnAMovedNote_KeepsTheDraft_AndNeverWritesTheCopy()
    {
        await _w.BuildAsync();
        var note = (await _w.Other.GetNoteAsync(_w.SN1))!;
        var editor = NoteEditorViewModel.ForEdit(_w.Content, _dialogs, note, "Alpha › Specs");
        editor.Content = "my unsaved draft";
        var vm = await View(_w.A, "Alpha");
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Folder, _w.S);
        _dialogs.OnCrossProject = Choose(_w.B, null, "Specs moved");
        await vm.MoveToProjectCommand.ExecuteAsync();

        await editor.SaveCommand.ExecuteAsync();

        Assert.Equal("my unsaved draft", editor.Content);
        Assert.True(editor.CanRecover);
        Assert.NotNull(editor.GeneralError);
        Assert.Equal("spec body", (await _w.Other.GetNoteAsync(_w.SN1))!.Content);
        Assert.Equal(0L, _w.Scalar($"SELECT count(*) FROM ProjectNotes WHERE ProjectNodeId = {_w.B} AND Content = 'my unsaved draft';"));
    }

    // ---- U7: the dialog -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task U7_TheDialog_ShowsCountsSkippedItemsAndPaths_AndAConflictOffersASuggestionThatMustBeConfirmed()
    {
        await _w.BuildAsync();
        var targets = await _w.Content.ListCrossProjectTargetsAsync(_w.A);
        var dialog = new CrossProjectDialogViewModel(_w.Content, CrossProjectMode.Move, new ContentRef(ContentKind.Folder, _w.S), "Specs", "Alpha", targets);
        Assert.Equal("Move “Specs” to another project", dialog.Title);
        Assert.DoesNotContain(dialog.Projects, p => p.ProjectNodeId == _w.A);
        Assert.Equal("Delta (archived)", dialog.Projects.Single(p => p.ProjectNodeId == _w.D).Display);

        dialog.SelectedProject = dialog.Projects.Single(p => p.ProjectNodeId == _w.B);
        await dialog.CurrentLoad;
        await dialog.CurrentPreview; // the root of Beta: a same-NameKey folder (full-width) exists there
        Assert.Equal(CrossProjectStatus.NameConflict, dialog.Preview!.Status);
        Assert.False(dialog.CanConfirm);
        Assert.Equal("Suggested name: “Specs (2)”", dialog.SuggestionText);
        Assert.Equal("Specs", dialog.RootName); // not renamed automatically

        dialog.UseSuggestedNameCommand.Execute(null);
        await dialog.CurrentPreview;
        Assert.True(dialog.CanConfirm);
        Assert.Equal("3 folders, 2 links, 2 notes will be moved.", dialog.CountsText);
        Assert.Equal("4 items in the Trash inside it are not included (3 Trash entries).", dialog.SkippedText);
        Assert.Equal("From Clients › Alpha to Beta.", dialog.PathText);
        Assert.StartsWith("The original goes to the Trash of Alpha", dialog.MoveNote);
        dialog.ConfirmCommand.Execute(null);
        Assert.Equal("Specs (2)", dialog.ConfirmedRequest!.RootName);
        Assert.Equal(dialog.Preview.Token, dialog.ConfirmedToken);
    }

    [Fact]
    public async Task U7_AStaleCommit_ShowsWhy_WritesNothing_AndIsNeverResubmitted()
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Folder, _w.S);
        _dialogs.OnCrossProject = d =>
        {
            var ok = Choose(_w.C, _w.Incoming)(d);
            Assert.True(_w.Other.RenameFolderAsync(_w.Incoming, "Incoming 2").GetAwaiter().GetResult().Succeeded); // another window
            return ok;
        };
        await vm.MoveToProjectCommand.ExecuteAsync();

        Assert.Equal("Something changed since you confirmed. Nothing was copied. Review and try again.", vm.BannerMessage);
        Assert.Single(_dialogs.CrossProjectDialogs);
        Assert.Equal(1, _store.CrossCalls);
        Assert.Contains(vm.Items, i => i.Kind == ProjectContentItemKind.Folder && i.Id == _w.S);
        Assert.Equal(0L, _w.Scalar($"SELECT count(*) FROM ProjectFolders WHERE ProjectNodeId = {_w.C} AND Name = 'Specs';"));
        Assert.Equal(0L, _w.Scalar($"SELECT count(*) FROM ProjectTrashEntries WHERE RootKind = 'Folder' AND RootId = {_w.S};"));
    }

    // ---- R2 (Codex review): one snapshot for names, confirmation text and token ---------------------------------------------------

    [Fact]
    public async Task R2_RenamedInAnotherWindowBeforeThePreviewRead_TheDialogShowsTheNewNames_AndConfirmsThatSnapshot()
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Folder, _w.S); // the listing said "Specs" in "Alpha"
        CrossProjectDialogViewModel? shown = null;
        string? titleAtConfirm = null, noteAtConfirm = null;
        _dialogs.OnCrossProject = d =>
        {
            shown = d;
            Choose(_w.C, _w.Incoming, confirm: false)(d);
            titleAtConfirm = d.Title;
            noteAtConfirm = d.MoveNote;
            d.ConfirmCommand.Execute(null);
            return true;
        };
        var targets = _store.Hold("crossTargets"); // held BEFORE the dialog exists and before any preview read

        var running = vm.MoveToProjectCommand.ExecuteAsync();
        Assert.True(targets.Entered.Wait(Wait));
        Assert.True((await _w.Other.RenameFolderAsync(_w.S, "Specs renamed")).Succeeded); // another window
        Assert.True((await _w.Workspace.RenameAsync(_w.A, "Alpha 2")).Succeeded);
        targets.Release();
        await running;
        await vm.CurrentCrossProject;

        Assert.Equal("Move “Specs renamed” to another project", titleAtConfirm);
        Assert.Equal("The original goes to the Trash of Alpha 2 and can be restored there. Restoring it does not change the copy.", noteAtConfirm);
        Assert.Equal("Specs renamed", shown!.RootName);
        Assert.False(shown.IsRootNameEdited);
        Assert.Equal(shown.Preview!.Token, shown.ConfirmedToken);
        Assert.Equal(("Specs renamed", "Alpha 2"), (shown.Preview.SourceName, shown.Preview.SourceProjectName));
        Assert.Null(shown.ConfirmedRequest!.RootName); // the copy's name follows the source in the same snapshot
        Assert.Equal("Moved “Specs renamed” (7 items) to Shelf › Gamma › Incoming. The original is in the Trash of Alpha 2 and can be restored there.", vm.DestinationMessage);
        Assert.Equal(1L, _w.Scalar($"SELECT count(*) FROM ProjectFolders WHERE ProjectNodeId = {_w.C} AND Name = 'Specs renamed';"));
    }

    [Fact]
    public async Task R2_WhileANewPreviewLoads_ConfirmIsDisabled_AndALateOlderResultNeverReplacesItOrItsToken()
    {
        await _w.BuildAsync();
        var targets = await _w.Content.ListCrossProjectTargetsAsync(_w.A);
        var dialog = new CrossProjectDialogViewModel(_content, CrossProjectMode.Copy, new ContentRef(ContentKind.Folder, _w.S), "Specs", "Alpha", targets);
        var older = _store.Hold("crossPreview"); // the first preview (C's root) reads and is held

        dialog.SelectedProject = dialog.Projects.Single(p => p.ProjectNodeId == _w.C);
        await dialog.CurrentLoad;
        older.WaitEntered();
        var first = dialog.CurrentPreview;
        Assert.False(dialog.CanConfirm);

        dialog.SelectedFolder = dialog.Folders.Single(f => f.FolderId == _w.Incoming); // newer preview, not held
        Assert.False(dialog.CanConfirm);                                              // loading
        await dialog.CurrentPreview;
        Assert.True(dialog.CanConfirm);
        var newer = dialog.Preview!.Token;
        Assert.Equal("From Clients › Alpha to Shelf › Gamma › Incoming.", dialog.PathText);

        older.Release();
        await first;

        Assert.Equal(newer, dialog.Preview!.Token); // the late result for C's root changed nothing
        Assert.Equal("From Clients › Alpha to Shelf › Gamma › Incoming.", dialog.PathText);
        var loading = _store.Hold("crossPreview");
        dialog.RootName = "Specs copy";            // a new preview starts and is held
        loading.WaitEntered();
        Assert.False(dialog.CanConfirm);
        loading.Release();
        await dialog.CurrentPreview;
        dialog.ConfirmCommand.Execute(null);
        Assert.Equal((_w.Incoming, "Specs copy"), (dialog.ConfirmedRequest!.TargetFolderId, dialog.ConfirmedRequest.RootName));
        Assert.Equal(dialog.Preview!.Token, dialog.ConfirmedToken);
    }

    [Fact]
    public async Task R2_ARefreshAfterASourceRename_UpdatesTheSourceName_AndKeepsTheDestinationNameTheUserTyped()
    {
        await _w.BuildAsync();
        var targets = await _w.Content.ListCrossProjectTargetsAsync(_w.A);
        var dialog = new CrossProjectDialogViewModel(_content, CrossProjectMode.Move, new ContentRef(ContentKind.Folder, _w.S), "Specs", "Alpha", targets);
        dialog.SelectedProject = dialog.Projects.Single(p => p.ProjectNodeId == _w.C);
        await dialog.CurrentLoad;
        await dialog.CurrentPreview;
        dialog.RootName = "Specs copy";
        await dialog.CurrentPreview;
        var before = dialog.Preview!.Token;
        Assert.True((await _w.Other.RenameFolderAsync(_w.S, "Specs renamed")).Succeeded); // another window

        dialog.SelectedFolder = dialog.Folders.Single(f => f.FolderId == _w.Incoming);   // any refresh
        await dialog.CurrentPreview;

        Assert.Equal("Move “Specs renamed” to another project", dialog.Title);
        Assert.Equal("Specs copy", dialog.RootName);
        Assert.Equal(("Specs renamed", "Specs copy"), (dialog.Preview!.SourceName, dialog.Preview.RootName));
        Assert.NotEqual(before, dialog.Preview.Token);
        Assert.True(dialog.CanConfirm);
    }

    [Theory]
    [InlineData("folder, default name")]
    [InlineData("folder, custom name")]
    [InlineData("source project")]
    public async Task R2_RenamedAfterReadyButBeforeConfirm_TheMoveIsStale_NoCopyAndNoTrashEntry(string what)
    {
        await _w.BuildAsync();
        var vm = await View(_w.A, "Alpha");
        vm.SelectedItem = Row(vm, ProjectContentItemKind.Folder, _w.S);
        _dialogs.OnCrossProject = d =>
        {
            Choose(_w.C, _w.Incoming, what == "folder, custom name" ? "Specs copy" : null, confirm: false)(d);
            Assert.True(d.CanConfirm);
            if (what == "source project")
                Assert.True(_w.Workspace.RenameAsync(_w.A, "Alpha 2").GetAwaiter().GetResult().Succeeded); // another window, after Ready
            else
                Assert.True(_w.Other.RenameFolderAsync(_w.S, "Specs renamed").GetAwaiter().GetResult().Succeeded);
            d.ConfirmCommand.Execute(null);
            return true;
        };

        await vm.MoveToProjectCommand.ExecuteAsync();

        Assert.Equal("Something changed since you confirmed. Nothing was copied. Review and try again.", vm.BannerMessage);
        Assert.False(vm.HasDestination);
        Assert.Contains(vm.Items, i => i.Kind == ProjectContentItemKind.Folder && i.Id == _w.S);
        Assert.Equal(0L, _w.Scalar($"SELECT count(*) FROM ProjectFolders WHERE ProjectNodeId = {_w.C} AND ParentFolderId = {_w.Incoming};"));
        Assert.Equal(0L, _w.Scalar($"SELECT count(*) FROM ProjectTrashEntries WHERE RootKind = 'Folder' AND RootId = {_w.S};"));
        Assert.Equal("Trash (3)", vm.TrashButtonText);
    }
}
