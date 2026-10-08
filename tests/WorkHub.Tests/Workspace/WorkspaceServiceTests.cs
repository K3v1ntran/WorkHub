using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>WorkspaceService + SqliteWorkspaceStore against a real isolated SQLite database.</summary>
public sealed class WorkspaceServiceTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteWorkspaceStore _store;
    private readonly WorkspaceService _workspace;

    public WorkspaceServiceTests()
    {
        _root.CreateInitializer().Initialize();
        _store = new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile));
        _workspace = new WorkspaceService(_store);
    }

    public void Dispose() => _root.Dispose();

    private long Count(string table)
    {
        using var connection = _root.Open();
        return Db.Count(connection, table);
    }

    private string? Scalar(string sql)
    {
        using var connection = _root.Open();
        return Db.ScalarString(connection, sql);
    }

    private async Task<WorkspaceNode> Folder(string name, long? parent = null) =>
        (await _workspace.CreateFolderAsync(parent, name)).Value!;

    private async Task<WorkspaceNode> Project(string name, long? parent = null) =>
        (await _workspace.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!;

    private async Task<long?> ParentOf(long id) => (await _workspace.GetProjectAsync(id))?.ParentId
        ?? (Scalar($"SELECT CAST(ParentId AS TEXT) FROM WorkspaceNodes WHERE Id = {id};") is { } p ? long.Parse(p) : null);

    // ---- Create, list, details -----------------------------------------------------------------------------------

    [Fact]
    public async Task CreateFoldersAndProjects_AtRootAndNested_ListedFoldersFirstByName()
    {
        var clients = await Folder("Clients");
        var archive = await Folder("archive");
        var nested = await Folder("東京", clients.Id);
        var project = await Project("販売管理システム", nested.Id);
        await Project("Alpha");

        Assert.Equal(["archive", "Clients", "Alpha"], (await _workspace.ListChildrenAsync(null)).Select(n => n.Name));
        var underClients = Assert.Single(await _workspace.ListChildrenAsync(clients.Id));
        Assert.Equal((nested.Id, WorkspaceNodeType.Folder, true), (underClients.Id, underClients.Type, underClients.HasChildren));
        var underTokyo = Assert.Single(await _workspace.ListChildrenAsync(nested.Id));
        Assert.Equal((project.Id, WorkspaceNodeType.Project, "販売管理システム", false), (underTokyo.Id, underTokyo.Type, underTokyo.Name, underTokyo.HasChildren));
        Assert.False((await _workspace.ListChildrenAsync(null)).Single(n => n.Id == archive.Id).HasChildren);
    }

    [Fact]
    public async Task Project_DetailsRoundTrip_WithGeneratedImmutableProjectId()
    {
        var created = await _workspace.CreateProjectAsync(null, new ProjectDraft(" 新製品  開発 ", ProjectStatus.Planning, "説明\r\n2行目", " 田中 ", ""));
        Assert.True(created.Succeeded, created.Error);

        var details = (await _workspace.GetProjectAsync(created.Value!.Id))!;
        Assert.Equal("新製品  開発", details.Name);
        Assert.Equal(ProjectStatus.Planning, details.Status);
        Assert.Equal("説明\r\n2行目", details.Description);
        Assert.Equal("田中", details.Owner);
        Assert.Null(details.ProjectManager);
        Assert.Matches("^[0-9a-f]{32}$", details.ProjectId);
        Assert.Null(details.ParentId);

        var updated = await _workspace.UpdateProjectAsync(details.NodeId, new ProjectDraft("新製品開発", ProjectStatus.OnHold, null, null, "鈴木"));
        Assert.True(updated.Succeeded, updated.Error);
        Assert.Equal(details.ProjectId, updated.Value!.ProjectId);
        Assert.Equal(("新製品開発", ProjectStatus.OnHold, (string?)null, (string?)null, "鈴木"),
            (updated.Value.Name, updated.Value.Status, updated.Value.Description, updated.Value.Owner, updated.Value.ProjectManager));
        Assert.Equal(updated.Value, await _workspace.GetProjectAsync(details.NodeId));
        Assert.Equal("新製品開発".ToUpperInvariant(), Scalar($"SELECT NameKey FROM WorkspaceNodes WHERE Id = {details.NodeId};"));
    }

    [Fact]
    public async Task ArchivedStatus_CannotBeChosen()
    {
        var create = await _workspace.CreateProjectAsync(null, new ProjectDraft("P", ProjectStatus.Archived));
        Assert.Equal(ProjectDraftValidator.Fields.Status, create.Field);

        var project = await Project("Q");
        var update = await _workspace.UpdateProjectAsync(project.Id, new ProjectDraft("Q", ProjectStatus.Archived));
        Assert.False(update.Succeeded);
        Assert.Equal(ProjectStatus.Active, (await _workspace.GetProjectAsync(project.Id))!.Status);
    }

    [Fact]
    public async Task GetProject_ForAFolderOrMissingNode_IsNull()
    {
        var folder = await Folder("F");

        Assert.Null(await _workspace.GetProjectAsync(folder.Id));
        Assert.Null(await _workspace.GetProjectAsync(12345));
    }

    // ---- Names ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SiblingConflicts_AreRejected_AcrossFoldersAndProjects_AtRootAndChildLevel()
    {
        var parent = await Folder("Parent");
        await Folder("Docs");
        await Folder("Docs", parent.Id);

        var rootProject = await _workspace.CreateProjectAsync(null, new ProjectDraft("ＤＯＣＳ", ProjectStatus.Active));
        var childFolder = await _workspace.CreateFolderAsync(parent.Id, "docs");

        Assert.Equal(WorkspaceService.NameField, rootProject.Field);
        Assert.Contains("already exists here", rootProject.Error);
        Assert.Contains("already exists here", childFolder.Error);
        Assert.Equal(3, Count("WorkspaceNodes"));
        Assert.Equal(0, Count("Projects"));
    }

    [Fact]
    public async Task LongestValidName_WithExpandedKey_IsStoredAndUnique()
    {
        var name = new string('㍿', 200);
        var other = await Folder("Other");

        var first = await _workspace.CreateProjectAsync(null, new ProjectDraft(name, ProjectStatus.Active));
        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(name, Scalar($"SELECT Name FROM WorkspaceNodes WHERE Id = {first.Value!.Id};"));
        Assert.Equal(WorkspaceName.Key(name), Scalar($"SELECT NameKey FROM WorkspaceNodes WHERE Id = {first.Value.Id};"));
        Assert.Equal(800, WorkspaceName.Key(name).Length);

        var duplicate = await _workspace.CreateProjectAsync(null, new ProjectDraft(name, ProjectStatus.Active));
        Assert.Contains("already exists here", duplicate.Error);
        Assert.Equal(2, Count("WorkspaceNodes"));

        var elsewhere = await _workspace.CreateFolderAsync(other.Id, name);
        Assert.True(elsewhere.Succeeded, elsewhere.Error);
    }

    [Fact]
    public async Task WidthAndCaseVariants_WithExpandedKeys_Collide()
    {
        var a = new string('㍿', 199) + "Ａ"; // ... + full-width 'Ａ'
        var b = new string('㍿', 199) + "a";
        Assert.Equal(200, a.Length);
        Assert.Equal(200, b.Length);
        Assert.NotEqual(a, b);
        Assert.Equal(797, WorkspaceName.Key(a).Length);
        Assert.Equal(WorkspaceName.Key(a), WorkspaceName.Key(b));

        Assert.True((await _workspace.CreateFolderAsync(null, a)).Succeeded);
        var clash = await _workspace.CreateProjectAsync(null, new ProjectDraft(b, ProjectStatus.Active));

        Assert.Contains("already exists here", clash.Error);
        Assert.Equal(1, Count("WorkspaceNodes"));
    }

    [Fact]
    public async Task Rename_RoundTrips_RejectsDuplicates_AndReportsMissingNodes()
    {
        var a = await Folder("A");
        await Project("B");

        var renamed = await _workspace.RenameAsync(a.Id, "  エー  ");
        Assert.Equal("エー", renamed.Value);
        Assert.Equal("エー", Scalar($"SELECT Name FROM WorkspaceNodes WHERE Id = {a.Id};"));

        var duplicate = await _workspace.RenameAsync(a.Id, "b");
        Assert.Contains("already exists here", duplicate.Error);
        Assert.Equal("エー", Scalar($"SELECT Name FROM WorkspaceNodes WHERE Id = {a.Id};"));

        Assert.Equal("This folder or project no longer exists.", (await _workspace.RenameAsync(999, "X")).Error);
        Assert.Equal(WorkspaceService.NameField, (await _workspace.RenameAsync(a.Id, "")).Field);
    }

    [Fact]
    public async Task RenamingToADifferentCaseOfItsOwnName_IsAllowed()
    {
        var a = await Folder("alpha");

        Assert.True((await _workspace.RenameAsync(a.Id, "Alpha")).Succeeded);
        Assert.Equal("Alpha", Scalar($"SELECT Name FROM WorkspaceNodes WHERE Id = {a.Id};"));
    }

    // ---- Parents ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_UnderAProjectOrAMissingParent_IsRejected()
    {
        var project = await Project("P");

        var underProject = await _workspace.CreateFolderAsync(project.Id, "X");
        var underMissing = await _workspace.CreateProjectAsync(999, new ProjectDraft("Y", ProjectStatus.Active));

        Assert.Equal("Only folders can contain folders and projects.", underProject.Error);
        Assert.Equal("The destination folder no longer exists.", underMissing.Error);
        Assert.Equal(1, Count("WorkspaceNodes"));
        Assert.Equal(1, Count("Projects"));
    }

    // ---- Atomicity ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProjectCreate_FailingDetailsInsert_LeavesNoOrphanNode()
    {
        const string projectId = "00000000000000000000000000000001";
        await _store.CreateProjectAsync(null, projectId, new ProjectDraft("First", ProjectStatus.Active));

        // Same ProjectId: the node INSERT succeeds, the details INSERT violates UQ_Projects_ProjectId.
        await Assert.ThrowsAnyAsync<Exception>(() => _store.CreateProjectAsync(null, projectId, new ProjectDraft("Second", ProjectStatus.Active)));

        Assert.Equal(1, Count("WorkspaceNodes"));
        Assert.Equal(1, Count("Projects"));
        Assert.Null(Scalar("SELECT Name FROM WorkspaceNodes WHERE Name = 'Second';"));
    }

    [Fact]
    public async Task ProjectUpdate_FailingDetailsUpdate_RollsBackTheNodeRename()
    {
        var project = await Project("Original");

        // Bypass the validator: the node rename succeeds, then the Projects CHECK rejects the overlong Owner.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _store.UpdateProjectAsync(project.Id, new ProjectDraft("Renamed", ProjectStatus.Completed, null, new string('o', 101))));

        var details = (await _workspace.GetProjectAsync(project.Id))!;
        Assert.Equal(("Original", ProjectStatus.Active, (string?)null), (details.Name, details.Status, details.Owner));
    }

    [Fact]
    public async Task ProjectUpdate_DuplicateName_ChangesNothing()
    {
        await Folder("Taken");
        var project = await Project("Mine");

        var result = await _workspace.UpdateProjectAsync(project.Id, new ProjectDraft("taken", ProjectStatus.Completed, "new"));

        Assert.Contains("already exists here", result.Error);
        var details = (await _workspace.GetProjectAsync(project.Id))!;
        Assert.Equal(("Mine", ProjectStatus.Active, (string?)null), (details.Name, details.Status, details.Description));
    }

    // ---- Move ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Move_IntoFolder_ToRoot_AndNoOp()
    {
        var a = await Folder("A");
        var b = await Folder("B");
        var project = await Project("P", a.Id);

        Assert.True((await _workspace.MoveAsync(project.Id, b.Id)).Value);
        Assert.Equal(b.Id, await ParentOf(project.Id));

        Assert.True((await _workspace.MoveAsync(a.Id, b.Id)).Value);
        Assert.Equal(b.Id, await ParentOf(a.Id));

        Assert.True((await _workspace.MoveAsync(project.Id, null)).Value);
        Assert.Null(await ParentOf(project.Id));

        var before = Scalar($"SELECT UpdatedAt FROM WorkspaceNodes WHERE Id = {a.Id};");
        var noOp = await _workspace.MoveAsync(a.Id, b.Id);
        Assert.True(noOp.Succeeded);
        Assert.False(noOp.Value);
        Assert.Equal(before, Scalar($"SELECT UpdatedAt FROM WorkspaceNodes WHERE Id = {a.Id};"));
    }

    [Fact]
    public async Task Move_IntoItselfOrADescendant_IsRejected_AndChangesNothing()
    {
        var a = await Folder("A");
        var b = await Folder("B", a.Id);
        var c = await Folder("C", b.Id);

        foreach (var target in new[] { a.Id, b.Id, c.Id })
        {
            var result = await _workspace.MoveAsync(a.Id, target);
            Assert.Equal("A folder cannot be moved into itself or into one of its own subfolders.", result.Error);
        }

        Assert.Null(await ParentOf(a.Id));
        Assert.Equal(a.Id, await ParentOf(b.Id));
        Assert.Equal(b.Id, await ParentOf(c.Id));
    }

    [Fact]
    public async Task Move_DestinationConflict_KeepsTheOriginalState()
    {
        var a = await Folder("A");
        var b = await Folder("B");
        var mine = await Project("Report", a.Id);
        await Folder("REPORT", b.Id);
        var updatedBefore = Scalar($"SELECT UpdatedAt FROM WorkspaceNodes WHERE Id = {mine.Id};");

        var result = await _workspace.MoveAsync(mine.Id, b.Id);

        Assert.Equal("The destination already contains a folder or project named 'Report'.", result.Error);
        Assert.Equal(a.Id, await ParentOf(mine.Id));
        Assert.Equal(updatedBefore, Scalar($"SELECT UpdatedAt FROM WorkspaceNodes WHERE Id = {mine.Id};"));
    }

    [Fact]
    public async Task Move_ToRootConflict_IsRejected()
    {
        var a = await Folder("A");
        await Project("Same");
        var nested = await Project("same", a.Id);

        var result = await _workspace.MoveAsync(nested.Id, null);

        Assert.Contains("already contains", result.Error);
        Assert.Equal(a.Id, await ParentOf(nested.Id));
    }

    [Fact]
    public async Task Move_IntoProjectOrMissingTarget_OrMissingNode_IsRejected()
    {
        var folder = await Folder("F");
        var project = await Project("P");

        Assert.Equal("Only folders can contain folders and projects.", (await _workspace.MoveAsync(folder.Id, project.Id)).Error);
        Assert.Equal("The destination folder no longer exists.", (await _workspace.MoveAsync(folder.Id, 999)).Error);
        Assert.Equal("This folder or project no longer exists.", (await _workspace.MoveAsync(999, folder.Id)).Error);
        Assert.Null(await ParentOf(folder.Id));
    }

    [Fact]
    public async Task MoveTargets_ExcludeTheNodeAndItsSubtree_AndShowPaths()
    {
        var a = await Folder("A");
        var b = await Folder("B", a.Id);
        await Folder("C", b.Id);
        var d = await Folder("D");
        await Folder("E", d.Id);
        await Project("P", d.Id);

        var forB = await _workspace.ListMoveTargetsAsync(b.Id);
        Assert.Equal(["A", "D", "D / E"], forB.Select(f => f.Path));

        var forProject = await _workspace.ListMoveTargetsAsync(999);
        Assert.Equal(["A", "A / B", "A / B / C", "D", "D / E"], forProject.Select(f => f.Path));
    }
}
