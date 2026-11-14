using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 14 store policy (proposal Revision 2 §5.5–§5.9, §7.1–§7.5): request modes, closure, evaluation order, the canonical
/// token, stale confirmations with a second real connection, the children-first transaction and its rollback, and the real
/// PreDelete copy (snapshotter, validator, catalog). Raw-SQL schema behaviour is in <c>PurgeSchemaBehaviorTests</c>.
/// </summary>
public sealed class ProjectTrashPurgeTests : IDisposable
{
    private readonly PurgeWorld _w = new();

    public void Dispose() => _w.Dispose();

    private Task<PurgeWorld> World() => _w.BuildAsync();

    private Task<PurgePreviewResult> Preview(PurgeRequest request, ProjectContentService? content = null) => (content ?? _w.Content).PreparePurgeAsync(request);

    private async Task<PurgePreview> Ready(PurgeRequest request)
    {
        var result = await Preview(request);
        Assert.Equal(PurgePreviewStatus.Ready, result.Status);
        Assert.True(result.IsReady);
        return result.Preview!;
    }

    private PurgeRequest Selected(params long[] ids) => PurgeRequest.Selected(_w.P, ids);

    private PurgeRequest All() => PurgeRequest.EntireTrash(_w.P);

    private async Task<PurgeResult> Commit(PurgeRequest request, string token, CancellationToken cancellationToken = default) =>
        await _w.Content.PurgeAsync(request, token, null, cancellationToken);

    private async Task<PurgeResult> PreviewAndCommit(PurgeRequest request)
    {
        var preview = await Ready(request);
        return await Commit(request, preview.Token);
    }

    private static (string, long)[] Rows(string table, params long[] ids) => ids.Select(id => (table, id)).ToArray();

    // ---- 7.1 Modes, closure and request validation -----------------------------------------------------------------------

    [Fact]
    public async Task A1_SelectedLinkAndNote_ExactlyThoseRowsAndEntriesGo_EverythingElseIsIdentical()
    {
        await World();
        var before = _w.Dump();

        var result = await PreviewAndCommit(Selected(_w.E_L1, _w.E_N1));

        Assert.Equal(PurgeOutcome.Purged, result.Outcome);
        Assert.True(result.Committed);
        Assert.Equal(new PurgeCounts(2, 0, 1, 1), result.Deleted);
        PurgeWorld.AssertOnlyRemoved(before, _w.Dump(), [.. Rows("ProjectLinks", _w.L1), .. Rows("ProjectNotes", _w.N1), .. Rows("ProjectTrashEntries", _w.E_L1, _w.E_N1)]);
        _w.AssertIntegrity();
        Assert.Empty(_w.Starter.Started); // nothing a link points to is ever touched
    }

