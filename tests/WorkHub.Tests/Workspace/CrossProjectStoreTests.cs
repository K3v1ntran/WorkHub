using System.Text;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;
using static WorkHub.Tests.TestSupport.CrossWorld;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 15 store (proposal Revision 2 §3–§6 with the §0.3 update; acceptance S1–S11 and T1–T5): Copy and Move between
/// Projects on the real SQLite store, the typed refusal order, the token, rollback at every stage, Restore after Move, and the
/// row, payload and post-graft depth bounds. A second service on the same database plays the other window.
/// </summary>
public sealed class CrossProjectStoreTests : IDisposable
{
    private CrossWorld _w = new();

    public void Dispose() => _w.Dispose();

    private Task<CrossWorld> World() => _w.BuildAsync();

    private static void AssertSame(Dictionary<string, List<string>> before, Dictionary<string, List<string>> after, params string[] except)
    {
        foreach (var table in before.Keys.Where(t => !except.Contains(t)))
            Assert.True(before[table].SequenceEqual(after[table]), $"{table} changed");
    }

    private static void AssertKept(Dictionary<string, List<string>> before, Dictionary<string, List<string>> after, string table)
    {
        var now = after[table].ToHashSet();
        Assert.All(before[table], row => Assert.Contains(row, now));
    }

    // ---- S1 / S2 / S4: copy, atomic move, restore -----------------------------------------------------------------------------

    [Fact]
    public async Task S1_CopyFolder_CopiesExactlyTheActiveSubtree_WithNewIds_AndLeavesTheSourceUnchanged()
    {
        await World();
        var before = _w.Dump();
        var request = Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S, _w.B, _w.Inbox);

        var (preview, result) = await _w.CommitAsync(request);

        Assert.Equal((3, 2, 2, 4, 3), (preview.Folders, preview.Links, preview.Notes, preview.SkippedItems, preview.SkippedEntries));
        Assert.Equal("Clients › Alpha", preview.SourcePath);
        Assert.Equal("Beta › Inbox", preview.TargetPath);
        Assert.Equal(CrossProjectOutcome.Copied, result.Outcome);
        Assert.Equal((ContentKind.Folder, _w.B, 7), (result.NewRoot!.Value.Kind, result.TargetProjectNodeId, result.Rows));
        Assert.NotEqual(_w.S, result.NewRoot!.Value.Id);
        var after = _w.Dump();
        AssertSame(before, after, "ProjectFolders", "ProjectLinks", "ProjectNotes", "sqlite_sequence");
        foreach (var table in new[] { "ProjectFolders", "ProjectLinks", "ProjectNotes" })
            AssertKept(before, after, table);
        Assert.Equal((3, 2, 2), (after["ProjectFolders"].Count - before["ProjectFolders"].Count, after["ProjectLinks"].Count - before["ProjectLinks"].Count, after["ProjectNotes"].Count - before["ProjectNotes"].Count));
        Assert.Equal(_w.Tree(_w.S), _w.Tree(result.NewRoot!.Value.Id)); // names, nesting, link fields, note contents, CreatedAt
        Assert.Equal(_w.Inbox, _w.Scalar($"SELECT ParentFolderId FROM ProjectFolders WHERE Id = {result.NewRoot!.Value.Id};"));
        Assert.Equal(0L, _w.Scalar($"SELECT count(*) FROM ProjectNotes WHERE ProjectNodeId = {_w.B} AND Revision <> 1;"));
        Assert.Equal(0L, _w.Scalar($"SELECT (SELECT count(*) FROM ProjectLinks WHERE ProjectNodeId = {_w.B} AND Name = 'Old link') + (SELECT count(*) FROM ProjectFolders WHERE ProjectNodeId = {_w.B} AND Name = 'Old') + (SELECT count(*) FROM ProjectNotes WHERE ProjectNodeId = {_w.B} AND Title IN ('Old note', 'Trashed draft'));"));
        Assert.Equal(1L, _w.Scalar($"SELECT count(*) FROM ProjectFolders WHERE Id = {result.NewRoot!.Value.Id} AND UpdatedAt > CreatedAt;")); // UpdatedAt = commit time
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task S1_CopyNote_KeepsTitleContentAndCreatedAt_AtRevision1()
    {
        await World();
        var (_, result) = await _w.CommitAsync(Req(CrossProjectMode.Copy, ContentKind.Note, _w.NA, _w.C, null));

        var copy = (await _w.Other.GetNoteAsync(result.NewRoot!.Value.Id))!;
        var original = (await _w.Other.GetNoteAsync(_w.NA))!;
        Assert.Equal(3, original.Revision);
        Assert.Equal((_w.C, (long?)null, 1L, original.Title, original.Content, original.CreatedAt), (copy.ProjectNodeId, copy.FolderId, copy.Revision, copy.Title, copy.Content, copy.CreatedAt));
        Assert.False(original.IsTrashed);
    }

    [Fact]
    public async Task S1_CopyLink_WhoseNameExistsAtTheTarget_IsAllowed()
    {
        await World();
        var (_, result) = await _w.CommitAsync(Req(CrossProjectMode.Copy, ContentKind.Link, _w.LA, _w.B, null));
        Assert.Equal(CrossProjectOutcome.Copied, result.Outcome);
        Assert.Equal(2L, _w.Scalar($"SELECT count(*) FROM ProjectLinks WHERE ProjectNodeId = {_w.B} AND FolderId IS NULL AND Name = 'Build server';"));
    }

