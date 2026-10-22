using System.Reflection;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Store enforcement of the effective lifecycle (Phase 13 proposal revision 4 §7.1, §7.6; B9, B24): every write method of
/// <see cref="IWorkspaceStore"/> and <see cref="IProjectContentStore"/>, run on a target BELOW a Folder F whose state is Live,
/// Archived or Trashed, is allowed or refused exactly as §7.1 says — Workspace node writes and clearing need a live chain;
/// content writes are allowed while Archived (D11) and refused while Trashed. A refused write changes nothing. The database
/// guards only the marked row and its direct children (V009); these targets sit deeper, so the store alone is tested.
/// <para>
/// The case table is checked against the interfaces by reflection: a new write method without a case fails
/// <see cref="EveryStoreWriteMethod_HasACase"/>. The mutation check (removing the store's lifecycle check from one method
/// makes a case fail) is recorded with the Phase 13B implementation evidence.
/// </para>
/// </summary>
public sealed class FolderLifecycleEnforcementTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly WorkspaceService _workspace;
    private readonly ProjectContentService _content;

    public FolderLifecycleEnforcementTests()
    {
        _root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(connections));
        _content = new ProjectContentService(new SqliteProjectContentStore(connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
    }

    public void Dispose() => _root.Dispose();

    private enum Rule
    {
        /// <summary>Workspace node writes and clearing: allowed only when the chain is live.</summary>
        LiveOnly,

        /// <summary>Content writes: allowed when Live or Archived, refused when Trashed.</summary>
        NotTrashed,
    }

    /// <summary>The objects every case works on. F is the top folder whose state varies; everything else is below F/D.</summary>
    private sealed record Fixture(
        long F, long D, long P, long ArchivedProject, long TrashedProject, long ArchivedFolder, long TrashedFolder, long EmptyFolder,
        long Outside, long OutsideFolder, long ContentFolder, long ContentFolder2, long Link, long Note, long TrashEntry,
        ArchiveEvent ProjectArchiveEvent, FolderLifecycleEvent FolderArchiveEvent, string ArchiveDToken, string TrashDToken);

    private static string W(string method) => $"{nameof(IWorkspaceStore)}.{method}";

    private static string C(string method) => $"{nameof(IProjectContentStore)}.{method}";

    private static readonly Dictionary<string, (Rule Rule, string Method, Func<FolderLifecycleEnforcementTests, Fixture, Task<bool>> Run)> Cases = new()
    {
        // IWorkspaceStore: node writes.
        ["CreateFolder"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.CreateFolderAsync)), async (t, x) => (await t._workspace.CreateFolderAsync(x.D, "New")).Succeeded),
        ["CreateProject"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.CreateProjectAsync)), async (t, x) => (await t._workspace.CreateProjectAsync(x.D, new ProjectDraft("New", ProjectStatus.Active))).Succeeded),
        ["Rename"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.RenameAsync)), async (t, x) => (await t._workspace.RenameAsync(x.P, "Renamed")).Succeeded),
        ["UpdateProject"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.UpdateProjectAsync)), async (t, x) => (await t._workspace.UpdateProjectAsync(x.P, new ProjectDraft("P", ProjectStatus.Completed))).Succeeded),
        ["MoveSource"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.MoveAsync)), async (t, x) => (await t._workspace.MoveAsync(x.P, x.OutsideFolder)).Succeeded),
        ["MoveDestination"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.MoveAsync)), async (t, x) => (await t._workspace.MoveAsync(x.Outside, x.D)).Succeeded),
        ["TrashProject"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.TrashProjectAsync)), async (t, x) => (await t._workspace.TrashProjectAsync(x.P)).Outcome == ProjectTrashOutcome.Trashed),
        ["RemoveEmptyFolder"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.RemoveEmptyFolderAsync)), async (t, x) => (await t._workspace.RemoveEmptyFolderAsync(x.EmptyFolder)).Outcome == FolderRemoveOutcome.Removed),
        ["ArchiveProject"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.ArchiveProjectAsync)), async (t, x) => (await t._workspace.ArchiveProjectAsync(x.P)).Outcome == ProjectArchiveOutcome.Archived),
        ["ArchiveFolder"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.ArchiveFolderAsync)), async (t, x) => (await t._workspace.MarkFolderAsync(x.D, FolderLifecycleKind.Archive, x.ArchiveDToken)).Outcome == FolderLifecycleOutcome.Committed),
        ["TrashFolder"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.TrashFolderAsync)), async (t, x) => (await t._workspace.MarkFolderAsync(x.D, FolderLifecycleKind.Trash, x.TrashDToken)).Outcome == FolderLifecycleOutcome.Committed),
        // IWorkspaceStore: clearing (Restore / Unarchive / Undo) needs a live parent chain (§7.1).
        ["RestoreProject"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.RestoreProjectAsync)), async (t, x) => (await t._workspace.RestoreProjectAsync(x.TrashedProject)).Outcome == ProjectRestoreOutcome.Restored),
        ["UnarchiveProject"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.UnarchiveProjectAsync)), async (t, x) => (await t._workspace.UnarchiveProjectAsync(x.ArchivedProject, ProjectStatus.Active)).Outcome == ProjectUnarchiveOutcome.Unarchived),
        ["UndoArchive"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.UndoArchiveAsync)), async (t, x) => (await t._workspace.UndoArchiveAsync(x.ProjectArchiveEvent)).Outcome == ArchiveUndoOutcome.Undone),
        ["UnarchiveFolder"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.UnarchiveFolderAsync)), async (t, x) => (await t._workspace.ClearFolderAsync(x.ArchivedFolder, FolderLifecycleKind.Archive)).Outcome == FolderClearOutcome.Cleared),
        ["RestoreFolder"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.RestoreFolderAsync)), async (t, x) => (await t._workspace.ClearFolderAsync(x.TrashedFolder, FolderLifecycleKind.Trash)).Outcome == FolderClearOutcome.Cleared),
        ["UndoFolder"] = (Rule.LiveOnly, W(nameof(IWorkspaceStore.UndoFolderLifecycleAsync)), async (t, x) => (await t._workspace.UndoFolderLifecycleAsync(x.FolderArchiveEvent)).Outcome == FolderClearOutcome.Cleared),
        // IProjectContentStore: content writes (D11: allowed under an archived ancestor).
        ["ContentCreateFolder"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.CreateFolderAsync)), async (t, x) => (await t._content.CreateFolderAsync(x.P, null, "New folder")).Succeeded),
        ["ContentRenameFolder"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.RenameFolderAsync)), async (t, x) => (await t._content.RenameFolderAsync(x.ContentFolder, "Renamed")).Succeeded),
        ["ContentCreateLink"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.CreateLinkAsync)), async (t, x) => (await t._content.CreateLinkAsync(x.P, null, new ProjectLinkDraft("New", ProjectLinkType.Web, "https://n.test/"))).Succeeded),
        ["ContentUpdateLink"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.UpdateLinkAsync)), async (t, x) => (await t._content.UpdateLinkAsync(x.Link, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://changed.test/"))).Succeeded),
        ["ContentMoveFolder"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.MoveFolderAsync)), async (t, x) => (await t._content.MoveFolderAsync(x.ContentFolder, null, x.ContentFolder2)).Outcome == ContentMoveOutcome.Moved),
        ["ContentMoveLink"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.MoveLinkAsync)), async (t, x) => (await t._content.MoveLinkAsync(x.Link, null, x.ContentFolder)).Outcome == ContentMoveOutcome.Moved),
        ["ContentCreateNote"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.CreateNoteAsync)), async (t, x) => (await t._content.CreateNoteAsync(x.P, null, new ProjectNoteDraft("New", "x"))).Status == NoteWriteStatus.Saved),
        ["ContentSaveNote"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.SaveNoteAsync)), async (t, x) => (await t._content.SaveNoteAsync(x.Note, 1, new ProjectNoteDraft("N", "edited"))).Status == NoteWriteStatus.Saved),
        ["ContentSaveNoteAsNew"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.SaveNoteAsNewAsync)), async (t, x) => (await t._content.SaveNoteAsNewAsync(x.Note, x.P, null, new ProjectNoteDraft("Copy", "x"))).Status == NoteWriteStatus.Saved),
        ["ContentMoveNote"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.MoveNoteAsync)), async (t, x) => (await t._content.MoveNoteAsync(x.Note, null, x.ContentFolder)).Outcome == ContentMoveOutcome.Moved),
        ["ContentTrash"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.TrashAsync)), async (t, x) => (await t._content.TrashAsync(new ContentRef(ContentKind.Link, x.Link))).Outcome == TrashOutcome.Trashed),
        ["ContentRestore"] = (Rule.NotTrashed, C(nameof(IProjectContentStore.RestoreAsync)), async (t, x) => (await t._content.RestoreAsync(x.TrashEntry)).Outcome == RestoreOutcome.Restored),
    };

    public static TheoryData<string, LifecycleState> Matrix()
    {
        var data = new TheoryData<string, LifecycleState>();
        foreach (var key in Cases.Keys)
            foreach (var state in Enum.GetValues<LifecycleState>())
                data.Add(key, state);
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task EveryWriteMethod_IsAllowedOrRefusedExactlyAsTheEffectiveStateSays(string key, LifecycleState state)
    {
        var x = await Build(state);
        var before = Data();
        var (rule, _, run) = Cases[key];

        var allowed = await run(this, x);

        var expected = state == LifecycleState.Live || (rule == Rule.NotTrashed && state == LifecycleState.Archived);
        Assert.True(expected == allowed, $"{key} below a {state} folder: {(allowed ? "allowed" : "refused")}");
        if (!allowed)
            Assert.Equal(before, Data()); // a refused write changes nothing
        else
            Assert.NotEqual(before, Data());
    }

    /// <summary>
    /// Reads are the methods named List*, Get*, Count* or Prepare* (they never write); every other method of the two store
    /// interfaces is a write and must have a case, and every case must name an existing write.
    /// </summary>
    [Fact]
    public void EveryStoreWriteMethod_HasACase()
    {
        static IEnumerable<string> Writes(Type store) => store.GetMethods()
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("List", StringComparison.Ordinal) && !n.StartsWith("Get", StringComparison.Ordinal)
                        && !n.StartsWith("Count", StringComparison.Ordinal) && !n.StartsWith("Prepare", StringComparison.Ordinal))
            .Select(n => $"{store.Name}.{n}");

        var writes = Writes(typeof(IWorkspaceStore)).Concat(Writes(typeof(IProjectContentStore))).Order(StringComparer.Ordinal).ToList();
        var covered = Cases.Values.Select(c => c.Method).Distinct().Order(StringComparer.Ordinal).ToList();

        Assert.Equal(writes, covered);
        Assert.Equal(28, writes.Count); // 16 Workspace writes (Move has a source and a destination case) and 12 content writes
    }

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private async Task<Fixture> Build(LifecycleState state)
    {
        async Task<long> Folder(string name, long? parent) => (await _workspace.CreateFolderAsync(parent, name)).Value!.Id;
        async Task<long> Project(string name, long? parent) => (await _workspace.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;
        async Task<string> Token(long folder, FolderLifecycleKind kind) => (await _workspace.PrepareFolderLifecycleAsync(folder, kind)).Confirmation!.Token;

        var f = await Folder("F", null);
        var d = await Folder("D", f);
        var p = await Project("P", d);
        var archivedProject = await Project("Archived", d);
        var trashedProject = await Project("Trashed", d);
        var archivedFolder = await Folder("ArchivedFolder", d);
        var trashedFolder = await Folder("TrashedFolder", d);
        var empty = await Folder("Empty", d);
        var outside = await Project("Outside", null);
        var outsideFolder = await Folder("OutsideFolder", null);
        var contentFolder = (await _content.CreateFolderAsync(p, null, "CF")).Value!.Id;
        var contentFolder2 = (await _content.CreateFolderAsync(p, null, "CF2")).Value!.Id;
        var link = (await _content.CreateLinkAsync(p, null, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://l.test/"))).Value!.Id;
        var trashedLink = (await _content.CreateLinkAsync(p, null, new ProjectLinkDraft("TL", ProjectLinkType.Web, "https://t.test/"))).Value!.Id;
        var note = (await _content.CreateNoteAsync(p, null, new ProjectNoteDraft("N", "body"))).Note!.Id;
        var entry = (await _content.TrashAsync(new ContentRef(ContentKind.Link, trashedLink))).Entry!.Id;
        var archiveEvent = (await _workspace.ArchiveProjectAsync(archivedProject)).Event!.Value;
        await _workspace.TrashProjectAsync(trashedProject);
        var folderEvent = (await _workspace.MarkFolderAsync(archivedFolder, FolderLifecycleKind.Archive, await Token(archivedFolder, FolderLifecycleKind.Archive))).Event!.Value;
        await _workspace.MarkFolderAsync(trashedFolder, FolderLifecycleKind.Trash, await Token(trashedFolder, FolderLifecycleKind.Trash));
        var archiveD = await Token(d, FolderLifecycleKind.Archive);
        var trashD = await Token(d, FolderLifecycleKind.Trash);
        switch (state)
        {
            case LifecycleState.Archived:
                Assert.Equal(FolderLifecycleOutcome.Committed, (await _workspace.MarkFolderAsync(f, FolderLifecycleKind.Archive, await Token(f, FolderLifecycleKind.Archive))).Outcome);
                break;
            case LifecycleState.Trashed:
                Assert.Equal(FolderLifecycleOutcome.Committed, (await _workspace.MarkFolderAsync(f, FolderLifecycleKind.Trash, await Token(f, FolderLifecycleKind.Trash))).Outcome);
                break;
        }

        return new Fixture(f, d, p, archivedProject, trashedProject, archivedFolder, trashedFolder, empty, outside, outsideFolder,
            contentFolder, contentFolder2, link, note, entry, archiveEvent, folderEvent, archiveD, trashD);
    }
}