    [Fact]
    public async Task A2_FolderWithThreeLevels_DeletesChildrenFirst_OneStatementPerDepth_WithTheEntryLast()
    {
        await World();
        var steps = new List<string>();
        var purge = _w.NewPurge();
        purge.OnWriteStep = steps.Add;
        _w.Use(purge);
        var before = _w.Dump();

        var preview = await Ready(Selected(_w.E_FA));
        Assert.Equal(new PurgeCounts(1, 3, 1, 2), preview.Counts);
        var result = await Commit(Selected(_w.E_FA), preview.Token);

        Assert.Equal(PurgeOutcome.Purged, result.Outcome);
        Assert.Equal(["purge:notes", "purge:links", "purge:folders:2", "purge:folders:1", "purge:folders:0", "purge:entries"], steps);
        PurgeWorld.AssertOnlyRemoved(before, _w.Dump(),
            [.. Rows("ProjectFolders", _w.FA, _w.FA1, _w.FA1a), .. Rows("ProjectLinks", _w.FA1Link), .. Rows("ProjectNotes", _w.FA1aNote, _w.FANote), .. Rows("ProjectTrashEntries", _w.E_FA)]);
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task A3_DependentsAreIncluded_ListedUnderTheirContainingEntry_AndNotMarkedSelected()
    {
        await World();
        var preview = await Ready(Selected(_w.E_FB));

        Assert.Equal(new[] { _w.E_FB, _w.E_FB1x, _w.E_NB }.Order(), preview.Entries.Select(e => e.EntryId).Order());
        var top = preview.Entries[0];
        Assert.Equal((_w.E_FB, true, 0, (long?)null), (top.EntryId, top.Selected, top.Depth, top.ContainingEntryId));
        Assert.All(preview.Entries.Skip(1), e => Assert.Equal((false, 1, (long?)_w.E_FB), (e.Selected, e.Depth, e.ContainingEntryId)));
        Assert.Equal(new PurgeCounts(3, 3, 2, 1), preview.Counts);
        Assert.True(preview.Entries.Single(e => e.EntryId == _w.E_NB).FromTrashed); // its origin FB is in the Trash
        Assert.Equal("Project P › FB", preview.Entries.Single(e => e.EntryId == _w.E_NB).From.Display);
        Assert.Equal("Project P", top.From.Display);

        var before = _w.Dump();
        Assert.Equal(PurgeOutcome.Purged, (await Commit(Selected(_w.E_FB), preview.Token)).Outcome);
        PurgeWorld.AssertOnlyRemoved(before, _w.Dump(),
            [.. Rows("ProjectFolders", _w.FB, _w.FB1, _w.FB1x), .. Rows("ProjectLinks", _w.FBLink, _w.FB1xLink), .. Rows("ProjectNotes", _w.NB), .. Rows("ProjectTrashEntries", _w.E_FB, _w.E_FB1x, _w.E_NB)]);
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task A4_ThreeLevelChain_IsDeletedDeepestFirst()
    {
        await World();
        var preview = await Ready(Selected(_w.E_FC));
        Assert.Equal([(_w.E_FC, 0), (_w.E_FC1, 1), (_w.E_NC, 2)], preview.Entries.Select(e => (e.EntryId, e.Depth)).ToArray());
        Assert.Equal(_w.E_FC1, preview.Entries[2].ContainingEntryId);

        var before = _w.Dump();
        Assert.Equal(PurgeOutcome.Purged, (await Commit(Selected(_w.E_FC), preview.Token)).Outcome);
        PurgeWorld.AssertOnlyRemoved(before, _w.Dump(), [.. Rows("ProjectFolders", _w.FC, _w.FC1), .. Rows("ProjectNotes", _w.NC), .. Rows("ProjectTrashEntries", _w.E_FC, _w.E_FC1, _w.E_NC)]);
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task A5_OnlyADependent_TheContainingEntryKeepsItsRows_AndCanStillBeRestored()
    {
        await World();
        var before = _w.Dump();

        Assert.Equal(PurgeOutcome.Purged, (await PreviewAndCommit(Selected(_w.E_NB))).Outcome);

        PurgeWorld.AssertOnlyRemoved(before, _w.Dump(), [.. Rows("ProjectNotes", _w.NB), .. Rows("ProjectTrashEntries", _w.E_NB)]);
        Assert.Equal(RestoreOutcome.Restored, (await _w.Other.RestoreAsync(_w.E_FB)).Outcome); // E_FB: ItemCount 3, its 3 rows
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task A6_TheMiddleOfAChain_TakesItsDependent_TheOuterEntryKeepsItsOwnRow()
    {
        await World();
        var before = _w.Dump();

        var preview = await Ready(Selected(_w.E_FC1));
        Assert.Equal([_w.E_FC1, _w.E_NC], preview.Entries.Select(e => e.EntryId).ToArray());
        Assert.DoesNotContain(preview.Entries, e => e.EntryId == _w.E_FC); // the containing entry is not in the plan
        Assert.Equal((0, (long?)_w.E_FC), (preview.Entries[0].Depth, preview.Entries[0].ContainingEntryId));
        Assert.Equal(PurgeOutcome.Purged, (await Commit(Selected(_w.E_FC1), preview.Token)).Outcome);

        PurgeWorld.AssertOnlyRemoved(before, _w.Dump(), [.. Rows("ProjectFolders", _w.FC1), .. Rows("ProjectNotes", _w.NC), .. Rows("ProjectTrashEntries", _w.E_FC1, _w.E_NC)]);
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task A7_EmptyTrash_RemovesEveryEntryOfThatProjectOnly()
    {
        await World();
        var before = _w.Dump();

        var preview = await Ready(All());
        Assert.Equal(new PurgeCounts(9, 8, 4, 5), preview.Counts);
        Assert.All(preview.Entries, e => Assert.True(e.Selected));
        var result = await Commit(All(), preview.Token);

        Assert.Equal(PurgeOutcome.Purged, result.Outcome);
        PurgeWorld.AssertOnlyRemoved(before, _w.Dump(),
        [
            .. Rows("ProjectFolders", _w.FA, _w.FA1, _w.FA1a, _w.FB, _w.FB1, _w.FB1x, _w.FC, _w.FC1),
            .. Rows("ProjectLinks", _w.L1, _w.FA1Link, _w.FB1xLink, _w.FBLink),
            .. Rows("ProjectNotes", _w.N1, _w.FA1aNote, _w.FANote, _w.NB, _w.NC),
            .. Rows("ProjectTrashEntries", _w.E_L1, _w.E_N1, _w.E_FA, _w.E_NB, _w.E_FB1x, _w.E_FB, _w.E_NC, _w.E_FC1, _w.E_FC),
        ]);
        Assert.Equal(PurgePreviewStatus.TrashEmpty, (await Preview(All())).Status);
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task ReadyIsTheOnlySuccessState_RefusalsCarryNoPreview()
    {
        await World();
        var ready = await Preview(Selected(_w.E_L1));
        var refused = await Preview(Selected(999_999));

        Assert.Equal(PurgePreviewStatus.Ready, ready.Status);
        Assert.NotNull(ready.Preview);
        Assert.Equal(PurgePreviewStatus.EntryNotFound, refused.Status);
        Assert.False(refused.IsReady);
        Assert.Null(refused.Preview);
        Assert.Throws<ArgumentException>(() => PurgePreviewResult.Refused(PurgePreviewStatus.Ready));
    }

    [Fact]
    public async Task A8_A9_A10_RequestValidation_PreviewAndCommitAlike_NoPartialPurge()
    {
        await World();
        var before = _w.Dump();

        Assert.Equal(PurgePreviewStatus.EmptySelection, (await Preview(Selected())).Status);
        Assert.Equal(PurgePreviewStatus.EntryNotFound, (await Preview(Selected(_w.E_Q1))).Status); // another Project's entry
        Assert.Equal(PurgePreviewStatus.EntryNotFound, (await Preview(Selected(_w.E_L1, 999_999))).Status);
        Assert.Equal(PurgePreviewStatus.ProjectNotFound, (await Preview(PurgeRequest.EntireTrash(_w.WF))).Status); // a Workspace Folder is not a Project

        var commit = await Commit(Selected(_w.E_L1, _w.E_Q1), "any");
        Assert.Equal((PurgeOutcome.Refused, PurgePreviewStatus.EntryNotFound, false), (commit.Outcome, commit.Refusal, commit.Committed));
        Assert.Null(commit.BackupFileName); // refused in the backup's snapshot: no copy is written
        Assert.Empty(_w.PreDeleteFiles());
        Assert.Equal(before, _w.Dump());
    }

    [Fact]
    public async Task A10_EmptyTrash_OnAnEmptyTrash_IsTrashEmpty()
    {
        await World();
        var empty = (await _w.Workspace.CreateProjectAsync(null, new ProjectDraft("Empty", ProjectStatus.Active))).Value!.Id;
        Assert.Equal(PurgePreviewStatus.TrashEmpty, (await Preview(PurgeRequest.EntireTrash(empty))).Status);
    }

    [Fact]
    public async Task A11_EmptyTrashConfirmed_ThenEverythingRestoredElsewhere_CommitIsTrashEmpty()
    {
        var w = await World();
        var preview = await Ready(PurgeRequest.EntireTrash(w.R));
        Assert.Equal(RestoreOutcome.Restored, (await w.Other.RestoreAsync(w.E_R1)).Outcome);
        var before = w.Dump();

        var result = await w.Content.PurgeAsync(PurgeRequest.EntireTrash(w.R), preview.Token);

        Assert.Equal((PurgeOutcome.Refused, PurgePreviewStatus.TrashEmpty), (result.Outcome, result.Refusal));
        Assert.Equal(before, w.Dump());
    }

    [Fact]
    public async Task A12_SelectionOrderIsIrrelevant_DifferentRootsWithTheSameClosureDiffer()
    {
        await World();
        var a = await Ready(Selected(_w.E_FB, _w.E_NB));
        var b = await Ready(Selected(_w.E_NB, _w.E_FB));
        var c = await Ready(Selected(_w.E_FB));

        Assert.Equal(a.Token, b.Token);
        Assert.Equal(a.Request, b.Request);
        Assert.NotEqual(a.Token, c.Token);
        Assert.Equal(a.Entries.Select(e => e.EntryId).Order(), c.Entries.Select(e => e.EntryId).Order());
        Assert.Equal(a.Token, (await Ready(Selected(_w.E_FB, _w.E_NB))).Token); // T1: deterministic
    }

    [Fact]
    public void A13_EmptyTrashWithIds_IsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => PurgeRequest.Create(1, PurgeMode.EntireTrash, [5]));
        Assert.Equal([3L, 5L], PurgeRequest.Create(1, PurgeMode.SelectedEntries, [5, 3, 5]).SelectedEntryIds);
    }

    // ---- 7.2 Stale confirmations and concurrency (a second real connection) ----------------------------------------------

    private async Task<(PurgeResult Result, Dictionary<string, List<string>> Before)> Interleave(PurgeRequest request, Func<Task> otherWindow)
    {
        var preview = await Ready(request);
        await otherWindow();
        var before = _w.Dump();
        var result = await Commit(request, preview.Token);
        if (!result.Committed)
            Assert.Equal(before, _w.Dump()); // a refused commit deleted nothing
        _w.AssertIntegrity();
        return (result, before);
    }

    [Fact]
    public async Task C1_EmptyTrash_ANewEntryInTheSameProject_IsStale_AndAFreshPreviewIncludesIt()
    {
        await World();
        long late = 0;
        var (result, _) = await Interleave(All(), async () => late = await _w.Trash(ContentKind.Link, _w.LK));

        Assert.Equal(PurgeOutcome.ConfirmationStale, result.Outcome);
        Assert.Contains((await Ready(All())).Entries, e => e.EntryId == late);
    }

    [Fact]
    public async Task C2_SelectedEntries_ANewUnrelatedEntry_DoesNotInvalidate()
    {
        await World();
        long late = 0;
        var (result, _) = await Interleave(Selected(_w.E_L1), async () => late = await _w.Trash(ContentKind.Link, _w.LK));

        Assert.Equal(PurgeOutcome.Purged, result.Outcome);
        Assert.Contains((await _w.Other.ListTrashAsync(_w.P)), e => e.Id == late);
    }

    [Fact]
    public async Task C3_ADependentPurgedElsewhere_IsStale()
    {
        await World();
        var (result, _) = await Interleave(Selected(_w.E_FB), async () =>
        {
            var other = await _w.Other.PreparePurgeAsync(Selected(_w.E_NB));
            Assert.Equal(PurgeOutcome.Purged, (await _w.Other.PurgeAsync(Selected(_w.E_NB), other.Preview!.Token)).Outcome);
        });
        Assert.Equal(PurgeOutcome.ConfirmationStale, result.Outcome);
    }

    [Fact]
    public async Task C4_ASelectedIndependentEntryBecomesADependent_IsStale_ReconfirmedItGoesAlone()
    {
        await World();
        var x = await _w.Trash(ContentKind.Note, _w.X);
        long outer = 0;
        Assert.Null((await Ready(Selected(x))).Entries.Single().ContainingEntryId);
        var (result, _) = await Interleave(Selected(x), async () => outer = await _w.Trash(ContentKind.Folder, _w.FK));
        Assert.Equal(PurgeOutcome.ConfirmationStale, result.Outcome);

        var fresh = await Ready(Selected(x));
        Assert.Equal(outer, fresh.Entries.Single().ContainingEntryId);
        Assert.True(fresh.Entries.Single().FromTrashed);
        Assert.Equal(PurgeOutcome.Purged, (await Commit(Selected(x), fresh.Token)).Outcome);
        Assert.Contains(await _w.Other.ListTrashAsync(_w.P), e => e.Id == outer);
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task C5_C6_NoNewDependentInsideATrashedFolder_RestoreAndRetrashGivesANewId()
    {
        await World();
        using (var connection = _w.Root.Open())
        {
            var ex = Assert.Throws<SqliteException>(() => Db.Exec(connection,
                $"INSERT INTO ProjectTrashEntries (ProjectNodeId, RootKind, RootId, ItemCount, TrashedAt) VALUES ({_w.P}, 'Link', {_w.FB1xLink}, 1, '{Db.Now}');"));
            Assert.Contains("WH_TRASH_ROOT", ex.Message);
        }

        var (result, _) = await Interleave(Selected(_w.E_FA), async () =>
        {
            Assert.Equal(RestoreOutcome.Restored, (await _w.Other.RestoreAsync(_w.E_FA)).Outcome);
            Assert.NotEqual(_w.E_FA, await _w.Trash(ContentKind.Folder, _w.FA));
        });
        Assert.Equal((PurgeOutcome.Refused, PurgePreviewStatus.EntryNotFound), (result.Outcome, result.Refusal));
    }

    [Fact]
    public async Task C7_OneOfTwoSelectedRestoredElsewhere_EntryNotFound_TheOtherIsIntact()
    {
        await World();
        var (result, _) = await Interleave(Selected(_w.E_L1, _w.E_N1), async () => await _w.Other.RestoreAsync(_w.E_L1));
        Assert.Equal((PurgeOutcome.Refused, PurgePreviewStatus.EntryNotFound), (result.Outcome, result.Refusal));
        Assert.Contains(await _w.Other.ListTrashAsync(_w.P), e => e.Id == _w.E_N1);
    }

    [Fact]
    public async Task C8_ASelectedEntryPurgedElsewhere_EntryNotFound()
    {
        await World();
        var (result, _) = await Interleave(Selected(_w.E_L1), async () =>
            await _w.Other.PurgeAsync(Selected(_w.E_L1), (await _w.Other.PreparePurgeAsync(Selected(_w.E_L1))).Preview!.Token));
        Assert.Equal((PurgeOutcome.Refused, PurgePreviewStatus.EntryNotFound), (result.Outcome, result.Refusal));
    }

    [Fact]
    public async Task C9_EmptyTrash_OneEntryRestoredElsewhere_IsStale()
    {
        await World();
        var (result, _) = await Interleave(All(), async () => await _w.Other.RestoreAsync(_w.E_N1));
        Assert.Equal(PurgeOutcome.ConfirmationStale, result.Outcome);
    }

    [Fact]
    public async Task C10_TheProjectTrashedElsewhere_ProjectTrashed_CheckedBeforeTheToken()
    {
        await World();
        var (result, _) = await Interleave(Selected(_w.E_L1), async () => await _w.Workspace.TrashProjectAsync(_w.P));
        Assert.Equal((PurgeOutcome.Refused, PurgePreviewStatus.ProjectTrashed), (result.Outcome, result.Refusal));
    }

    [Fact]
    public async Task C11_OnlyAnAncestorWorkspaceFolderTrashedElsewhere_TheStoreRefuses()
    {
        await World();
        var (result, _) = await Interleave(Selected(_w.E_L1), async () =>
        {
            var preview = await _w.Workspace.PrepareFolderLifecycleAsync(_w.WF, FolderLifecycleKind.Trash);
            await _w.Workspace.MarkFolderAsync(_w.WF, FolderLifecycleKind.Trash, preview.Confirmation!.Token);
        });
        Assert.Equal((PurgeOutcome.Refused, PurgePreviewStatus.ProjectTrashed), (result.Outcome, result.Refusal));
        Assert.Equal(PurgePreviewStatus.ProjectTrashed, (await Preview(Selected(_w.E_L1))).Status);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("ancestor")]
    public async Task C12_P4_ArchivedElsewhere_IsStale_ReconfirmedTheArchivedProjectIsPurged(string what)
    {
        await World();
        var (result, _) = await Interleave(Selected(_w.E_L1), async () =>
        {
            if (what == "project")
                await _w.Workspace.ArchiveProjectAsync(_w.P);
            else
                await _w.Workspace.MarkFolderAsync(_w.WF, FolderLifecycleKind.Archive, (await _w.Workspace.PrepareFolderLifecycleAsync(_w.WF, FolderLifecycleKind.Archive)).Confirmation!.Token);
        });
        Assert.Equal(PurgeOutcome.ConfirmationStale, result.Outcome);

        var fresh = await Ready(Selected(_w.E_L1));
        Assert.Equal(LifecycleState.Archived, fresh.EffectiveState);
        Assert.Equal(PurgeOutcome.Purged, (await Commit(Selected(_w.E_L1), fresh.Token)).Outcome);
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task C13_AnAncestorTrashedAndRestoredAgain_SameDisplayedStateAndSet_Proceeds()
    {
        await World();
        var (result, _) = await Interleave(Selected(_w.E_L1), async () =>
        {
            await _w.Workspace.MarkFolderAsync(_w.WF, FolderLifecycleKind.Trash, (await _w.Workspace.PrepareFolderLifecycleAsync(_w.WF, FolderLifecycleKind.Trash)).Confirmation!.Token);
            await _w.Workspace.ClearFolderAsync(_w.WF, FolderLifecycleKind.Trash);
        });
        Assert.Equal(PurgeOutcome.Purged, result.Outcome);
    }

    [Theory]
    [InlineData("project name")]
    [InlineData("origin folder name")]
    [InlineData("origin folder moved")]
    public async Task C14_T2_ADisplayedNameOrPathChangesElsewhere_IsStale(string change)
    {
        await World();
        var x = await _w.Trash(ContentKind.Note, _w.X); // displayed origin: Project P › FK (an active folder)
        var preview = await Ready(Selected(x));
        Assert.Equal("Project P › FK", preview.Entries.Single().From.Display);

        switch (change)
        {
            case "project name":
                await _w.Workspace.RenameAsync(_w.P, "Project P renamed");
                break;
            case "origin folder name":
                await _w.Other.RenameFolderAsync(_w.FK, "FK renamed");
                break;
            default:
                var parent = await _w.Folder(_w.P, null, "Parent");
                Assert.Equal(ContentMoveOutcome.Moved, (await _w.Other.MoveFolderAsync(_w.FK, null, parent)).Outcome);
                break;
        }

        Assert.NotEqual(preview.Token, (await Ready(Selected(x))).Token);
        Assert.Equal(PurgeOutcome.ConfirmationStale, (await Commit(Selected(x), preview.Token)).Outcome);
    }

    [Fact]
    public async Task C15_C16_ActiveEditsInTheProject_AndAnyChangeInOtherProjects_DoNotInvalidate()
    {
        await World();
        var (result, _) = await Interleave(All(), async () =>
        {
            var note = (await _w.Other.GetNoteAsync(_w.NK))!;
            await _w.Other.SaveNoteAsync(note.Id, note.Revision, new ProjectNoteDraft("NK", "edited after the preview"));
            await _w.Trash(ContentKind.Link, _w.QKLink);
            await _w.Other.PurgeAsync(PurgeRequest.Selected(_w.Q, [_w.E_Q1]), (await _w.Other.PreparePurgeAsync(PurgeRequest.Selected(_w.Q, [_w.E_Q1]))).Preview!.Token);
            await _w.Workspace.RenameAsync(_w.Q, "Project Q renamed");
            await _w.Workspace.TrashProjectAsync(_w.R);
        });
        Assert.Equal(PurgeOutcome.Purged, result.Outcome);
    }

    [Fact]
    public async Task C17_AnotherWriterHoldsTheDatabase_DatabaseBusy_TheBackupIsKept_TheSameTokenSucceedsLater()
    {
        await World();
        _w.Use(_w.NewPurge(connections: new SqliteConnectionFactory(_w.Root.Paths.DatabaseFile, defaultTimeoutSeconds: 1)));
        var preview = await Ready(Selected(_w.E_L1));
        var before = _w.Dump();
        PurgeResult busy;
        using (var holder = BackupKit.Open(_w.Root.Paths.DatabaseFile))
        {
            var purge = _w.NewPurge(connections: new SqliteConnectionFactory(_w.Root.Paths.DatabaseFile, defaultTimeoutSeconds: 1));
            purge.OnCheckpoint = point =>
            {
                if (point == "published")
                    BackupKit.Exec(holder, "BEGIN IMMEDIATE;"); // after the copy: the deletion cannot take the write lock
            };
            busy = await _w.Use(purge).PurgeAsync(Selected(_w.E_L1), preview.Token);
            BackupKit.Exec(holder, "ROLLBACK;");
        }

        Assert.Equal((PurgeOutcome.DatabaseBusy, false, true), (busy.Outcome, busy.Committed, busy.BackupKept));
        Assert.Equal([busy.BackupFileName!], _w.PreDeleteFiles());
        Assert.Equal(before, _w.Dump());
        var later = await _w.Use(_w.NewPurge()).PurgeAsync(Selected(_w.E_L1), preview.Token);
        Assert.Equal(PurgeOutcome.Purged, later.Outcome);
        Assert.Equal(2, _w.PreDeleteFiles().Length);
    }

    [Fact]
    public async Task C18_AnOriginChainBeyondTheBound_AncestryUnverified()
    {
        await World();
        var d1 = await _w.Folder(_w.P, null, "D1");
        var d2 = await _w.Folder(_w.P, d1, "D2");
        var d3 = await _w.Folder(_w.P, d2, "D3");
        var deep = await _w.Trash(ContentKind.Note, await _w.Note(_w.P, d3, "deep"));
        _w.Use(_w.NewPurge(maxDepth: 1)); // the origin chain D3 › D2 › D1 needs 3 levels; the Workspace chain P › WF fits
        // Dependents are found through every entry's origin, so one unverifiable entry fails the whole Project closed.
        Assert.Equal(PurgePreviewStatus.AncestryUnverified, (await Preview(Selected(_w.E_L1))).Status);
        Assert.Equal(PurgePreviewStatus.AncestryUnverified, (await Preview(Selected(deep))).Status);
        Assert.Equal(PurgePreviewStatus.AncestryUnverified, (await Preview(All())).Status);
    }

    [Fact]
    public async Task C18_AWorkspaceChainBeyondTheBound_AncestryUnverified()
    {
        await World();
        var deep = (await _w.Workspace.CreateFolderAsync(_w.WF, "Deeper")).Value!.Id;
        var project = (await _w.Workspace.CreateProjectAsync(deep, new ProjectDraft("Deep", ProjectStatus.Active))).Value!.Id;
        var entry = await _w.Trash(ContentKind.Note, await _w.Note(project, null, "n"));
        _w.Use(_w.NewPurge(maxDepth: 1)); // Deep › Deeper › WF exceeds it; P › WF does not
        Assert.Equal(PurgePreviewStatus.AncestryUnverified, (await Preview(PurgeRequest.Selected(project, [entry]))).Status);
        Assert.Equal(PurgePreviewStatus.Ready, (await Preview(Selected(_w.E_L1))).Status);
    }

    [Fact]
    public async Task C19_RowsNotMatchingItemCount_TrashDataInconsistent_NothingDeleted()
    {
        await World();
        _w.Exec($"DELETE FROM ProjectNotes WHERE Id = {_w.FA1aNote};"); // an external edit the app never makes (P is live)
        var before = _w.Dump();

        Assert.Equal(PurgePreviewStatus.TrashDataInconsistent, (await Preview(Selected(_w.E_FA))).Status);
        Assert.Equal(PurgePreviewStatus.TrashDataInconsistent, (await Preview(All())).Status);
        var commit = await Commit(Selected(_w.E_FA), "any");
        Assert.Equal((PurgeOutcome.Refused, PurgePreviewStatus.TrashDataInconsistent), (commit.Outcome, commit.Refusal));
        Assert.Equal(before, _w.Dump());
    }

    // ---- 7.3 Transaction, rollback and invariants ------------------------------------------------------------------------

    [Theory]
    [InlineData("purge:notes")]
    [InlineData("purge:links")]
    [InlineData("purge:folders:2")]
    [InlineData("purge:folders:1")]
    [InlineData("purge:folders:0")]
    [InlineData("purge:entries")]
    public async Task P1_AFailureAfterAnyDeleteStep_RollsEverythingBack_TheBackupIsKept(string step)
    {
        await World();
        var purge = _w.NewPurge();
        purge.OnWriteStep = s =>
        {
            if (s == step)
                throw new InvalidOperationException("injected");
        };
        _w.Use(purge);
        var preview = await Ready(Selected(_w.E_FB));
        var before = _w.Dump();

        var result = await Commit(Selected(_w.E_FB), preview.Token);

        Assert.Equal((PurgeOutcome.Failed, false, true), (result.Outcome, result.Committed, result.BackupKept));
        Assert.Equal(before, _w.Dump());
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task P6_PurgedEntryIdsAreNeverReused()
    {
        await World();
        long max;
        using (var connection = _w.Root.Open())
            max = Db.ScalarLong(connection, "SELECT max(Id) FROM ProjectTrashEntries;");
        Assert.Equal(PurgeOutcome.Purged, (await _w.Other.PurgeAsync(PurgeRequest.Selected(_w.R, [max]), (await _w.Other.PreparePurgeAsync(PurgeRequest.Selected(_w.R, [max]))).Preview!.Token)).Outcome);

        Assert.True(await _w.Trash(ContentKind.Link, _w.LK) > max);
    }

    [Fact]
    public async Task P8_AfterPurges_TheSchemaIsStillTheFrozenV9Reference()
    {
        await World();
        await PreviewAndCommit(All());
        using var connection = _w.Root.Open();
        Assert.Equal(10, SchemaMigrations.LatestVersion); // v9 when Phase 14 shipped; V010 (Phase 17) only adds ProjectTags
        Assert.Equal((long)SchemaMigrations.LatestVersion, Db.ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Empty(SchemaReference.Frozen.DifferencesFrom(SchemaMigrations.LatestVersion, SchemaFingerprint.Read(connection)));
    }

    // ---- 7.5 PreDelete copy -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task B1_ThePreDeleteCopyIsFullyValidated_Listed_AndHoldsEveryDeletedRowColumnForColumn()
    {
        await World();
        var before = _w.Dump();
        var result = await PreviewAndCommit(All());

        var file = Assert.Single(_w.PreDeleteFiles());
        Assert.Equal(file, result.BackupFileName);
        Assert.Matches($@"^workhub_predelete_v{SchemaMigrations.LatestVersion}_\d{{8}}-\d{{9}}\.db$", file);
        var path = Path.Combine(_w.Root.Paths.PreDeleteBackupsDirectory, file);
        Assert.True(BackupKit.Validator().Check(path, BackupCheckLevel.Full, SchemaMigrations.LatestVersion).IsFullyValidated);
        Assert.Empty(BackupKit.Sidecars(path));

        // The copy is the whole database as it was when it was taken: every deleted row is there, unchanged.
        Assert.Equal(before, _w.Dump(path));

        var catalog = new BackupCatalog(_w.Root.Paths);
        var entry = Assert.Single(await catalog.ListAsync(), e => e.Folder == BackupFolder.PreDelete);
        Assert.Equal((BackupKind.PreDelete, file, SchemaMigrations.LatestVersion), (entry.Kind, entry.FileName, entry.NameVersion));
        Assert.True((await catalog.CheckAsync(entry, BackupCheckLevel.Full)).IsFullyValidated);
        Assert.DoesNotContain(await catalog.ListAsync(), e => e.FileName == "predelete.lock");
    }

    [Fact]
    public async Task B2_AChangeBeforeTheSnapshot_IsStaleInTheSnapshot_NoCopyIsWritten()
    {
        await World();
        var preview = await Ready(All());
        await _w.Trash(ContentKind.Link, _w.LK);
        var before = _w.Dump();

        var result = await Commit(All(), preview.Token);

        Assert.Equal((PurgeOutcome.ConfirmationStale, (string?)null), (result.Outcome, result.BackupFileName));
        Assert.Empty(_w.PreDeleteFiles());
        Assert.Equal(before, _w.Dump());
    }

    [Fact]
    public async Task B3_AChangeAfterPublication_TheCommitIsStale_TheValidCopyIsKept()
    {
        await World();
        var purge = _w.NewPurge();
        purge.OnCheckpoint = point =>
        {
            if (point == "published")
                _w.Trash(ContentKind.Link, _w.LK).GetAwaiter().GetResult();
        };
        _w.Use(purge);
        var preview = await Ready(All());

        var result = await Commit(All(), preview.Token);

        Assert.Equal((PurgeOutcome.ConfirmationStale, true), (result.Outcome, result.BackupKept));
        var path = Path.Combine(_w.Root.Paths.PreDeleteBackupsDirectory, Assert.Single(_w.PreDeleteFiles()));
        Assert.True(BackupKit.Validator().Check(path, BackupCheckLevel.Full, SchemaMigrations.LatestVersion).IsFullyValidated);
        Assert.Contains("A copy of the whole database was made first and is kept", ProjectContentService.PurgeResultText(result));
    }

    [Fact]
    public async Task B4_AnUnrelatedChangeAfterTheSnapshot_IsNotInTheCopy_RestoringItWouldRevertIt()
    {
        await World();
        var purge = _w.NewPurge();
        purge.OnCheckpoint = point =>
        {
            if (point == "published")
                _w.Exec($"UPDATE ProjectNotes SET Content = 'edited after the copy', Revision = Revision + 1, UpdatedAt = '{Db.Now}' WHERE Id = {_w.NK};");
        };
        _w.Use(purge);

        var result = await PreviewAndCommit(Selected(_w.E_L1));

        Assert.Equal(PurgeOutcome.Purged, result.Outcome);
        using var copy = BackupKit.Open(Path.Combine(_w.Root.Paths.PreDeleteBackupsDirectory, result.BackupFileName!), SqliteOpenMode.ReadOnly);
        Assert.Equal("synthetic text", Db.ScalarString(copy, $"SELECT Content FROM ProjectNotes WHERE Id = {_w.NK};"));
        Assert.Equal(1L, Db.ScalarLong(copy, $"SELECT count(*) FROM ProjectLinks WHERE Id = {_w.L1};"));
    }

    [Fact]
    public async Task B5_ThePreDeleteLockIsHeld_BackupFailed_NothingDeleted()
    {
        await World();
        var preview = await Ready(Selected(_w.E_L1));
        Directory.CreateDirectory(_w.Root.Paths.PreDeleteBackupsDirectory);
        var before = _w.Dump();
        PurgeResult result;
        using (new FileStream(_w.Root.Paths.PreDeleteLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            result = await Commit(Selected(_w.E_L1), preview.Token);

        Assert.Equal((PurgeOutcome.BackupFailed, false, (string?)null), (result.Outcome, result.Committed, result.BackupFileName));
        Assert.Contains("another WorkHub window", result.Detail);
        Assert.Equal(before, _w.Dump());
    }

    [Fact]
    public async Task B5_PublishingFailsWithDiskFull_BackupFailed_TheCandidateIsRemoved_NothingDeleted()
    {
        await World();
        var files = new FaultyBackupFileOps { OnMove = (_, _) => throw FaultyBackupFileOps.DiskFull() };
        _w.Use(_w.NewPurge(files));
        var preview = await Ready(Selected(_w.E_L1));
        var before = _w.Dump();

        var result = await Commit(Selected(_w.E_L1), preview.Token);

        Assert.Equal(PurgeOutcome.BackupFailed, result.Outcome);
        Assert.Empty(_w.PreDeleteFiles());
        Assert.Equal(before, _w.Dump());
        Assert.Contains("The backup could not be made", ProjectContentService.PurgeResultText(result));
    }

    // ---- Cancellation: honoured until "Deleting", reported exactly ------------------------------------------------------------

    private async Task<(PurgeResult Result, List<PurgeStage> Stages)> CancelAt(string checkpoint, PurgeRequest request)
    {
        using var cts = new CancellationTokenSource();
        var purge = _w.NewPurge();
        purge.OnCheckpoint = point =>
        {
            if (point == checkpoint)
                cts.Cancel();
        };
        purge.OnWriteStep = step =>
        {
            if (step == checkpoint)
                cts.Cancel();
        };
        var stages = new List<PurgeStage>();
        var preview = await Ready(request);
        var result = await purge.PurgeAsync(request, preview.Token, new SyncProgress(stages), cts.Token);
        return (result, stages);
    }

    private sealed class SyncProgress(List<PurgeStage> stages) : IProgress<PurgeStage>
    {
        public void Report(PurgeStage value)
        {
            lock (stages)
                stages.Add(value);
        }
    }

    [Fact]
    public async Task Cancelled_BeforeItStarts_NothingIsMadeOrDeleted()
    {
        await World();
        var preview = await Ready(Selected(_w.E_L1));
        var before = _w.Dump();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _w.Content.PurgeAsync(Selected(_w.E_L1), preview.Token, null, cts.Token);

        Assert.Equal((PurgeOutcome.Cancelled, false, (string?)null), (result.Outcome, result.Committed, result.BackupFileName));
        Assert.Empty(_w.PreDeleteFiles());
        Assert.Equal(before, _w.Dump());
    }

    [Theory]
    [InlineData("in-snapshot")]
    [InlineData("candidate-validated")]
    public async Task Cancelled_DuringStaging_TheUnpublishedCandidateIsRemoved_NoCopyIsKept(string checkpoint)
    {
        await World();
        var before = _w.Dump();

        var (result, stages) = await CancelAt(checkpoint, Selected(_w.E_L1));

        Assert.Equal((PurgeOutcome.Cancelled, (string?)null), (result.Outcome, result.BackupFileName));
        Assert.Equal([PurgeStage.MakingBackup], stages);
        Assert.DoesNotContain(Directory.GetFiles(_w.Root.Paths.PreDeleteBackupsDirectory), f => !f.EndsWith("predelete.lock", StringComparison.Ordinal));
        Assert.Equal(before, _w.Dump());
        Assert.Equal("Cancelled. Nothing was deleted.", ProjectContentService.PurgeResultText(result));
    }

    [Theory]
    [InlineData("published")]
    [InlineData("before-delete")]
    public async Task Cancelled_AfterPublication_BeforeDeletion_NothingIsDeleted_ThePublishedCopyIsKeptAndNamed(string checkpoint)
    {
        await World();
        var before = _w.Dump();

        var (result, stages) = await CancelAt(checkpoint, Selected(_w.E_L1));

        Assert.Equal((PurgeOutcome.Cancelled, false, true), (result.Outcome, result.Committed, result.BackupKept));
        Assert.Equal([result.BackupFileName!], _w.PreDeleteFiles());
        Assert.True(BackupKit.Validator().Check(Path.Combine(_w.Root.Paths.PreDeleteBackupsDirectory, result.BackupFileName!), BackupCheckLevel.Full, SchemaMigrations.LatestVersion).IsFullyValidated);
        Assert.Equal(before, _w.Dump());
        Assert.Equal(checkpoint == "before-delete" ? [PurgeStage.MakingBackup, PurgeStage.BackupPublished, PurgeStage.Deleting] : [PurgeStage.MakingBackup, PurgeStage.BackupPublished], stages);
        Assert.Contains($"Cancelled. Nothing was deleted. A copy of the whole database was made first and is kept: {result.BackupFileName}.", ProjectContentService.PurgeResultText(result));
    }

    [Theory]
    [InlineData("purge:notes")]
    [InlineData("purge:entries")]
    [InlineData("committed")]
    public async Task ACancellationAfterDeletionBegan_IsIgnored_TheCommitIsReportedAsCommitted(string point)
    {
        await World();

        var (result, stages) = await CancelAt(point, Selected(_w.E_L1, _w.E_N1));

        Assert.Equal((PurgeOutcome.Purged, true), (result.Outcome, result.Committed));
        Assert.Equal(new PurgeCounts(2, 0, 1, 1), result.Deleted);
        Assert.Equal(PurgeStage.Deleting, stages[^1]);
        Assert.StartsWith("Removed from the database: 2 entries (0 folders, 1 link, 1 note).", ProjectContentService.PurgeResultText(result));
        Assert.DoesNotContain("Nothing was deleted", ProjectContentService.PurgeResultText(result));
    }

    // ---- Outcome precedence: a cancellation requested before "Deleting…" wins over a later handled backup failure ------------

    private string[] OwnCandidates() =>
        Directory.Exists(_w.Root.Paths.PreDeleteBackupsDirectory)
            ? Directory.GetFiles(_w.Root.Paths.PreDeleteBackupsDirectory).Select(f => Path.GetFileName(f)!).Where(PreDeleteNames.IsOwnCandidate).ToArray()
            : [];

    /// <summary>Runs the purge while another connection holds an EXCLUSIVE lock (DELETE journal: the copy's read pin waits 1 s, then gives up).</summary>
    private async Task<PurgeResult> WithExclusiveLock(ProjectTrashPurge purge, PurgeRequest request, string token, CancellationToken cancellationToken = default)
    {
        using var holder = BackupKit.Open(_w.Root.Paths.DatabaseFile);
        BackupKit.Exec(holder, "BEGIN EXCLUSIVE;");
        try
        {
            return await purge.PurgeAsync(request, token, null, cancellationToken);
        }
        finally
        {
            BackupKit.Exec(holder, "ROLLBACK;");
        }
    }

    private static void AssertNoContentLogged(RecordingLog log)
    {
        foreach (var content in new[] { "synthetic text", "example.test", "Project P" })
            Assert.DoesNotContain(content, log.All);
    }

    [Fact]
    public async Task C21_TheCopyWaitsForALockedDatabaseAndTimesOut_WithoutCancellation_BackupFailed()
    {
        await World();
        var preview = await Ready(Selected(_w.E_L1));
        var before = _w.Dump();
        var log = new RecordingLog();

        var result = await WithExclusiveLock(_w.NewPurge(log: log, lockWaitSeconds: 1), Selected(_w.E_L1), preview.Token);

        Assert.Equal((PurgeOutcome.BackupFailed, false, (string?)null), (result.Outcome, result.Committed, result.BackupFileName));
        Assert.Contains("stayed locked", result.Detail);
        Assert.StartsWith("The backup could not be made (", ProjectContentService.PurgeResultText(result));
        Assert.Empty(_w.PreDeleteFiles());
        Assert.Empty(OwnCandidates());
        Assert.Equal(before, _w.Dump());
        AssertNoContentLogged(log);
    }

    [Fact]
    public async Task C21_CancelRequestedWhileTheCopyWaitsForALockedDatabase_TheTimeoutIsReportedAsCancelled_TheCauseIsLogged()
    {
        await World();
        var preview = await Ready(Selected(_w.E_L1));
        var before = _w.Dump();
        var log = new RecordingLog();
        using var cts = new CancellationTokenSource();
        var purge = _w.NewPurge(log: log, lockWaitSeconds: 1);
        purge.OnCheckpoint = point =>
        {
            if (point == "backup-start")
                cts.Cancel(); // requested before the copy's lock wait, which then runs to its timeout
        };

        var result = await WithExclusiveLock(purge, Selected(_w.E_L1), preview.Token, cts.Token);

        Assert.Equal((PurgeOutcome.Cancelled, false, (string?)null), (result.Outcome, result.Committed, result.BackupFileName));
        Assert.Equal("Cancelled. Nothing was deleted.", ProjectContentService.PurgeResultText(result));
        Assert.Empty(_w.PreDeleteFiles());
        Assert.Empty(OwnCandidates());
        Assert.Equal(before, _w.Dump());
        Assert.Contains(log.Lines, l => l.StartsWith("WARN ", StringComparison.Ordinal) && l.Contains("stayed locked") && l.Contains("Cancelled"));
        AssertNoContentLogged(log);
    }

    [Fact]
    public async Task C21_CancelRequested_ThenThePreDeleteLockIsUnavailable_Cancelled_TheCauseIsLogged()
    {
        await World();
        var preview = await Ready(Selected(_w.E_L1));
        Directory.CreateDirectory(_w.Root.Paths.PreDeleteBackupsDirectory);
        var before = _w.Dump();
        var log = new RecordingLog();
        using var cts = new CancellationTokenSource();
        var purge = _w.NewPurge(log: log);
        purge.OnCheckpoint = point =>
        {
            if (point == "backup-start")
                cts.Cancel();
        };
        PurgeResult result;
        using (new FileStream(_w.Root.Paths.PreDeleteLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            result = await purge.PurgeAsync(Selected(_w.E_L1), preview.Token, null, cts.Token);

        Assert.Equal((PurgeOutcome.Cancelled, false, (string?)null), (result.Outcome, result.Committed, result.BackupFileName));
        Assert.Equal(before, _w.Dump());
        Assert.Contains(log.Lines, l => l.StartsWith("WARN ", StringComparison.Ordinal) && l.Contains("another WorkHub window"));
        AssertNoContentLogged(log);
    }

    [Fact]
    public async Task C21_CancelRequested_ThenPublishingFailsWithDiskFull_Cancelled_TheUnpublishedCandidateIsRemoved()
    {
        await World();
        using var cts = new CancellationTokenSource();
        var files = new FaultyBackupFileOps
        {
            OnMove = (_, _) =>
            {
                cts.Cancel(); // after the last staging check, while the copy is being put in place
                throw FaultyBackupFileOps.DiskFull();
            },
        };
        var log = new RecordingLog();
        var purge = _w.NewPurge(files, log: log);
        var preview = await Ready(Selected(_w.E_L1));
        var before = _w.Dump();

        var result = await purge.PurgeAsync(Selected(_w.E_L1), preview.Token, null, cts.Token);

        Assert.Equal((PurgeOutcome.Cancelled, false, (string?)null), (result.Outcome, result.Committed, result.BackupFileName));
        Assert.Empty(_w.PreDeleteFiles());
        Assert.Empty(OwnCandidates());
        Assert.Equal(before, _w.Dump());
        Assert.Contains(log.Lines, l => l.StartsWith("WARN ", StringComparison.Ordinal) && l.Contains("could not be put in place"));
    }

    [Fact]
    public async Task C21_ALateCancellation_ThenTheDeletionRollsBack_TheTrueFailureIsReported_NeverCancelled()
    {
        await World();
        using var cts = new CancellationTokenSource();
        var purge = _w.NewPurge();
        purge.OnWriteStep = step =>
        {
            if (step != "purge:links")
                return;
            cts.Cancel(); // after the non-cancellable boundary
            throw new InvalidOperationException("injected");
        };
        var preview = await Ready(Selected(_w.E_L1, _w.E_N1));
        var before = _w.Dump();

        var result = await purge.PurgeAsync(Selected(_w.E_L1, _w.E_N1), preview.Token, null, cts.Token);

        Assert.Equal((PurgeOutcome.Failed, false, true), (result.Outcome, result.Committed, result.BackupKept));
        Assert.DoesNotContain("Cancelled", ProjectContentService.PurgeResultText(result));
        Assert.Equal(before, _w.Dump());
        Assert.Equal([result.BackupFileName!], _w.PreDeleteFiles());
    }

    // ---- Crash boundaries in this process (the backup protocol's own crash hook) ----------------------------------------

    [Fact]
    public async Task ASimulatedCrashDuringTheCopy_LeavesAnOrphanCandidate_TheNextPurgeCleansIt_TheDatabaseIsUnchanged()
    {
        await World();
        var preview = await Ready(Selected(_w.E_L1));
        var before = _w.Dump();
        var crashing = new ProjectTrashPurge(_w.Root.Paths, _w.Connections, WorkHub.Core.Diagnostics.NullAppLog.Instance, TimeProvider.System, SchemaReference.Frozen,
            new BackupFileOps(), SqliteProjectContentStore.MaxDepth, backupHook: stage =>
            {
                if (stage == BackupStage.Copied)
                    throw new SimulatedCrashException(stage);
            });

        await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.PurgeAsync(Selected(_w.E_L1), preview.Token));
        BackupKit.ClearPoolsUnder(_w.Root.Paths.Root);
        Assert.Contains(Directory.GetFiles(_w.Root.Paths.PreDeleteBackupsDirectory), f => PreDeleteNames.IsOwnCandidate(Path.GetFileName(f)));
        Assert.Equal(before, _w.Dump());

        Assert.Equal(PurgeOutcome.Purged, (await Commit(Selected(_w.E_L1), preview.Token)).Outcome);
        Assert.DoesNotContain(Directory.GetFiles(_w.Root.Paths.PreDeleteBackupsDirectory), f => PreDeleteNames.IsOwnCandidate(Path.GetFileName(f)));
    }

    // ---- Search after a purge -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task V9_SearchFindsNoPurgedItem_AndStillFindsActiveContent()
    {
        await World();
        var restoredNote = await _w.Note(_w.P, null, "zzpurgecheck kept", "zzpurgecheck");
        var purgedNote = await _w.Note(_w.P, null, "zzpurgecheck gone", "zzpurgecheck");
        var entry = await _w.Trash(ContentKind.Note, purgedNote);
        var search = new WorkHub.Core.Workspace.Search.WorkspaceSearchService(new SqliteWorkspaceSearchStore(_w.Connections));
        Assert.Equal(PurgeOutcome.Purged, (await PreviewAndCommit(Selected(entry))).Outcome);

        var hits = (await search.SearchAsync(WorkHub.Core.Workspace.Search.WorkspaceSearchService.Parse("zzpurgecheck")!)).Hits;

        Assert.Contains(hits, h => h.Item.Id == restoredNote);
        Assert.DoesNotContain(hits, h => h.Item.Id == purgedNote);
    }

    // ---- Retention never touches PreDelete ------------------------------------------------------------------------------------

    [Fact]
    public async Task B10_DailyBackupsAndTheirRetention_NeverTouchPreDeleteCopies()
    {
        await World();
        await PreviewAndCommit(Selected(_w.E_L1));
        var kept = _w.PreDeleteFiles();
        var hashes = kept.Select(f => BackupKit.Hash(Path.Combine(_w.Root.Paths.PreDeleteBackupsDirectory, f))).ToArray();

        for (var day = 1; day <= 5; day++)
            BackupKit.Service(_w.Root, new SettableClock(SettableClock.Tokyo(2026, 11, day))).Run();

        Assert.Equal(kept, _w.PreDeleteFiles());
        Assert.Equal(hashes, kept.Select(f => BackupKit.Hash(Path.Combine(_w.Root.Paths.PreDeleteBackupsDirectory, f))).ToArray());
    }
}