    [Fact]
    public async Task S2_MoveFolder_CopiesAndTrashesTheSourceInOneTransaction()
    {
        await World();
        var before = _w.Dump();
        var tree = _w.Tree(_w.S);

        var (_, result) = await _w.CommitAsync(Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, _w.B, _w.Inbox));

        Assert.Equal(CrossProjectOutcome.Moved, result.Outcome);
        var entry = result.SourceTrashEntryId!.Value;
        Assert.Equal($"{_w.A}|Folder|{_w.S}|7", string.Join("|", _w.Scalar($"SELECT ProjectNodeId FROM ProjectTrashEntries WHERE Id = {entry};"), "Folder", _w.Scalar($"SELECT RootId FROM ProjectTrashEntries WHERE Id = {entry} AND RootKind = 'Folder';"), _w.Scalar($"SELECT ItemCount FROM ProjectTrashEntries WHERE Id = {entry};")));
        Assert.Equal(7L, _w.Scalar($"SELECT (SELECT count(*) FROM ProjectFolders WHERE TrashEntryId = {entry}) + (SELECT count(*) FROM ProjectLinks WHERE TrashEntryId = {entry}) + (SELECT count(*) FROM ProjectNotes WHERE TrashEntryId = {entry});"));
        Assert.Equal(4L, _w.Scalar($"SELECT (SELECT count(*) FROM ProjectNotes WHERE TrashEntryId IN ({_w.E_S1T}, {_w.E_S2})) + (SELECT count(*) FROM ProjectLinks WHERE TrashEntryId = {_w.E_SL2}) + (SELECT count(*) FROM ProjectFolders WHERE TrashEntryId = {_w.E_S2});"));
        Assert.Equal(tree, _w.Tree(result.NewRoot!.Value.Id));
        var after = _w.Dump();
        AssertSame(before, after, "ProjectFolders", "ProjectLinks", "ProjectNotes", "ProjectTrashEntries", "sqlite_sequence");
        AssertKept(before, after, "ProjectTrashEntries");
        Assert.Equal("Moved “Specs” (7 items) to Beta › Inbox. The original is in the Trash of Alpha and can be restored there.", ProjectContentService.CrossProjectResultText(result));
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task S4_RestoringTheMovedSource_BringsItBack_AndLeavesTheCopyUnchanged()
    {
        await World();
        var before = _w.Dump();
        var (_, result) = await _w.CommitAsync(Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, _w.B, _w.Inbox));
        var moved = _w.Dump();

        Assert.Equal(RestoreOutcome.Restored, (await _w.Other.RestoreAsync(result.SourceTrashEntryId!.Value)).Outcome);

        var restored = _w.Dump();
        foreach (var table in new[] { "WorkspaceNodes", "Projects", "ProjectTrashEntries" })
            Assert.Equal(before[table], restored[table]);
        string Owner(string row) => row.Split('|')[1];
        foreach (var table in new[] { "ProjectFolders", "ProjectLinks", "ProjectNotes" })
        {
            Assert.Equal(moved[table].Where(r => Owner(r) == $"{_w.B}"), restored[table].Where(r => Owner(r) == $"{_w.B}"));
            Assert.Equal(before[table].Where(r => Owner(r) == $"{_w.A}"), restored[table].Where(r => Owner(r) == $"{_w.A}"));
        }

