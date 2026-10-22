using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Project Archive, state-based Unarchive and event-specific Undo through the real SQLite stores, with independent services
/// on the same database standing in for other windows (Phase 12 proposal revision 3 §5.4, §6, §7, §11.2).
/// </summary>
public sealed class ProjectArchiveStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly RecordingProcessStarter _starter = new();
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceService _otherWorkspace;
    private readonly ProjectContentService _content;
    private readonly long _clients;
    private readonly long _tokyo;
    private readonly long _alpha;

    public ProjectArchiveStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(_connections));
        _otherWorkspace = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _content = Content(_connections);
        _clients = Create(null, "Clients", folder: true);
        _tokyo = Create(_clients, "東京支社", folder: true);
        _alpha = Create(_tokyo, "Alpha Portal");
    }

    public void Dispose() => _root.Dispose();

    private ProjectContentService Content(SqliteConnectionFactory factory) =>
        new(new SqliteProjectContentStore(factory), new ProcessProjectLinkLauncher(_starter, _ => true, _ => true));

    private WorkspaceService Workspace(SqliteConnectionFactory factory) => new(new SqliteWorkspaceStore(factory));

    private long Create(long? parent, string name, bool folder = false, ProjectStatus status = ProjectStatus.Active) => folder
        ? _workspace.CreateFolderAsync(parent, name).Result.Value!.Id
        : _workspace.CreateProjectAsync(parent, new ProjectDraft(name, status, "desc", "Owner A")).Result.Value!.Id;

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private (string Status, long Serial) Row(long nodeId)
    {
        using var connection = _root.Open();
        var text = Db.ScalarString(connection, "SELECT Status || '|' || ArchiveSerial FROM Projects WHERE NodeId = $id;", ("$id", nodeId))!.Split('|');
        return (text[0], long.Parse(text[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<string> Children(long? parent) =>
        string.Join(",", (await _workspace.ListChildrenAsync(parent)).Nodes.Select(n => $"{n.Name}{(n.HasChildren ? "+" : string.Empty)}"));

    private void Raw(string sql, long id)
    {
        using var connection = _root.Open();
        Db.Exec(connection, sql, ("$id", id));
    }

    // ---- Archive ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ProjectStatus.Planning)]
    [InlineData(ProjectStatus.Active)]
    [InlineData(ProjectStatus.Maintenance)]
    [InlineData(ProjectStatus.OnHold)]
    [InlineData(ProjectStatus.Completed)]
    public async Task Archive_ChangesOnlyStatusAndSerial_AndReturnsTheCommittedEvent(ProjectStatus previous)
    {
        var project = Create(_tokyo, $"P-{previous}", status: previous);
        var note = (await _content.CreateNoteAsync(project, null, new ProjectNoteDraft("N", "text"))).Note!.Id;
        var before = Data();

        var result = await _workspace.ArchiveProjectAsync(project);

        Assert.Equal(ProjectArchiveOutcome.Archived, result.Outcome);
        Assert.Equal(new ArchiveEvent(project, 1, previous), result.Event);
        Assert.Equal((project, $"P-{previous}", (long?)_tokyo, "Clients / 東京支社"), (result.Entry!.NodeId, result.Entry.Name, result.Entry.ParentId, result.Entry.ParentPath));
        var after = Data();
        var changed = after.Except(before).ToList();
        Assert.Equal(before.Except(after).Count(), changed.Count);
        var row = Assert.Single(changed); // exactly one row changed: this Project's details
        Assert.StartsWith($"Projects|NodeId={project}|", row);
        Assert.Contains("|Status=Archived|", row);
        Assert.EndsWith("|ArchiveSerial=1", row);
        var old = before.Except(after).Single();
        Assert.Equal(old.Replace($"|Status={ProjectStatuses.StorageCode(previous)}|", "|Status=Archived|").Replace("|ArchiveSerial=0", "|ArchiveSerial=1"), row);
        Assert.Equal("text", (await _content.GetNoteAsync(note))!.Content);
    }

    [Fact]
    public async Task Archive_Refusals_WriteNothing()
    {
        var trashed = Create(null, "Trashed");
        await _workspace.TrashProjectAsync(trashed);
        await _workspace.ArchiveProjectAsync(_alpha);
        var before = Data();

        Assert.Equal(ProjectArchiveOutcome.NotFound, (await _workspace.ArchiveProjectAsync(999)).Outcome);
        Assert.Equal(ProjectArchiveOutcome.NotAProject, (await _workspace.ArchiveProjectAsync(_tokyo)).Outcome);
        Assert.Equal(ProjectArchiveOutcome.AlreadyArchived, (await _otherWorkspace.ArchiveProjectAsync(_alpha)).Outcome);
        Assert.Equal(ProjectArchiveOutcome.ProjectTrashed, (await _workspace.ArchiveProjectAsync(trashed)).Outcome);

        Assert.Equal(before, Data());
        Assert.Null((await _otherWorkspace.ArchiveProjectAsync(_alpha)).Event);
    }

    // ---- Unarchive (state-based) -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ProjectStatus.Planning)]
    [InlineData(ProjectStatus.Active)]
    [InlineData(ProjectStatus.Maintenance)]
    [InlineData(ProjectStatus.OnHold)]
    [InlineData(ProjectStatus.Completed)]
    public async Task Unarchive_SetsTheChosenStatus_KeepsTheSerial_AndReturnsThePlacement(ProjectStatus status)
    {
        await _workspace.ArchiveProjectAsync(_alpha);
        var before = Data();

        var result = await _workspace.UnarchiveProjectAsync(_alpha, status);

        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, result.Outcome);
        Assert.Equal(new WorkspaceNode(_alpha, _tokyo, WorkspaceNodeType.Project, "Alpha Portal", false), result.Unarchived!.Node);
        Assert.Equal("Clients / 東京支社", result.Unarchived.ParentPath);
        Assert.Equal((ProjectStatuses.StorageCode(status), 1L), Row(_alpha));
        Assert.Single(Data().Except(before)); // the details row only
    }

    [Fact]
    public async Task Unarchive_Refusals_WriteNothing_AndANonEditableTargetIsNeverSent()
    {
        var trashed = Create(null, "Trashed");
        Raw("UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;", trashed);
        Raw("UPDATE WorkspaceNodes SET TrashedAt = '2026-01-01T00:00:00.000Z' WHERE Id = $id;", trashed); // an earlier build's state
        var before = Data();

        Assert.Equal(ProjectUnarchiveOutcome.NotFound, (await _workspace.UnarchiveProjectAsync(999, ProjectStatus.Active)).Outcome);
        Assert.Equal(ProjectUnarchiveOutcome.NotAProject, (await _workspace.UnarchiveProjectAsync(_tokyo, ProjectStatus.Active)).Outcome);
        Assert.Equal(ProjectUnarchiveOutcome.NotArchived, (await _workspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Active)).Outcome);
        Assert.Equal(ProjectUnarchiveOutcome.ProjectTrashed, (await _workspace.UnarchiveProjectAsync(trashed, ProjectStatus.Active)).Outcome);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _workspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Archived));

        Assert.Equal(before, Data());
    }

    // ---- Undo (event-specific) --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Undo_RestoresTheExactPreviousStatus_KeepsTheSerial_AndOnlyOnce()
    {
        var onHold = Create(_tokyo, "On hold", status: ProjectStatus.OnHold);
        var archived = await _workspace.ArchiveProjectAsync(onHold);

        var undo = await _workspace.UndoArchiveAsync(archived.Event!.Value);

        Assert.Equal(ArchiveUndoOutcome.Undone, undo.Outcome);
        Assert.Equal(_tokyo, undo.Restored!.Node.ParentId);
        Assert.Equal(("OnHold", 1L), Row(onHold));
        var before = Data();
        Assert.Equal(ArchiveUndoOutcome.NotArchived, (await _otherWorkspace.UndoArchiveAsync(archived.Event.Value)).Outcome); // a second Undo
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task Undo_AfterAnotherWindowUnarchived_IsNotArchived_AndWritesNothing()
    {
        var archived = (await _workspace.ArchiveProjectAsync(_alpha)).Event!.Value;
        await _otherWorkspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Completed);
        var before = Data();

        Assert.Equal(ArchiveUndoOutcome.NotArchived, (await _workspace.UndoArchiveAsync(archived)).Outcome);

        Assert.Equal(before, Data());
        Assert.Equal(("Completed", 1L), Row(_alpha));
    }

    /// <summary>The ABA sequence (§1.3): a status check alone could not tell the two archive events apart; the serial does.</summary>
    [Fact]
    public async Task Undo_AfterAnotherWindowUnarchivedAndArchivedAgain_IsArchivedAgain_AndWritesNothing()
    {
        var a = (await _workspace.ArchiveProjectAsync(_alpha)).Event!.Value;     // window A, from Active (serial 1)
        await _otherWorkspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Completed); // window B (serial stays 1)
        var b = (await _otherWorkspace.ArchiveProjectAsync(_alpha)).Event!.Value; // window B again (serial 2)
        Assert.Equal((1L, 2L, ProjectStatus.Active, ProjectStatus.Completed), (a.Serial, b.Serial, a.PreviousStatus, b.PreviousStatus));
        var before = Data();

        Assert.Equal(ArchiveUndoOutcome.ArchivedAgain, (await _workspace.UndoArchiveAsync(a)).Outcome);

        Assert.Equal(before, Data());
        Assert.Equal(("Archived", 2L), Row(_alpha));
        Assert.Equal(ArchiveUndoOutcome.Undone, (await _otherWorkspace.UndoArchiveAsync(b)).Outcome); // B's own Undo still works
        Assert.Equal(("Completed", 2L), Row(_alpha));
    }

    [Fact]
    public async Task Undo_NoOpPaths_WriteNothing_AndInvalidTokensAreNeverSent()
    {
        var archived = (await _workspace.ArchiveProjectAsync(_alpha)).Event!.Value;
        Raw("UPDATE WorkspaceNodes SET TrashedAt = '2026-01-01T00:00:00.000Z' WHERE Id = $id;", _alpha); // trashed by an earlier build
        var before = Data();

        Assert.Equal(ArchiveUndoOutcome.ProjectTrashed, (await _workspace.UndoArchiveAsync(archived)).Outcome);
        Assert.Equal(ArchiveUndoOutcome.NotFound, (await _workspace.UndoArchiveAsync(archived with { NodeId = 999 })).Outcome);
        Assert.Equal(ArchiveUndoOutcome.NotFound, (await _workspace.UndoArchiveAsync(archived with { NodeId = _tokyo })).Outcome);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _workspace.UndoArchiveAsync(archived with { Serial = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _workspace.UndoArchiveAsync(archived with { PreviousStatus = ProjectStatus.Archived }));

        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task AnUpgradedArchivedRow_SerialZero_IsNeverMatchedByAnUndoToken()
    {
        var legacy = Create(null, "Legacy");
        using (var connection = _root.Open())
            Db.Exec(connection, "UPDATE Projects SET Status = 'Archived', ArchiveSerial = 1 WHERE NodeId = $id;", ("$id", legacy));
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _workspace.UnarchiveProjectAsync(legacy, ProjectStatus.Active)).Outcome);
        Assert.Equal(1L, Row(legacy).Serial); // only a v8 Archive ever raises it

        var v7Row = Create(null, "From v7"); // a v7 row upgraded with serial 0 and archived by an earlier build
        Raw("UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;", v7Row);
        Assert.Equal(ArchiveUndoOutcome.ArchivedAgain, (await _workspace.UndoArchiveAsync(new ArchiveEvent(v7Row, 2, ProjectStatus.Active))).Outcome);
    }

    // ---- Visibility, names and archived-state refusals ------------------------------------------------------------------

    [Fact]
    public async Task ArchivedProjects_AreHiddenFromTheTree_AndHasChildrenFollows()
    {
        Assert.Equal("Alpha Portal", await Children(_tokyo));
        Assert.Equal("東京支社+", await Children(_clients));

        await _workspace.ArchiveProjectAsync(_alpha);
        Assert.Equal(string.Empty, await Children(_tokyo));
        Assert.Equal("東京支社", await Children(_clients)); // a folder with only archived Projects has no expander

        await _workspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Completed);
        Assert.Equal("Alpha Portal", await Children(_tokyo));
        Assert.Equal("東京支社+", await Children(_clients));
    }

    [Fact]
    public async Task AnArchivedSibling_KeepsItsName_AndTheRefusalNamesIt()
    {
        var other = Create(_clients, "Elsewhere");
        var folder = Create(_tokyo, "Specs", folder: true);
        await _workspace.ArchiveProjectAsync(_alpha);
        var before = Data();

        var create = await _workspace.CreateProjectAsync(_tokyo, new ProjectDraft("ALPHA PORTAL", ProjectStatus.Active));
        Assert.Equal(WorkspaceFailure.DuplicateName, create.Failure);
        Assert.Equal("An archived project named 'ALPHA PORTAL' already exists here. Unarchive it or choose another name.", create.Error);
        Assert.Equal(WorkspaceFailure.DuplicateName, (await _workspace.CreateFolderAsync(_tokyo, "Alpha Portal")).Failure);
        Assert.Contains("archived project", (await _workspace.RenameAsync(folder, "alpha portal")).Error);
        await _workspace.RenameAsync(other, "Alpha Portal");
        var move = await _workspace.MoveAsync(other, _tokyo);
        Assert.Equal("The destination already contains an archived project named 'Alpha Portal'. Unarchive or rename it first.", move.Error);
        Assert.Equal(WorkspaceFailure.DuplicateName, move.Failure);
        Assert.Equal(before.Count, Data().Count);

        // An ACTIVE blocker keeps the ordinary message.
        Assert.Equal("A folder or project named 'specs' already exists here (names are compared ignoring case and width).",
            (await _workspace.CreateFolderAsync(_tokyo, "specs")).Error);
        // Unarchive never conflicts: the name stayed reserved while archived.
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _workspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Active)).Outcome);
    }

    [Fact]
    public async Task NodeWritesOnAnArchivedProject_AreRefused_AndWriteNothing()
    {
        await _workspace.ArchiveProjectAsync(_alpha);
        var before = Data();

        var rename = await _otherWorkspace.RenameAsync(_alpha, "Renamed");
        var update = await _otherWorkspace.UpdateProjectAsync(_alpha, new ProjectDraft("Alpha Portal", ProjectStatus.Completed, "desc", "Owner A"));
        var move = await _otherWorkspace.MoveAsync(_alpha, _clients);
        var trash = await _otherWorkspace.TrashProjectAsync(_alpha);

        Assert.Equal((WorkspaceFailure.ProjectArchived, WorkspaceService.ProjectIsArchived), (rename.Failure, rename.Error));
        Assert.Equal(WorkspaceFailure.ProjectArchived, update.Failure); // a stale editor never silently unarchives
        Assert.Equal(WorkspaceFailure.ProjectArchived, move.Failure);
        Assert.Equal(ProjectTrashOutcome.Archived, trash.Outcome);
        Assert.Equal(before, Data());

        // The editor still cannot CHOOSE Archived (unchanged contract).
        var other = Create(null, "Other");
        Assert.Equal(ProjectDraftValidator.Fields.Status, (await _workspace.UpdateProjectAsync(other, new ProjectDraft("Other", ProjectStatus.Archived))).Field);
    }

    [Fact]
    public async Task RemoveFolder_WithArchivedProjects_IsNotEmpty_AndCountsThem()
    {
        await _workspace.ArchiveProjectAsync(_alpha);
        var before = Data();

        Assert.Equal(new FolderRemoveResult(FolderRemoveOutcome.NotEmpty, 0, 0) { ArchivedProjects = 1 }, await _workspace.RemoveEmptyFolderAsync(_tokyo));
        Assert.Equal(before, Data());
        var state = await _workspace.GetNodeStateAsync(_tokyo);
        Assert.Equal((0, 1, 0), (state!.ActiveChildren, state.ArchivedChildren, state.TrashedChildren));
        Assert.True((await _workspace.GetNodeStateAsync(_alpha))!.Archived);

        await _workspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Active);
        Assert.True((await _workspace.MoveAsync(_alpha, _clients)).Succeeded);
        Assert.Equal(FolderRemoveOutcome.Removed, (await _workspace.RemoveEmptyFolderAsync(_tokyo)).Outcome);
    }

    /// <summary>A3 (user-selected): archived content stays writable — existing editors and other windows keep working.</summary>
    [Fact]
    public async Task ContentOfAnArchivedProject_StaysWritable()
    {
        var note = (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("Draft", "v1"))).Note!;
        await _workspace.ArchiveProjectAsync(_alpha);

        Assert.True((await _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Draft", "saved after archive"))).Succeeded);
        Assert.True((await _content.SaveNoteAsNewAsync(note.Id, _alpha, null, new ProjectNoteDraft("Copy", "x"))).Succeeded);
        var folder = (await _content.CreateFolderAsync(_alpha, null, "Specs")).Value!.Id;
        Assert.True((await _content.CreateLinkAsync(_alpha, folder, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://l.example.test/"))).Succeeded);
        Assert.True((await _content.MoveNoteAsync(note.Id, null, folder)).Succeeded);
        Assert.Equal(TrashOutcome.Trashed, (await _content.TrashAsync(new ContentRef(ContentKind.Folder, folder))).Outcome);
        var entry = (await _content.ListTrashAsync(_alpha)).Single();
        Assert.Equal(RestoreOutcome.Restored, (await _content.RestoreAsync(entry.Id)).Outcome);
        var listing = (await _content.ListAsync(_alpha, null)).Listing!;
        Assert.True(listing.ProjectArchived);
        Assert.Equal(("Archived", 1L), Row(_alpha));
    }

    [Fact]
    public async Task ListAndCount_ShowArchivedProjectsOnly_WithCurrentPaths()
    {
        var beta = Create(null, "Beta");
        var trashedArchived = Create(null, "Gone");
        Create(_clients, "Active one");
        await _workspace.ArchiveProjectAsync(_alpha);
        await _workspace.ArchiveProjectAsync(beta);
        Raw("UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;", trashedArchived);
        Raw("UPDATE WorkspaceNodes SET TrashedAt = '2026-01-01T00:00:00.000Z' WHERE Id = $id;", trashedArchived);
        await _workspace.RenameAsync(_tokyo, "Osaka");

        var listed = await _workspace.ListArchivedAsync();

        Assert.Equal(["Alpha Portal|Clients / Osaka", "Beta|"], listed.Select(e => $"{e.Name}|{e.ParentPath}"));
        Assert.Equal(2, await _workspace.CountArchivedAsync());
        var trash = Assert.Single(await _workspace.ListTrashAsync());
        Assert.True(trash.IsArchived);
    }

    [Fact]
    public async Task RestoringAnArchivedAndTrashedProject_BringsItBackArchived()
    {
        var gone = Create(_tokyo, "Gone");
        Raw("UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;", gone);
        Raw("UPDATE WorkspaceNodes SET TrashedAt = '2026-01-01T00:00:00.000Z' WHERE Id = $id;", gone); // an earlier build trashed it

        var restored = await _workspace.RestoreProjectAsync(gone);

        Assert.Equal(ProjectRestoreOutcome.Restored, restored.Outcome);
        Assert.True(restored.Restored!.IsArchived);
        Assert.Equal("Alpha Portal", await Children(_tokyo));
        Assert.Contains(await _workspace.ListArchivedAsync(), e => e.NodeId == gone);
    }

    // ---- Races: both commit orders, sequential and overlapping -----------------------------------------------------------

    private async Task<(T1 First, T2 Second)> Overlap<T1, T2>(string table, string operation,
        Func<SqliteConnectionFactory, Task<T1>> first, Func<SqliteConnectionFactory, Task<T2>> second)
    {
        using var gate = new TransactionGate(_root.Paths.DatabaseFile, table, operation);
        var signal = new OpenSignal(_root.Paths.DatabaseFile);
        var a = first(gate.Factory);
        gate.WaitEntered();
        var b = second(signal.Factory);
        signal.WaitOpened();
        Assert.False(a.IsCompleted);
        Assert.False(b.IsCompleted); // it cannot finish while the first holds the write lock
        gate.Release();
        return (await a, await b);
    }

    [Fact]
    public async Task ArchiveAndEdit_BothOrders()
    {
        // Edit first (holds the lock): the Archive then archives the EDITED Project.
        var (edited, archived) = await Overlap("WorkspaceNodes", "UPDATE",
            f => Workspace(f).UpdateProjectAsync(_alpha, new ProjectDraft("Alpha Portal", ProjectStatus.Maintenance, "edited", "Owner A")),
            f => Workspace(f).ArchiveProjectAsync(_alpha));
        Assert.True(edited.Succeeded);
        Assert.Equal((ProjectArchiveOutcome.Archived, ProjectStatus.Maintenance), (archived.Outcome, archived.Event!.Value.PreviousStatus));
        await _workspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Active);

        // Archive first: the Edit is refused and writes nothing.
        var before = Data();
        var (archivedFirst, refused) = await Overlap("Projects", "UPDATE",
            f => Workspace(f).ArchiveProjectAsync(_alpha),
            f => Workspace(f).UpdateProjectAsync(_alpha, new ProjectDraft("Renamed", ProjectStatus.Completed)));
        Assert.Equal(ProjectArchiveOutcome.Archived, archivedFirst.Outcome);
        Assert.Equal(WorkspaceFailure.ProjectArchived, refused.Failure);
        Assert.Single(Data().Except(before)); // only the archive's details row
        Assert.Equal(("Archived", 2L), Row(_alpha));
        await _workspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Active);

        // Archive first, then a Move and a Rename of the same Project: both refused.
        var (third, moveRefused) = await Overlap("Projects", "UPDATE",
            f => Workspace(f).ArchiveProjectAsync(_alpha),
            f => Workspace(f).MoveAsync(_alpha, _clients));
        Assert.Equal(ProjectArchiveOutcome.Archived, third.Outcome);
        Assert.Equal(WorkspaceFailure.ProjectArchived, moveRefused.Failure);
        Assert.Equal(WorkspaceFailure.ProjectArchived, (await _otherWorkspace.RenameAsync(_alpha, "Renamed")).Failure);
        Assert.Equal("Alpha Portal", (await _workspace.GetProjectAsync(_alpha))!.Name);
        Assert.Equal(_tokyo, (await _workspace.GetProjectAsync(_alpha))!.ParentId);
    }

    [Fact]
    public async Task ArchiveAndTrash_BothOrders()
    {
        var (trashed, archive) = await Overlap("WorkspaceNodes", "UPDATE",
            f => Workspace(f).TrashProjectAsync(_alpha),
            f => Workspace(f).ArchiveProjectAsync(_alpha));
        Assert.Equal(ProjectTrashOutcome.Trashed, trashed.Outcome);
        Assert.Equal(ProjectArchiveOutcome.ProjectTrashed, archive.Outcome);
        await _workspace.RestoreProjectAsync(_alpha);

        var (archivedFirst, trash) = await Overlap("Projects", "UPDATE",
            f => Workspace(f).ArchiveProjectAsync(_alpha),
            f => Workspace(f).TrashProjectAsync(_alpha));
        Assert.Equal(ProjectArchiveOutcome.Archived, archivedFirst.Outcome);
        Assert.Equal(ProjectTrashOutcome.Archived, trash.Outcome);
        using var connection = _root.Open();
        Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM WorkspaceNodes WHERE TrashedAt IS NOT NULL;"));
    }

    [Fact]
    public async Task ArchiveAndContentWrites_BothOrders_BothSucceed()
    {
        var note = (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("N", "v1"))).Note!.Id;

        var (saved, archived) = await Overlap("ProjectNotes", "UPDATE",
            f => Content(f).SaveNoteAsync(note, 1, new ProjectNoteDraft("N", "saved first")),
            f => Workspace(f).ArchiveProjectAsync(_alpha));
        Assert.True(saved.Succeeded);
        Assert.Equal(ProjectArchiveOutcome.Archived, archived.Outcome);
        await _workspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Active);

        var (archivedFirst, savedAfter) = await Overlap("Projects", "UPDATE",
            f => Workspace(f).ArchiveProjectAsync(_alpha),
            f => Content(f).SaveNoteAsync(note, 2, new ProjectNoteDraft("N", "saved after the archive")));
        Assert.Equal(ProjectArchiveOutcome.Archived, archivedFirst.Outcome);
        Assert.True(savedAfter.Succeeded); // A3: not frozen
        Assert.Equal("saved after the archive", (await _content.GetNoteAsync(note))!.Content);
    }

    [Fact]
    public async Task UndoAndUnarchive_BothOrders_ExactlyOneWins()
    {
        var first = (await _workspace.ArchiveProjectAsync(_alpha)).Event!.Value;
        var (undone, unarchive) = await Overlap("Projects", "UPDATE",
            f => Workspace(f).UndoArchiveAsync(first),
            f => Workspace(f).UnarchiveProjectAsync(_alpha, ProjectStatus.Completed));
        Assert.Equal((ArchiveUndoOutcome.Undone, ProjectUnarchiveOutcome.NotArchived), (undone.Outcome, unarchive.Outcome));
        Assert.Equal(("Active", 1L), Row(_alpha));

        var second = (await _workspace.ArchiveProjectAsync(_alpha)).Event!.Value;
        var (unarchived, undo) = await Overlap("Projects", "UPDATE",
            f => Workspace(f).UnarchiveProjectAsync(_alpha, ProjectStatus.Completed),
            f => Workspace(f).UndoArchiveAsync(second));
        Assert.Equal((ProjectUnarchiveOutcome.Unarchived, ArchiveUndoOutcome.NotArchived), (unarchived.Outcome, undo.Outcome));
        Assert.Equal(("Completed", 2L), Row(_alpha));
    }

    [Fact]
    public async Task UndoAgainstAnUnarchiveAndReArchive_InAnotherWindow_IsArchivedAgain()
    {
        var mine = (await _workspace.ArchiveProjectAsync(_alpha)).Event!.Value;
        await _otherWorkspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Active);
        var (rearchived, undo) = await Overlap("Projects", "UPDATE",
            f => Workspace(f).ArchiveProjectAsync(_alpha),
            f => Workspace(f).UndoArchiveAsync(mine));
        Assert.Equal(ProjectArchiveOutcome.Archived, rearchived.Outcome);
        Assert.Equal(ArchiveUndoOutcome.ArchivedAgain, undo.Outcome);
        Assert.Equal(("Archived", 2L), Row(_alpha));
    }

    // ---- Failure atomicity -----------------------------------------------------------------------------------------------

    /// <summary>A store whose connections carry a TEMP trigger that aborts every update of Projects AFTER the row was written.</summary>
    private WorkspaceService FailingAfterTheWrite() =>
        Workspace(new SqliteConnectionFactory(_root.Paths.DatabaseFile, defaultTimeoutSeconds: null, opened: connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TEMP TRIGGER wh_test_fail AFTER UPDATE ON main.Projects BEGIN SELECT RAISE(ABORT, 'WH_TEST_FAILURE'); END;";
            command.ExecuteNonQuery();
        }));

    [Fact]
    public async Task ArchiveUnarchiveAndUndo_FailingAfterTheirWrite_ChangeNothing_AndARetrySucceeds()
    {
        var failing = FailingAfterTheWrite();
        var before = Data();
        Assert.Contains("WH_TEST_FAILURE", (await Assert.ThrowsAsync<SqliteException>(() => failing.ArchiveProjectAsync(_alpha))).Message);
        Assert.Equal(before, Data());

        var archived = (await _workspace.ArchiveProjectAsync(_alpha)).Event!.Value;
        before = Data();
        await Assert.ThrowsAsync<SqliteException>(() => failing.UnarchiveProjectAsync(_alpha, ProjectStatus.Active));
        await Assert.ThrowsAsync<SqliteException>(() => failing.UndoArchiveAsync(archived));
        Assert.Equal(before, Data());

        Assert.Equal(ArchiveUndoOutcome.Undone, (await _workspace.UndoArchiveAsync(archived)).Outcome);
        Assert.Equal(("Active", 1L), Row(_alpha));
    }
}
