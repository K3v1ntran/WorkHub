using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>ProjectFolder/Link use cases through the real SQLite store, including move concurrency (revision 3 §8.1, D14).</summary>
public sealed class ProjectContentStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly ProjectContentService _content;
    private readonly long _alpha;
    private readonly long _beta;

    public ProjectContentStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _content = NewService();
        var workspace = new WorkspaceService(new SqliteWorkspaceStore(_connections));
        _alpha = workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active)).Result.Value!.Id;
        _beta = workspace.CreateProjectAsync(null, new ProjectDraft("Beta", ProjectStatus.Active)).Result.Value!.Id;
    }

    public void Dispose() => _root.Dispose();

    /// <summary>An independent service on the same database file: models a second window or process.</summary>
    private ProjectContentService NewService(int? maxDepth = null) => new(
        maxDepth is { } depth ? new SqliteProjectContentStore(_connections, null, depth) : new SqliteProjectContentStore(_connections),
        new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));

    private async Task<ProjectFolder> NewFolder(string name, long? parent = null, long? project = null) =>
        (await _content.CreateFolderAsync(project ?? _alpha, parent, name)).Value!;

    private async Task<ProjectLink> NewLink(string name, long? folder = null, ProjectLinkType type = ProjectLinkType.Web, string target = "https://example.test/") =>
        (await _content.CreateLinkAsync(_alpha, folder, new ProjectLinkDraft(name, type, target))).Value!;

    private string? Row(string sql, long id)
    {
        using var connection = _root.Open();
        return Db.ScalarString(connection, sql, ("$id", id));
    }

    // ---- Create / list / rename / edit --------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAndList_RootAndNested_SortedFoldersThenLinks_WithBreadcrumb()
    {
        var docs = await NewFolder("docs");
        await NewFolder("Archive");
        var specs = await NewFolder("Specs", docs.Id);
        await NewLink("zeta");
        await NewLink("Alpha link");
        await NewLink("Inside", specs.Id);

        var root = (await _content.ListAsync(_alpha, null)).Listing!;
        Assert.Equal(["Archive", "docs"], root.Folders.Select(f => f.Name));
        Assert.Equal(["Alpha link", "zeta"], root.Links.Select(l => l.Name));
        Assert.Empty(root.Breadcrumb);

        var nested = (await _content.ListAsync(_alpha, specs.Id)).Listing!;
        Assert.Equal(["docs", "Specs"], nested.Breadcrumb.Select(c => c.Name));
        Assert.Equal(["Inside"], nested.Links.Select(l => l.Name));
        Assert.Empty((await _content.ListAsync(_beta, null)).Listing!.Folders);
    }

    [Fact]
    public async Task List_ReportsMissingProjectAndForeignOrMissingFolder()
    {
        var betaFolder = await NewFolder("B", project: _beta);

        Assert.Equal(ContentContainerProblem.ProjectNotFound, (await _content.ListAsync(9999, null)).Problem);
        Assert.Equal(ContentContainerProblem.FolderNotFound, (await _content.ListAsync(_alpha, 9999)).Problem);
        Assert.Equal(ContentContainerProblem.WrongProject, (await _content.ListAsync(_alpha, betaFolder.Id)).Problem);
    }

    [Fact]
    public async Task Create_RejectsDuplicateFolderNames_MissingParentsAndForeignParents_WritingNothing()
    {
        var docs = await NewFolder("Docs");
        var betaFolder = await NewFolder("B", project: _beta);

        var duplicate = await _content.CreateFolderAsync(_alpha, null, "  ＤＯＣＳ ");
        Assert.Equal(ProjectContentService.NameField, duplicate.Field);
        Assert.Contains("already exists", duplicate.Error);
        Assert.False((await _content.CreateFolderAsync(_alpha, 9999, "X")).Succeeded);
        Assert.Contains("another project", (await _content.CreateFolderAsync(_alpha, betaFolder.Id, "X")).Error);
        Assert.Contains("no longer exists", (await _content.CreateFolderAsync(9999, null, "X")).Error);
        Assert.Contains("another project", (await _content.CreateLinkAsync(_alpha, betaFolder.Id, new ProjectLinkDraft("L", ProjectLinkType.Web, "https://x.test/"))).Error);

        using var connection = _root.Open();
        Assert.Equal(2, Db.Count(connection, "ProjectFolders"));
        Assert.Equal(0, Db.Count(connection, "ProjectLinks"));
        Assert.True(docs.Id > 0);
    }

    [Fact]
    public async Task RenameFolder_And_EditLink_UpdateOnlyTheRowAndItsTimestamp_NotTheProject()
    {
        var projectUpdatedAt = Row("SELECT UpdatedAt FROM WorkspaceNodes WHERE Id = $id;", _alpha);
        var docs = await NewFolder("Docs");
        var link = await NewLink("Spec", docs.Id, ProjectLinkType.File, @"C:\Docs\spec v3.docx");

        var renamed = await _content.RenameFolderAsync(docs.Id, "  Documents ");
        Assert.Equal("Documents", renamed.Value!.Name);
        var edited = await _content.UpdateLinkAsync(link.Id, new ProjectLinkDraft("Spec (web)", ProjectLinkType.Web, " https://example.test/a b?x=%20#frag "));
        Assert.False(edited.Succeeded); // whitespace inside a URL is rejected, not rewritten
        edited = await _content.UpdateLinkAsync(link.Id, new ProjectLinkDraft("Spec (web)", ProjectLinkType.Web, " https://example.test/a?x=%20#frag "));
        Assert.Equal("https://example.test/a?x=%20#frag", edited.Value!.Target); // only outer whitespace trimmed
        Assert.Equal(ProjectLinkType.Web, edited.Value.Type);
        Assert.Equal(docs.Id, edited.Value.FolderId);

        Assert.Equal(projectUpdatedAt, Row("SELECT UpdatedAt FROM WorkspaceNodes WHERE Id = $id;", _alpha));
        Assert.False((await _content.RenameFolderAsync(9999, "X")).Succeeded);
        Assert.False((await _content.UpdateLinkAsync(9999, new ProjectLinkDraft("X", ProjectLinkType.Web, "https://x.test/"))).Succeeded);
    }

    [Theory]
    [InlineData(ProjectLinkType.File, @"C:\Projects\見積 v2\spec #1 (final) 100%.xlsx")]
    [InlineData(ProjectLinkType.Folder, @"\\server\share\Projects\..\Alpha")]
    [InlineData(ProjectLinkType.Folder, @"C:\Work\out\")]
    [InlineData(ProjectLinkType.Web, "https://portal.example.test/path?q=a%2Fb&x=1#section")]
    public async Task ValidTargets_AreStoredByteForByte(ProjectLinkType type, string target)
    {
        var link = await _content.CreateLinkAsync(_alpha, null, new ProjectLinkDraft("T", type, "\t " + target + "  "));

        Assert.True(link.Succeeded, link.Error);
        Assert.Equal(target, link.Value!.Target);
        Assert.Equal(target, Row("SELECT Target FROM ProjectLinks WHERE Id = $id;", link.Value.Id));
    }

    [Theory]
    [InlineData(ProjectLinkType.File, @"Docs\relative.txt")]
    [InlineData(ProjectLinkType.File, "file:///C:/x.txt")]
    [InlineData(ProjectLinkType.Folder, @"\\?\C:\x")]
    [InlineData(ProjectLinkType.Folder, @"%USERPROFILE%\Docs")]
    [InlineData(ProjectLinkType.Web, "https://user:pass@example.test/")]
    [InlineData(ProjectLinkType.Web, "javascript:alert(1)")]
    [InlineData(ProjectLinkType.Web, "cmd:/c calc")]
    [InlineData(ProjectLinkType.Web, "C:\\x.txt")]
    public async Task InvalidTargets_AreRejected_WithATargetFieldError(ProjectLinkType type, string target)
    {
        var link = await _content.CreateLinkAsync(_alpha, null, new ProjectLinkDraft("T", type, target));

        Assert.False(link.Succeeded);
        Assert.Equal(ProjectLinkValidator.Fields.Target, link.Field);
    }

    // ---- Moves --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MoveFolder_MovesTheSubtree_AndOnlyTheMovedRowTimestampChanges()
    {
        var docs = await NewFolder("Docs");
        var specs = await NewFolder("Specs", docs.Id);
        var archive = await NewFolder("Archive");
        var inside = await NewLink("Inside", specs.Id);
        var linkUpdated = Row("SELECT UpdatedAt FROM ProjectLinks WHERE Id = $id;", inside.Id);

        var moved = await _content.MoveFolderAsync(docs.Id, null, archive.Id);

        Assert.Equal(ContentMoveOutcome.Moved, moved.Outcome);
        var listing = (await _content.ListAsync(_alpha, specs.Id)).Listing!;
        Assert.Equal(["Archive", "Docs", "Specs"], listing.Breadcrumb.Select(c => c.Name));
        Assert.Equal(["Inside"], listing.Links.Select(l => l.Name));
        Assert.Equal(linkUpdated, Row("SELECT UpdatedAt FROM ProjectLinks WHERE Id = $id;", inside.Id));
    }

    [Fact]
    public async Task Move_IntoSelfDescendantOrDeepChain_IsRefused_WritingNothing()
    {
        var a = await NewFolder("A");
        var b = await NewFolder("B", a.Id);
        var c = await NewFolder("C", b.Id);
        var d = await NewFolder("D", c.Id);

        Assert.Equal(ContentMoveOutcome.Cycle, (await _content.MoveFolderAsync(a.Id, null, a.Id)).Outcome);
        Assert.Equal(ContentMoveOutcome.Cycle, (await _content.MoveFolderAsync(a.Id, null, b.Id)).Outcome);
        Assert.Equal(ContentMoveOutcome.Cycle, (await _content.MoveFolderAsync(a.Id, null, d.Id)).Outcome);
        Assert.Null(Row("SELECT ParentFolderId FROM ProjectFolders WHERE Id = $id;", a.Id));
    }

    [Fact]
    public async Task Move_ToAnotherProjectsFolder_OrAMissingDestination_IsRefused()
    {
        var docs = await NewFolder("Docs");
        var link = await NewLink("L");
        var betaFolder = await NewFolder("B", project: _beta);

        Assert.Equal(ContentMoveOutcome.WrongProject, (await _content.MoveFolderAsync(docs.Id, null, betaFolder.Id)).Outcome);
        Assert.Equal(ContentMoveOutcome.WrongProject, (await _content.MoveLinkAsync(link.Id, null, betaFolder.Id)).Outcome);
        Assert.Equal(ContentMoveOutcome.DestinationNotFound, (await _content.MoveLinkAsync(link.Id, null, 9999)).Outcome);
        Assert.Equal(ContentMoveOutcome.NotFound, (await _content.MoveLinkAsync(9999, null, docs.Id)).Outcome);
    }

    [Fact]
    public async Task Move_ToADestinationWithTheSameFolderName_IsDuplicate()
    {
        var target = await NewFolder("Target");
        await NewFolder("Docs", target.Id);
        var docs = await NewFolder("DOCS");

        var result = await _content.MoveFolderAsync(docs.Id, null, target.Id);

        Assert.Equal(ContentMoveOutcome.Duplicate, result.Outcome);
        Assert.Null(Row("SELECT ParentFolderId FROM ProjectFolders WHERE Id = $id;", docs.Id));
    }

    [Fact]
    public async Task AlreadyThere_WritesNothing_AndKeepsTheTimestamp()
    {
        var docs = await NewFolder("Docs");
        var link = await NewLink("L", docs.Id);
        var before = Row("SELECT UpdatedAt FROM ProjectLinks WHERE Id = $id;", link.Id);

        var result = await _content.MoveLinkAsync(link.Id, docs.Id, docs.Id);

        Assert.Equal(ContentMoveOutcome.AlreadyThere, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.False(result.Moved);
        Assert.Equal(before, Row("SELECT UpdatedAt FROM ProjectLinks WHERE Id = $id;", link.Id));
        Assert.Equal(ContentMoveOutcome.AlreadyThere, (await _content.MoveFolderAsync(docs.Id, null, null)).Outcome);
    }

    [Fact]
    public async Task TwoWindows_StaleMove_IsMovedMeanwhile_AndDoesNotMoveAgain()
    {
        var p = await NewFolder("P");
        var q = await NewFolder("Q");
        var r = await NewFolder("R");
        var link = await NewLink("X", p.Id);
        var windowB = NewService();

        // A observed X in P (its dialog captured expected = P). B moves X from P to Q and commits.
        Assert.Equal(ContentMoveOutcome.Moved, (await windowB.MoveLinkAsync(link.Id, p.Id, q.Id)).Outcome);
        var afterB = Row("SELECT FolderId || '|' || UpdatedAt FROM ProjectLinks WHERE Id = $id;", link.Id);

        var a = await _content.MoveLinkAsync(link.Id, p.Id, r.Id);

        Assert.Equal(ContentMoveOutcome.MovedMeanwhile, a.Outcome);
        Assert.True(a.IsStale);
        Assert.Contains("moved meanwhile", a.Error);
        Assert.Equal(afterB, Row("SELECT FolderId || '|' || UpdatedAt FROM ProjectLinks WHERE Id = $id;", link.Id));
    }

    [Fact]
    public async Task TwoWindows_StaleFolderMove_IsMovedMeanwhile()
    {
        var p = await NewFolder("P");
        var q = await NewFolder("Q");
        var r = await NewFolder("R");
        var x = await NewFolder("X", p.Id);

        await NewService().MoveFolderAsync(x.Id, p.Id, q.Id);
        var afterB = Row("SELECT ParentFolderId || '|' || UpdatedAt FROM ProjectFolders WHERE Id = $id;", x.Id);

        Assert.Equal(ContentMoveOutcome.MovedMeanwhile, (await _content.MoveFolderAsync(x.Id, p.Id, r.Id)).Outcome);
        Assert.Equal(afterB, Row("SELECT ParentFolderId || '|' || UpdatedAt FROM ProjectFolders WHERE Id = $id;", x.Id));
    }

    [Fact]
    public async Task MovedMeanwhile_TakesPrecedenceOverAlreadyThere()
    {
        var p = await NewFolder("P");
        var q = await NewFolder("Q");
        var link = await NewLink("X", p.Id);
        await NewService().MoveLinkAsync(link.Id, p.Id, q.Id);
        var afterB = Row("SELECT FolderId || '|' || UpdatedAt FROM ProjectLinks WHERE Id = $id;", link.Id);

        // A picks Q, where B already put X: still a stale observation.
        Assert.Equal(ContentMoveOutcome.MovedMeanwhile, (await _content.MoveLinkAsync(link.Id, p.Id, q.Id)).Outcome);
        Assert.Equal(afterB, Row("SELECT FolderId || '|' || UpdatedAt FROM ProjectLinks WHERE Id = $id;", link.Id));
    }

    [Fact]
    public async Task RootAsObservedContainer_IsComparedNullSafely_BothDirections()
    {
        var f = await NewFolder("F");
        var g = await NewFolder("G");
        var rootLink = await NewLink("AtRoot");
        var folderLink = await NewLink("InF", f.Id);

        await NewService().MoveLinkAsync(rootLink.Id, null, f.Id);
        Assert.Equal(ContentMoveOutcome.MovedMeanwhile, (await _content.MoveLinkAsync(rootLink.Id, null, g.Id)).Outcome);

        await NewService().MoveLinkAsync(folderLink.Id, f.Id, null);
        Assert.Equal(ContentMoveOutcome.MovedMeanwhile, (await _content.MoveLinkAsync(folderLink.Id, f.Id, g.Id)).Outcome);
        Assert.Null(Row("SELECT FolderId FROM ProjectLinks WHERE Id = $id;", folderLink.Id));
    }

    [Fact]
    public async Task NotFound_TakesPrecedenceOverAStaleExpectation()
    {
        var p = await NewFolder("P");
        var link = await NewLink("X", p.Id);
        using (var connection = _root.Open())
            Db.Exec(connection, "DELETE FROM ProjectLinks WHERE Id = $id;", ("$id", link.Id)); // external removal (no API deletes)

        Assert.Equal(ContentMoveOutcome.NotFound, (await _content.MoveLinkAsync(link.Id, 12345, null)).Outcome);
    }

    [Fact]
    public async Task AncestryWalk_FailsClosed_AtItsDepthBound()
    {
        var a = await NewFolder("A");
        var b = await NewFolder("B", a.Id);
        var c = await NewFolder("C", b.Id);
        var mover = await NewFolder("Mover");

        // Destination C is 3 levels deep; a walk bounded at depth 1 cannot reach a root and must refuse.
        var shallow = NewService(maxDepth: 1);
        Assert.Equal(ContentMoveOutcome.AncestryUnverified, (await shallow.MoveFolderAsync(mover.Id, null, c.Id)).Outcome);
        Assert.Null(Row("SELECT ParentFolderId FROM ProjectFolders WHERE Id = $id;", mover.Id));

        // With enough depth the same move is verified safe.
        Assert.Equal(ContentMoveOutcome.Moved, (await NewService(maxDepth: 5).MoveFolderAsync(mover.Id, null, c.Id)).Outcome);
    }

    [Fact]
    public async Task CorruptCycle_WrittenOutsideTheApi_NeverEndsInAWrite()
    {
        var a = await NewFolder("A");
        var b = await NewFolder("B", a.Id);
        var mover = await NewFolder("Mover");
        using (var connection = _root.Open())
            Db.Exec(connection, "UPDATE ProjectFolders SET ParentFolderId = $b WHERE Id = $a;", ("$b", b.Id), ("$a", a.Id)); // A <-> B

        var result = await NewService(maxDepth: 50).MoveFolderAsync(mover.Id, null, b.Id);

        Assert.Equal(ContentMoveOutcome.AncestryUnverified, result.Outcome);
        Assert.Null(Row("SELECT ParentFolderId FROM ProjectFolders WHERE Id = $id;", mover.Id));
    }

    [Fact]
    public async Task MoveTargets_ExcludeTheMovingFolderSubtree_AndOtherProjects()
    {
        var docs = await NewFolder("Docs");
        await NewFolder("Specs", docs.Id);
        await NewFolder("Archive");
        await NewFolder("Other", project: _beta);

        Assert.Equal(["Archive"], (await _content.ListFolderMoveTargetsAsync(_alpha, docs.Id)).Select(t => t.Path));
        Assert.Equal(["Archive", "Docs", "Docs / Specs"], (await _content.ListFolderMoveTargetsAsync(_alpha, null)).Select(t => t.Path));
    }

    [Fact]
    public async Task AFailedWriteInsideTheTransaction_LeavesNothingBehind()
    {
        var p = await NewFolder("P");
        var link = await NewLink("X");
        using (var connection = _root.Open())
        {
            Db.Exec(connection, "CREATE TRIGGER Test_FailInsert BEFORE INSERT ON ProjectFolders BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            Db.Exec(connection, "CREATE TRIGGER Test_FailMove BEFORE UPDATE OF FolderId ON ProjectLinks BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        }

        await Assert.ThrowsAnyAsync<Exception>(() => _content.CreateFolderAsync(_alpha, null, "New"));
        await Assert.ThrowsAnyAsync<Exception>(() => _content.MoveLinkAsync(link.Id, null, p.Id));

        using var after = _root.Open();
        Assert.Equal(1, Db.Count(after, "ProjectFolders"));
        Assert.Null(Db.ScalarString(after, "SELECT FolderId FROM ProjectLinks WHERE Id = $id;", ("$id", link.Id)));
    }

    [Fact]
    public async Task Open_UsesTheStoredTarget_AndReportsAMissingLink()
    {
        var starter = new RecordingProcessStarter();
        var service = new ProjectContentService(new SqliteProjectContentStore(_connections), new ProcessProjectLinkLauncher(starter, _ => false, _ => true));
        var folderLink = await NewLink("Out", type: ProjectLinkType.Folder, target: @"C:\Work\out");
        var fileLink = await NewLink("Gone", type: ProjectLinkType.File, target: @"C:\Work\gone.txt");

        Assert.True((await service.OpenLinkAsync(folderLink.Id)).Succeeded);
        Assert.Equal(@"C:\Work\out", Assert.Single(starter.Started).FileName);
        var missing = await service.OpenLinkAsync(fileLink.Id);
        Assert.Equal(ProjectLinkOpenProblem.TargetMissing, missing.Problem);
        Assert.Equal(ProjectLinkOpenProblem.LinkNotFound, (await service.OpenLinkAsync(9999)).Problem);
        Assert.Single(starter.Started);
    }
}