        _w.AssertIntegrity();
    }

    // ---- S3: rollback at every stage ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("cross:folders")]
    [InlineData("cross:links")]
    [InlineData("cross:notes")]
    [InlineData("trash:entry")]
    [InlineData("trash:folders")]
    [InlineData("trash:links")]
    public async Task S3_AFailureAtAnyStage_RollsBothProjectsAndTheirTrashBack(string step)
    {
        await World();
        var request = Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, _w.B, _w.Inbox);
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        var before = _w.Dump();
        _w.Store.OnWriteStep = s =>
        {
            if (s == step)
                throw new InvalidOperationException("injected");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.Content.CrossProjectAsync(request, preview.Token!));

        _w.Store.OnWriteStep = null;
        Assert.Equal(before, _w.Dump()); // every table and sqlite_sequence
        _w.AssertIntegrity();
    }

    // ---- S5 / S8 / S9: typed refusals write nothing; archived targets are allowed ------------------------------------------------

    public static TheoryData<string> Refusals => new()
    {
        "SourceNotFound", "SourceProjectTrashed", "SourceTrashed", "TargetProjectNotFound", "SameProjectFolder", "SameProjectRoot", "TargetProjectTrashed",
        "TargetUnderTrashedFolder", "TargetFolderNotFound", "TargetFolderWrongProject", "TargetFolderTrashed", "InvalidNameEmpty", "InvalidNameLong",
        "InvalidNameControl", "NameConflictWide", "NameConflictCase",
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task S5_S9_EveryTypedRefusal_InPreviewAndCommit_WritesNothing(string @case)
    {
        await World();
        async Task TrashFolder(long folder) =>
            Assert.Equal(FolderLifecycleOutcome.Committed, (await _w.Workspace.MarkFolderAsync(folder, FolderLifecycleKind.Trash, (await _w.Workspace.PrepareFolderLifecycleAsync(folder, FolderLifecycleKind.Trash)).Confirmation!.Token)).Outcome);
        var (request, expected) = @case switch
        {
            "SourceNotFound" => (Req(CrossProjectMode.Copy, ContentKind.Note, 999999, _w.B, null), CrossProjectStatus.SourceNotFound),
            "SourceProjectTrashed" => (Req(CrossProjectMode.Copy, ContentKind.Link, _w.LA, _w.B, null), CrossProjectStatus.SourceProjectTrashed),
            "SourceTrashed" => (Req(CrossProjectMode.Copy, ContentKind.Note, _w.S1T, _w.B, null), CrossProjectStatus.SourceTrashed),
            "TargetProjectNotFound" => (Req(CrossProjectMode.Copy, ContentKind.Link, _w.LA, 999999, null), CrossProjectStatus.TargetProjectNotFound),
            "SameProjectFolder" => (Req(CrossProjectMode.Move, ContentKind.Link, _w.LA, _w.A, _w.K), CrossProjectStatus.SameProject),
            "SameProjectRoot" => (Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S1, _w.A, null), CrossProjectStatus.SameProject),
            "TargetProjectTrashed" => (Req(CrossProjectMode.Copy, ContentKind.Link, _w.LA, _w.B, null), CrossProjectStatus.TargetProjectTrashed),
            "TargetUnderTrashedFolder" => (Req(CrossProjectMode.Copy, ContentKind.Link, _w.LA, _w.C, _w.Incoming), CrossProjectStatus.TargetProjectTrashed),
            "TargetFolderNotFound" => (Req(CrossProjectMode.Copy, ContentKind.Link, _w.LA, _w.B, 999999), CrossProjectStatus.TargetFolderNotFound),
            "TargetFolderWrongProject" => (Req(CrossProjectMode.Copy, ContentKind.Link, _w.LA, _w.B, _w.Incoming), CrossProjectStatus.TargetFolderWrongProject),
            "TargetFolderTrashed" => (Req(CrossProjectMode.Copy, ContentKind.Link, _w.LA, _w.C, _w.Incoming), CrossProjectStatus.TargetFolderTrashed),
            "InvalidNameEmpty" => (Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S, _w.B, null, "   "), CrossProjectStatus.InvalidName),
            "InvalidNameLong" => (Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S, _w.B, null, new string('x', 201)), CrossProjectStatus.InvalidName),
            "InvalidNameControl" => (Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S, _w.B, null, "Specs\t2"), CrossProjectStatus.InvalidName),
            "NameConflictWide" => (Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S, _w.B, null), CrossProjectStatus.NameConflict),
            "NameConflictCase" => (Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, _w.B, null, "sPeCs"), CrossProjectStatus.NameConflict),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };
        switch (@case)
        {
            case "SourceProjectTrashed": await TrashFolder(_w.WS1); break;
            case "TargetProjectTrashed": Assert.Equal(ProjectTrashOutcome.Trashed, (await _w.Workspace.TrashProjectAsync(_w.B)).Outcome); break;
            case "TargetUnderTrashedFolder": await TrashFolder(_w.WS2); break;
            case "TargetFolderTrashed": await _w.Trash(ContentKind.Folder, _w.Incoming); break;
        }

        var before = _w.Dump();
        Assert.Equal(expected, (await _w.Content.PrepareCrossProjectAsync(request)).Status);
        var result = await _w.Content.CrossProjectAsync(request, "any-token");
        Assert.Equal((CrossProjectOutcome.Refused, (CrossProjectStatus?)expected), (result.Outcome, result.Refusal));
        Assert.Equal(before, _w.Dump());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task S8_ArchivedTargets_AreAllowed_AndKeepTheirArchiveState(bool underArchivedFolder)
    {
        await World();
        var (project, folder) = underArchivedFolder ? (_w.E, _w.Ein) : (_w.D, _w.Din);
        var lifecycle = $"SELECT group_concat(Id || ':' || coalesce(FolderArchivedAt, '-') || ':' || coalesce(FolderTrashedAt, '-') || ':' || LifecycleSerial, ',') FROM WorkspaceNodes";
        string State() { using var c = _w.Root.Open(); return Db.ScalarString(c, lifecycle)! + "#" + Db.ScalarString(c, "SELECT group_concat(NodeId || ':' || Status || ':' || ArchiveSerial, ',') FROM Projects;"); }
        var state = State();

        var (preview, result) = await _w.CommitAsync(Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, project, folder));

        Assert.True(preview.TargetArchived);
        Assert.Equal(CrossProjectOutcome.Moved, result.Outcome);
        Assert.Equal(state, State());
    }

    // ---- S6: NameKey and the suggested name ----------------------------------------------------------------------------------------

    [Fact]
    public async Task S6_AConflictOffersASuggestion_TheConfirmedNameIsValidatedTrimmedAndBound()
    {
        await World();
        var conflict = await _w.Content.PrepareCrossProjectAsync(Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S, _w.B, null));
        Assert.Equal((CrossProjectStatus.NameConflict, "Specs (2)"), (conflict.Status, conflict.SuggestedName));
        Assert.Null(conflict.Token);

        var request = Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S, _w.B, null, "  Specs (2)  ");
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        Assert.Equal((CrossProjectStatus.Ready, "Specs (2)"), (preview.Status, preview.RootName));
        Assert.NotEqual(preview.Token, (await _w.Content.PrepareCrossProjectAsync(Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S, _w.B, null, "Specs (3)"))).Token);

        var result = await _w.Content.CrossProjectAsync(request, preview.Token!);
        using var connection = _w.Root.Open();
        Assert.Equal("Specs (2)|SPECS (2)", Db.ScalarString(connection, $"SELECT Name || '|' || NameKey FROM ProjectFolders WHERE Id = {result.NewRoot!.Value.Id};"));
    }

    // ---- S7: the row bound -------------------------------------------------------------------------------------------------------

    /// <summary>A folder in A with exactly <paramref name="rows"/> active rows: itself, 99 subfolders, links and notes (raw SQL, one transaction).</summary>
    private long BigFolder(int rows)
    {
        var at = "2026-11-11T03:00:00.000Z";
        using var connection = _w.Root.Open();
        Db.Exec(connection, "BEGIN;");
        var root = Db.ScalarLongWith(connection, "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Big', 'BIG', $at, $at) RETURNING Id;", ("$p", _w.A), ("$at", at));
        Db.Exec(connection, $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 99) INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) SELECT {_w.A}, {root}, 'sub ' || i, 'SUB ' || i, '{at}', '{at}' FROM n;");
        var links = (rows - 100) / 2;
        var notes = rows - 100 - links;
        Db.Exec(connection, $"WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {links - 1}) INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) SELECT {_w.A}, {root}, 'Web', 'link ' || i, 'https://example.test/' || i, '{at}', '{at}' FROM n;");
        Db.Exec(connection, $"WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < {notes - 1}) INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) SELECT {_w.A}, {root}, 'note ' || i, 'body ' || i, '{at}', '{at}' FROM n;");
        Db.Exec(connection, "COMMIT;");
        return root;
    }

    [Theory]
    [InlineData(10000, true)]
    [InlineData(10001, false)]
    public async Task S7_TheRowBound_IsCheckedInThePreviewAndUnderTheLock(int rows, bool ready)
    {
        await World();
        var big = BigFolder(rows);
        var request = Req(CrossProjectMode.Copy, ContentKind.Folder, big, _w.B, null);
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        Assert.Equal(rows, preview.Rows);
        if (ready)
        {
            Assert.True(preview.IsReady);
            Assert.Equal(CrossProjectOutcome.Copied, (await _w.Content.CrossProjectAsync(request, preview.Token!)).Outcome);
            return;
        }

        Assert.Equal(CrossProjectStatus.TooLarge, preview.Status);
        var before = _w.Dump();
        Assert.Equal(CrossProjectStatus.TooLarge, (await _w.Content.CrossProjectAsync(request, "any")).Refusal);
        Assert.Equal(before, _w.Dump());
    }

    // ---- S10: the exact payload boundary (D-15.6: 32 MiB = 33,554,432 bytes), with multi-byte text ------------------------------------

    private const string PayloadRoot = "Payload";

    /// <summary>The decided bound, written out here so the regression pins the decision rather than whatever the constant says.</summary>
    private const long PayloadBound = 33_554_432;

    [Fact]
    public void S10_ThePayloadBound_Is32MiB_And10000Rows()
    {
        Assert.Equal(PayloadBound, CrossProjectLimits.MaxPayloadBytes);
        Assert.Equal(32L * 1024 * 1024, CrossProjectLimits.MaxPayloadBytes);
        Assert.Equal(10_000, CrossProjectLimits.MaxRows);
    }

    /// <summary>
    /// A folder in A whose counted UTF-8 payload (its name, every note title and content) is exactly
    /// <see cref="PayloadBound"/> + <paramref name="extra"/> bytes. Note contents are mostly '日' (3 bytes, 1 character), so a
    /// character count would be about a third of the byte count. Returns the folder and the last note.
    /// </summary>
    private async Task<(long Folder, long LastNote)> PayloadFolder(int extra)
    {
        var folder = await _w.Folder(_w.A, null, PayloadRoot);
        var remaining = PayloadBound + extra - Encoding.UTF8.GetByteCount(PayloadRoot);
        long last = 0;
        for (var i = 0; remaining > 0; i++)
        {
            string title, content;
            if (remaining <= 200)
            {
                title = new string('t', (int)remaining);
                content = string.Empty;
            }
            else
            {
                title = $"p{i}";
                var bytes = (int)Math.Min(remaining - title.Length, 299_997);
                content = new string('日', bytes / 3) + new string('a', bytes % 3); // at most 100,000 characters
            }

            last = await _w.Note(_w.A, folder, title, content);
            remaining -= Encoding.UTF8.GetByteCount(title) + Encoding.UTF8.GetByteCount(content);
        }

        Assert.Equal(0, remaining);
        return (folder, last);
    }

    private long PayloadBytesInDatabase(long folder) =>
        _w.Scalar($"SELECT (SELECT length(CAST(Name AS BLOB)) FROM ProjectFolders WHERE Id = {folder}) + (SELECT coalesce(sum(length(CAST(Title AS BLOB)) + length(CAST(Content AS BLOB))), 0) FROM ProjectNotes WHERE FolderId = {folder} AND TrashEntryId IS NULL);");

    [Theory]
    [InlineData(CrossProjectMode.Copy)]
    [InlineData(CrossProjectMode.Move)]
    public async Task S10_ExactlyAtThePayloadBound_IsReady_AndCommits(CrossProjectMode mode)
    {
        await World();
        var (folder, _) = await PayloadFolder(0);
        Assert.Equal(33_554_432L, PayloadBytesInDatabase(folder)); // the fixture's real UTF-8 byte count
        Assert.True(_w.Scalar($"SELECT sum(length(Content)) FROM ProjectNotes WHERE FolderId = {folder};") < PayloadBound / 2); // multi-byte: characters ≪ bytes

        var request = Req(mode, ContentKind.Folder, folder, _w.B, null);
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        Assert.Equal((CrossProjectStatus.Ready, 33_554_432L), (preview.Status, preview.PayloadBytes));
        Assert.True(preview.Rows < CrossProjectLimits.MaxRows);

        var result = await _w.Content.CrossProjectAsync(request, preview.Token!);
        Assert.Equal(mode == CrossProjectMode.Move ? CrossProjectOutcome.Moved : CrossProjectOutcome.Copied, result.Outcome);
        Assert.Equal(33_554_432L, PayloadBytesInDatabase(result.NewRoot!.Value.Id));
        Assert.Equal(preview.Rows, 1 + (int)_w.Scalar($"SELECT count(*) FROM ProjectNotes WHERE FolderId = {result.NewRoot!.Value.Id} AND ProjectNodeId = {_w.B};"));
        if (mode == CrossProjectMode.Move)
            Assert.Equal(preview.Rows, (int)_w.Scalar($"SELECT ItemCount FROM ProjectTrashEntries WHERE Id = {result.SourceTrashEntryId};"));
        _w.AssertIntegrity();
    }

    [Theory]
    [InlineData(CrossProjectMode.Copy)]
    [InlineData(CrossProjectMode.Move)]
    public async Task S10_OneByteOverThePayloadBound_IsTooLarge_InThePreviewAndUnderTheLock_AndWritesNothing(CrossProjectMode mode)
    {
        await World();
        var (folder, _) = await PayloadFolder(1);
        Assert.Equal(33_554_433L, PayloadBytesInDatabase(folder));

        var request = Req(mode, ContentKind.Folder, folder, _w.B, null);
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        Assert.Equal((CrossProjectStatus.TooLarge, 33_554_433L, (string?)null), (preview.Status, preview.PayloadBytes, preview.Token));
        Assert.True(preview.Rows < CrossProjectLimits.MaxRows);
        var entries = _w.Scalar($"SELECT count(*) FROM ProjectTrashEntries WHERE ProjectNodeId = {_w.A};");
        var before = _w.Dump();

        var result = await _w.Content.CrossProjectAsync(request, "any-token");

        Assert.Equal((CrossProjectOutcome.Refused, (CrossProjectStatus?)CrossProjectStatus.TooLarge), (result.Outcome, result.Refusal));
        Assert.Equal(before, _w.Dump()); // nothing written anywhere
        Assert.Equal(0L, _w.Scalar($"SELECT count(*) FROM ProjectFolders WHERE ProjectNodeId = {_w.B} AND Name = '{PayloadRoot}';")); // no copy
        Assert.Equal(entries, _w.Scalar($"SELECT count(*) FROM ProjectTrashEntries WHERE ProjectNodeId = {_w.A};"));                // no new entry
        Assert.Equal(0L, _w.Scalar($"SELECT (SELECT count(*) FROM ProjectFolders WHERE Id = {folder} AND TrashEntryId IS NOT NULL) + (SELECT count(*) FROM ProjectNotes WHERE FolderId = {folder} AND TrashEntryId IS NOT NULL);")); // no marker
    }

    [Fact]
    public async Task S10_OneByteAddedAfterAReadyPreview_TheCommitIsTooLarge_NotStale()
    {
        await World();
        var (folder, last) = await PayloadFolder(0);
        var request = Req(CrossProjectMode.Move, ContentKind.Folder, folder, _w.B, null);
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        Assert.Equal((CrossProjectStatus.Ready, 33_554_432L), (preview.Status, preview.PayloadBytes));
        var note = (await _w.Other.GetNoteAsync(last))!;
        Assert.Equal(NoteWriteStatus.Saved, (await _w.Other.SaveNoteAsync(last, note.Revision, new ProjectNoteDraft(note.Title, note.Content + "a"))).Status); // another window: +1 byte
        var before = _w.Dump();

        var result = await _w.Content.CrossProjectAsync(request, preview.Token!);

        Assert.Equal((CrossProjectOutcome.Refused, (CrossProjectStatus?)CrossProjectStatus.TooLarge), (result.Outcome, result.Refusal)); // D-15.8
        Assert.Equal(before, _w.Dump());
    }

    // ---- S11: depth after grafting ---------------------------------------------------------------------------------------------------

    private const int SmallBound = 6; // MaxDepth for these tests: a folder may sit at most 7 folders below its Project root

    private async Task<List<long>> Chain(long project, long? parent, string prefix, int levels)
    {
        var ids = new List<long>();
        for (var i = 1; i <= levels; i++)
            ids.Add(await _w.Folder(project, ids.Count > 0 ? ids[^1] : parent, $"{prefix}{i}"));
        return ids;
    }

    private async Task UseSmallBound()
    {
        _w.Dispose();
        _w = new CrossWorld(SmallBound);
        await World();
    }

    [Theory]
    [InlineData(CrossProjectMode.Copy)]
    [InlineData(CrossProjectMode.Move)]
    public async Task S11_ASourceTreeAndADestinationValidAlone_ButTooDeepOnceGrafted_AreRefused_DepthExceeded(CrossProjectMode mode)
    {
        await UseSmallBound();
        var source = await Chain(_w.A, null, "s", 4);                // H = 4 (valid alone: 4 ≤ 7)
        await _w.Note(_w.A, source[^1], "deepest note");
        var target = await Chain(_w.B, null, "t", 4);                // L(target) = 4 (valid alone)
        Assert.NotNull(await _w.Content.GetContainerPathAsync(_w.B, target[^1]));
        var request = Req(mode, ContentKind.Folder, source[0], _w.B, target[^1]); // 4 + 4 = 8 > SmallBound + 1

        Assert.Equal(CrossProjectStatus.DepthExceeded, (await _w.Content.PrepareCrossProjectAsync(request)).Status);
        var before = _w.Dump();
        var result = await _w.Content.CrossProjectAsync(request, "any-token");
        Assert.Equal((CrossProjectOutcome.Refused, (CrossProjectStatus?)CrossProjectStatus.DepthExceeded), (result.Outcome, result.Refusal));
        Assert.Equal(before, _w.Dump());

        // A link or note adds no folder level: copying one into the deepest valid folder is fine.
        Assert.True((await _w.Content.PrepareCrossProjectAsync(Req(mode, ContentKind.Note, (await _w.Note(_w.A, null, "n")), _w.B, target[^1]))).IsReady);
    }

    [Theory]
    [InlineData(CrossProjectMode.Copy)]
    [InlineData(CrossProjectMode.Move)]
    public async Task S11_ExactlyAtTheDepthBound_Commits_AndTheDeepestFolderIsReadableAndRevealable(CrossProjectMode mode)
    {
        await UseSmallBound();
        var source = await Chain(_w.A, null, "s", 4);
        var deepestNote = await _w.Note(_w.A, source[^1], "deepest note");
        var target = await Chain(_w.B, null, "t", 3);                // 3 + 4 = 7 = SmallBound + 1
        var (_, result) = await _w.CommitAsync(Req(mode, ContentKind.Folder, source[0], _w.B, target[^1]));

        // The deepest copied folder: 7 folders below B's root, with a complete breadcrumb (the existing walks' bound).
        var deepest = _w.Scalar($"SELECT Id FROM ProjectFolders WHERE ProjectNodeId = {_w.B} AND Name = 's4';");
        var listing = (await _w.Content.ListAsync(_w.B, deepest)).Listing!;
        Assert.Equal(SmallBound + 1, listing.Breadcrumb.Count);
        Assert.Equal(new[] { "t1", "t2", "t3", "s1", "s2", "s3", "s4" }, listing.Breadcrumb.Select(c => c.Name));
        var copiedNote = Assert.Single(listing.Notes);
        Assert.Contains(await _w.Content.ListFolderMoveTargetsAsync(_w.B, null), f => f.Id == deepest);

        var view = new ProjectContentViewModel(_w.Content, new ScriptedLinkChecker(), new FakeDialogs(), NullAppLog.Instance, new LinkCheckCache(), _w.B, "Beta");
        Assert.Equal(ContentRevealOutcome.Selected, await view.RevealAsync(deepest, ProjectContentItemKind.Note, copiedNote.Id));
        Assert.Equal(SmallBound + 2, view.Breadcrumb.Count); // the Project crumb + 7 folders
        Assert.NotEqual(deepestNote, copiedNote.Id);
        Assert.Equal(mode == CrossProjectMode.Move ? CrossProjectOutcome.Moved : CrossProjectOutcome.Copied, result.Outcome);
    }

    [Fact]
    public async Task S11_TheDestinationMovedDeeperAfterAReadyPreview_TheCommitIsDepthExceeded_NotStale()
    {
        await UseSmallBound();
        var source = await Chain(_w.A, null, "s", 4);
        var target = await Chain(_w.B, null, "t", 3);
        var request = Req(CrossProjectMode.Move, ContentKind.Folder, source[0], _w.B, target[^1]);
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        Assert.True(preview.IsReady);
        var wrapper = await _w.Folder(_w.B, null, "wrapper");
        Assert.Equal(ContentMoveOutcome.Moved, (await _w.Other.MoveFolderAsync(target[0], null, wrapper)).Outcome); // another window: one level deeper
        var before = _w.Dump();

        var result = await _w.Content.CrossProjectAsync(request, preview.Token!);

        Assert.Equal((CrossProjectOutcome.Refused, (CrossProjectStatus?)CrossProjectStatus.DepthExceeded), (result.Outcome, result.Refusal));
        Assert.Equal(before, _w.Dump());
    }

    // ---- T1–T5: token and concurrency --------------------------------------------------------------------------------------------------

    public static TheoryData<string> StaleChanges => new()
    {
        "destination folder renamed", "destination Project renamed", "destination ancestor renamed", "destination Project archived",
        "source note content edited", "source link target edited", "source descendant reparented (same Ids and counts)", "source note moved within the subtree",
        "source descendant trashed", "skipped item restored", "source root moved", "new note inside the subtree",
    };

    [Theory]
    [MemberData(nameof(StaleChanges))]
    public async Task T1_AChangeToWhatIsShownOrCopied_MakesTheConfirmationStale_AndWritesNothing(string change)
    {
        await World();
        var request = Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, _w.C, _w.Incoming);
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        Assert.True(preview.IsReady);
        switch (change)
        {
            case "destination folder renamed": Assert.True((await _w.Other.RenameFolderAsync(_w.Incoming, "Incoming 2")).Succeeded); break;
            case "destination Project renamed": Assert.True((await _w.Workspace.RenameAsync(_w.C, "Gamma 2")).Succeeded); break;
            case "destination ancestor renamed": Assert.True((await _w.Workspace.RenameAsync(_w.WS2, "Shelf 2")).Succeeded); break;
            case "destination Project archived": Assert.Equal(ProjectArchiveOutcome.Archived, (await _w.Workspace.ArchiveProjectAsync(_w.C)).Outcome); break;
            case "source note content edited": await _w.Edit(_w.SN1, 1, "spec body, edited"); break;
            case "source link target edited": Assert.True((await _w.Other.UpdateLinkAsync(_w.SL1, new ProjectLinkDraft("Spec index", ProjectLinkType.File, @"C:\WorkHubTest11\synthetic\other.txt"))).Succeeded); break;
            case "source descendant reparented (same Ids and counts)": Assert.Equal(ContentMoveOutcome.Moved, (await _w.Other.MoveFolderAsync(_w.S1a, _w.S1, _w.S)).Outcome); break;
            case "source note moved within the subtree": Assert.Equal(ContentMoveOutcome.Moved, (await _w.Other.MoveNoteAsync(_w.SN1, _w.S, _w.S1)).Outcome); break;
            case "source descendant trashed": await _w.Trash(ContentKind.Link, _w.SL1); break;
            case "skipped item restored": Assert.Equal(RestoreOutcome.Restored, (await _w.Other.RestoreAsync(_w.E_SL2)).Outcome); break;
            case "source root moved": Assert.Equal(ContentMoveOutcome.Moved, (await _w.Other.MoveFolderAsync(_w.S, null, _w.K)).Outcome); break;
            case "new note inside the subtree": await _w.Note(_w.A, _w.S1a, "late note"); break;
        }

        var before = _w.Dump();
        var result = await _w.Content.CrossProjectAsync(request, preview.Token!);
        Assert.Equal(CrossProjectOutcome.ConfirmationStale, result.Outcome);
        Assert.Equal(before, _w.Dump());
    }

    [Fact]
    public async Task T2_ATypedRefusalAppearingAfterThePreview_IsReportedBeforeStale()
    {
        await World();
        var request = Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S, _w.C, _w.Incoming);
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        await _w.Folder(_w.C, _w.Incoming, "SPECS"); // another window: a same-NameKey folder at the destination
        Assert.Equal(CrossProjectStatus.NameConflict, (await _w.Content.CrossProjectAsync(request, preview.Token!)).Refusal);

        var token = (await _w.Workspace.PrepareFolderLifecycleAsync(_w.WS2, FolderLifecycleKind.Trash)).Confirmation!.Token;
        Assert.Equal(FolderLifecycleOutcome.Committed, (await _w.Workspace.MarkFolderAsync(_w.WS2, FolderLifecycleKind.Trash, token)).Outcome);
        Assert.Equal(CrossProjectStatus.TargetProjectTrashed, (await _w.Content.CrossProjectAsync(request, preview.Token!)).Refusal);
    }

    [Fact]
    public async Task T3_UnrelatedChanges_DoNotInvalidate_TheCommitProceeds()
    {
        await World();
        var request = Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, _w.C, _w.Incoming);
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        await _w.Edit(_w.SN1, 1, "temporary");
        await _w.Edit(_w.SN1, 2, "spec body");                     // edited and edited back: Revision +2, same content
        await _w.Edit(_w.KN, 1, "outside the subtree");
        await _w.Trash(ContentKind.Note, _w.NA2);
        await _w.Link(_w.C, null, "elsewhere");
        await _w.Note(_w.C, _w.Incoming, "Specs");                 // a note: notes have no name scope
        await _w.Folder(_w.C, null, "Other");
        Assert.Equal(preview.Token, (await _w.Content.PrepareCrossProjectAsync(request)).Token);

        Assert.Equal(CrossProjectOutcome.Moved, (await _w.Content.CrossProjectAsync(request, preview.Token!)).Outcome);
        _w.AssertIntegrity();
    }

    [Fact]
    public async Task T4_AnotherWriterHoldsTheLock_DatabaseBusy_NothingWritten_ANewConfirmationLaterSucceeds()
    {
        await World();
        var store = new SqliteProjectContentStore(new SqliteConnectionFactory(_w.Root.Paths.DatabaseFile, defaultTimeoutSeconds: 1));
        var content = CrossWorld.Service(store);
        var request = Req(CrossProjectMode.Copy, ContentKind.Link, _w.LA, _w.B, null);
        var preview = await content.PrepareCrossProjectAsync(request);
        var before = _w.Dump();
        CrossProjectResult busy;
        using (var holder = BackupKit.Open(_w.Root.Paths.DatabaseFile))
        {
            BackupKit.Exec(holder, "BEGIN IMMEDIATE;");
            busy = await content.CrossProjectAsync(request, preview.Token!);
            BackupKit.Exec(holder, "ROLLBACK;");
        }

        Assert.Equal(CrossProjectOutcome.DatabaseBusy, busy.Outcome);
        Assert.Equal(before, _w.Dump());
        Assert.Equal(CrossProjectOutcome.Copied, (await content.CrossProjectAsync(request, preview.Token!)).Outcome);
    }

    [Fact]
    public async Task T5_TheTokenIsDeterministic_AndBindsModeDestinationAndName()
    {
        await World();
        var copy = Req(CrossProjectMode.Copy, ContentKind.Folder, _w.S, _w.B, _w.Inbox);
        var first = (await _w.Content.PrepareCrossProjectAsync(copy)).Token;
        Assert.Equal(first, (await _w.Other.PrepareCrossProjectAsync(copy)).Token);
        Assert.NotEqual(first, (await _w.Content.PrepareCrossProjectAsync(copy with { Mode = CrossProjectMode.Move })).Token);
        Assert.NotEqual(first, (await _w.Content.PrepareCrossProjectAsync(copy with { TargetFolderId = null, RootName = "Specs B" })).Token);
        Assert.NotEqual(first, (await _w.Content.PrepareCrossProjectAsync(copy with { RootName = "Specs copy" })).Token);
        Assert.Equal(64, first!.Length);
    }

    [Fact]
    public async Task Targets_ListLiveAndArchivedProjects_LabelArchived_AndNeverTheSourceOrTrashed()
    {
        await World();
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _w.Workspace.TrashProjectAsync(_w.B)).Outcome);

        var targets = await _w.Content.ListCrossProjectTargetsAsync(_w.A);

        Assert.DoesNotContain(targets, t => t.ProjectNodeId == _w.A || t.ProjectNodeId == _w.B);
        Assert.Equal("Shelf › Gamma", targets.Single(t => t.ProjectNodeId == _w.C).Path);
        Assert.True(targets.Single(t => t.ProjectNodeId == _w.D).Archived);
        Assert.True(targets.Single(t => t.ProjectNodeId == _w.E).Archived);
        Assert.False(targets.Single(t => t.ProjectNodeId == _w.C).Archived);
    }

    // ---- R1 (Codex review): the source's names belong to the confirmed snapshot ----------------------------------------------------

    [Fact]
    public async Task R1_ThePreviewReportsTheSourcesCurrentNames_AndTheTokenChangesWithThem()
    {
        await World();
        var request = Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, _w.C, _w.Incoming);
        var before = await _w.Content.PrepareCrossProjectAsync(request);
        Assert.Equal(("Specs", "Alpha", "Specs"), (before.SourceName, before.SourceProjectName, before.RootName));
        Assert.True((await _w.Other.RenameFolderAsync(_w.S, "Specs renamed")).Succeeded); // another window
        Assert.True((await _w.Workspace.RenameAsync(_w.A, "Alpha 2")).Succeeded);

        var after = await _w.Content.PrepareCrossProjectAsync(request);

        Assert.Equal(("Specs renamed", "Alpha 2", "Specs renamed"), (after.SourceName, after.SourceProjectName, after.RootName));
        Assert.NotEqual(before.Token, after.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R1_TheSourceFolderRenamedAfterAReadyPreview_TheMoveIsStale_NoCopyAndNoEntry(bool customName)
    {
        await World();
        var request = Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, _w.C, _w.Incoming, customName ? "Specs copy" : null);
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        Assert.True(preview.IsReady);
        Assert.True((await _w.Other.RenameFolderAsync(_w.S, "Specs renamed")).Succeeded); // another window, after Ready
        var before = _w.Dump();

        var result = await _w.Content.CrossProjectAsync(request, preview.Token!);

        Assert.Equal(CrossProjectOutcome.ConfirmationStale, result.Outcome);
        Assert.Equal(before, _w.Dump());
        Assert.Equal(0L, _w.Scalar($"SELECT count(*) FROM ProjectFolders WHERE ProjectNodeId = {_w.C} AND ParentFolderId = {_w.Incoming};"));
        Assert.Equal(0L, _w.Scalar($"SELECT count(*) FROM ProjectTrashEntries WHERE RootKind = 'Folder' AND RootId = {_w.S};"));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("link")]
    public async Task R1_TheSourceProjectOrALinkRenamedAfterAReadyPreview_IsStale(string what)
    {
        await World();
        var request = what == "link" ? Req(CrossProjectMode.Move, ContentKind.Link, _w.LA, _w.B, null) : Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, _w.C, _w.Incoming, "Specs copy");
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        if (what == "link")
            Assert.True((await _w.Other.UpdateLinkAsync(_w.LA, new ProjectLinkDraft("Build server 2", ProjectLinkType.Web, "https://build.example.test/nightly"))).Succeeded);
        else
            Assert.True((await _w.Workspace.RenameAsync(_w.A, "Alpha 2")).Succeeded);
        var before = _w.Dump();

        Assert.Equal(CrossProjectOutcome.ConfirmationStale, (await _w.Content.CrossProjectAsync(request, preview.Token!)).Outcome);
        Assert.Equal(before, _w.Dump());
    }

    [Fact]
    public async Task R1_ARenameTogetherWithATypedRefusal_ReportsTheTypedRefusalFirst()
    {
        await World();
        var request = Req(CrossProjectMode.Move, ContentKind.Folder, _w.S, _w.C, _w.Incoming, "Specs copy");
        var preview = await _w.Content.PrepareCrossProjectAsync(request);
        Assert.True((await _w.Other.RenameFolderAsync(_w.S, "Specs renamed")).Succeeded);
        var token = (await _w.Workspace.PrepareFolderLifecycleAsync(_w.WS2, FolderLifecycleKind.Trash)).Confirmation!.Token;
        Assert.Equal(FolderLifecycleOutcome.Committed, (await _w.Workspace.MarkFolderAsync(_w.WS2, FolderLifecycleKind.Trash, token)).Outcome);
        var before = _w.Dump();

        var result = await _w.Content.CrossProjectAsync(request, preview.Token!);

        Assert.Equal((CrossProjectOutcome.Refused, (CrossProjectStatus?)CrossProjectStatus.TargetProjectTrashed), (result.Outcome, result.Refusal)); // D-15.8
        Assert.Equal(before, _w.Dump());
    }
}
