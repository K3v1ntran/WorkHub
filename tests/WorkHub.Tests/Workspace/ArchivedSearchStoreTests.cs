using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Search and typed resolve of archived Projects and their content (Phase 12 proposal revision 3 §8.4, A5): included and
/// marked, resolved to the owning Project's node Id WITHOUT snapshots, and never placed by reveal branches.
/// </summary>
public sealed class ArchivedSearchStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly WorkspaceService _workspace;
    private readonly ProjectContentService _content;
    private readonly WorkspaceSearchService _search;
    private readonly SqliteWorkspaceSearchStore _store;
    private readonly long _clients;
    private readonly long _alpha;
    private readonly long _beta;
    private readonly long _folder;
    private readonly long _link;
    private readonly long _note;

    public ArchivedSearchStoreTests()
    {
        _root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(connections));
        _content = new ProjectContentService(new SqliteProjectContentStore(connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
        _store = new SqliteWorkspaceSearchStore(connections);
        _search = new WorkspaceSearchService(_store);
        _clients = _workspace.CreateFolderAsync(null, "Clients").Result.Value!.Id;
        _alpha = _workspace.CreateProjectAsync(_clients, new ProjectDraft("Zebra archive", ProjectStatus.Completed, "zebra description")).Result.Value!.Id;
        _beta = _workspace.CreateProjectAsync(_clients, new ProjectDraft("Zebra active", ProjectStatus.Active)).Result.Value!.Id;
        _folder = _content.CreateFolderAsync(_alpha, null, "Zebra specs").Result.Value!.Id;
        _link = _content.CreateLinkAsync(_alpha, _folder, new ProjectLinkDraft("Zebra link", ProjectLinkType.Web, "https://zebra.example.test/")).Result.Value!.Id;
        _note = _content.CreateNoteAsync(_alpha, _folder, new ProjectNoteDraft("Plain", "zebra body")).Result.Note!.Id;
        Assert.Equal(ProjectArchiveOutcome.Archived, _workspace.ArchiveProjectAsync(_alpha).Result.Outcome);
    }

    public void Dispose() => _root.Dispose();

    private async Task<IReadOnlyList<WorkspaceSearchHit>> Search(string text) => (await _search.SearchAsync(WorkspaceSearchService.Parse(text)!)).Hits;

    private Task<RevealResolution> Resolve(SearchResultKind kind, long id) => _store.ResolveAsync(new SearchItemRef(kind, id), CancellationToken.None);

    [Fact]
    public async Task ArchivedProjectsAndTheirContent_AreFound_AndMarked()
    {
        var hits = await Search("zebra");

        Assert.Equal(
            new[] { "Link:Zebra link:True", "Note:Plain:True", "Project:Zebra active:False", "Project:Zebra archive:True", "ProjectFolder:Zebra specs:True" },
            hits.Select(h => $"{h.Item.Kind}:{h.Name}:{h.InArchivedProject}").Order(StringComparer.Ordinal));
        Assert.Equal("Clients", hits.Single(h => h.Item.Id == _alpha && h.Item.Kind == SearchResultKind.Project).Location);

        // Trashed Projects stay excluded even when archived (an earlier build's state).
        await _workspace.UnarchiveProjectAsync(_alpha, ProjectStatus.Active);
        await _workspace.TrashProjectAsync(_alpha);
        Assert.Equal(["Zebra active"], (await Search("zebra")).Select(h => h.Name));
    }

    [Fact]
    public async Task ResolvingAnArchivedProject_ReturnsItsNodeId_WithoutSnapshots()
    {
        var resolution = await Resolve(SearchResultKind.Project, _alpha);

        Assert.Equal(RevealResolveStatus.Archived, resolution.Status);
        Assert.Null(resolution.Target);
        Assert.Equal(_alpha, resolution.ArchivedRootId);
    }

    [Theory]
    [InlineData(SearchResultKind.ProjectFolder)]
    [InlineData(SearchResultKind.Link)]
    [InlineData(SearchResultKind.Note)]
    public async Task ResolvingContentOfAnArchivedProject_ReturnsTheOwningProjectId_WithoutSnapshots(SearchResultKind kind)
    {
        var id = kind switch { SearchResultKind.ProjectFolder => _folder, SearchResultKind.Link => _link, _ => _note };

        var resolution = await Resolve(kind, id);

        Assert.Equal(RevealResolveStatus.ProjectArchived, resolution.Status);
        Assert.Null(resolution.Target);
        Assert.Equal(_alpha, resolution.ArchivedRootId);
    }

    [Fact]
    public async Task EvaluationOrder_TrashBeatsArchive_AndOtherStatusesCarryNoProjectId()
    {
        // A content item in the content Trash of an archived Project → Trashed.
        Assert.Equal(TrashOutcome.Trashed, (await _content.TrashAsync(new ContentRef(ContentKind.Link, _link))).Outcome);
        var trashedItem = await Resolve(SearchResultKind.Link, _link);
        Assert.Equal((RevealResolveStatus.Trashed, (long?)null), (trashedItem.Status, trashedItem.ArchivedRootId));

        // An archived Project that an earlier build also trashed → Trashed / ProjectTrashed.
        using (var connection = _root.Open())
            Db.Exec(connection, "UPDATE WorkspaceNodes SET TrashedAt = '2026-01-01T00:00:00.000Z' WHERE Id = $id;", ("$id", _alpha));
        Assert.Equal(RevealResolveStatus.Trashed, (await Resolve(SearchResultKind.Project, _alpha)).Status);
        Assert.Equal(RevealResolveStatus.ProjectTrashed, (await Resolve(SearchResultKind.Note, _note)).Status);

        var found = await Resolve(SearchResultKind.Project, _beta);
        Assert.Equal((RevealResolveStatus.Found, (long?)null), (found.Status, found.ArchivedRootId));
        Assert.Equal(RevealResolveStatus.NotFound, (await Resolve(SearchResultKind.Project, 999)).Status);
    }

    [Fact]
    public async Task RevealBranches_ListVisibleNodesOnly_ExactlyAsTheTreeDoes()
    {
        var resolution = await Resolve(SearchResultKind.Project, _beta);

        var clients = resolution.Target!.Branches.Single(b => b.ParentId == _clients);
        Assert.Equal(["Zebra active"], clients.Children.Select(c => c.Name));
        var tree = (await _workspace.ListChildrenAsync(_clients)).Nodes;
        Assert.Equal(tree.Select(n => (n.Id, n.HasChildren)), clients.Children.Select(n => (n.Id, n.HasChildren)));
        var root = resolution.Target.Branches.Single(b => b.ParentId is null);
        Assert.Equal((await _workspace.ListChildrenAsync(null)).Nodes.Select(n => (n.Id, n.HasChildren)), root.Children.Select(n => (n.Id, n.HasChildren)));

        // With only the archived Project left, the folder has no expander in either reading.
        await _workspace.ArchiveProjectAsync(_beta);
        var folder = (await Resolve(SearchResultKind.WorkspaceFolder, _clients)).Target!.Branches.Single(b => b.ParentId is null).Children.Single(c => c.Id == _clients);
        Assert.False(folder.HasChildren);
        Assert.False((await _workspace.ListChildrenAsync(null)).Nodes.Single(n => n.Id == _clients).HasChildren);
    }
}
