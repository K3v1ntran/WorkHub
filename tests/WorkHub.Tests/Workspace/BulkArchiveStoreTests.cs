using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 19 Bulk Archive through the real SQLite store (proposal Revision 2 + R2a–R2d, §4.3 and §5): the location-bound
/// <see cref="IWorkspaceStore.ArchiveProjectInPlaceAsync"/> and its fixed check order, the dialog's candidate read, the additive
/// <see cref="WorkspaceNodeState.Node"/>, and R1b (the single Archive keeps no location check). A second service on the same
/// database plays the other window.
/// </summary>
public sealed class BulkArchiveStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceService _other;
    private readonly long _f;
    private readonly long _x;
    private readonly long[] _p;

    public BulkArchiveStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _other = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _f = Folder(null, "F");
        _x = Folder(null, "X");
        _p = [Project(_f, "P1"), Project(_f, "P2"), Project(_f, "P3"), Project(_f, "P4"), Project(_f, "P5")];
    }

    public void Dispose() => _root.Dispose();

    private long Folder(long? parent, string name) => _other.CreateFolderAsync(parent, name).Result.Value!.Id;

    private long Project(long? parent, string name, ProjectStatus status = ProjectStatus.Completed) =>
        _other.CreateProjectAsync(parent, new ProjectDraft(name, status, null, "Owner A")).Result.Value!.Id;

    private (string Status, long Serial, long? Parent, string Name, string UpdatedAt) Row(long nodeId)
    {
        using var connection = _root.Open();
        var text = Db.ScalarString(connection,
            "SELECT p.Status || '|' || p.ArchiveSerial || '|' || ifnull(n.ParentId, '') || '|' || n.Name || '|' || n.UpdatedAt FROM Projects AS p JOIN WorkspaceNodes AS n ON n.Id = p.NodeId WHERE p.NodeId = $id;",
            ("$id", nodeId))!.Split('|');
        return (text[0], long.Parse(text[1], System.Globalization.CultureInfo.InvariantCulture),
            text[2].Length == 0 ? null : long.Parse(text[2], System.Globalization.CultureInfo.InvariantCulture), text[3], text[4]);
    }

    private async Task Archive(long folder, FolderLifecycleKind kind) =>
        Assert.Equal(FolderLifecycleOutcome.Committed,
            (await _other.MarkFolderAsync(folder, kind, (await _other.PrepareFolderLifecycleAsync(folder, kind)).Confirmation!.Token)).Outcome);

    [Fact]
    public async Task B2_SelectAll_ArchivesEach_WithStatusAndSerialOnly()
    {
        var before = _p.Select(Row).ToList();
        foreach (var id in _p)
        {
            var result = await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(id, _f));
            Assert.Equal(BulkArchiveOutcome.Archived, result.Outcome);
            Assert.Equal(new ArchiveEvent(id, 1, ProjectStatus.Completed), result.Event);
        }

        for (var i = 0; i < _p.Length; i++)
        {
            var after = Row(_p[i]);
            Assert.Equal(("Archived", 1L), (after.Status, after.Serial));
            Assert.Equal((before[i].Parent, before[i].Name, before[i].UpdatedAt), (after.Parent, after.Name, after.UpdatedAt));
        }
    }

    [Fact]
    public async Task B17_B18_MovedProject_IsRefusedWithoutWrite_AndTheNextTargetIsStillArchived()
    {
        Assert.True((await _other.MoveAsync(_p[1], _x)).Value);

        var first = await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_p[0], _f));
        var moved = await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_p[1], _f));
        var third = await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_p[2], _f));

        Assert.Equal(BulkArchiveOutcome.Archived, first.Outcome);
        Assert.Equal(BulkArchiveOutcome.MovedElsewhere, moved.Outcome);
        Assert.Equal(_x, moved.CurrentParentId);
        Assert.Null(moved.Event);
        Assert.Equal(BulkArchiveOutcome.Archived, third.Outcome);
        Assert.Equal(("Completed", 0L, (long?)_x), (Row(_p[1]).Status, Row(_p[1]).Serial, Row(_p[1]).Parent));
    }

    [Fact]
    public async Task B19_TopLevel_NullIsComparedAsNull_AndNeverReportsLocationNotLive()
    {
        var t1 = Project(null, "T1");
        var t2 = Project(null, "T2");
        var t3 = Project(null, "T3");
        Assert.True((await _other.MoveAsync(t2, _f)).Value);

        Assert.Equal(BulkArchiveOutcome.Archived, (await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(t1, null))).Outcome);
        var moved = await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(t2, null));
        Assert.Equal((BulkArchiveOutcome.MovedElsewhere, (long?)_f), (moved.Outcome, moved.CurrentParentId));
        Assert.Equal(BulkArchiveOutcome.Archived, (await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(t3, null))).Outcome);
        Assert.Equal(("Completed", 0L), (Row(t2).Status, Row(t2).Serial));
    }

    [Fact]
    public async Task B19b_NullExpectedParent_DoesNotMatchAFolder()
    {
        var result = await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_p[0], null));

        Assert.Equal((BulkArchiveOutcome.MovedElsewhere, (long?)_f), (result.Outcome, result.CurrentParentId));
        Assert.Equal(("Completed", 0L), (Row(_p[0]).Status, Row(_p[0]).Serial));
    }

    [Fact]
    public async Task B21_MovedAndTrashed_ReportsMovedElsewhere_LocationIsCheckedFirst()
    {
        Assert.True((await _other.MoveAsync(_p[1], _x)).Value);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _other.TrashProjectAsync(_p[1])).Outcome);

        Assert.Equal(BulkArchiveOutcome.MovedElsewhere, (await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_p[1], _f))).Outcome);
    }

    [Theory]
    [InlineData(FolderLifecycleKind.Archive, LifecycleState.Archived)]
    [InlineData(FolderLifecycleKind.Trash, LifecycleState.Trashed)]
    public async Task B11_B11b_ConfirmedPlaceMarked_ReportsLocationNotLive_WithTheFolderAsMarkedNode(FolderLifecycleKind kind, LifecycleState state)
    {
        Assert.Equal(BulkArchiveOutcome.Archived, (await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_p[0], _f))).Outcome);
        await Archive(_f, kind);

        var result = await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_p[1], _f));

        Assert.Equal((BulkArchiveOutcome.LocationNotLive, (LifecycleState?)state, (long?)_f), (result.Outcome, result.LocationState, result.MarkedNodeId));
        Assert.Null(result.Event);
        Assert.Equal(("Completed", 0L), (Row(_p[1]).Status, Row(_p[1]).Serial));
    }

    [Fact]
    public async Task B11c_AncestorOfTheConfirmedPlaceArchived_NamesTheAncestor()
    {
        var g = Folder(null, "G");
        var inner = Folder(g, "Inner");
        var q = Project(inner, "Q");
        await Archive(g, FolderLifecycleKind.Archive);

        var result = await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(q, inner));

        Assert.Equal((BulkArchiveOutcome.LocationNotLive, (LifecycleState?)LifecycleState.Archived, (long?)g), (result.Outcome, result.LocationState, result.MarkedNodeId));
    }

    [Fact]
    public async Task B20_MovedIntoABranchThatIsThenArchived_IsMovedElsewhere_NotLocationNotLive()
    {
        Assert.True((await _other.MoveAsync(_p[1], _x)).Value);
        await Archive(_x, FolderLifecycleKind.Archive);

        var result = await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_p[1], _f));

        Assert.Equal(BulkArchiveOutcome.MovedElsewhere, result.Outcome);
        Assert.Equal(("Completed", 0L), (Row(_p[1]).Status, Row(_p[1]).Serial));
    }

    [Fact]
    public async Task B9_B11e_PerProjectRefusals_WriteNothing()
    {
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(_p[1])).Outcome);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _other.TrashProjectAsync(_p[2])).Outcome);

        Assert.Equal(BulkArchiveOutcome.AlreadyArchived, (await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_p[1], _f))).Outcome);
        Assert.Equal(BulkArchiveOutcome.ProjectTrashed, (await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_p[2], _f))).Outcome);
        Assert.Equal(BulkArchiveOutcome.NotFound, (await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(999_999, _f))).Outcome);
        Assert.Equal(BulkArchiveOutcome.NotAProject, (await _workspace.ArchiveProjectInPlaceAsync(new BulkArchiveTarget(_x, null))).Outcome);
        Assert.Equal(("Archived", 1L), (Row(_p[1]).Status, Row(_p[1]).Serial));
        Assert.Equal(0L, Row(_p[2]).Serial);
    }

    [Fact]
    public async Task R1b_SingleArchive_HasNoLocationCheck_AndKeepsAncestorNotLive()
    {
        Assert.True((await _other.MoveAsync(_p[1], _x)).Value);
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _workspace.ArchiveProjectAsync(_p[1])).Outcome);

        await Archive(_f, FolderLifecycleKind.Archive);
        Assert.Equal(ProjectArchiveOutcome.AncestorNotLive, (await _workspace.ArchiveProjectAsync(_p[2])).Outcome);
    }

    [Fact]
    public async Task Candidates_ListLiveProjectsOnly_SortedLikeTheTree_WithStatusOwnerAndUpdated()
    {
        Folder(_f, "Sub");
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _other.ArchiveProjectAsync(_p[3])).Outcome);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _other.TrashProjectAsync(_p[4])).Outcome);
        var active = Project(_f, "A-active", ProjectStatus.Active);

        var list = await _workspace.ListBulkArchiveCandidatesAsync(_f);

        Assert.Equal(ChildrenReadStatus.Listed, list.Status);
        Assert.False(list.Truncated);
        Assert.Equal(["A-active", "P1", "P2", "P3"], list.Projects.Select(p => p.Name).ToList());
        Assert.Equal(ProjectStatus.Active, list.Projects.Single(p => p.NodeId == active).Status);
        Assert.All(list.Projects, p => Assert.Equal("Owner A", p.Owner));
    }

    [Fact]
    public async Task Candidates_PlaceNotLiveOrMissing_ListNothing()
    {
        await Archive(_f, FolderLifecycleKind.Archive);

        Assert.Equal(ChildrenReadStatus.ParentNotLive, (await _workspace.ListBulkArchiveCandidatesAsync(_f)).Status);
        Assert.Equal(ChildrenReadStatus.ParentMissing, (await _workspace.ListBulkArchiveCandidatesAsync(999_999)).Status);
    }

    [Fact]
    public async Task B16_Candidates_AreBoundedTo500_AndSayTruncated()
    {
        var big = Folder(null, "Big");
        for (var i = 0; i < 501; i++)
            Project(big, $"N{i:D4}", ProjectStatus.Active);

        var list = await _workspace.ListBulkArchiveCandidatesAsync(big);

        Assert.True(list.Truncated);
        Assert.Equal(BulkArchiveCandidates.MaxRows, list.Projects.Count);
        Assert.Equal("N0000", list.Projects[0].Name);
    }

    [Fact]
    public async Task R2d_NodeState_CarriesTheNodeRow_FromTheSameRead()
    {
        Folder(_f, "Sub");
        Assert.True((await _other.RenameAsync(_p[0], "Renamed")).Succeeded);

        var project = await _workspace.GetNodeStateAsync(_p[0]);
        var folder = await _workspace.GetNodeStateAsync(_f);

        Assert.Equal(new WorkspaceNode(_p[0], _f, WorkspaceNodeType.Project, "Renamed", false), project!.Node);
        Assert.Equal(new WorkspaceNode(_f, null, WorkspaceNodeType.Folder, "F", true), folder!.Node);
        Assert.Null(await _workspace.GetNodeStateAsync(999_999));
    }
}
