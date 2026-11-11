using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 14 view models (proposal Revision 2 §5.4, §5.10, §7.4, §7.6): Delete permanently and Empty Trash in the Project's
/// Trash view, the confirmation of exactly one ready preview, the cancellation boundary as the user sees it, result ownership
/// after navigation, no Undo, no automatic resubmission, and note drafts of purged notes. Real SQLite; deterministic gates are
/// the purge's own checkpoints.
/// </summary>
public sealed class ProjectTrashPurgeViewModelTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly PurgeWorld _w = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly List<ProjectContentViewModel> _views = [];

    public void Dispose()
    {
        var pending = _views.SelectMany(v => new[] { v.CurrentLoad, v.Trash.CurrentLoad, v.Trash.CurrentPurge, v.Trash.CurrentCountReconciliation });
        Task.WhenAll(pending).ContinueWith(_ => { }).Wait(Wait);
        _w.Dispose();
    }

    private async Task<ProjectTrashViewModel> OpenTrash(ProjectContentService? content = null)
    {
        var vm = new ProjectContentViewModel(content ?? _w.Content, new ScriptedLinkChecker(), _dialogs, NullAppLog.Instance, new LinkCheckCache(), _w.P, "Project P");
        _views.Add(vm);
        await vm.ActivateAsync();
        await vm.OpenTrashCommand.ExecuteAsync();
        await vm.Trash.CurrentLoad;
        return vm.Trash;
    }

    private static TrashEntryViewModel Entry(ProjectTrashViewModel trash, long id) => trash.Entries.Single(e => e.Id == id);

    private ProjectContentViewModel Owner => _views[^1];

    // ---- Commands and confirmation content ---------------------------------------------------------------------------

    [Fact]
    public async Task V1_DeleteNeedsASelection_EmptyTrashNeedsEntries()
    {
        await _w.BuildAsync();
        var trash = await OpenTrash();

        Assert.False(trash.DeletePermanentlyCommand.CanExecute(null));
        Assert.True(trash.EmptyTrashCommand.CanExecute(null));
        trash.UpdateSelection([Entry(trash, _w.E_L1)]);
        Assert.True(trash.DeletePermanentlyCommand.CanExecute(null));

        var empty = (await _w.Workspace.CreateProjectAsync(null, new ProjectDraft("Empty", ProjectStatus.Active))).Value!.Id;
        var vm = new ProjectContentViewModel(_w.Content, new ScriptedLinkChecker(), _dialogs, NullAppLog.Instance, new LinkCheckCache(), empty, "Empty");
        _views.Add(vm);
        await vm.ActivateAsync();
        await vm.OpenTrashCommand.ExecuteAsync();
        await vm.Trash.CurrentLoad;
        Assert.False(vm.Trash.EmptyTrashCommand.CanExecute(null));
        Assert.False(vm.Trash.DeletePermanentlyCommand.CanExecute(null));
    }

    [Fact]
    public async Task T4_T5_V5_TheConfirmationShowsThePreview_Honestly_ThenTheResultHasNoUndo()
    {
        await _w.BuildAsync();
        var trash = await OpenTrash();
        trash.UpdateSelection([Entry(trash, _w.E_FB), Entry(trash, _w.E_L1)]);

        await trash.DeletePermanentlyCommand.ExecuteAsync();

        var dialog = Assert.Single(_dialogs.PurgeConfirmations);
        Assert.Equal("Delete permanently?", dialog.Title);
        Assert.Equal("Delete permanently", dialog.ConfirmText);
        Assert.Equal("Project P", dialog.ProjectText);
        Assert.Equal("4 entries: 3 folders, 3 links, 1 note.", dialog.TotalsText);
        Assert.True(dialog.HasDependents);
        var nb = dialog.Rows.Single(r => r.Entry.EntryId == _w.E_NB);
        Assert.Equal(("inside “FB”", 1, "from Project P › FB (in the Trash)", "Note"), (nb.InsideText, nb.Depth, nb.FromText, nb.KindText));
        Assert.Equal(dialog.Preview.Entries.Count, dialog.Rows.Count);
        foreach (var phrase in new[] { "removed from WorkHub's database", "cannot be undone in WorkHub", "Backups\\PreDelete", "other backups still contain these items", "whole database", "not just these items", "are not touched" })
            Assert.Contains(phrase, dialog.ExplanationText);
        foreach (var claim in new[] { "secure", "erase", "wipe", "from your computer", "permanently from" })
            Assert.DoesNotContain(claim, dialog.ExplanationText, StringComparison.OrdinalIgnoreCase);

        Assert.StartsWith("Removed from the database: 4 entries (3 folders, 3 links, 1 note). Backup made first: workhub_predelete_v9_", trash.Notice);
        await trash.CurrentLoad;
        Assert.DoesNotContain(trash.Entries, e => e.Id == _w.E_FB || e.Id == _w.E_L1 || e.Id == _w.E_NB);
        Assert.False(Owner.HasUndo); // no item Undo after a purge
        Assert.Null(trash.PurgeStatus);
        Assert.False(Owner.IsWriteBusy);
    }

    [Fact]
    public async Task EmptyTrash_ConfirmsTheWholeTrash_ThenTheListIsEmpty()
    {
        await _w.BuildAsync();
        var trash = await OpenTrash();

        await trash.EmptyTrashCommand.ExecuteAsync();

        Assert.Equal("Empty the Trash of “Project P”?", Assert.Single(_dialogs.PurgeConfirmations).Title);
        await trash.CurrentLoad;
        Assert.Empty(trash.Entries);
        Assert.True(trash.IsEmpty);
        Assert.False(trash.EmptyTrashCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelInTheDialog_NothingIsMadeOrDeleted()
    {
        await _w.BuildAsync();
        var trash = await OpenTrash();
        _dialogs.OnPurgeConfirmation = _ => false;
        var before = _w.Dump();

        await trash.EmptyTrashCommand.ExecuteAsync();

        Assert.Null(trash.Notice);
        Assert.Empty(_w.PreDeleteFiles());
        Assert.Equal(before, _w.Dump());
        Assert.Equal(12 - 3, trash.Entries.Count); // P's nine entries are still listed
    }

    [Fact]
    public async Task ArchivedProject_IsLabelled_InTheConfirmation()
    {
        await _w.BuildAsync();
        await _w.Workspace.ArchiveProjectAsync(_w.P);
        var trash = await OpenTrash();
        trash.UpdateSelection([Entry(trash, _w.E_L1)]);
        _dialogs.OnPurgeConfirmation = _ => false;

        await trash.DeletePermanentlyCommand.ExecuteAsync();

        Assert.Equal("Project P (archived)", Assert.Single(_dialogs.PurgeConfirmations).ProjectText);
    }

    // ---- Refusals, stale confirmations: never resubmitted or re-shown ----------------------------------------------------

    [Fact]
    public async Task V6_C20_AStaleCommit_ShowsWhy_ReloadsTheList_AndNeverReopensTheDialog()
    {
        await _w.BuildAsync();
        var trash = await OpenTrash();
        _dialogs.OnPurgeConfirmation = _ =>
        {
            _w.Trash(ContentKind.Link, _w.LK).GetAwaiter().GetResult(); // another window, while the dialog is open
            return true;
        };
        var count = trash.Entries.Count;

        await trash.EmptyTrashCommand.ExecuteAsync();

        Assert.Single(_dialogs.PurgeConfirmations);
        Assert.Equal("The Trash changed since you confirmed. Nothing was deleted. Review the items and confirm again.", trash.Notice);
        await trash.CurrentLoad;
        Assert.Equal(count + 1, trash.Entries.Count);
        Assert.Empty(_w.PreDeleteFiles()); // stale in the backup's snapshot: no copy was written
    }

    [Fact]
    public async Task APreviewRefusal_ShowsWhy_WithoutADialog()
    {
        await _w.BuildAsync();
        var trash = await OpenTrash();
        trash.UpdateSelection([Entry(trash, _w.E_L1)]);
        await _w.Other.RestoreAsync(_w.E_L1); // restored in another window after the list was read

        await trash.DeletePermanentlyCommand.ExecuteAsync();

        Assert.Empty(_dialogs.PurgeConfirmations);
        Assert.StartsWith("One of the selected items is no longer in the Trash", trash.Notice);
        await trash.CurrentLoad;
        Assert.DoesNotContain(trash.Entries, e => e.Id == _w.E_L1);
    }

    // ---- Stages, cancellation and result ownership --------------------------------------------------------------------------

    private sealed class Gate
    {
        public SemaphoreSlim Reached { get; } = new(0);

        public ManualResetEventSlim Release { get; } = new(false);

        public Action<string> At(string point) => reached =>
        {
            if (reached != point)
                return;
            Reached.Release();
            Release.Wait(Wait);
        };
    }

    private (ProjectContentService Content, Gate Gate) Gated(string point, bool writeStep = false, int lockWaitSeconds = WorkHub.Infrastructure.Backup.SqliteSnapshotter.DefaultLockWaitSeconds)
    {
        var gate = new Gate();
        var purge = _w.NewPurge(lockWaitSeconds: lockWaitSeconds);
        if (writeStep)
            purge.OnWriteStep = gate.At(point);
        else
            purge.OnCheckpoint = gate.At(point);
        return (_w.NewService(purge), gate);
    }

    [Fact]
    public async Task CancellingAfterPublication_DeletesNothing_AndNamesTheKeptCopy()
    {
        await _w.BuildAsync();
        var (content, gate) = Gated("published");
        var trash = await OpenTrash(content);
        trash.UpdateSelection([Entry(trash, _w.E_L1)]);
        var before = _w.Dump();

        var running = trash.DeletePermanentlyCommand.ExecuteAsync();
        Assert.True(await gate.Reached.WaitAsync(Wait));
        Assert.True(trash.IsPurging);
        Assert.True(trash.CancelPurgeCommand.CanExecute(null));
        Assert.False(trash.DeletePermanentlyCommand.CanExecute(null)); // V3: one operation at a time
        Assert.False(trash.EmptyTrashCommand.CanExecute(null));
        trash.CancelPurgeCommand.Execute(null);
        Assert.Equal("Cancelling…", trash.PurgeStatus);
        gate.Release.Set();
        await running;
        await trash.CurrentPurge;

        var kept = Assert.Single(_w.PreDeleteFiles());
        Assert.Equal($"Cancelled. Nothing was deleted. A copy of the whole database was made first and is kept: {kept}.", trash.Notice);
        Assert.Equal(before, _w.Dump());
        Assert.False(trash.IsPurging);
    }

    /// <summary>
    /// The backup stage waits for a database another connection holds EXCLUSIVE (taken while the confirmation is open, after the
    /// preview); the copy gives up after 1 s. With <paramref name="cancel"/>, Cancel is clicked at "backup-start", before that wait.
    /// </summary>
    private async Task<(ProjectTrashViewModel Trash, Dictionary<string, List<string>> Before)> PurgeAgainstALockedDatabase(bool cancel)
    {
        await _w.BuildAsync();
        var (content, gate) = Gated("backup-start", lockWaitSeconds: 1);
        var trash = await OpenTrash(content);
        trash.UpdateSelection([Entry(trash, _w.E_L1)]);
        var before = _w.Dump();
        using var holder = BackupKit.Open(_w.Root.Paths.DatabaseFile);
        _dialogs.OnPurgeConfirmation = _ =>
        {
            BackupKit.Exec(holder, "BEGIN EXCLUSIVE;");
            return true;
        };
        try
        {
            var running = trash.DeletePermanentlyCommand.ExecuteAsync();
            Assert.True(await gate.Reached.WaitAsync(Wait));
            Assert.Equal("Making a backup…", trash.PurgeStatus);
            Assert.True(trash.IsPurging);
            Assert.True(Owner.IsWriteBusy);
            if (cancel)
            {
                Assert.True(trash.CancelPurgeCommand.CanExecute(null));
                trash.CancelPurgeCommand.Execute(null);
                Assert.Equal("Cancelling…", trash.PurgeStatus);
            }

            gate.Release.Set();
            await running;
            await trash.CurrentPurge;
        }
        finally
        {
            BackupKit.Exec(holder, "ROLLBACK;");
        }

        Assert.False(trash.IsPurging); // the view is idle again either way
        Assert.Null(trash.PurgeStatus);
        Assert.False(trash.CanCancelPurge);
        Assert.False(Owner.IsWriteBusy);
        Assert.True(trash.DeletePermanentlyCommand.CanExecute(null));
        Assert.Equal(before, _w.Dump());
        Assert.Empty(_w.PreDeleteFiles());
        return (trash, before);
    }

    [Fact]
    public async Task C21_TheBackupTimesOutOnALockedDatabase_WithoutCancel_TheNoticeSaysTheBackupFailed()
    {
        var (trash, _) = await PurgeAgainstALockedDatabase(cancel: false);

        Assert.StartsWith("The backup could not be made (The database stayed locked by another connection", trash.Notice);
        Assert.EndsWith("Nothing was deleted.", trash.Notice);
    }

    [Fact]
    public async Task C21_CancelWhileTheBackupWaitsOnALockedDatabase_TheTimeoutIsShownAsCancelled()
    {
        var (trash, _) = await PurgeAgainstALockedDatabase(cancel: true);

        Assert.Equal("Cancelled. Nothing was deleted.", trash.Notice);
        Assert.Contains(trash.Entries, e => e.Id == _w.E_L1);
    }

    [Fact]
    public async Task OnceDeletingBegan_CancelIsDisabled_ALateCancelIsIgnored_TheCommitIsReported()
    {
        await _w.BuildAsync();
        var (content, gate) = Gated("purge:notes", writeStep: true);
        var trash = await OpenTrash(content);
        trash.UpdateSelection([Entry(trash, _w.E_N1)]);

        var running = trash.DeletePermanentlyCommand.ExecuteAsync();
        Assert.True(await gate.Reached.WaitAsync(Wait));
        Assert.True(SpinWait.SpinUntil(() => !trash.CanCancelPurge, Wait), "Deleting… disables Cancel");
        Assert.Equal("Deleting…", trash.PurgeStatus);
        trash.CancelPurgeCommand.Execute(null); // ignored
        gate.Release.Set();
        await running;
        await trash.CurrentPurge;

        Assert.StartsWith("Removed from the database: 1 entry (0 folders, 0 links, 1 note).", trash.Notice);
        Assert.DoesNotContain("Nothing was deleted", trash.Notice);
    }

    [Fact]
    public async Task V2_CommittedAfterTheUserWentBack_NoNoticeAnywhere_TheTrashCountIsReRead()
    {
        await _w.BuildAsync();
        var (content, gate) = Gated("published");
        var trash = await OpenTrash(content);
        var owner = Owner;
        trash.UpdateSelection([Entry(trash, _w.E_L1)]);

        var running = trash.DeletePermanentlyCommand.ExecuteAsync();
        Assert.True(await gate.Reached.WaitAsync(Wait));
        await owner.CloseTrashCommand.ExecuteAsync(); // Back: a navigation (fresh listing, read before the commit)
        await owner.CurrentLoad;
        Assert.Equal("Trash (9)", owner.TrashButtonText);
        var selection = owner.SelectedItem = owner.Items.First();
        gate.Release.Set();
        await running;
        await trash.CurrentPurge;
        await trash.CurrentCountReconciliation;

        Assert.Null(trash.Notice);           // the Trash view that asked is gone
        Assert.Null(owner.BannerMessage);    // and nothing is shown in the listing
        Assert.Same(selection, owner.SelectedItem); // the selection is not replaced
        Assert.Equal("Trash (8)", owner.TrashButtonText);
        Assert.Equal(8, (await _w.Other.ListTrashAsync(_w.P)).Count);
    }

    [Fact]
    public async Task V2_CommittedAfterAnotherProjectWasSelected_TheOldViewChangesNothing()
    {
        await _w.BuildAsync();
        var (content, gate) = Gated("published");
        var trash = await OpenTrash(content);
        var owner = Owner;
        trash.UpdateSelection([Entry(trash, _w.E_L1)]);

        var running = trash.DeletePermanentlyCommand.ExecuteAsync();
        Assert.True(await gate.Reached.WaitAsync(Wait));
        owner.Deactivate(); // another Project selected
        gate.Release.Set();
        await running;
        await trash.CurrentPurge;

        Assert.Null(trash.Notice);
        Assert.Null(owner.BannerMessage);
        Assert.DoesNotContain(await _w.Other.ListTrashAsync(_w.P), e => e.Id == _w.E_L1); // the database result stands
    }

    // ---- Note drafts of purged notes (existing NotFound behaviour) -----------------------------------------------------------

    [Fact]
    public async Task V4_AnOpenEditorOnAPurgedNote_KeepsTheDraft_CopyDraftWorks_SaveAsNewFollowsTheExistingRules()
    {
        await _w.BuildAsync();
        var note = (await _w.Other.GetNoteAsync(_w.NK))!;
        var editor = NoteEditorViewModel.ForEdit(_w.Content, _dialogs, note, "Project P");
        editor.Content = "my unsaved draft";
        var entry = await _w.Trash(ContentKind.Note, _w.NK);
        var request = PurgeRequest.Selected(_w.P, [entry]);
        Assert.Equal(PurgeOutcome.Purged, (await _w.Other.PurgeAsync(request, (await _w.Other.PreparePurgeAsync(request)).Preview!.Token)).Outcome);

        await editor.SaveCommand.ExecuteAsync();

        Assert.Equal("my unsaved draft", editor.Content);
        Assert.True(editor.CanRecover);
        Assert.NotNull(editor.GeneralError);
        editor.CopyDraftCommand.Execute(null);
        Assert.Equal("NK\r\n\r\nmy unsaved draft", _dialogs.Clipboard); // the existing Copy draft format
        await editor.SaveAsNewCommand.ExecuteAsync(); // the note lived at the Project root, which still exists
        Assert.Contains((await _w.Other.ListAsync(_w.P, null)).Listing!.Notes, n => n.Title == "NK");
    }

    [Fact]
    public async Task V4_APurgedNoteWhoseFolderWasPurgedToo_KeepsTheDraft_SaveAsNewCannotRecreateTheFolder()
    {
        await _w.BuildAsync();
        var note = (await _w.Other.GetNoteAsync(_w.X))!; // in active folder FK
        var editor = NoteEditorViewModel.ForEdit(_w.Content, _dialogs, note, "Project P › FK");
        editor.Content = "draft in FK";
        var entry = await _w.Trash(ContentKind.Folder, _w.FK);
        var request = PurgeRequest.Selected(_w.P, [entry]);
        Assert.Equal(PurgeOutcome.Purged, (await _w.Other.PurgeAsync(request, (await _w.Other.PreparePurgeAsync(request)).Preview!.Token)).Outcome);

        await editor.SaveCommand.ExecuteAsync();
        Assert.Equal("draft in FK", editor.Content);
        Assert.True(editor.CanRecover);
        await editor.SaveAsNewCommand.ExecuteAsync();

        Assert.Equal("draft in FK", editor.Content); // never lost; nothing is created anywhere else
        Assert.DoesNotContain((await _w.Other.ListAsync(_w.P, null)).Listing!.Notes, n => n.Title == "X");
    }
}
