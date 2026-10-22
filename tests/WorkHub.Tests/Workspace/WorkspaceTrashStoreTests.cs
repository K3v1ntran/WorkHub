using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Workspace Project Trash, Restore and empty-folder removal through the real SQLite stores, with independent services on
/// the same database for races (Phase 8 proposal revision 2 §5, §8.1, §10.2).
/// </summary>
public sealed class WorkspaceTrashStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly RecordingProcessStarter _starter = new();
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceService _otherWorkspace;
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _otherContent;
    private readonly long _clients;
    private readonly long _tokyo;
    private readonly long _alpha;

    public WorkspaceTrashStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(_connections));
        _otherWorkspace = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _content = Content(_connections);
        _otherContent = Content(new SqliteConnectionFactory(_root.Paths.DatabaseFile));
        _clients = Create(null, "Clients", folder: true);
        _tokyo = Create(_clients, "東京支社", folder: true);
        _alpha = Create(_tokyo, "Alpha Portal");
    }

    public void Dispose() => _root.Dispose();

    private ProjectContentService Content(SqliteConnectionFactory factory) =>
        new(new SqliteProjectContentStore(factory), new ProcessProjectLinkLauncher(_starter, _ => true, _ => true));

    private long Create(long? parent, string name, bool folder = false) => folder
        ? _workspace.CreateFolderAsync(parent, name).Result.Value!.Id
        : _workspace.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active, "desc")).Result.Value!.Id;

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private static List<string> WithoutNodeMarkers(List<string> rows) =>
        rows.Select(r => r.StartsWith("WorkspaceNodes|", StringComparison.Ordinal)
            ? System.Text.RegularExpressions.Regex.Replace(r, @"\|TrashedAt=[^|]*", string.Empty) // V009 columns follow TrashedAt
            : r).ToList();

    private async Task<string> Children(long? parent) =>
        string.Join(",", (await _workspace.ListChildrenAsync(parent)).Nodes.Select(n => $"{n.Name}{(n.HasChildren ? "+" : string.Empty)}"));

    /// <summary>Content in Alpha including nested Phase 7 entries (Drafts trashed, then Specs) and a revised note.</summary>
    private async Task<(long Specs, long Drafts, long Note, long Link)> SeedContent()
    {
        var specs = (await _content.CreateFolderAsync(_alpha, null, "Specs")).Value!.Id;
        var drafts = (await _content.CreateFolderAsync(_alpha, specs, "Drafts")).Value!.Id;
        var note = (await _content.CreateNoteAsync(_alpha, drafts, new ProjectNoteDraft("Kickoff", "v1"))).Note!;
        Assert.True((await _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Kickoff", "v2"))).Succeeded);
        var link = (await _content.CreateLinkAsync(_alpha, null, new ProjectLinkDraft("Spec", ProjectLinkType.File, @"C:\Docs\spec.docx"))).Value!.Id;
        Assert.Equal(TrashOutcome.Trashed, (await _content.TrashAsync(new ContentRef(ContentKind.Folder, drafts))).Outcome);
        Assert.Equal(TrashOutcome.Trashed, (await _content.TrashAsync(new ContentRef(ContentKind.Folder, specs))).Outcome);
        return (specs, drafts, note.Id, link);
    }

    // ---- Trash ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Trash_MarksOnlyTheNode_AndHidesItFromTheTree()
    {
        await SeedContent();
        var before = Data();

        var result = await _workspace.TrashProjectAsync(_alpha);

        Assert.Equal(ProjectTrashOutcome.Trashed, result.Outcome);
        Assert.Equal(new WorkspaceTrashEntry(_alpha, "Alpha Portal", _tokyo, "Clients / 東京支社", result.Entry!.TrashedAt), result.Entry);
        Assert.Equal(WithoutNodeMarkers(before), WithoutNodeMarkers(Data())); // details, content, entries, UpdatedAt unchanged
        Assert.Equal(string.Empty, await Children(_tokyo));
        Assert.Equal("東京支社", await Children(_clients)); // a folder holding only a trashed Project shows no expander
        Assert.True((await _workspace.GetProjectAsync(_alpha))!.IsTrashed);
        var state = (await _workspace.GetNodeStateAsync(_alpha))!;
        Assert.Equal((_alpha, WorkspaceNodeType.Project, (long?)_tokyo, true, false), (state.NodeId, state.Type, state.ParentId, state.Trashed, state.Archived));
        Assert.Equal(new EffectiveLifecycle(LifecycleState.Trashed, _alpha, WorkspaceNodeType.Project), state.Effective); // schema v9: the effective state too
        Assert.Equal([_alpha], state.ChainFromRoot);
        Assert.Equal(1, await _workspace.CountTrashAsync());
    }

    [Fact]
    public async Task Trash_Refusals_WriteNothing()
    {
        Assert.Equal(ProjectTrashOutcome.NotAProject, (await _workspace.TrashProjectAsync(_clients)).Outcome);
        Assert.Equal(ProjectTrashOutcome.NotFound, (await _workspace.TrashProjectAsync(9999)).Outcome);
        await _workspace.TrashProjectAsync(_alpha);
        var before = Data();
        Assert.Equal(ProjectTrashOutcome.AlreadyTrashed, (await _workspace.TrashProjectAsync(_alpha)).Outcome);
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task ListTrash_IsNewestFirst_WithParentPaths()
    {
        var root = Create(null, "Root Project");
        await _workspace.TrashProjectAsync(_alpha);
        await _workspace.TrashProjectAsync(root);

        var entries = await _workspace.ListTrashAsync();

        Assert.Equal([root, _alpha], entries.Select(e => e.NodeId));
        Assert.Null(entries[0].ParentPath);
        Assert.Equal("Clients / 東京支社", entries[1].ParentPath);
        Assert.Equal(2, await _workspace.CountTrashAsync());
    }

    // ---- Restore -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TrashThenRestore_LeavesEveryRowIdentical_IncludingNestedPhase7Entries()
    {
        var (specs, drafts, note, _) = await SeedContent();
        var before = Data();

        await _workspace.TrashProjectAsync(_alpha);
        var restored = await _workspace.RestoreProjectAsync(_alpha);

        Assert.Equal(ProjectRestoreOutcome.Restored, restored.Outcome);
        Assert.Equal(new WorkspaceNode(_alpha, _tokyo, WorkspaceNodeType.Project, "Alpha Portal", false), restored.Restored!.Node);
        Assert.Equal("Clients / 東京支社", restored.Restored.ParentPath);
        Assert.Equal(before, Data());
        Assert.Equal("Alpha Portal", await Children(_tokyo));

        // Phase 7 is usable again and the nested entries are still independent.
        var trash = await _content.ListTrashAsync(_alpha);
        Assert.Equal(2, trash.Count);
        Assert.Equal(RestoreOutcome.Restored, (await _content.RestoreAsync(trash.Single(e => e.Root.Id == specs).Id)).Outcome);
        Assert.Equal(RestoreOutcome.Restored, (await _content.RestoreAsync(trash.Single(e => e.Root.Id == drafts).Id)).Outcome);
        Assert.Equal(3, (await _content.SaveNoteAsync(note, 2, new ProjectNoteDraft("Kickoff", "v3"))).Note!.Revision);
    }

    [Fact]
    public async Task Restore_FollowsTheOriginalParentById_AfterItMoved()
    {
        await _workspace.TrashProjectAsync(_alpha);
        Assert.True((await _workspace.MoveAsync(_tokyo, null)).Value); // the folder holding a trashed Project moves; it follows

        var restored = await _workspace.RestoreProjectAsync(_alpha);

        Assert.Equal("東京支社", restored.Restored!.ParentPath);
        Assert.Equal("Alpha Portal", await Children(_tokyo));
    }

    [Fact]
    public async Task Restore_IsRefused_ByAnActiveSiblingName_FolderOrProject_AtChildAndRootLevel()
    {
        await _workspace.TrashProjectAsync(_alpha);
        var blocker = Create(_tokyo, "ALPHA PORTAL", folder: true); // the name is free while Alpha is trashed (D3)
        var before = Data();

        var refused = await _workspace.RestoreProjectAsync(_alpha);

        Assert.Equal(ProjectRestoreOutcome.NameConflict, refused.Outcome);
        Assert.Equal("ALPHA PORTAL", refused.BlockingName);
        Assert.Equal(before, Data());

        var root = Create(null, "Beta");
        await _workspace.TrashProjectAsync(root);
        Create(null, "beta");
        Assert.Equal(ProjectRestoreOutcome.NameConflict, (await _workspace.RestoreProjectAsync(root)).Outcome);

        Assert.True((await _workspace.RenameAsync(blocker, "Old Alpha")).Succeeded);
        Assert.Equal(ProjectRestoreOutcome.Restored, (await _workspace.RestoreProjectAsync(_alpha)).Outcome);
    }

    [Fact]
    public async Task Restore_Refusals()
    {
        Assert.Equal(ProjectRestoreOutcome.NotTrashed, (await _workspace.RestoreProjectAsync(_alpha)).Outcome);
        Assert.Equal(ProjectRestoreOutcome.NotTrashed, (await _workspace.RestoreProjectAsync(_clients)).Outcome);
        Assert.Equal(ProjectRestoreOutcome.NotFound, (await _workspace.RestoreProjectAsync(9999)).Outcome);
    }

    // ---- Remove empty folder ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RemoveEmptyFolder_RemovesOnlyATrulyEmptyFolder()
    {
        var empty = Create(null, "Drafts", folder: true);

        Assert.Equal(FolderRemoveOutcome.Removed, (await _workspace.RemoveEmptyFolderAsync(empty)).Outcome);
        Assert.Null(await _workspace.GetNodeStateAsync(empty));
        Assert.Equal(FolderRemoveOutcome.NotFound, (await _workspace.RemoveEmptyFolderAsync(empty)).Outcome);
        Assert.Equal(FolderRemoveOutcome.NotAFolder, (await _workspace.RemoveEmptyFolderAsync(_alpha)).Outcome);
        Assert.Equal(new FolderRemoveResult(FolderRemoveOutcome.NotEmpty, 1, 0), await _workspace.RemoveEmptyFolderAsync(_clients));

        await _workspace.TrashProjectAsync(_alpha);
        var before = Data();
        Assert.Equal(new FolderRemoveResult(FolderRemoveOutcome.NotEmpty, 0, 1), await _workspace.RemoveEmptyFolderAsync(_tokyo)); // D8
        Assert.Equal(before, Data());
    }

    // ---- Existing Workspace operations on a trashed Project --------------------------------------------------------------------

    [Fact]
    public async Task RenameEditAndMove_OfATrashedProject_AreRefusedTyped_NothingWritten()
    {
        await _workspace.TrashProjectAsync(_alpha);
        var before = Data();

        Assert.Equal(WorkspaceFailure.ProjectTrashed, (await _workspace.RenameAsync(_alpha, "New")).Failure);
        Assert.Equal(WorkspaceFailure.ProjectTrashed, (await _workspace.UpdateProjectAsync(_alpha, new ProjectDraft("New", ProjectStatus.Completed))).Failure);
        Assert.Equal(WorkspaceFailure.ProjectTrashed, (await _workspace.MoveAsync(_alpha, null)).Failure);
        Assert.Equal("This project is in the Trash.", (await _workspace.MoveAsync(_alpha, null)).Error);
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task WorkspaceFailures_AreTyped()
    {
        Assert.Equal(WorkspaceFailure.DuplicateName, (await _workspace.CreateFolderAsync(_tokyo, "alpha portal")).Failure);
        Assert.Equal(WorkspaceFailure.ParentNotFound, (await _workspace.CreateFolderAsync(9999, "X")).Failure);
        Assert.Equal(WorkspaceFailure.ParentNotAFolder, (await _workspace.CreateProjectAsync(_alpha, new ProjectDraft("X", ProjectStatus.Active))).Failure);
        Assert.Equal(WorkspaceFailure.NodeNotFound, (await _workspace.RenameAsync(9999, "X")).Failure);
        Assert.Equal(WorkspaceFailure.MoveCycle, (await _workspace.MoveAsync(_clients, _tokyo)).Failure);
        Assert.Equal(WorkspaceFailure.ParentNotFound, (await _workspace.MoveAsync(_alpha, 9999)).Failure);
        Assert.Null((await _workspace.CreateFolderAsync(_tokyo, " ")).Failure); // validation, not a store refusal
    }

    // ---- Content of a trashed Project --------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryContentOperation_OnATrashedProject_IsRefusedTyped_NothingWritten()
    {
        var (specs, drafts, note, link) = await SeedContent();
        var root = (await _content.CreateFolderAsync(_alpha, null, "Active")).Value!.Id;
        var activeNote = (await _content.CreateNoteAsync(_alpha, root, new ProjectNoteDraft("Live", "x"))).Note!;
        var entry = (await _content.ListTrashAsync(_alpha))[0].Id;
        await _workspace.TrashProjectAsync(_alpha);
        var before = Data();

        Assert.Equal(ContentContainerProblem.ProjectTrashed, (await _content.ListAsync(_alpha, null)).Problem);
        Assert.Equal(WorkspaceFailure.ProjectTrashed, (await _content.CreateFolderAsync(_alpha, null, "New")).Failure);
        Assert.Equal(WorkspaceFailure.ProjectTrashed, (await _content.RenameFolderAsync(root, "Renamed")).Failure);
        Assert.Equal(WorkspaceFailure.ProjectTrashed, (await _content.CreateLinkAsync(_alpha, null, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://x.example.test/"))).Failure);
        Assert.Equal(WorkspaceFailure.ProjectTrashed, (await _content.UpdateLinkAsync(link, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://x.example.test/"))).Failure);
        Assert.Equal(ContentMoveOutcome.ProjectTrashed, (await _content.MoveFolderAsync(root, null, null)).Outcome);
        Assert.Equal(ContentMoveOutcome.ProjectTrashed, (await _content.MoveLinkAsync(link, null, root)).Outcome);
        Assert.Equal(ContentMoveOutcome.ProjectTrashed, (await _content.MoveNoteAsync(activeNote.Id, root, null)).Outcome);
        Assert.Equal(NoteWriteStatus.ProjectTrashed, (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("N", ""))).Status);
        var save = await _content.SaveNoteAsync(activeNote.Id, 1, new ProjectNoteDraft("Live", "late"));
        Assert.Equal(NoteWriteStatus.ProjectTrashed, save.Status);
        Assert.Equal("This project is in the Trash. Your text is unchanged here.", save.Error);
        Assert.Equal(NoteWriteStatus.ProjectTrashed, (await _content.SaveNoteAsNewAsync(activeNote.Id, _alpha, root, new ProjectNoteDraft("Live", "late"))).Status);
        Assert.Equal(NoteWriteStatus.ProjectTrashed, (await _content.SaveNoteAsync(note, 2, new ProjectNoteDraft("Kickoff", "late"))).Status); // checked before the note's own Trash
        Assert.Equal(TrashOutcome.ProjectTrashed, (await _content.TrashAsync(new ContentRef(ContentKind.Link, link))).Outcome);
        Assert.Equal(TrashOutcome.ProjectTrashed, (await _content.TrashAsync(new ContentRef(ContentKind.Folder, specs))).Outcome);
        Assert.Equal(RestoreOutcome.ProjectTrashed, (await _content.RestoreAsync(entry)).Outcome);
        Assert.Equal(ProjectLinkOpenProblem.ProjectTrashed, (await _content.OpenLinkAsync(link)).Problem);
        Assert.Empty(_starter.Started);
        Assert.Equal(before, Data());
        Assert.NotNull(await _content.GetContainerPathAsync(_alpha, drafts)); // display-only lookups still answer
    }

    // ---- Search -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_ExcludesATrashedProjectAndItsContent_AndResolveReportsIt()
    {
        var folder = (await _content.CreateFolderAsync(_alpha, null, "Alpha Specs")).Value!.Id;
        var note = (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("Alpha kickoff", "alpha"))).Note!.Id;
        var search = new WorkspaceSearchService(new SqliteWorkspaceSearchStore(_connections));
        Assert.Equal(3, (await search.SearchAsync(WorkspaceSearchService.Parse("alpha")!)).Hits.Count);

        await _workspace.TrashProjectAsync(_alpha);

        Assert.Empty((await search.SearchAsync(WorkspaceSearchService.Parse("alpha")!)).Hits);
        Assert.Equal(RevealResolveStatus.Trashed, (await search.ResolveAsync(new SearchItemRef(SearchResultKind.Project, _alpha))).Status);
        Assert.Equal(RevealResolveStatus.ProjectTrashed, (await search.ResolveAsync(new SearchItemRef(SearchResultKind.ProjectFolder, folder))).Status);
        Assert.Equal(RevealResolveStatus.ProjectTrashed, (await search.ResolveAsync(new SearchItemRef(SearchResultKind.Note, note))).Status);
        var tokyo = await search.ResolveAsync(new SearchItemRef(SearchResultKind.WorkspaceFolder, _tokyo));
        Assert.All(tokyo.Target!.Branches, b => Assert.DoesNotContain(b.Children, c => c.Id == _alpha));
        Assert.False(tokyo.Target.Branches[^1].Children.Single(c => c.Id == _tokyo).HasChildren);

        await _workspace.RestoreProjectAsync(_alpha);
        Assert.Equal(3, (await search.SearchAsync(WorkspaceSearchService.Parse("alpha")!)).Hits.Count);
    }

    // ---- Races: both commit orders, sequential and overlapping (D14) -----------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="first"/> through a store on the gated factory so it pauses inside its write transaction, starts
    /// <paramref name="second"/> on its own connection (it then waits for the write lock), and releases the first. The first
    /// therefore commits first and the second's checks run against that commit.
    /// </summary>
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

    private WorkspaceService Workspace(SqliteConnectionFactory factory) => new(new SqliteWorkspaceStore(factory));

    [Fact]
    public async Task ContentWriteThenTrash_SavedContentKept()
    {
        var note = (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("N", "v1"))).Note!.Id;

        // Sequential.
        Assert.True((await _otherContent.SaveNoteAsync(note, 1, new ProjectNoteDraft("N", "saved first"))).Succeeded);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _workspace.TrashProjectAsync(_alpha)).Outcome);
        await _workspace.RestoreProjectAsync(_alpha);
        Assert.Equal("saved first", (await _content.GetNoteAsync(note))!.Content);

        // Overlapping: the save holds the lock first.
        var (saved, trashed) = await Overlap("ProjectNotes", "UPDATE",
            f => Content(f).SaveNoteAsync(note, 2, new ProjectNoteDraft("N", "overlap")),
            f => Workspace(f).TrashProjectAsync(_alpha));
        Assert.True(saved.Succeeded);
        Assert.Equal(ProjectTrashOutcome.Trashed, trashed.Outcome);
        await _workspace.RestoreProjectAsync(_alpha);
        Assert.Equal("overlap", (await _content.GetNoteAsync(note))!.Content);

        var (link, trashedAgain) = await Overlap("ProjectLinks", "INSERT",
            f => Content(f).CreateLinkAsync(_alpha, null, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://l.example.test/")),
            f => Workspace(f).TrashProjectAsync(_alpha));
        Assert.True(link.Succeeded);
        Assert.Equal(ProjectTrashOutcome.Trashed, trashedAgain.Outcome);
    }

    [Fact]
    public async Task TrashThenContentWrite_ProjectTrashed_NothingWritten()
    {
        var note = (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("N", "v1"))).Note!.Id;
        var link = (await _content.CreateLinkAsync(_alpha, null, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://l.example.test/"))).Value!.Id;

        // Sequential.
        await _workspace.TrashProjectAsync(_alpha);
        var before = Data();
        Assert.Equal(NoteWriteStatus.ProjectTrashed, (await _otherContent.SaveNoteAsync(note, 1, new ProjectNoteDraft("N", "late"))).Status);
        Assert.Equal(TrashOutcome.ProjectTrashed, (await _otherContent.TrashAsync(new ContentRef(ContentKind.Link, link))).Outcome);
        Assert.Equal(before, Data());
        await _workspace.RestoreProjectAsync(_alpha);

        // Overlapping: the Trash holds the lock first; each content write then sees it.
        var (trashed, save) = await Overlap("WorkspaceNodes", "UPDATE",
            f => Workspace(f).TrashProjectAsync(_alpha),
            f => Content(f).SaveNoteAsync(note, 1, new ProjectNoteDraft("N", "late")));
        Assert.Equal(ProjectTrashOutcome.Trashed, trashed.Outcome);
        Assert.Equal(NoteWriteStatus.ProjectTrashed, save.Status);
        Assert.Equal("v1", (await _content.GetNoteAsync(note))!.Content);
        await _workspace.RestoreProjectAsync(_alpha);

        var (_, create) = await Overlap("WorkspaceNodes", "UPDATE",
            f => Workspace(f).TrashProjectAsync(_alpha),
            f => Content(f).CreateLinkAsync(_alpha, null, new ProjectLinkDraft("Late", ProjectLinkType.Web, "https://late.example.test/")));
        Assert.Equal(WorkspaceFailure.ProjectTrashed, create.Failure);
        await _workspace.RestoreProjectAsync(_alpha);

        var (_, contentTrash) = await Overlap("WorkspaceNodes", "UPDATE",
            f => Workspace(f).TrashProjectAsync(_alpha),
            f => Content(f).TrashAsync(new ContentRef(ContentKind.Link, link)));
        Assert.Equal(TrashOutcome.ProjectTrashed, contentTrash.Outcome);
        using var connection = _root.Open();
        Assert.Equal(0, Db.Count(connection, "ProjectTrashEntries"));
    }

    [Fact]
    public async Task CreateThenRemove_NotEmpty()
    {
        var empty = Create(null, "Drafts", folder: true);
        Assert.True((await _otherWorkspace.CreateFolderAsync(empty, "Child")).Succeeded);
        Assert.Equal(new FolderRemoveResult(FolderRemoveOutcome.NotEmpty, 1, 0), await _workspace.RemoveEmptyFolderAsync(empty));

        var second = Create(null, "Second", folder: true);
        var (created, removed) = await Overlap("WorkspaceNodes", "INSERT",
            f => Workspace(f).CreateProjectAsync(second, new ProjectDraft("P", ProjectStatus.Active)),
            f => Workspace(f).RemoveEmptyFolderAsync(second));
        Assert.True(created.Succeeded);
        Assert.Equal(new FolderRemoveResult(FolderRemoveOutcome.NotEmpty, 1, 0), removed);
        Assert.Equal("P", await Children(second));
    }

    [Fact]
    public async Task RemoveThenCreate_ParentNotFound_NothingCreated()
    {
        var empty = Create(null, "Drafts", folder: true);
        Assert.Equal(FolderRemoveOutcome.Removed, (await _workspace.RemoveEmptyFolderAsync(empty)).Outcome);
        var before = Data();
        Assert.Equal(WorkspaceFailure.ParentNotFound, (await _otherWorkspace.CreateFolderAsync(empty, "Child")).Failure);
        Assert.Equal(WorkspaceFailure.ParentNotFound, (await _otherWorkspace.CreateProjectAsync(empty, new ProjectDraft("P", ProjectStatus.Active))).Failure);
        Assert.Equal(before, Data());

        var second = Create(null, "Second", folder: true);
        var (removed, created) = await Overlap("WorkspaceNodes", "DELETE",
            f => Workspace(f).RemoveEmptyFolderAsync(second),
            f => Workspace(f).CreateProjectAsync(second, new ProjectDraft("P", ProjectStatus.Active)));
        Assert.Equal(FolderRemoveOutcome.Removed, removed.Outcome);
        Assert.Equal(WorkspaceFailure.ParentNotFound, created.Failure);
        Assert.Equal("The destination folder no longer exists.", created.Error);
        using var connection = _root.Open();
        Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM WorkspaceNodes WHERE Name = 'P';"));
    }

    [Fact]
    public async Task MoveIntoThenRemove_NotEmpty_AndRemoveThenMoveInto_ParentNotFound()
    {
        var target = Create(null, "Target", folder: true);
        var (moved, removed) = await Overlap("WorkspaceNodes", "UPDATE",
            f => Workspace(f).MoveAsync(_alpha, target),
            f => Workspace(f).RemoveEmptyFolderAsync(target));
        Assert.True(moved.Value);
        Assert.Equal(FolderRemoveOutcome.NotEmpty, removed.Outcome);

        var gone = Create(null, "Gone", folder: true);
        var (removedFirst, moveLate) = await Overlap("WorkspaceNodes", "DELETE",
            f => Workspace(f).RemoveEmptyFolderAsync(gone),
            f => Workspace(f).MoveAsync(_alpha, gone));
        Assert.Equal(FolderRemoveOutcome.Removed, removedFirst.Outcome);
        Assert.Equal(WorkspaceFailure.ParentNotFound, moveLate.Failure);
        Assert.Equal(target, (await _workspace.GetNodeStateAsync(_alpha))!.ParentId);
    }

    [Fact]
    public async Task RenameThenTrash_KeepsTheRename_AndTrashThenRename_IsRefused()
    {
        var (renamed, trashed) = await Overlap("WorkspaceNodes", "UPDATE",
            f => Workspace(f).RenameAsync(_alpha, "Alpha 2"),
            f => Workspace(f).TrashProjectAsync(_alpha));
        Assert.True(renamed.Succeeded);
        Assert.Equal("Alpha 2", trashed.Entry!.Name);
        await _workspace.RestoreProjectAsync(_alpha);

        var (_, late) = await Overlap("WorkspaceNodes", "UPDATE",
            f => Workspace(f).TrashProjectAsync(_alpha),
            f => Workspace(f).RenameAsync(_alpha, "Alpha 3"));
        Assert.Equal(WorkspaceFailure.ProjectTrashed, late.Failure);
        Assert.Equal("Alpha 2", (await _workspace.GetProjectAsync(_alpha))!.Name);
    }

    [Fact]
    public async Task CreateThenRestore_NameConflict_AndRestoreThenCreate_DuplicateName()
    {
        await _workspace.TrashProjectAsync(_alpha);
        var (created, restore) = await Overlap("WorkspaceNodes", "INSERT",
            f => Workspace(f).CreateFolderAsync(_tokyo, "Alpha Portal"),
            f => Workspace(f).RestoreProjectAsync(_alpha));
        Assert.True(created.Succeeded);
        Assert.Equal(ProjectRestoreOutcome.NameConflict, restore.Outcome);

        await _workspace.RenameAsync(created.Value!.Id, "Other");
        var (restored, duplicate) = await Overlap("WorkspaceNodes", "UPDATE",
            f => Workspace(f).RestoreProjectAsync(_alpha),
            f => Workspace(f).CreateProjectAsync(_tokyo, new ProjectDraft("alpha portal", ProjectStatus.Active)));
        Assert.Equal(ProjectRestoreOutcome.Restored, restored.Outcome);
        Assert.Equal(WorkspaceFailure.DuplicateName, duplicate.Failure);
    }

    [Fact]
    public async Task SameOperationRaces_OneWins()
    {
        var trashes = await Task.WhenAll(_workspace.TrashProjectAsync(_alpha), _otherWorkspace.TrashProjectAsync(_alpha));
        Assert.Single(trashes, r => r.Outcome == ProjectTrashOutcome.Trashed);
        Assert.Single(trashes, r => r.Outcome == ProjectTrashOutcome.AlreadyTrashed);

        var restores = await Task.WhenAll(_workspace.RestoreProjectAsync(_alpha), _otherWorkspace.RestoreProjectAsync(_alpha));
        Assert.Single(restores, r => r.Outcome == ProjectRestoreOutcome.Restored);
        Assert.Single(restores, r => r.Outcome == ProjectRestoreOutcome.NotTrashed);

        var empty = Create(null, "Drafts", folder: true);
        var removes = await Task.WhenAll(_workspace.RemoveEmptyFolderAsync(empty), _otherWorkspace.RemoveEmptyFolderAsync(empty));
        Assert.Single(removes, r => r.Outcome == FolderRemoveOutcome.Removed);
        Assert.Single(removes, r => r.Outcome == FolderRemoveOutcome.NotFound);
    }
}
