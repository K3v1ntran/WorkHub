using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 17 Workspace search over Project tags through the real store (proposal R2b §4.4; tests Q1–Q13): tags are one more field
/// after Description, every word must be in some field, one result per Project, Name keeps its priority, and the explanation names
/// the first tag in display order that contains a word.
/// </summary>
public sealed class ProjectTagsSearchTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceSearchService _search;

    public ProjectTagsSearchTests()
    {
        _root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(connections));
        _search = new WorkspaceSearchService(new SqliteWorkspaceSearchStore(connections));
    }

    public void Dispose() => _root.Dispose();

    private async Task<long> Project(string name, string? owner, long? parent, params string[] tags) =>
        (await _workspace.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active, null, owner, null, tags))).Value!.Id;

    private async Task<IReadOnlyList<WorkspaceSearchHit>> Search(string text) => (await _search.SearchAsync(WorkspaceSearchService.Parse(text)!)).Hits;

    [Fact]
    public async Task Q1_Q5_ATagMatch_IsOneProjectHit_ReportedAsTag_FoldedForCaseAndWidth()
    {
        var id = await Project("Portal", null, null, "Infra");
        foreach (var query in new[] { "infra", "INFRA", "ｉｎｆｒａ" })
        {
            var hit = Assert.Single(await Search(query));
            Assert.Equal((SearchResultKind.Project, id), (hit.Item.Kind, hit.Item.Id));
            Assert.Equal((SearchField.Tag, "Infra"), (hit.MatchedField, hit.Excerpt));
        }
    }

    [Fact]
    public async Task Q2_WordsInNameAndTag_AreFound_ReportedAsName_NotANameMatchForRanking()
    {
        await Project("Portal", null, null, "Infra");
        await Project("Portal infra", null, null);
        var hits = await Search("portal infra");
        Assert.Equal(["Portal infra", "Portal"], hits.Select(h => h.Name)); // the full name match ranks first
        Assert.All(hits, h => Assert.Equal(SearchField.Name, h.MatchedField));
    }

    [Fact]
    public async Task Q3_Q4_ArchivedAreFoundAndMarked_TrashedAreNot()
    {
        var folder = (await _workspace.CreateFolderAsync(null, "Box")).Value!.Id;
        var archived = await Project("A1", null, null, "Infra");
        await _workspace.ArchiveProjectAsync(archived);
        var inFolder = await Project("A2", null, folder, "Infra");
        var trashed = await Project("T1", null, null, "Infra");
        await _workspace.TrashProjectAsync(trashed);
        var confirmation = await _workspace.PrepareFolderLifecycleAsync(folder, FolderLifecycleKind.Archive);
        await _workspace.MarkFolderAsync(folder, FolderLifecycleKind.Archive, confirmation.Confirmation!.Token);

        var hits = await Search("infra");

        Assert.Equal([archived, inFolder], hits.Select(h => h.Item.Id).Order());
        Assert.All(hits, h => Assert.True(h.InArchivedProject));
        Assert.Equal("Box", hits.Single(h => h.Item.Id == inFolder).ArchivedFolderName);

        // Q4b: a Project inside a trashed Folder is not found either.
        await _workspace.ClearFolderAsync(folder, FolderLifecycleKind.Archive);
        var trash = await _workspace.PrepareFolderLifecycleAsync(folder, FolderLifecycleKind.Trash);
        await _workspace.MarkFolderAsync(folder, FolderLifecycleKind.Trash, trash.Confirmation!.Token);
        Assert.Equal([archived], (await Search("infra")).Select(h => h.Item.Id));
    }

    [Fact]
    public async Task Q6_Q9_NameMatchesComeFirst_TheNameKeepsPriorityOverTheTag()
    {
        await Project("Other", null, null, "Infra");
        await Project("Infra Portal", null, null, "Infra");
        var hits = await Search("infra");
        Assert.Equal(["Infra Portal", "Other"], hits.Select(h => h.Name));
        Assert.Equal((SearchField.Name, (string?)null), (hits[0].MatchedField, hits[0].Excerpt));
        Assert.Equal((SearchField.Tag, "Infra"), (hits[1].MatchedField, hits[1].Excerpt));
    }

    [Fact]
    public async Task Q7_SeveralMatchingTags_TheFirstInDisplayOrderIsNamed()
    {
        await Project("Portal", null, null, "Infra", "Alpha-Infra");
        var hit = Assert.Single(await Search("infra"));
        Assert.Equal("Alpha-Infra", hit.Excerpt); // ALPHA-INFRA < INFRA
    }

    [Fact]
    public async Task Q8_WordsInDifferentTags_AreFound_TheFirstTagContainingAWordIsNamed()
    {
        await Project("Portal", null, null, "Ops", "Client A");
        var hit = Assert.Single(await Search("ops client"));
        Assert.Equal((SearchField.Tag, "Client A"), (hit.MatchedField, hit.Excerpt)); // CLIENT A < OPS
    }

    [Fact]
    public async Task Q10_TheOwnerFieldComesBeforeTag()
    {
        await Project("Portal", "alice", null, "Ops");
        var hit = Assert.Single(await Search("alice ops"));
        Assert.Equal(SearchField.Owner, hit.MatchedField);
    }

    [Fact]
    public async Task Q11_AWordInNoField_FindsNothing()
    {
        await Project("Portal", null, null, "Ops");
        Assert.Empty(await Search("ops zzz"));
    }

    [Fact]
    public async Task Q12_TheResultDoesNotDependOnTheOrderTagsWereAdded()
    {
        var a = await Project("P1", null, null, "Ops", "Infra", "Alpha-Infra");
        var b = await Project("P2", null, null, "Alpha-Infra", "Infra", "Ops");
        var hits = await Search("infra");
        Assert.Equal(["Alpha-Infra", "Alpha-Infra"], hits.Select(h => h.Excerpt));
        Assert.Equal([a, b], hits.Select(h => h.Item.Id));
        Assert.Equal(hits.Select(h => (h.Item, h.MatchedField, h.Excerpt)), (await Search("infra")).Select(h => (h.Item, h.MatchedField, h.Excerpt)));
    }

    [Fact]
    public void Q13_AChipWithMoreThanEightWords_IsShortenedByTheExistingRule()
    {
        var query = WorkspaceSearchService.Parse("a b c d e f g h i")!;
        Assert.Equal(8, query.Terms.Count);
        Assert.True(query.WasShortened);
    }
}
