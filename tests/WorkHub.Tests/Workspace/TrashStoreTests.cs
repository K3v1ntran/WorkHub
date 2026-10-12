using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Move to Trash, Trash listing and Restore through the real SQLite store, with a second independent service on the same
/// database for races (Phase 7 proposal revision 2 §4, §6, §10.2).
/// </summary>
public sealed class TrashStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly RecordingProcessStarter _starter = new();
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _other;
    private readonly long _alpha;
    private readonly long _beta;

    public TrashStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _content = NewService();
        _other = NewService();
        var workspace = new WorkspaceService(new SqliteWorkspaceStore(_connections));
        _alpha = workspace.CreateProjectAsync(null, new ProjectDraft("Alpha Portal", ProjectStatus.Active)).Result.Value!.Id;
        _beta = workspace.CreateProjectAsync(null, new ProjectDraft("Beta", ProjectStatus.Active)).Result.Value!.Id;
    }

    public void Dispose() => _root.Dispose();

    /// <summary>An independent service (another window or process). The launcher records instead of starting anything.</summary>
    private ProjectContentService NewService(SqliteProjectContentStore? store = null) =>
        new(store ?? new SqliteProjectContentStore(_connections), new ProcessProjectLinkLauncher(_starter, _ => true, _ => true));

    private async Task<ProjectFolder> Folder(string name, long? parent = null, long? project = null) =>
        (await _content.CreateFolderAsync(project ?? _alpha, parent, name)).Value!;

    private async Task<ProjectLink> Link(string name, long? folder = null, string target = @"C:\Work\out") =>
        (await _content.CreateLinkAsync(_alpha, folder, new ProjectLinkDraft(name, ProjectLinkType.Folder, target))).Value!;

    private async Task<ProjectNote> Note(string title, long? folder = null, string content = "body") =>
        (await _content.CreateNoteAsync(_alpha, folder, new ProjectNoteDraft(title, content))).Note!;

    private static ContentRef F(long id) => new(ContentKind.Folder, id);

    private static ContentRef L(long id) => new(ContentKind.Link, id);

    private static ContentRef N(long id) => new(ContentKind.Note, id);

    /// <summary>Every column except the marker, so Trash/Restore can be proven marker-only.</summary>
    private string RowWithoutMarker(string table, long id)
    {
        using var connection = _root.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return string.Join("|", Enumerable.Range(0, reader.FieldCount).Where(i => reader.GetName(i) != "TrashEntryId")
            .Select(i => $"{reader.GetName(i)}={(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i))}"));
    }

    private long? Marker(string table, long id)
    {
        using var connection = _root.Open();
        var text = Db.ScalarString(connection, $"SELECT CAST(TrashEntryId AS TEXT) FROM {table} WHERE Id = $id;", ("$id", id));
        return text is null ? null : long.Parse(text);
    }

    private long Entries()
    {
        using var connection = _root.Open();
        return Db.Count(connection, "ProjectTrashEntries");
    }

    // ---- Move to Trash ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TrashLink_And_TrashNote_OneEntryEach_MarkerOnly()
    {
        var link = await Link("Build output");
        var note = await Note("Kickoff");
        var linkBefore = RowWithoutMarker("ProjectLinks", link.Id);
        var noteBefore = RowWithoutMarker("ProjectNotes", note.Id);

        var trashedLink = await _content.TrashAsync(L(link.Id));
        var trashedNote = await _content.TrashAsync(N(note.Id));

        Assert.Equal(TrashOutcome.Trashed, trashedLink.Outcome);
        Assert.True(trashedLink.Committed);
        Assert.Null(trashedLink.Error);
        Assert.Equal(new TrashEntry(trashedLink.Entry!.Id, _alpha, L(link.Id), "Build output", null, 1, trashedLink.Entry.TrashedAt), trashedLink.Entry);
        Assert.Equal(1, trashedNote.Entry!.ItemCount);
        Assert.Equal(linkBefore, RowWithoutMarker("ProjectLinks", link.Id)); // no UpdatedAt, container or name change
        Assert.Equal(noteBefore, RowWithoutMarker("ProjectNotes", note.Id)); // Revision and UpdatedAt unchanged
        Assert.Equal(trashedLink.Entry.Id, Marker("ProjectLinks", link.Id));
        var listing = (await _content.ListAsync(_alpha, null)).Listing!;
        Assert.Empty(listing.Links);
        Assert.Empty(listing.Notes);
        Assert.Equal(2, listing.TrashCount);
        Assert.Empty(_starter.Started); // nothing on disk or in the shell is ever touched
    }

    [Fact]
    public async Task TrashFolder_TakesItsWholeActiveSubtree_AsOneEntry_WithTheActualCount()
    {
        var specs = await Folder("Specs");
        var drafts = await Folder("Drafts", specs.Id);
        var deep = await Folder("Deep", drafts.Id);
        var link = await Link("Spec", drafts.Id);
        var note = await Note("Deep note", deep.Id);
        var sibling = await Link("Outside");
        var before = RowWithoutMarker("ProjectFolders", drafts.Id);

        var result = await _content.TrashAsync(F(specs.Id));

        Assert.Equal(TrashOutcome.Trashed, result.Outcome);
        Assert.Equal(5, result.Entry!.ItemCount); // Specs, Drafts, Deep, the link and the note
        Assert.Equal("Specs", result.Entry.RootName);
        Assert.Null(result.Entry.RootContainerFolderId);
        foreach (var (table, id) in new[] { ("ProjectFolders", specs.Id), ("ProjectFolders", drafts.Id), ("ProjectFolders", deep.Id), ("ProjectLinks", link.Id), ("ProjectNotes", note.Id) })
            Assert.Equal(result.Entry.Id, Marker(table, id));
        Assert.Null(Marker("ProjectLinks", sibling.Id));
        Assert.Equal(before, RowWithoutMarker("ProjectFolders", drafts.Id)); // parent, name and UpdatedAt untouched
        Assert.Equal(["Outside"], (await _content.ListAsync(_alpha, null)).Listing!.Links.Select(l => l.Name));
    }

    [Fact]
    public async Task ATrashedNestedFolder_KeepsItsOwnEntry_AndIsNotCountedAgain()
    {
        var parent = await Folder("P");
        var inner = await Folder("X", parent.Id);
        await Note("inner note", inner.Id);
        await Link("parent link", parent.Id);

        var innerEntry = (await _content.TrashAsync(F(inner.Id))).Entry!;
        var parentEntry = (await _content.TrashAsync(F(parent.Id))).Entry!;

        Assert.Equal(2, innerEntry.ItemCount);
        Assert.Equal(2, parentEntry.ItemCount); // P and its link; X and its note stay in their own entry
        Assert.Equal(innerEntry.Id, Marker("ProjectFolders", inner.Id));

        var restored = await _content.RestoreAsync(parentEntry.Id);
        Assert.Equal(RestoreOutcome.Restored, restored.Outcome);
        Assert.Equal(innerEntry.Id, Marker("ProjectFolders", inner.Id)); // the nested entry is not restored implicitly
        Assert.Equal(["P"], (await _content.ListAsync(_alpha, null)).Listing!.Folders.Select(f => f.Name));
        Assert.Empty((await _content.ListAsync(_alpha, parent.Id)).Listing!.Folders);
        Assert.Equal(RestoreOutcome.Restored, (await _content.RestoreAsync(innerEntry.Id)).Outcome);
        Assert.Equal(["X"], (await _content.ListAsync(_alpha, parent.Id)).Listing!.Folders.Select(f => f.Name));
    }

    [Fact]
    public async Task AlreadyTrashed_AndNotFound_WriteNothing_AndKindsAreNeverMixed()
    {
        var note = await Note("N");
        var folder = await Folder("Same id space");
        Assert.Equal(note.Id, folder.Id); // independent Id spaces: the kind decides

        Assert.Equal(TrashOutcome.Trashed, (await _content.TrashAsync(N(note.Id))).Outcome);
        var again = await _content.TrashAsync(N(note.Id));
        Assert.Equal(TrashOutcome.AlreadyTrashed, again.Outcome);
        Assert.False(again.Committed);
        Assert.Equal("This item is already in the Trash.", again.Error);
        var missing = await _content.TrashAsync(L(note.Id));
        Assert.Equal(TrashOutcome.NotFound, missing.Outcome);
        Assert.Equal("This item no longer exists.", missing.Error);
        Assert.Equal(1, Entries());
        Assert.Null(Marker("ProjectFolders", folder.Id)); // the folder with the same numeric Id was not touched
    }

    [Fact]
    public async Task TheSubtreeWalk_FailsClosed_AtItsDepthBound()
    {
        var a = await Folder("A");
        var b = await Folder("B", a.Id);
        var c = await Folder("C", b.Id);
        await Folder("D", c.Id);
        var bounded = NewService(new SqliteProjectContentStore(_connections, null, 2));

        var result = await bounded.TrashAsync(F(a.Id));

        Assert.Equal(TrashOutcome.AncestryUnverified, result.Outcome);
        Assert.Equal(ProjectContentService.TrashFailed, result.Error);
        Assert.Equal(0, Entries());
        Assert.Null(Marker("ProjectFolders", a.Id));
        Assert.Equal(TrashOutcome.Trashed, (await bounded.TrashAsync(F(c.Id))).Outcome); // within the bound: fine
    }

    [Theory]
    [InlineData("trash:entry")]
    [InlineData("trash:folders")]
    [InlineData("trash:links")]
    public async Task AFailureInsideTrash_RollsBackTheEntryAndEveryMarker(string step)
    {
        var folder = await Folder("F");
        var link = await Link("L", folder.Id);
        var note = await Note("N", folder.Id);
        var store = new SqliteProjectContentStore(_connections) { OnWriteStep = s => { if (s == step) throw new IOException("injected"); } };

        await Assert.ThrowsAsync<IOException>(() => NewService(store).TrashAsync(F(folder.Id)));

        Assert.Equal(0, Entries());
        Assert.Null(Marker("ProjectFolders", folder.Id));
        Assert.Null(Marker("ProjectLinks", link.Id));
        Assert.Null(Marker("ProjectNotes", note.Id));
    }

    // ---- Changes between the confirmation count and the Trash (T11) ----------------------------------------------------

    [Fact]
    public async Task Count_IsASnapshot_OfTheActiveItems_IncludingTheRoot()
    {
        var folder = await Folder("F");
        await Link("L", folder.Id);
        var note = await Note("N");

        Assert.Equal(new TrashCount(true, false, 2), await _content.CountTrashSetAsync(F(folder.Id)));
        Assert.Equal(new TrashCount(true, false, 1), await _content.CountTrashSetAsync(N(note.Id)));
        Assert.Equal(new TrashCount(false, false, 0), await _content.CountTrashSetAsync(L(9999)));
        await _content.TrashAsync(N(note.Id));
        Assert.Equal(new TrashCount(true, true, 0), await _content.CountTrashSetAsync(N(note.Id)));
    }

    [Fact]
    public async Task ANoteAddedAfterTheCount_IsTrashedAndCounted()
    {
        var folder = await Folder("F");
        Assert.Equal(1, (await _content.CountTrashSetAsync(F(folder.Id))).ActiveItems);
        var added = (await _other.CreateNoteAsync(_alpha, folder.Id, new ProjectNoteDraft("Added", ""))).Note!;

        var result = await _content.TrashAsync(F(folder.Id));

        Assert.Equal(2, result.Entry!.ItemCount);
        Assert.Equal(result.Entry.Id, Marker("ProjectNotes", added.Id));
    }

    [Fact]
    public async Task AChildMovedOut_IsExcluded_AndALinkMovedIn_IsIncluded()
    {
        var folder = await Folder("F");
        var child = await Folder("Child", folder.Id);
        var outsideLink = await Link("Outside");
        Assert.Equal(2, (await _content.CountTrashSetAsync(F(folder.Id))).ActiveItems);

        Assert.True((await _other.MoveFolderAsync(child.Id, folder.Id, null)).Moved);
        Assert.True((await _other.MoveLinkAsync(outsideLink.Id, null, folder.Id)).Moved);
        var result = await _content.TrashAsync(F(folder.Id));

        Assert.Equal(2, result.Entry!.ItemCount); // F and the link, not Child
        Assert.Null(Marker("ProjectFolders", child.Id));
        Assert.Equal(result.Entry.Id, Marker("ProjectLinks", outsideLink.Id));
    }

    [Fact]
    public async Task ARootMovedAfterTheCount_IsTrashedInItsNewContainer_AndRestoredThere()
    {
        var destination = await Folder("Destination");
        var root = await Folder("Root");
        await _content.CountTrashSetAsync(F(root.Id));
        Assert.True((await _other.MoveFolderAsync(root.Id, null, destination.Id)).Moved);

        var result = await _content.TrashAsync(F(root.Id));

        Assert.Equal(destination.Id, result.Entry!.RootContainerFolderId);
        var restored = await _content.RestoreAsync(result.Entry.Id);
        Assert.Equal(destination.Id, restored.Restored!.ContainerFolderId);
        Assert.Equal("Alpha Portal › Destination", restored.Restored.ContainerPath);
        Assert.Equal(["Root"], (await _content.ListAsync(_alpha, destination.Id)).Listing!.Folders.Select(f => f.Name));
    }

    [Fact]
    public async Task ANoteEditedAfterTheCount_IsTrashedWithItsLatestRevision()
    {
        var folder = await Folder("F");
        var note = await Note("N", folder.Id, "v1");
        await _content.CountTrashSetAsync(F(folder.Id));
        Assert.True((await _other.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("N", "v2"))).Succeeded);

        await _content.TrashAsync(F(folder.Id));

        var stored = (await _content.GetNoteAsync(note.Id))!;
        Assert.True(stored.IsTrashed);
        Assert.Equal(2, stored.Revision);
        Assert.Equal("v2", stored.Content);
    }

    [Fact]
    public async Task PartOfTheSubtreeTrashedSeparatelyMeanwhile_StaysSeparate_AndIsNotCounted()
    {
        var folder = await Folder("F");
        var inner = await Link("Inner", folder.Id);
        Assert.Equal(2, (await _content.CountTrashSetAsync(F(folder.Id))).ActiveItems);
        var separate = (await _other.TrashAsync(L(inner.Id))).Entry!;

        var result = await _content.TrashAsync(F(folder.Id));

        Assert.Equal(1, result.Entry!.ItemCount);
        Assert.Equal(separate.Id, Marker("ProjectLinks", inner.Id));
        Assert.Equal(2, Entries());
    }

    [Fact]
    public async Task ARootTrashedByAnotherWindowMeanwhile_IsAlreadyTrashed_WithoutASecondEntry()
    {
        var folder = await Folder("F");
        await _content.CountTrashSetAsync(F(folder.Id));
        await _other.TrashAsync(F(folder.Id));

        var result = await _content.TrashAsync(F(folder.Id));

        Assert.Equal(TrashOutcome.AlreadyTrashed, result.Outcome);
        Assert.False(result.Committed);
        Assert.Equal(1, Entries());
    }

    // ---- Trash list -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListTrash_IsPerProject_NewestFirst_WithTheContainersCurrentPath()
    {
        var specs = await Folder("Specs");
        var archive = await Folder("Archive");
        var note = await Note("Old agenda", specs.Id);
        var link = await Link("Out");
        var betaFolder = await Folder("B", project: _beta);
        await _content.TrashAsync(N(note.Id));
        await _content.TrashAsync(L(link.Id));
        await _content.TrashAsync(F(betaFolder.Id));
        Assert.True((await _content.MoveFolderAsync(specs.Id, null, archive.Id)).Moved); // the container moved since

        var entries = await _content.ListTrashAsync(_alpha);

        Assert.Equal(["Out", "Old agenda"], entries.Select(e => e.RootName));
        Assert.Equal("Alpha Portal", entries[0].ContainerPath!.Display);
        Assert.Equal("Alpha Portal › Archive › Specs", entries[1].ContainerPath!.Display);
        Assert.All(entries, e => Assert.False(e.ContainerTrashed));
        Assert.Equal([ContentKind.Link, ContentKind.Note], entries.Select(e => e.Root.Kind));
        Assert.Single(await _content.ListTrashAsync(_beta));
    }

    // ---- Restore --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Restore_ClearsOnlyTheMarkers_DeletesTheEntry_AndReturnsTheCommittedTypedRow()
    {
        var specs = await Folder("Specs");
        var link = await Link("Spec", specs.Id);
        var note = await Note("Kickoff", specs.Id);
        var rows = new[] { ("ProjectFolders", specs.Id), ("ProjectLinks", link.Id), ("ProjectNotes", note.Id) };
        var before = rows.Select(r => RowWithoutMarker(r.Item1, r.Item2)).ToList();
        var entry = (await _content.TrashAsync(F(specs.Id))).Entry!;

        var result = await _content.RestoreAsync(entry.Id);

        Assert.Equal(RestoreOutcome.Restored, result.Outcome);
        Assert.True(result.Committed);
        Assert.Equal(F(specs.Id), result.Restored!.Root);
        Assert.Equal(_alpha, result.Restored.ProjectNodeId);
        Assert.Null(result.Restored.ContainerFolderId);
        Assert.Equal("Alpha Portal", result.Restored.ContainerPath);
        Assert.Equal(specs with { }, result.Restored.Folder);
        Assert.Null(result.Restored.Link);
        Assert.Null(result.Restored.Note);
        Assert.Equal(before, rows.Select(r => RowWithoutMarker(r.Item1, r.Item2)).ToList()); // names, containers, Revision, UpdatedAt
        Assert.All(rows, r => Assert.Null(Marker(r.Item1, r.Item2)));
        Assert.Equal(0, Entries());

        var noteEntry = (await _content.TrashAsync(N(note.Id))).Entry!;
        var restoredNote = (await _content.RestoreAsync(noteEntry.Id)).Restored!;
        Assert.Equal(N(note.Id), restoredNote.Root);
        Assert.Equal(new ProjectNoteSummary(note.Id, _alpha, specs.Id, "Kickoff", 1, note.UpdatedAt), restoredNote.Note);
        Assert.Equal("Alpha Portal › Specs", restoredNote.ContainerPath);
    }

    [Fact]
    public async Task RestoreTwice_TheSecondFindsNoEntry()
    {
        var entry = (await _content.TrashAsync(L((await Link("L")).Id))).Entry!;

        Assert.Equal(RestoreOutcome.Restored, (await _content.RestoreAsync(entry.Id)).Outcome);
        var second = await _other.RestoreAsync(entry.Id);

        Assert.Equal(RestoreOutcome.EntryNotFound, second.Outcome);
        Assert.False(second.Committed);
        Assert.NotNull(second.Error);
    }

    [Fact]
    public async Task RestoreIntoATrashedContainer_IsRefused_NamingTheBlocker_AndChangesNothing()
    {
        var drafts = await Folder("Drafts");
        var note = await Note("N", drafts.Id);
        var noteEntry = (await _content.TrashAsync(N(note.Id))).Entry!;
        await _content.TrashAsync(F(drafts.Id));

        var result = await _content.RestoreAsync(noteEntry.Id);

        Assert.Equal(RestoreOutcome.ContainerTrashed, result.Outcome);
        Assert.Equal("Drafts", result.BlockingName);
        Assert.Equal("The folder it came from is in the Trash. Restore “Drafts” first.", result.Error);
        Assert.Equal(noteEntry.Id, Marker("ProjectNotes", note.Id));
        Assert.Equal(2, Entries());
        Assert.True((await _content.ListTrashAsync(_alpha)).Single(e => e.Id == noteEntry.Id).ContainerTrashed);
    }

    [Fact]
    public async Task RestoreIntoAContainerThatNoLongerExists_IsRefused_WithoutARootFallback()
    {
        var gone = await Folder("Gone");
        var link = await Link("L", gone.Id);
        var entry = (await _content.TrashAsync(L(link.Id))).Entry!;
        using (var connection = new SqliteConnection($"Data Source={_root.Paths.DatabaseFile};Pooling=False"))
        {
            // Only an external edit can do this (every FK is RESTRICT and nothing is ever permanently deleted).
            connection.Open();
            Db.Exec(connection, "PRAGMA foreign_keys = OFF;");
            Db.Exec(connection, "DELETE FROM ProjectFolders WHERE Id = $id;", ("$id", gone.Id));
        }

        var result = await _content.RestoreAsync(entry.Id);

        Assert.Equal(RestoreOutcome.ContainerMissing, result.Outcome);
        Assert.Equal(entry.Id, Marker("ProjectLinks", link.Id));
        Assert.Null((await _content.ListTrashAsync(_alpha)).Single().ContainerPath);
    }

    [Fact]
    public async Task AFolderNameConflict_RefusesRestore_NothingRenamed_DuplicateLinkAndNoteNamesAreFine()
    {
        var specs = await Folder("Specs");
        var folderEntry = (await _content.TrashAsync(F(specs.Id))).Entry!;
        var link = await Link("Same");
        var note = await Note("Same");
        var linkEntry = (await _content.TrashAsync(L(link.Id))).Entry!;
        var noteEntry = (await _content.TrashAsync(N(note.Id))).Entry!;
        await Folder("SPECS"); // trashed folders reserve no names
        await Link("Same");
        await Note("Same");

        var conflict = await _content.RestoreAsync(folderEntry.Id);

        Assert.Equal(RestoreOutcome.NameConflict, conflict.Outcome);
        Assert.Equal(folderEntry.Id, Marker("ProjectFolders", specs.Id));
        Assert.Equal("Specs", (await _content.ListTrashAsync(_alpha)).Single(e => e.Id == folderEntry.Id).RootName);
        Assert.Equal(RestoreOutcome.Restored, (await _content.RestoreAsync(linkEntry.Id)).Outcome);
        Assert.Equal(RestoreOutcome.Restored, (await _content.RestoreAsync(noteEntry.Id)).Outcome);
    }

    [Theory]
    [InlineData("restore:folders")]
    [InlineData("restore:links")]
    [InlineData("restore:notes")]
    public async Task AFailureInsideRestore_LeavesEverythingTrashed_AndKeepsTheEntry(string step)
    {
        var folder = await Folder("F");
        var link = await Link("L", folder.Id);
        var note = await Note("N", folder.Id);
        var entry = (await _content.TrashAsync(F(folder.Id))).Entry!;
        var store = new SqliteProjectContentStore(_connections) { OnWriteStep = s => { if (s == step) throw new IOException("injected"); } };

        await Assert.ThrowsAsync<IOException>(() => NewService(store).RestoreAsync(entry.Id));

        Assert.Equal(entry.Id, Marker("ProjectFolders", folder.Id));
        Assert.Equal(entry.Id, Marker("ProjectLinks", link.Id));
        Assert.Equal(entry.Id, Marker("ProjectNotes", note.Id));
        Assert.Equal(1, Entries());
    }

    // ---- Existing operations against trashed items and containers (§4.5) -----------------------------------------------

    [Fact]
    public async Task ListingATrashedFolder_IsFolderTrashed_AndCreatesIntoItAreRefused()
    {
        var folder = await Folder("F");
        await _content.TrashAsync(F(folder.Id));

        Assert.Equal(ContentContainerProblem.FolderTrashed, (await _content.ListAsync(_alpha, folder.Id)).Problem);
        Assert.Equal("The destination folder is in the Trash.", (await _content.CreateFolderAsync(_alpha, folder.Id, "X")).Error);
        Assert.Equal("The destination folder is in the Trash.", (await _content.CreateLinkAsync(_alpha, folder.Id, new ProjectLinkDraft("X", ProjectLinkType.Web, "https://x.test/"))).Error);
        var note = await _content.CreateNoteAsync(_alpha, folder.Id, new ProjectNoteDraft("X", ""));
        Assert.Equal(NoteWriteStatus.LocationMissing, note.Status);
        Assert.Equal("The folder for this note is in the Trash. Your text is unchanged here.", note.Error);
        Assert.Equal(1, (await _content.ListTrashAsync(_alpha)).Single().ItemCount);
    }

    [Fact]
    public async Task RenameAndEdit_OfTrashedItems_AreRefused_AndChangeNothing()
    {
        var folder = await Folder("F");
        var link = await Link("L");
        await _content.TrashAsync(F(folder.Id));
        await _content.TrashAsync(L(link.Id));
        var folderBefore = RowWithoutMarker("ProjectFolders", folder.Id);

        var rename = await _content.RenameFolderAsync(folder.Id, "Renamed");
        var edit = await _content.UpdateLinkAsync(link.Id, new ProjectLinkDraft("L2", ProjectLinkType.Web, "https://y.test/"));

        Assert.Equal("This item is in the Trash.", rename.Error);
        Assert.Equal("This item is in the Trash.", edit.Error);
        Assert.Equal(folderBefore, RowWithoutMarker("ProjectFolders", folder.Id));
        Assert.Equal("This folder no longer exists.", (await _content.RenameFolderAsync(9999, "X")).Error); // unchanged
    }

    [Fact]
    public async Task Moves_RefuseTrashedItemsAndTrashedDestinations_AndTargetsExcludeTrashedFolders()
    {
        var trashedFolder = await Folder("Trashed");
        var inside = await Folder("Inside", trashedFolder.Id);
        var active = await Folder("Active");
        var link = await Link("L");
        var note = await Note("N");
        var trashedLink = await Link("TL");
        await _content.TrashAsync(F(trashedFolder.Id));
        await _content.TrashAsync(L(trashedLink.Id));

        Assert.Equal(ContentMoveOutcome.DestinationTrashed, (await _content.MoveLinkAsync(link.Id, null, trashedFolder.Id)).Outcome);
        Assert.Equal(ContentMoveOutcome.DestinationTrashed, (await _content.MoveNoteAsync(note.Id, null, trashedFolder.Id)).Outcome);
        Assert.Equal(ContentMoveOutcome.DestinationTrashed, (await _content.MoveFolderAsync(active.Id, null, inside.Id)).Outcome);
        var refused = await _content.MoveLinkAsync(trashedLink.Id, null, active.Id);
        Assert.Equal(ContentMoveOutcome.ItemTrashed, refused.Outcome);
        Assert.Equal("This item is in the Trash.", refused.Error);
        Assert.Equal(ContentMoveOutcome.ItemTrashed, (await _content.MoveFolderAsync(inside.Id, trashedFolder.Id, null)).Outcome);
        Assert.Equal(["Active"], (await _content.ListFolderMoveTargetsAsync(_alpha, null)).Select(t => t.Path));
        Assert.True((await _content.MoveLinkAsync(link.Id, null, active.Id)).Moved); // active moves are unchanged
    }

    [Fact]
    public async Task NoteSave_OnATrashedNote_IsTrashed_KeepsTheDraft_AndAfterRestoreSavesWithoutAConflict()
    {
        var note = await Note("N", content: "v1");
        var entry = (await _other.TrashAsync(N(note.Id))).Entry!;

        var save = await _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("N", "my draft"));

        Assert.Equal(NoteWriteStatus.Trashed, save.Status);
        Assert.True(save.OffersRecovery);
        Assert.Equal("This note is in the Trash. Your text is unchanged here.", save.Error);
        Assert.Equal("v1", (await _content.GetNoteAsync(note.Id))!.Content);

        await _other.RestoreAsync(entry.Id);
        var after = await _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("N", "my draft"));
        Assert.Equal(NoteWriteStatus.Saved, after.Status); // Restore did not change the Revision
        Assert.Equal(2, after.Note!.Revision);
    }

    [Fact]
    public async Task SaveAsNew_ForATrashedNote_GoesToItsContainerOnlyIfThatIsActive()
    {
        var active = await Folder("Active");
        var alone = await Note("Alone", active.Id);
        await _content.TrashAsync(N(alone.Id)); // the note itself is trashed; its folder is active
        var withFolder = await Folder("Doomed");
        var inside = await Note("Inside", withFolder.Id);
        await _content.TrashAsync(F(withFolder.Id)); // trashed together with its folder

        var next = await _content.SaveNoteAsNewAsync(alone.Id, _alpha, active.Id, new ProjectNoteDraft("Copy", "text"));
        var refused = await _content.SaveNoteAsNewAsync(inside.Id, _alpha, withFolder.Id, new ProjectNoteDraft("Copy", "text"));

        Assert.Equal(NoteWriteStatus.Saved, next.Status);
        Assert.Equal(active.Id, next.Note!.FolderId);
        Assert.Equal(NoteWriteStatus.LocationMissing, refused.Status);
        Assert.Equal("The note's location is in the Trash. Copy your text instead.", refused.Error);
        Assert.Equal(2, (await _content.ListTrashAsync(_alpha)).Count); // nothing restored implicitly
    }

    [Fact]
    public async Task SaveAsNew_ForAMissingOriginal_RefusesATrashedLastKnownFolder()
    {
        var folder = await Folder("F");
        await _content.TrashAsync(F(folder.Id));

        var result = await _content.SaveNoteAsNewAsync(9999, _alpha, folder.Id, new ProjectNoteDraft("Copy", "text"));

        Assert.Equal("The note's location is in the Trash. Copy your text instead.", result.Error);
    }

    [Fact]
    public async Task SingleReads_ReportTrashedRowsDistinctly_AndOpenNeverLaunchesATrashedLink()
    {
        var note = await Note("N");
        var link = await Link("L", target: @"C:\Work\exists");
        await _content.TrashAsync(N(note.Id));
        await _content.TrashAsync(L(link.Id));

        Assert.True((await _content.GetNoteAsync(note.Id))!.IsTrashed);
        Assert.True((await _content.GetNotesAsync([note.Id])).Single().IsTrashed);
        Assert.True((await _content.GetLinkAsync(link.Id))!.IsTrashed);
        var open = await _content.OpenLinkAsync(link.Id);
        Assert.Equal(ProjectLinkOpenProblem.LinkTrashed, open.Problem);
        Assert.Equal("This link is in the Trash.", open.Message);
        Assert.Empty(_starter.Started);
        Assert.Equal("Alpha Portal", (await _content.GetContainerPathAsync(_alpha, null))!.Display);
    }

    // ---- Races (two services on one database) -----------------------------------------------------------------------------

    [Fact]
    public async Task TrashTrash_OneWins_OneEntry()
    {
        var folder = await Folder("F");
        await Note("N", folder.Id);

        var results = await Task.WhenAll(_content.TrashAsync(F(folder.Id)), _other.TrashAsync(F(folder.Id)));

        Assert.Single(results, r => r.Outcome == TrashOutcome.Trashed);
        Assert.Single(results, r => r.Outcome == TrashOutcome.AlreadyTrashed);
        Assert.Equal(1, Entries());
    }

    [Fact]
    public async Task RestoreRestore_OneWins()
    {
        var entry = (await _content.TrashAsync(F((await Folder("F")).Id))).Entry!;

        var results = await Task.WhenAll(_content.RestoreAsync(entry.Id), _other.RestoreAsync(entry.Id));

        Assert.Single(results, r => r.Outcome == RestoreOutcome.Restored);
        Assert.Single(results, r => r.Outcome == RestoreOutcome.EntryNotFound);
    }

    [Fact]
    public async Task TrashAndMove_InBothOrders()
    {
        var destination = await Folder("Destination");
        var first = await Link("First");
        Assert.True((await _other.MoveLinkAsync(first.Id, null, destination.Id)).Moved);
        Assert.Equal(destination.Id, (await _content.TrashAsync(L(first.Id))).Entry!.RootContainerFolderId); // trashed in its new container

        var second = await Link("Second");
        await _content.TrashAsync(L(second.Id));
        Assert.Equal(ContentMoveOutcome.ItemTrashed, (await _other.MoveLinkAsync(second.Id, null, destination.Id)).Outcome);
        var target = await Folder("Target");
        await _content.TrashAsync(F(target.Id));
        var third = await Link("Third");
        Assert.Equal(ContentMoveOutcome.DestinationTrashed, (await _other.MoveLinkAsync(third.Id, null, target.Id)).Outcome);
    }

    [Fact]
    public async Task TrashAndSave_InBothOrders()
    {
        var a = await Note("A", content: "v1");
        Assert.True((await _other.SaveNoteAsync(a.Id, 1, new ProjectNoteDraft("A", "saved first"))).Succeeded);
        await _content.TrashAsync(N(a.Id));
        Assert.Equal("saved first", (await _content.GetNoteAsync(a.Id))!.Content); // the saved revision was trashed

        var b = await Note("B", content: "v1");
        await _content.TrashAsync(N(b.Id));
        Assert.Equal(NoteWriteStatus.Trashed, (await _other.SaveNoteAsync(b.Id, 1, new ProjectNoteDraft("B", "late"))).Status);
    }

    [Fact]
    public async Task RestoreAndCreateOfTheSameName_FirstCommitWins_NothingRenamed()
    {
        var specs = await Folder("Specs");
        var entry = (await _content.TrashAsync(F(specs.Id))).Entry!;

        var restoring = _content.RestoreAsync(entry.Id);
        var creating = _other.CreateFolderAsync(_alpha, null, "specs");
        var restore = await restoring;
        var create = await creating;

        var restored = restore.Outcome == RestoreOutcome.Restored;
        Assert.NotEqual(restored, create.Succeeded); // exactly one of them
        if (!restored)
            Assert.Equal(RestoreOutcome.NameConflict, restore.Outcome);
        var names = (await _content.ListAsync(_alpha, null)).Listing!.Folders.Select(f => f.Name).ToList();
        Assert.Single(names);
        Assert.Contains(names.Single(), new[] { "Specs", "specs" });
    }

    [Fact]
    public async Task GetContentState_ReportsTheCurrentContainerAndTrashStateByKindAndId()
    {
        var specs = await Folder("Specs");
        var link = await Link("Portal", specs.Id);
        var note = await Note("Kickoff");

        Assert.Equal(new ContentState(L(link.Id), _alpha, specs.Id, false), await _content.GetContentStateAsync(L(link.Id)));
        Assert.Equal(new ContentState(N(note.Id), _alpha, null, false), await _content.GetContentStateAsync(N(note.Id)));
        Assert.Null(await _content.GetContentStateAsync(N(link.Id + note.Id + 100))); // no such row
        var before = RowWithoutMarker("ProjectLinks", link.Id);

        Assert.Equal(TrashOutcome.Trashed, (await _other.TrashAsync(F(specs.Id))).Outcome);
        Assert.Equal(new ContentState(F(specs.Id), _alpha, null, true), await _content.GetContentStateAsync(F(specs.Id)));
        Assert.Equal(new ContentState(L(link.Id), _alpha, specs.Id, true), await _content.GetContentStateAsync(L(link.Id))); // trashed with its folder
        Assert.Equal(before, RowWithoutMarker("ProjectLinks", link.Id)); // a read only
    }
}
