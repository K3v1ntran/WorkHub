using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 20 Bulk Unarchive through the real SQLite store (proposal Revision 2 + R2a, §4.3 and §5; D9(a)): the candidate read with
/// each Project's <c>ArchiveSerial</c> (BS2), the serial-bound <see cref="IWorkspaceStore.UnarchiveProjectBoundAsync"/> and its fixed
/// check order (BS1), the data it leaves (BU3, BU4), BU15 (archived again on a second connection) and BU15b (the single Unarchive
/// stays state-based). A second service on the same database plays the other window.
/// <para>Fixture: live folders F and G; archived Projects A1–A3 in F, A4 in G, A5 at the top level (each serial 1); archived Folder
/// X holding archived Project A6; archived Folder Y holding the not-archived Project A7.</para>
/// </summary>
public sealed class BulkUnarchiveStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceService _other;
    private readonly long _f;
    private readonly long _g;
    private readonly long _x;
    private readonly long _y;
    private readonly long[] _a; // A1..A7 at index 0..6

    public BulkUnarchiveStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _other = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _f = Folder(null, "F");
        _g = Folder(null, "G");
        _x = Folder(null, "X");
        _y = Folder(null, "Y");
        _a = [Project(_f, "A1"), Project(_f, "A2"), Project(_f, "A3"), Project(_g, "A4"), Project(null, "A5"), Project(_x, "A6"), Project(_y, "A7")];
        foreach (var id in _a.Take(6))
            Assert.Equal(ProjectArchiveOutcome.Archived, _other.ArchiveProjectAsync(id).Result.Outcome);
        MarkFolder(_x, FolderLifecycleKind.Archive).Wait();
        MarkFolder(_y, FolderLifecycleKind.Archive).Wait();
    }

    public void Dispose() => _root.Dispose();

    private long Folder(long? parent, string name) => _other.CreateFolderAsync(parent, name).Result.Value!.Id;

    private long Project(long? parent, string name, ProjectStatus status = ProjectStatus.Completed) =>
        _other.CreateProjectAsync(parent, new ProjectDraft(name, status, "Description", "Owner A", null, ["Alpha", "Beta"])).Result.Value!.Id;

    private async Task MarkFolder(long folder, FolderLifecycleKind kind) =>
        Assert.Equal(FolderLifecycleOutcome.Committed,
            (await _other.MarkFolderAsync(folder, kind, (await _other.PrepareFolderLifecycleAsync(folder, kind)).Confirmation!.Token)).Outcome);

    /// <summary>Every stored column of the Project's node, details and tags, as one text (equal before/after = nothing written).</summary>
    private string Bytes(long nodeId)
    {
        using var connection = _root.Open();
        return string.Join("¦",
            Db.ScalarString(connection, "SELECT quote(Id)||quote(ParentId)||quote(NodeType)||quote(Name)||quote(NameKey)||quote(UpdatedAt)||quote(TrashedAt) FROM WorkspaceNodes WHERE Id = $id;", ("$id", nodeId)),
            Db.ScalarString(connection, "SELECT quote(Status)||quote(ArchiveSerial)||quote(Description)||quote(Owner) FROM Projects WHERE NodeId = $id;", ("$id", nodeId)),
            Db.ScalarString(connection, "SELECT group_concat(Name, ',') FROM (SELECT Name FROM ProjectTags WHERE ProjectNodeId = $id ORDER BY Name);", ("$id", nodeId)));
    }

    private (string Status, long Serial) Row(long nodeId)
    {
        using var connection = _root.Open();
        var text = Db.ScalarString(connection, "SELECT Status || '|' || ArchiveSerial FROM Projects WHERE NodeId = $id;", ("$id", nodeId))!.Split('|');
        return (text[0], long.Parse(text[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private Task<BulkUnarchiveResult> Bound(long id, long serial, ProjectStatus status = ProjectStatus.Active) =>
        _workspace.UnarchiveProjectBoundAsync(new BulkUnarchiveTarget(id, serial), status);

    // ---- BS2: the candidate read ------------------------------------------------------------------------------------------

    [Fact]
    public async Task BS2_Candidates_AreTheArchivedProjectRoots_InPanelOrder_EachWithItsSerial()
    {
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(_a[2], ProjectStatus.Completed)).Outcome);
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(_a[2])).Outcome); // A3 now at serial 2

        var candidates = await _workspace.ListBulkUnarchiveCandidatesAsync();

        Assert.False(candidates.Truncated);
        Assert.Equal(new[] { "A1", "A2", "A3", "A4", "A5" }, candidates.Projects.Select(c => c.Name)); // not X, A6 (inside X) or A7
        Assert.Equal(new long[] { 1, 1, 2, 1, 1 }, candidates.Projects.Select(c => c.ArchiveSerial));
        Assert.Equal(new long?[] { _f, _f, _f, _g, null }, candidates.Projects.Select(c => c.ParentId));
        Assert.Equal(new[] { "F", "F", "F", "G", null }, candidates.Projects.Select(c => c.ParentPath));
        var listed = await _workspace.ListArchivedAsync(); // the panel: the same Project rows, plus the Folders X and Y
        Assert.Equal(listed.Where(e => e.Type == WorkspaceNodeType.Project).Select(e => e.NodeId), candidates.Projects.Select(c => c.NodeId));
        Assert.Contains(listed, e => e.NodeId == _x);
    }

    [Fact]
    public async Task BS2_MoreThan500ArchivedProjects_List500_AndReportTruncated()
    {
        var h = Folder(null, "H");
        for (var i = 0; i < 497; i++) // 5 already archived roots + 497 = 502
        {
            var id = (await _other.CreateProjectAsync(h, new ProjectDraft($"B{i:D3}", ProjectStatus.Completed))).Value!.Id;
            Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(id)).Outcome);
        }

        var candidates = await _workspace.ListBulkUnarchiveCandidatesAsync();

        Assert.True(candidates.Truncated);
        Assert.Equal(BulkUnarchiveCandidates.MaxRows, candidates.Projects.Count);
        Assert.Equal("A1", candidates.Projects[0].Name); // the panel order, then bounded
        Assert.All(candidates.Projects, c => Assert.Equal(1L, c.ArchiveSerial));
    }

    // ---- BU3 / BU4: the data an Unarchived result leaves ----------------------------------------------------------------------

    [Fact]
    public async Task BU3_Unarchived_ChangesOnlyTheStatus_TheSerialAndEverythingElseStay()
    {
        var targets = new[] { _a[0], _a[1], _a[3] };
        var before = targets.ToDictionary(id => id, Bytes);

        foreach (var id in targets)
        {
            var result = await Bound(id, 1);
            Assert.Equal(BulkUnarchiveOutcome.Unarchived, result.Outcome);
            Assert.Equal(id, result.Unarchived!.Node.Id);
            Assert.Null(result.CurrentSerial);
        }

        foreach (var id in targets)
        {
            Assert.Equal(("Active", 1L), Row(id));
            Assert.Equal(before[id].Replace("'Archived'", "'Active'", StringComparison.Ordinal), Bytes(id));
        }

        Assert.Equal(("Archived", 1L), Row(_a[2])); // not ticked: untouched
    }

    [Fact]
    public async Task BU4_TopLevelProject_ReturnsToTheTopLevel()
    {
        var result = await Bound(_a[4], 1, ProjectStatus.OnHold);

        Assert.Equal(BulkUnarchiveOutcome.Unarchived, result.Outcome);
        Assert.Null(result.Unarchived!.Node.ParentId);
        Assert.Null(result.Unarchived.ParentPath);
        Assert.Equal(("OnHold", 1L), Row(_a[4]));
    }

    // ---- BU15 / BU15b: bound to the archive event the user saw ----------------------------------------------------------------

    [Fact]
    public async Task BU15_ArchivedAgainOnASecondConnection_IsRefusedWithoutWrite_AndTheNextTargetIsStillUnarchived()
    {
        var candidates = await _workspace.ListBulkUnarchiveCandidatesAsync();
        var serials = candidates.Projects.ToDictionary(c => c.NodeId, c => c.ArchiveSerial);
        Assert.Equal(BulkUnarchiveOutcome.Unarchived, (await Bound(_a[0], serials[_a[0]])).Outcome);

        // The other window, after the checklist read: unarchive A2, then archive it again (serial s+1).
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(_a[1], ProjectStatus.Completed)).Outcome);
        Assert.Equal(new ArchiveEvent(_a[1], 2, ProjectStatus.Completed), (await _other.ArchiveProjectAsync(_a[1])).Event);
        var a2Before = Bytes(_a[1]);

        var again = await Bound(_a[1], serials[_a[1]]);
        var next = await Bound(_a[3], serials[_a[3]]);

        Assert.Equal(BulkUnarchiveOutcome.ArchivedAgain, again.Outcome);
        Assert.Equal(2L, again.CurrentSerial);
        Assert.Null(again.Unarchived);
        Assert.Equal(a2Before, Bytes(_a[1])); // neither status nor serial
        Assert.Equal(("Archived", 2L), Row(_a[1]));
        Assert.Equal(BulkUnarchiveOutcome.Unarchived, next.Outcome);
        Assert.Equal(("Active", 1L), Row(_a[0]));
        Assert.Equal(("Active", 1L), Row(_a[3]));
    }

    [Fact]
    public async Task BU15b_Control_TheSingleUnarchiveStaysStateBased_AndEndsTheCurrentArchive()
    {
        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, (await _other.UnarchiveProjectAsync(_a[1], ProjectStatus.Completed)).Outcome);
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(_a[1])).Outcome); // serial 2

        var single = await _workspace.UnarchiveProjectAsync(_a[1], ProjectStatus.Active);

        Assert.Equal(ProjectUnarchiveOutcome.Unarchived, single.Outcome);
        Assert.Equal(("Active", 2L), Row(_a[1]));
    }

    // ---- BS1: the check order ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData('a', BulkUnarchiveOutcome.NotArchived)]
    [InlineData('b', BulkUnarchiveOutcome.ArchivedAgain)]
    [InlineData('c', BulkUnarchiveOutcome.ArchivedAgain)] // step 4 before step 5
    [InlineData('d', BulkUnarchiveOutcome.ParentArchived)]
    [InlineData('e', BulkUnarchiveOutcome.ProjectTrashed)]
    public async Task BS1_CheckOrder_EachRefusalWritesNothing(char variant, BulkUnarchiveOutcome expected)
    {
        var a2 = _a[1];
        switch (variant)
        {
            case 'a':
                await _other.UnarchiveProjectAsync(a2, ProjectStatus.Completed);
                break;
            case 'b':
                await _other.UnarchiveProjectAsync(a2, ProjectStatus.Completed);
                await _other.ArchiveProjectAsync(a2);
                break;
            case 'c':
                await _other.UnarchiveProjectAsync(a2, ProjectStatus.Completed);
                await _other.ArchiveProjectAsync(a2);
                await MarkFolder(_f, FolderLifecycleKind.Archive);
                break;
            case 'd':
                await MarkFolder(_f, FolderLifecycleKind.Archive);
                break;
            case 'e':
                await _other.UnarchiveProjectAsync(a2, ProjectStatus.Completed);
                Assert.Equal(ProjectTrashOutcome.Trashed, (await _other.TrashProjectAsync(a2)).Outcome);
                break;
        }

        var before = Bytes(a2);
        var result = await Bound(a2, 1);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Unarchived);
        Assert.Equal(before, Bytes(a2));
    }

    [Fact]
    public async Task BS1_ParentTrashed_AndTheGuards()
    {
        Assert.Equal(FolderLifecycleOutcome.Committed,
            (await _other.MarkFolderAsync(_g, FolderLifecycleKind.Trash, (await _other.PrepareFolderLifecycleAsync(_g, FolderLifecycleKind.Trash)).Confirmation!.Token)).Outcome);
        var before = Bytes(_a[3]);

        Assert.Equal(BulkUnarchiveOutcome.ParentTrashed, (await Bound(_a[3], 1)).Outcome);
        Assert.Equal(before, Bytes(_a[3]));
        Assert.Equal(BulkUnarchiveOutcome.NotAProject, (await Bound(_f, 1)).Outcome);
        Assert.Equal(BulkUnarchiveOutcome.NotFound, (await Bound(999_999, 1)).Outcome);
        Assert.Equal(BulkUnarchiveOutcome.ParentArchived, (await Bound(_a[5], 1)).Outcome); // A6 inside the archived Folder X
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Bound(_a[0], 1, ProjectStatus.Archived));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Bound(_a[0], 0));
        Assert.Equal(("Archived", 1L), Row(_a[0]));
    }
}
