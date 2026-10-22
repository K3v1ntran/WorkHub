using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Folder Archive and Folder Trash through the real SQLite stores (Phase 13 proposal revision 4 §7.1-§7.8, §10.2 B2-B5, B7,
/// B10, B13-B17, B19-B23 store parts, B25-B34). Independent services on the same database play the other window. Every store
/// in this class shares ONE clock that never moves unless a test moves it (a fixed clock): confirmation identity can never be
/// carried by timestamps here, only by identities and serials (B34).
/// </summary>
public sealed class FolderLifecycleStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SettableClock _clock = new(new DateTimeOffset(2026, 10, 22, 1, 2, 3, 456, TimeSpan.Zero));
    private readonly SqliteConnectionFactory _connections;
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceService _other;
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _otherContent;
    private readonly SqliteWorkspaceSearchStore _search;

    public FolderLifecycleStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var otherConnections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(_connections, _clock));
        _other = new WorkspaceService(new SqliteWorkspaceStore(otherConnections, _clock));
        _content = Content(_connections);
        _otherContent = Content(otherConnections);
        _search = new SqliteWorkspaceSearchStore(_connections);
    }

    public void Dispose() => _root.Dispose();

    private ProjectContentService Content(SqliteConnectionFactory factory) =>
        new(new SqliteProjectContentStore(factory, _clock), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));

    // ---- Fixture -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The B2 subtree, built through the services: 2024 / Q1 / Deep / Alpha (content) and ArchivedDeep (archived), Q1 / Beta
    /// (Completed) and TrashedQ1 (trashed), 2024 / Q2 / Gamma (archived), 2024 / Delta (Planning); and the sibling 2026 / Epsilon.
    /// Two archived and one trashed Project, three levels.
    /// </summary>
    private async Task<Dictionary<string, long>> Tree()
    {
        var ids = new Dictionary<string, long>();
        ids["2024"] = await Folder("2024");
        ids["Q1"] = await Folder("Q1", ids["2024"]);
        ids["Deep"] = await Folder("Deep", ids["Q1"]);
        ids["Q2"] = await Folder("Q2", ids["2024"]);
        ids["2026"] = await Folder("2026");
        ids["Alpha"] = await Project("Alpha", ids["Deep"]);
        ids["ArchivedDeep"] = await Project("ArchivedDeep", ids["Deep"]);
        ids["Beta"] = await Project("Beta", ids["Q1"], ProjectStatus.Completed);
        ids["TrashedQ1"] = await Project("TrashedQ1", ids["Q1"]);
        ids["Gamma"] = await Project("Gamma", ids["Q2"], ProjectStatus.OnHold);
        ids["Delta"] = await Project("Delta", ids["2024"], ProjectStatus.Planning);
        ids["Epsilon"] = await Project("Epsilon", ids["2026"], ProjectStatus.Maintenance);
        var specs = (await _content.CreateFolderAsync(ids["Alpha"], null, "Specs")).Value!.Id;
        ids["Specs"] = specs;
        ids["SpecLink"] = (await _content.CreateLinkAsync(ids["Alpha"], specs, new ProjectLinkDraft("Spec", ProjectLinkType.Web, "https://spec.example.test/"))).Value!.Id;
        ids["WikiLink"] = (await _content.CreateLinkAsync(ids["Alpha"], null, new ProjectLinkDraft("Wiki", ProjectLinkType.Web, "https://wiki.example.test/"))).Value!.Id;
        ids["Note"] = (await _content.CreateNoteAsync(ids["Alpha"], null, new ProjectNoteDraft("Kickoff", "agenda"))).Note!.Id;
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _workspace.ArchiveProjectAsync(ids["ArchivedDeep"])).Outcome);
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _workspace.ArchiveProjectAsync(ids["Gamma"])).Outcome);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _workspace.TrashProjectAsync(ids["TrashedQ1"])).Outcome);
        return ids;
    }

    private async Task<long> Folder(string name, long? parent = null) => (await _workspace.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null, ProjectStatus status = ProjectStatus.Active) =>
        (await _workspace.CreateProjectAsync(parent, new ProjectDraft(name, status))).Value!.Id;

    private async Task<LifecycleConfirmation> Confirm(long folder, FolderLifecycleKind kind, WorkspaceService? service = null) =>
        (await (service ?? _workspace).PrepareFolderLifecycleAsync(folder, kind)).Confirmation ?? throw new InvalidOperationException("no confirmation");

    /// <summary>Confirms and commits at once (the other window's ordinary use).</summary>
    private async Task<FolderLifecycleResult> Mark(long folder, FolderLifecycleKind kind, WorkspaceService? service = null)
    {
        service ??= _workspace;
        return await service.MarkFolderAsync(folder, kind, (await Confirm(folder, kind, service)).Token);
    }

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private static List<string> Changed(List<string> before, List<string> after) => after.Except(before).Order(StringComparer.Ordinal).ToList();

    private string Markers(long id)
    {
        using var connection = _root.Open();
        return Db.ScalarString(connection, "SELECT coalesce(FolderArchivedAt, '-') || '|' || coalesce(FolderTrashedAt, '-') || '|' || LifecycleSerial FROM WorkspaceNodes WHERE Id = $id;", ("$id", id))!;
    }

    private async Task<string> Children(long? parent)
    {
        var result = await _workspace.ListChildrenAsync(parent);
        return result.Status == ChildrenReadStatus.Listed
            ? string.Join(",", result.Nodes.Select(n => $"{n.Name}{(n.HasChildren ? "+" : string.Empty)}"))
            : $"{result.Status}:{result.ParentState!.State}:{result.ParentState.RootId}";
    }

    private async Task<List<string>> TrashList() => (await _workspace.ListTrashAsync()).Select(e => $"{e.Type}:{e.Name}{(e.ArchivedAncestorName is { } a ? $"@{a}" : string.Empty)}").ToList();

    private async Task<List<string>> ArchivedList() => (await _workspace.ListArchivedAsync()).Select(e => $"{e.Type}:{e.Name}").ToList();

    private void Raw(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = _root.Open();
        Db.Exec(connection, sql, parameters);
    }

    private static string Now(int ms = 456) => new DateTimeOffset(2026, 10, 22, 1, 2, 3, ms, TimeSpan.Zero).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    // ---- B2, B3: Archive writes one row; Unarchive clears one marker -----------------------------------------------------------

    [Fact]
    public async Task B2_Archive_WritesOnlyTheRootRow_TheSubtreeLeavesTheTree_TheSiblingIsUntouched()
    {
        var ids = await Tree();
        var before = Data();

        var preview = await Confirm(ids["2024"], FolderLifecycleKind.Archive);
        var result = await _workspace.MarkFolderAsync(ids["2024"], FolderLifecycleKind.Archive, preview.Token);

        Assert.Equal(FolderLifecycleOutcome.Committed, result.Outcome);
        Assert.Equal(new FolderLifecycleEvent(ids["2024"], FolderLifecycleKind.Archive, 1), result.Event);
        var after = Data();
        var changed = Assert.Single(Changed(before, after)); // exactly one row: the Folder that roots the subtree
        Assert.Single(before.Except(after));
        Assert.StartsWith($"WorkspaceNodes|Id={ids["2024"]}|", changed);
        Assert.EndsWith($"|FolderArchivedAt={Now()}|FolderTrashedAt=NULL|LifecycleSerial=1", changed);

        Assert.Equal("2026+", await Children(null));
        Assert.Equal("Epsilon", await Children(ids["2026"]));
        Assert.Equal($"ParentNotLive:Archived:{ids["2024"]}", await Children(ids["2024"]));
        Assert.Equal($"ParentNotLive:Archived:{ids["2024"]}", await Children(ids["Deep"]));
        Assert.Equal(["Folder:2024"], await ArchivedList()); // nested archived Projects are inside the outermost root
        Assert.Equal(["Project:TrashedQ1@2024"], await TrashList()); // the trashed Project stays in the Trash, below an archived folder
        Assert.Equal((1, 1), (await _workspace.CountArchivedAsync(), await _workspace.CountTrashAsync()));
    }

    [Fact]
    public async Task B3_Unarchive_ClearsOnlyTheMarker_TheIndependentDescendantStatesReEmerge()
    {
        var ids = await Tree();
        var original = Data();
        await Mark(ids["2024"], FolderLifecycleKind.Archive);
        var archived = Data();

        var result = await _workspace.ClearFolderAsync(ids["2024"], FolderLifecycleKind.Archive);

        Assert.Equal(FolderClearOutcome.Cleared, result.Outcome);
        Assert.Equal(new WorkspaceNode(ids["2024"], null, WorkspaceNodeType.Folder, "2024", true), result.Folder!.Node);
        Assert.Null(result.Folder.ParentPath);
        var after = Data();
        var cleared = Assert.Single(Changed(archived, after)); // the logical diff is only 2024's marker...
        Assert.EndsWith("|FolderArchivedAt=NULL|FolderTrashedAt=NULL|LifecycleSerial=1", cleared);
        var fromOriginal = Assert.Single(Changed(original, after)); // ...and against the start, only its (higher) serial
        Assert.EndsWith("|LifecycleSerial=1", fromOriginal);
        Assert.Equal(["Project:ArchivedDeep", "Project:Gamma"], await ArchivedList());
        Assert.Equal(["Project:TrashedQ1"], await TrashList());
        Assert.Equal("Q1+,Q2,Delta", await Children(ids["2024"])); // Q2 holds only an archived Project: no expander
    }

    // ---- B4: strict Restore ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task B4_StrictRestore_EveryRefusalWritesNothing_SuccessIsTheOriginalParentAndName()
    {
        var ids = await Tree();
        await Mark(ids["Q1"], FolderLifecycleKind.Trash);
        Assert.Equal(["Folder:Q1"], await TrashList()); // TrashedQ1 is inside the outermost root Q1 now

        // NameConflict: a live folder took the freed name.
        var blocker = await Folder("Q1", ids["2024"]);
        await AssertRestoreRefused(ids["Q1"], FolderClearOutcome.NameConflict, "Q1");
        Raw("DELETE FROM WorkspaceNodes WHERE Id = $id;", ("$id", blocker));

        // ParentArchived: the original parent's chain is archived.
        await Mark(ids["2024"], FolderLifecycleKind.Archive);
        await AssertRestoreRefused(ids["Q1"], FolderClearOutcome.ParentArchived, null);
        await _workspace.ClearFolderAsync(ids["2024"], FolderLifecycleKind.Archive);

        // ParentTrashed: the original parent is itself in the Trash (Q1 is then nested and not listed).
        await Mark(ids["2024"], FolderLifecycleKind.Trash);
        Assert.Equal(["Folder:2024"], await TrashList());
        await AssertRestoreRefused(ids["Q1"], FolderClearOutcome.ParentTrashed, null);
        await _workspace.ClearFolderAsync(ids["2024"], FolderLifecycleKind.Trash);

        // Success: the original parent and name; never relocated, renamed or recreated.
        var result = await _workspace.ClearFolderAsync(ids["Q1"], FolderLifecycleKind.Trash);
        Assert.Equal(FolderClearOutcome.Cleared, result.Outcome);
        Assert.Equal(new WorkspaceNode(ids["Q1"], ids["2024"], WorkspaceNodeType.Folder, "Q1", true), result.Folder!.Node);
        Assert.Equal("2024", result.Folder.ParentPath);
        Assert.Equal(["Project:TrashedQ1"], await TrashList()); // its own trash state re-emerges
        Assert.Equal(FolderClearOutcome.NotTrashed, (await _other.ClearFolderAsync(ids["Q1"], FolderLifecycleKind.Trash)).Outcome); // §7.7: both Restore
    }

    private async Task AssertRestoreRefused(long folder, FolderClearOutcome expected, string? blocking)
    {
        var data = Data();
        new SqliteConnectionFactory(_root.Paths.DatabaseFile).ClearPools();
        var hash = BackupKit.Hash(_root.Paths.DatabaseFile);

        var result = await _workspace.ClearFolderAsync(folder, FolderLifecycleKind.Trash);

        Assert.Equal((expected, blocking), (result.Outcome, result.BlockingName));
        Assert.Null(result.Folder);
        Assert.Equal(data, Data()); // logical: nothing changed
        new SqliteConnectionFactory(_root.Paths.DatabaseFile).ClearPools();
        Assert.Equal(hash, BackupKit.Hash(_root.Paths.DatabaseFile)); // additional evidence: no write transaction committed
    }

    // ---- Refusals before any write (§7.2, §7.3, D15) ---------------------------------------------------------------------------

    [Fact]
    public async Task MarkRefusals_AreTyped_AndWriteNothing()
    {
        var ids = await Tree();
        await Mark(ids["Q2"], FolderLifecycleKind.Archive);
        await Mark(ids["Deep"], FolderLifecycleKind.Trash);
        var before = Data();
        var token = (await Confirm(ids["Q1"], FolderLifecycleKind.Archive)).Token;

        Assert.Equal(FolderLifecycleOutcome.AlreadyArchived, (await _workspace.PrepareFolderLifecycleAsync(ids["Q2"], FolderLifecycleKind.Archive)).Refusal);
        Assert.Equal(FolderLifecycleOutcome.FolderArchived, (await _workspace.PrepareFolderLifecycleAsync(ids["Q2"], FolderLifecycleKind.Trash)).Refusal); // D15
        Assert.Equal(FolderLifecycleOutcome.FolderTrashed, (await _workspace.PrepareFolderLifecycleAsync(ids["Deep"], FolderLifecycleKind.Archive)).Refusal);
        Assert.Equal(FolderLifecycleOutcome.FolderTrashed, (await _workspace.PrepareFolderLifecycleAsync(ids["Deep"], FolderLifecycleKind.Trash)).Refusal);
        Assert.Equal(FolderLifecycleOutcome.NotAFolder, (await _workspace.PrepareFolderLifecycleAsync(ids["Delta"], FolderLifecycleKind.Trash)).Refusal);
        Assert.Equal(FolderLifecycleOutcome.NotFound, (await _workspace.PrepareFolderLifecycleAsync(99999, FolderLifecycleKind.Trash)).Refusal);
        Assert.Equal(FolderLifecycleOutcome.AlreadyArchived, (await _workspace.MarkFolderAsync(ids["Q2"], FolderLifecycleKind.Archive, token)).Outcome);
        Assert.Equal(FolderLifecycleOutcome.FolderArchived, (await _workspace.MarkFolderAsync(ids["Q2"], FolderLifecycleKind.Trash, token)).Outcome);

        // Below an archived folder: AncestorNotLive, with the blocker's root.
        await Mark(ids["2024"], FolderLifecycleKind.Archive);
        before = Data();
        var refused = await _workspace.PrepareFolderLifecycleAsync(ids["Q1"], FolderLifecycleKind.Trash);
        Assert.Equal(FolderLifecycleOutcome.AncestorNotLive, refused.Refusal);
        Assert.Equal(new EffectiveLifecycle(LifecycleState.Archived, ids["2024"], WorkspaceNodeType.Folder), refused.Blocker);
        Assert.Equal(FolderLifecycleOutcome.AncestorNotLive, (await _workspace.MarkFolderAsync(ids["Q1"], FolderLifecycleKind.Archive, token)).Outcome);
        Assert.Equal(before, Data());
        await Assert.ThrowsAsync<ArgumentException>(() => _workspace.MarkFolderAsync(ids["2026"], FolderLifecycleKind.Trash, string.Empty));
    }

    // ---- D10: names ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task D10_AnArchivedFolderKeepsItsName_ATrashedFolderFreesIt()
    {
        var ids = await Tree();
        await Mark(ids["2024"], FolderLifecycleKind.Archive);
        var refused = await _workspace.CreateFolderAsync(null, "2024");
        Assert.Equal(WorkspaceFailure.DuplicateName, refused.Failure);
        Assert.Equal("An archived folder named '2024' already exists here. Unarchive it or choose another name.", refused.Error);

        await _workspace.ClearFolderAsync(ids["2024"], FolderLifecycleKind.Archive);
        await Mark(ids["2024"], FolderLifecycleKind.Trash);
        Assert.True((await _workspace.CreateFolderAsync(null, "2024")).Succeeded); // freed
        Assert.Equal(FolderClearOutcome.NameConflict, (await _workspace.ClearFolderAsync(ids["2024"], FolderLifecycleKind.Trash)).Outcome);
    }

    // ---- B5: atomic failure -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("archive")]
    [InlineData("trash")]
    [InlineData("restore")]
    public async Task B5_AnInjectedTriggerAbortInsideTheWrite_LeavesTheLogicalStateEntirelyBefore(string operation)
    {
        var ids = await Tree();
        if (operation == "restore")
            await Mark(ids["Q1"], FolderLifecycleKind.Trash);
        var before = Data();
        var token = operation == "restore" ? null : (await Confirm(ids["Q1"], operation == "archive" ? FolderLifecycleKind.Archive : FolderLifecycleKind.Trash)).Token;

        // A TEMP trigger on this connection aborts the statement right after the marker changes, inside the transaction.
        var failing = new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile, defaultTimeoutSeconds: null, opened: connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TEMP TRIGGER wh_test_abort AFTER UPDATE ON main.WorkspaceNodes BEGIN SELECT RAISE(ABORT, 'WH_TEST_INJECTED'); END;";
            command.ExecuteNonQuery();
        }), _clock);

        var ex = await Assert.ThrowsAsync<SqliteException>(() => operation switch
        {
            "archive" => failing.ArchiveFolderAsync(ids["Q1"], token!),
            "trash" => failing.TrashFolderAsync(ids["Q1"], token!),
            _ => (Task)failing.RestoreFolderAsync(ids["Q1"]),
        });

        Assert.Contains("WH_TEST_INJECTED", ex.Message);
        Assert.Equal(before, Data());
    }

    // ---- B7: Undo identity -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FolderLifecycleKind.Archive)]
    [InlineData(FolderLifecycleKind.Trash)]
    public async Task B7_Undo_AppliesOnlyToItsOwnEvent_AnotherWindowsCycleMakesItANoOp(FolderLifecycleKind kind)
    {
        var ids = await Tree();
        var first = (await Mark(ids["Q1"], kind)).Event!.Value;
        Assert.Equal(1, first.Serial);

        // Another window clears it and marks it again: serial 2 (with the fixed clock, even the timestamp is identical).
        Assert.Equal(FolderClearOutcome.Cleared, (await _other.ClearFolderAsync(ids["Q1"], kind)).Outcome);
        var second = (await Mark(ids["Q1"], kind, _other)).Event!.Value;
        Assert.Equal(2, second.Serial);
        var before = Data();

        Assert.Equal(FolderClearOutcome.MarkedAgain, (await _workspace.UndoFolderLifecycleAsync(first)).Outcome);
        Assert.Equal(before, Data());

        Assert.Equal(FolderClearOutcome.Cleared, (await _workspace.UndoFolderLifecycleAsync(second)).Outcome); // its own event
        Assert.Equal("-|-|2", Markers(ids["Q1"]));
        Assert.Equal(kind == FolderLifecycleKind.Archive ? FolderClearOutcome.NotArchived : FolderClearOutcome.NotTrashed,
            (await _workspace.UndoFolderLifecycleAsync(second)).Outcome);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _workspace.UndoFolderLifecycleAsync(first with { Serial = 0 }));
    }

    // ---- B10: referenced files are never touched -----------------------------------------------------------------------------------

    [Fact]
    public async Task B10_LinkedFilesAndFolders_AreNeverMovedEditedOrDeleted()
    {
        var ids = await Tree();
        var files = Path.Combine(Directory.GetParent(_root.Paths.Root)!.FullName, "linked-files");
        Directory.CreateDirectory(Path.Combine(files, "dir"));
        var file = Path.Combine(files, "spec.docx");
        await File.WriteAllTextAsync(file, "synthetic linked file");
        await File.WriteAllTextAsync(Path.Combine(files, "dir", "inside.txt"), "inside");
        await _content.CreateLinkAsync(ids["Alpha"], null, new ProjectLinkDraft("File", ProjectLinkType.File, file));
        await _content.CreateLinkAsync(ids["Alpha"], null, new ProjectLinkDraft("Dir", ProjectLinkType.Folder, Path.Combine(files, "dir")));
        var before = Fingerprint(files);

        await Mark(ids["2024"], FolderLifecycleKind.Archive);
        await _workspace.ClearFolderAsync(ids["2024"], FolderLifecycleKind.Archive);
        await Mark(ids["2024"], FolderLifecycleKind.Trash);
        await _workspace.ClearFolderAsync(ids["2024"], FolderLifecycleKind.Trash);

        Assert.Equal(before, Fingerprint(files));
    }

    /// <summary>Name, size, last write time and SHA-256 of every file, and every directory, below <paramref name="root"/>.</summary>
    private static List<string> Fingerprint(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path =>
            File.Exists(path)
                ? $"{path}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path):O}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}"
                : $"{path}|dir|{Directory.GetLastWriteTimeUtc(path):O}").ToList();

    // ---- B13: the preview ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task B13_ThePreview_CountsTheWholeSubtree_NamesAtMostThreeChildren_AndTheRestorePath()
    {
        var ids = await Tree();
        var trashedNote = (await _content.CreateNoteAsync(ids["Alpha"], null, new ProjectNoteDraft("Old", "x"))).Note!.Id;
        await _content.TrashAsync(new ContentRef(ContentKind.Note, trashedNote));
        await Folder("Q3", ids["2024"]);
        await Project("Zeta", ids["2024"]);

        var preview = await Confirm(ids["2024"], FolderLifecycleKind.Trash);

        Assert.Equal((ids["2024"], FolderLifecycleKind.Trash, "2024", (string?)null), (preview.RootId, preview.Kind, preview.Name, preview.Location));
        Assert.Equal(new LifecycleCounts(Folders: 4, Projects: 7, ArchivedProjects: 2, TrashedProjects: 1, Links: 2, Notes: 2, ItemsInProjectTrash: 1), preview.Counts);
        Assert.Equal(["Q1", "Q2", "Q3"], preview.ShownChildren); // tree order: folders first
        Assert.Equal(2, preview.MoreChildren); // Delta, Zeta
        Assert.Equal("2024", (await Confirm(ids["Q1"], FolderLifecycleKind.Archive)).Location);
        Assert.Equal("2024 / Q1", (await Confirm(ids["Deep"], FolderLifecycleKind.Archive)).Location);
        Assert.Matches("^[0-9a-f]{64}$", preview.Token);
    }

    [Fact]
    public async Task B13_FiveHundredDescendants_StayABoundedPreview()
    {
        var big = await Folder("Big");
        using (var connection = _root.Open())
        {
            for (var i = 0; i < 500; i++)
                Db.InsertNode(connection, big, "Folder", $"F{i:D3}");
        }

        var preview = await Confirm(big, FolderLifecycleKind.Trash);

        Assert.Equal(500, preview.Counts.Folders);
        Assert.Equal(["F000", "F001", "F002"], preview.ShownChildren);
        Assert.Equal(497, preview.MoreChildren);
    }

    // ---- B14-B18, B25-B34: confirmation identity ---------------------------------------------------------------------------------

    /// <summary>Confirms Trash of <paramref name="folder"/>, lets the other window act, then commits with the confirmed token.</summary>
    private async Task<(FolderLifecycleResult Result, List<string> Before)> CommitAfter(long folder, Func<Task> otherWindow, FolderLifecycleKind kind = FolderLifecycleKind.Trash)
    {
        var confirmed = await Confirm(folder, kind);
        await otherWindow();
        var before = Data();
        return (await _workspace.MarkFolderAsync(folder, kind, confirmed.Token), before);
    }

    private async Task AssertStale(long folder, Func<Task> otherWindow, FolderLifecycleKind kind = FolderLifecycleKind.Trash)
    {
        var (result, before) = await CommitAfter(folder, otherWindow, kind);
        Assert.Equal(FolderLifecycleOutcome.ConfirmationStale, result.Outcome);
        Assert.Null(result.Event);
        Assert.Equal(before, Data()); // nothing was written
        Assert.Equal((await Confirm(folder, kind)).Token, result.Fresh!.Token); // the fresh preview is the current state
    }

    private async Task AssertNotStale(long folder, Func<Task> otherWindow, FolderLifecycleKind kind = FolderLifecycleKind.Trash)
    {
        var (result, before) = await CommitAfter(folder, otherWindow, kind);
        Assert.Equal(FolderLifecycleOutcome.Committed, result.Outcome);
        Assert.Single(Changed(before, Data())); // only the Folder's marker row
    }

    [Fact]
    public async Task B14_AnEqualCountSwapOfProjects_IsStale_TheFreshDialogShowsTheNewNames_ASecondClickCommits()
    {
        var root = await Folder("2024");
        var a = await Project("A", root);
        foreach (var name in new[] { "B1", "C", "D", "E" })
            await Project(name, root);
        var outside = await Project("B");
        var confirmed = await Confirm(root, FolderLifecycleKind.Trash);
        Assert.Equal(5, confirmed.Counts.Projects);

        await _other.MoveAsync(a, null);
        await _other.MoveAsync(outside, root);
        var before = Data();
        var stale = await _workspace.MarkFolderAsync(root, FolderLifecycleKind.Trash, confirmed.Token);

        Assert.Equal(FolderLifecycleOutcome.ConfirmationStale, stale.Outcome);
        Assert.Equal(before, Data());
        Assert.Equal(5, stale.Fresh!.Counts.Projects); // still 5...
        Assert.Equal(["B", "B1", "C"], stale.Fresh.ShownChildren); // ...but other projects
        Assert.Equal(FolderLifecycleOutcome.Committed, (await _workspace.MarkFolderAsync(root, FolderLifecycleKind.Trash, stale.Fresh.Token)).Outcome);
    }

    [Fact]
    public async Task B15_AnEqualCountContentReplacement_IsStale_ButContentAttributeEditsAreNot()
    {
        var root = await Folder("2024");
        var p = await Project("P", root);
        var links = new List<long>();
        for (var i = 0; i < 140; i++)
            links.Add((await _content.CreateLinkAsync(p, null, new ProjectLinkDraft($"L{i:D3}", ProjectLinkType.Web, $"https://x.test/{i}"))).Value!.Id);
        var note = (await _content.CreateNoteAsync(p, null, new ProjectNoteDraft("N", "body"))).Note!;

        await AssertStale(root, async () =>
        {
            Raw("DELETE FROM ProjectLinks WHERE Id = $id;", ("$id", links[7])); // an external delete (the application has none)
            await _otherContent.CreateLinkAsync(p, null, new ProjectLinkDraft("Y", ProjectLinkType.Web, "https://y.test/"));
        });
        Assert.Equal(140, (await Confirm(root, FolderLifecycleKind.Trash)).Counts.Links); // the count never changed

        await AssertNotStale(root, async () =>
        {
            await _otherContent.UpdateLinkAsync(links[8], new ProjectLinkDraft("L008", ProjectLinkType.Web, "https://changed.test/"));
            await _otherContent.SaveNoteAsync(note.Id, note.Revision, new ProjectNoteDraft("N", "new body"));
        });
    }

    public static TheoryData<string> RelevantChanges() => ["rename the root", "rename an ancestor on the restore path", "rename a displayed child", "move a descendant folder inside the subtree", "archive a descendant project", "move the root"];

    [Theory]
    [MemberData(nameof(RelevantChanges))]
    public async Task B16_ARelevantRenameMoveOrLifecycleChange_IsStale(string change)
    {
        var top = await Folder("Clients");
        var root = await Folder("2024", top);
        var shown = await Folder("A-shown", root);
        var other = await Folder("B-shown", root);
        var deep = await Folder("Deep", shown);
        var p = await Project("P", other);
        var elsewhere = await Folder("Elsewhere");

        await AssertStale(root, change switch
        {
            "rename the root" => () => _other.RenameAsync(root, "2024b"),
            "rename an ancestor on the restore path" => () => _other.RenameAsync(top, "Customers"),
            "rename a displayed child" => () => _other.RenameAsync(shown, "A-renamed"),
            "move a descendant folder inside the subtree" => () => _other.MoveAsync(deep, other),
            "archive a descendant project" => () => _other.ArchiveProjectAsync(p),
            _ => () => _other.MoveAsync(root, elsewhere),
        });
    }

    [Fact]
    public async Task B16_RenamingADescendantThatIsNotDisplayed_IsNotStale()
    {
        var root = await Folder("2024");
        var shown = await Folder("A", root);
        var deep = await Folder("Deep", shown);
        await AssertNotStale(root, () => _other.RenameAsync(deep, "Deeper"));
    }

    [Fact]
    public async Task B17_ChangesInASiblingSubtreeAndContentEditsElsewhere_AreNotStale()
    {
        var ids = await Tree();
        var sibling = await Folder("Inner", ids["2026"]);
        await AssertNotStale(ids["2024"], async () =>
        {
            await _other.RenameAsync(ids["2026"], "2026b");
            await _other.MoveAsync(sibling, null);
            await _other.ArchiveProjectAsync(ids["Epsilon"]);
            await _otherContent.CreateNoteAsync(ids["Epsilon"], null, new ProjectNoteDraft("Elsewhere", "x"));
            await Mark(sibling, FolderLifecycleKind.Trash, _other);
        });
    }

    public static TheoryData<string> CaseOnlyRenames() => ["root", "ancestor", "displayed child"];

    [Theory]
    [MemberData(nameof(CaseOnlyRenames))]
    public async Task B25_ACaseOnlyRename_KeepsTheNameKey_ButIsStale_TheFreshPreviewShowsIt(string which)
    {
        var top = await Folder("Holding");
        var root = await Folder("Clients", top);
        var child = await Folder("Tools", root);
        var (node, renamed) = which switch { "root" => (root, "CLIENTS"), "ancestor" => (top, "HOLDING"), _ => (child, "TOOLS") };

        var confirmed = await Confirm(root, FolderLifecycleKind.Archive);
        Assert.True((await _other.RenameAsync(node, renamed)).Succeeded);
        var stale = await _workspace.MarkFolderAsync(root, FolderLifecycleKind.Archive, confirmed.Token);

        Assert.Equal(FolderLifecycleOutcome.ConfirmationStale, stale.Outcome);
        Assert.Equal("-|-|0", Markers(root));
        var fresh = stale.Fresh!;
        Assert.Contains(renamed, which == "root" ? fresh.Name : which == "ancestor" ? fresh.Location! : fresh.ShownChildren.Single());
    }

    [Fact]
    public async Task B26_ANormalizationEquivalentRename_IsStaleForADisplayedChild_NotForADescendantThatIsNotDisplayed()
    {
        var root = await Folder("Clients");
        var shown = await Folder("TOOLS", root);
        var deep = await Folder("TOOLS", shown);
        Assert.Equal(WorkspaceName.Key("TOOLS"), WorkspaceName.Key("ＴＯＯＬＳ")); // the premise: the same NameKey

        await AssertStale(root, () => _other.RenameAsync(shown, "ＴＯＯＬＳ"));
        Assert.Equal(["ＴＯＯＬＳ"], (await Confirm(root, FolderLifecycleKind.Trash)).ShownChildren);
        await AssertNotStale(root, () => _other.RenameAsync(deep, "ＴＯＯＬＳ"));
    }

    [Fact]
    public async Task B27_ADescendantProjectArchiveCycle_IsDetectedByItsArchiveSerial()
    {
        var root = await Folder("2024");
        var p = await Project("P", root);
        await _workspace.ArchiveProjectAsync(p); // archived at confirmation (serial 1)

        await AssertStale(root, async () =>
        {
            await _other.UnarchiveProjectAsync(p, ProjectStatus.Active);
            await _other.ArchiveProjectAsync(p); // archived again: serial 2
        });
    }

    [Theory]
    [InlineData(FolderLifecycleKind.Archive)]
    [InlineData(FolderLifecycleKind.Trash)]
    public async Task B28_ADescendantFolderCycleBackToLive_IsDetectedByItsLifecycleSerial(FolderLifecycleKind kind)
    {
        var root = await Folder("2024");
        var inner = await Folder("Inner", root);

        await AssertStale(root, async () =>
        {
            await Mark(inner, kind, _other);
            await _other.ClearFolderAsync(inner, kind); // live again, with the fixed clock: only the serial differs (0 → 1)
        });
        Assert.Equal("-|-|1", Markers(inner));
    }

    [Fact]
    public async Task B29_ATrashedProjectRestoredAndTrashedAgainInTheSameMillisecond_IsNotStale_TheCommitWritesOnlyTheFolderMarker()
    {
        var root = await Folder("2024");
        var p = await Project("P", root);
        await _workspace.TrashProjectAsync(p);
        string? first = null, second = null;

        var (result, before) = await CommitAfter(root, async () =>
        {
            first = TrashedAt(p);
            await _other.RestoreProjectAsync(p);
            await _other.TrashProjectAsync(p);
            second = TrashedAt(p);
        });

        Assert.Equal(first, second); // the premise (fixed clock): an identical TrashedAt — not an event identity (D17)
        Assert.Equal(FolderLifecycleOutcome.Committed, result.Outcome); // the narrowed guarantee: every compared field matches
        var row = Assert.Single(Changed(before, Data()));
        Assert.StartsWith($"WorkspaceNodes|Id={root}|", row);
    }

    [Fact]
    public async Task B29_TheSameCycleOneMillisecondLater_IsADifferentTrashedAt_AndIsStale()
    {
        var root = await Folder("2024");
        var p = await Project("P", root);
        await _workspace.TrashProjectAsync(p);

        await AssertStale(root, async () =>
        {
            await _other.RestoreProjectAsync(p);
            _clock.Now = _clock.Now.AddMilliseconds(1);
            await _other.TrashProjectAsync(p);
        });
    }

    private string? TrashedAt(long node)
    {
        using var connection = _root.Open();
        return Db.ScalarString(connection, "SELECT TrashedAt FROM WorkspaceNodes WHERE Id = $id;", ("$id", node));
    }

    [Fact]
    public async Task B31_ALiveProjectTrashedAndRestoredElsewhere_IsNotStale_AndIsIncludedAsConfirmed()
    {
        var root = await Folder("2024");
        var p = await Project("P", root);

        await AssertNotStale(root, async () =>
        {
            await _other.TrashProjectAsync(p);
            await _other.RestoreProjectAsync(p);
        });

        Assert.Equal($"ParentNotLive:Trashed:{root}", await Children(root));
        Assert.Equal(["Folder:2024"], await TrashList()); // P came along, in its confirmed state (live, same parent and name)
        Assert.Equal((root, "P", false), ((await _workspace.GetProjectAsync(p))!.ParentId!.Value, (await _workspace.GetProjectAsync(p))!.Name, (await _workspace.GetProjectAsync(p))!.IsTrashed));
    }

    [Fact]
    public async Task B32_ContentTrashThenRestore_IsNotStale_ButReTrashUnderANewEntry_Is()
    {
        var root = await Folder("2024");
        var p = await Project("P", root);
        var live = (await _content.CreateNoteAsync(p, null, new ProjectNoteDraft("Live", "x"))).Note!.Id;
        var inTrash = (await _content.CreateNoteAsync(p, null, new ProjectNoteDraft("Old", "x"))).Note!.Id;
        await _content.TrashAsync(new ContentRef(ContentKind.Note, inTrash));

        await AssertNotStale(root, async () =>
        {
            var entry = (await _otherContent.TrashAsync(new ContentRef(ContentKind.Note, live))).Entry!.Id;
            await _otherContent.RestoreAsync(entry); // TrashEntryId back to NULL: the narrowed, undetected cycle
        });

        await _workspace.ClearFolderAsync(root, FolderLifecycleKind.Trash);
        await AssertStale(root, async () =>
        {
            var current = (await _otherContent.ListTrashAsync(p)).Single(e => e.Root.Id == inTrash).Id;
            await _otherContent.RestoreAsync(current);
            await _otherContent.TrashAsync(new ContentRef(ContentKind.Note, inTrash)); // a NEW entry Id (AUTOINCREMENT), fixed clock
        });
    }

    /// <remarks>
    /// "A content row moved to another Project inside the subtree" (B33) cannot happen: V004 makes a content row's Project
    /// immutable (<see cref="ContentRowsCannotChangeProject_TheDatabaseRefusesIt"/>). Its observable equivalent — a row leaving one
    /// Project and an equal row appearing in another, counts unchanged — is the "content replaced across projects" case.
    /// </remarks>
    public static TheoryData<string> DifferentStates() =>
        ["project live then trashed", "project trashed then live", "content live then in its trash", "content replaced across projects", "project archived flag differs"];

    [Fact]
    public async Task ContentRowsCannotChangeProject_TheDatabaseRefusesIt()
    {
        var p = await Project("P");
        var q = await Project("Q");
        var link = (await _content.CreateLinkAsync(p, null, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://x.test/"))).Value!.Id;
        var ex = Assert.Throws<SqliteException>(() => Raw("UPDATE ProjectLinks SET ProjectNodeId = $q WHERE Id = $id;", ("$q", q), ("$id", link)));
        Assert.Contains("WH_CONTENT_IMMUTABLE", ex.Message);
    }

    [Theory]
    [MemberData(nameof(DifferentStates))]
    public async Task B33_AGenuinelyDifferentCurrentState_IsStale(string change)
    {
        var root = await Folder("2024");
        var p = await Project("P", root);
        var q = await Project("Q", root);
        var t = await Project("T", root);
        await _workspace.TrashProjectAsync(t);
        var link = (await _content.CreateLinkAsync(p, null, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://x.test/"))).Value!.Id;

        await AssertStale(root, change switch
        {
            "project live then trashed" => () => _other.TrashProjectAsync(p),
            "project trashed then live" => () => _other.RestoreProjectAsync(t),
            "content live then in its trash" => () => _otherContent.TrashAsync(new ContentRef(ContentKind.Link, link)),
            "content replaced across projects" => async () =>
            {
                Raw("DELETE FROM ProjectLinks WHERE Id = $id;", ("$id", link));
                await _otherContent.CreateLinkAsync(q, null, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://x.test/"));
            },
            _ => () => _other.ArchiveProjectAsync(q),
        });
    }

    // ---- B19-B23: nested scenarios (store side: panels, tree, search, reveal, writes, clearing) ---------------------------------

    [Fact]
    public async Task S1_B19_AnArchivedChildUnderATrashedAncestor_IsOnlyInTheTrash_AndReturnsArchived()
    {
        var f = await Folder("F");
        var c = await Folder("C", f);
        var item = await Project("Item in C", c);
        var note = (await _content.CreateNoteAsync(item, null, new ProjectNoteDraft("Findme", "x"))).Note!.Id;
        await Mark(c, FolderLifecycleKind.Archive);
        var archivedC = Data();
        await Mark(f, FolderLifecycleKind.Trash);

        Assert.Equal(["Folder:F"], await TrashList());
        Assert.Empty(await ArchivedList());
        Assert.Equal(string.Empty, await Children(null));
        Assert.Empty((await Search("Findme")).Hits);
        Assert.Equal(RevealResolveStatus.FolderTrashed, (await Resolve(SearchResultKind.Note, note)).Status);
        Assert.Equal(RevealResolveStatus.FolderTrashed, (await Resolve(SearchResultKind.Project, item)).Status);
        Assert.Equal(WorkspaceFailure.AncestorNotLive, (await _workspace.RenameAsync(item, "X")).Failure); // node write
        Assert.Equal(NoteWriteStatus.ProjectTrashed, (await _content.CreateNoteAsync(item, null, new ProjectNoteDraft("N", "x"))).Status); // content write

        await _workspace.ClearFolderAsync(f, FolderLifecycleKind.Trash);
        Assert.Equal(["Folder:C"], await ArchivedList());
        var hit = (await Search("Findme")).Hits.Single();
        Assert.True(hit.InArchivedProject);
        Assert.Equal("C", hit.ArchivedFolderName);
        Assert.Equal(RevealResolution.InArchivedFolder(c), await Resolve(SearchResultKind.Note, note));
        Assert.EndsWith("|FolderArchivedAt=NULL|FolderTrashedAt=NULL|LifecycleSerial=1", Assert.Single(Changed(archivedC, Data()))); // B23: only F's serial
    }

    [Fact]
    public async Task S2_B20_ATrashedProjectUnderAnArchivedAncestor_IsListedWithIt_RestoreWaitsForUnarchive()
    {
        var f = await Folder("F");
        var p = await Project("P", f);
        var other = await Project("Other", f);
        await _workspace.TrashProjectAsync(p);
        await Mark(f, FolderLifecycleKind.Archive);

        Assert.Equal(["Project:P@F"], await TrashList());
        Assert.Equal(["Folder:F"], await ArchivedList());
        Assert.DoesNotContain((await Search("P")).Hits, h => h.Item == new SearchItemRef(SearchResultKind.Project, p));
        Assert.True((await Search("Other")).Hits.Single().InArchivedProject);
        Assert.Equal(RevealResolution.InArchivedFolder(f), await Resolve(SearchResultKind.Project, other));
        var before = Data();
        Assert.Equal(ProjectRestoreOutcome.ParentArchived, (await _workspace.RestoreProjectAsync(p)).Outcome);
        Assert.Equal(before, Data());

        await _workspace.ClearFolderAsync(f, FolderLifecycleKind.Archive);
        Assert.Equal(["Project:P"], await TrashList()); // still in the Trash, now restorable
        Assert.Equal(ProjectRestoreOutcome.Restored, (await _workspace.RestoreProjectAsync(p)).Outcome);
    }

    [Fact]
    public async Task S3_B21_MultipleNestedMarkers_EveryIntermediateStateListsExactlyTheOutermostRoots()
    {
        var r = await Folder("R");
        var g = await Folder("G", r);
        var h = await Folder("H", r);
        var q = await Folder("Q", g);
        var inH = await Project("In H", h);
        await Mark(q, FolderLifecycleKind.Archive);
        await Mark(g, FolderLifecycleKind.Trash);
        await Mark(h, FolderLifecycleKind.Archive);
        await Mark(r, FolderLifecycleKind.Archive);

        Assert.Equal(["Folder:R"], await ArchivedList());
        Assert.Equal(["Folder:G@R"], await TrashList());
        Assert.Equal(RevealResolution.InArchivedFolder(r), await Resolve(SearchResultKind.Project, inH));
        Assert.Equal(FolderClearOutcome.ParentArchived, (await _workspace.ClearFolderAsync(g, FolderLifecycleKind.Trash)).Outcome);
        Assert.Equal(FolderClearOutcome.ParentArchived, (await _workspace.ClearFolderAsync(h, FolderLifecycleKind.Archive)).Outcome);

        var before = Data();
        await _workspace.ClearFolderAsync(r, FolderLifecycleKind.Archive);
        Assert.Single(Changed(before, Data())); // B23: only R's marker
        Assert.Equal(["Folder:H"], await ArchivedList());
        Assert.Equal(["Folder:G"], await TrashList());
        Assert.Equal(RevealResolution.InArchivedFolder(h), await Resolve(SearchResultKind.Project, inH));

        before = Data();
        await _workspace.ClearFolderAsync(g, FolderLifecycleKind.Trash);
        Assert.Single(Changed(before, Data())); // only G's marker
        Assert.Equal(["Folder:H", "Folder:Q"], await ArchivedList());
        Assert.Empty(await TrashList());
    }

    [Fact]
    public async Task S4_B22_ProjectsKeepTheirOwnStatesUnderMarkedFolders()
    {
        var f = await Folder("F");
        var completed = await Project("Completed one", f, ProjectStatus.Completed);
        var archived = await Project("Archived one", f);
        await _workspace.ArchiveProjectAsync(archived);
        await Mark(f, FolderLifecycleKind.Archive);
        await _workspace.ClearFolderAsync(f, FolderLifecycleKind.Archive);
        Assert.Equal(ProjectStatus.Completed, (await _workspace.GetProjectAsync(completed))!.Status);
        Assert.True((await _workspace.GetProjectAsync(completed))!.Effective.IsLive);

        await Mark(f, FolderLifecycleKind.Trash);
        Assert.Equal(LifecycleState.Trashed, (await _workspace.GetProjectAsync(archived))!.Effective.State);
        await _workspace.ClearFolderAsync(f, FolderLifecycleKind.Trash);
        Assert.Equal(["Project:Archived one"], await ArchivedList());
        using var connection = _root.Open();
        Assert.Equal("Archived|1", Db.ScalarString(connection, "SELECT Status || '|' || ArchiveSerial FROM Projects WHERE NodeId = $id;", ("$id", archived)));
    }

    // ---- §7.5 Move targets and §7.7 concurrency (store side) -------------------------------------------------------------------

    [Fact]
    public async Task MoveTargets_ExcludeEveryFolderThatIsNotLive_AndMoveChecksSourceAndDestination()
    {
        var ids = await Tree();
        await Mark(ids["Q2"], FolderLifecycleKind.Archive);
        await Mark(ids["Deep"], FolderLifecycleKind.Trash);

        var targets = (await _workspace.ListMoveTargetsAsync(ids["Epsilon"])).Select(t => t.Path).ToList();
        Assert.Equal(["2024", "2024 / Q1", "2026"], targets);

        var before = Data();
        Assert.Equal(WorkspaceFailure.DestinationNotLive, (await _workspace.MoveAsync(ids["Epsilon"], ids["Q2"])).Failure);
        Assert.Equal(WorkspaceFailure.AncestorNotLive, (await _workspace.MoveAsync(ids["Alpha"], ids["2026"])).Failure); // source below a trashed folder
        Assert.Equal(WorkspaceFailure.AncestorNotLive, (await _workspace.MoveAsync(ids["Q2"], ids["2026"])).Failure); // the marked folder itself
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task Concurrency_ArchiveThenAStaleMove_IsRefused_MoveFirstMakesTheArchiveStale()
    {
        var ids = await Tree();
        var confirmed = await Confirm(ids["2024"], FolderLifecycleKind.Archive);
        await _other.MarkFolderAsync(ids["2024"], FolderLifecycleKind.Archive, (await Confirm(ids["2024"], FolderLifecycleKind.Archive, _other)).Token);
        Assert.Equal(WorkspaceFailure.AncestorNotLive, (await _workspace.MoveAsync(ids["Delta"], null)).Failure); // §7.7 row 1
        Assert.Equal(FolderLifecycleOutcome.AlreadyArchived, (await _workspace.MarkFolderAsync(ids["2024"], FolderLifecycleKind.Archive, confirmed.Token)).Outcome);

        await _workspace.ClearFolderAsync(ids["2024"], FolderLifecycleKind.Archive);
        await AssertStale(ids["2024"], () => _other.MoveAsync(ids["Delta"], null), FolderLifecycleKind.Archive); // §7.7 row 2
    }

    // ---- search / resolve helpers ------------------------------------------------------------------------------------------------

    private Task<WorkspaceSearchResult> Search(string text) => _search.SearchAsync(WorkspaceSearchService.Parse(text)!, 200, CancellationToken.None);

    private Task<RevealResolution> Resolve(SearchResultKind kind, long id) => _search.ResolveAsync(new SearchItemRef(kind, id), CancellationToken.None);
}
