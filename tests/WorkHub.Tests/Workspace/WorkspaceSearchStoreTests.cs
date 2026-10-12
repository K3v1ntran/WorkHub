using System.Text;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>Workspace search and typed resolve through the real SQLite store (Phase 6 proposal §4, §8.2).</summary>
public sealed class WorkspaceSearchStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly WorkspaceService _workspace;
    private readonly ProjectContentService _content;
    private readonly WorkspaceSearchService _search;

    public WorkspaceSearchStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(_connections));
        _content = new ProjectContentService(new SqliteProjectContentStore(_connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
        _search = new WorkspaceSearchService(new SqliteWorkspaceSearchStore(_connections));
    }

    public void Dispose() => _root.Dispose();

    private SqliteWorkspaceSearchStore StoreWith(WorkspaceSearchStoreOptions options) => new(_connections, options);

    private async Task<long> Folder(string name, long? parent = null) => (await _workspace.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null, string? owner = null, string? manager = null, string? description = null) =>
        (await _workspace.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active, description, owner, manager))).Value!.Id;

    private async Task<long> ContentFolder(long project, string name, long? parent = null) => (await _content.CreateFolderAsync(project, parent, name)).Value!.Id;

    private async Task<long> Link(long project, string name, string target, long? folder = null, ProjectLinkType type = ProjectLinkType.Web) =>
        (await _content.CreateLinkAsync(project, folder, new ProjectLinkDraft(name, type, target))).Value!.Id;

    private async Task<long> Note(long project, string title, string content, long? folder = null) =>
        (await _content.CreateNoteAsync(project, folder, new ProjectNoteDraft(title, content))).Note!.Id;

    private async Task<WorkspaceSearchResult> Search(string text) => await _search.SearchAsync(WorkspaceSearchService.Parse(text)!);

    private static WorkspaceSearchHit Single(WorkspaceSearchResult result) => Assert.Single(result.Hits);

    // ---- Trash (schema v6) ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TrashedContent_IsNeverACandidate_AndRestoredContentIsFoundAgain()
    {
        var alpha = await Project("Alpha");
        var specs = await ContentFolder(alpha, "Trashy specs");
        await Link(alpha, "Trashy link", "https://trashy.test/", specs);
        await Note(alpha, "Inside", "trashy body", specs);
        var lone = await Note(alpha, "Trashy lone note", "");
        await ContentFolder(alpha, "Trashy kept");
        Assert.Equal(5, (await Search("trashy")).Hits.Count);

        var folderEntry = (await _content.TrashAsync(new ContentRef(ContentKind.Folder, specs))).Entry!;
        await _content.TrashAsync(new ContentRef(ContentKind.Note, lone));

        Assert.Equal([new SearchItemRef(SearchResultKind.ProjectFolder, (await _content.ListAsync(alpha, null)).Listing!.Folders.Single().Id)],
            (await Search("trashy")).Hits.Select(h => h.Item));
        await _content.RestoreAsync(folderEntry.Id);
        Assert.Equal(4, (await Search("trashy")).Hits.Count);
    }

    [Fact]
    public async Task ResolvingATrashedItem_ReportsTrashed_PerKind_WithoutTouchingTheOtherKinds()
    {
        var alpha = await Project("Alpha");
        using (var c = _root.Open())
        {
            Db.Exec(c, "INSERT INTO ProjectFolders (Id, ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES (700, $p, NULL, 'F', 'F', $n, $n);", ("$p", alpha), ("$n", Db.Now));
            Db.Exec(c, "INSERT INTO ProjectLinks (Id, ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES (700, $p, NULL, 'Web', 'L', 'https://l.test/', $n, $n);", ("$p", alpha), ("$n", Db.Now));
            Db.Exec(c, "INSERT INTO ProjectNotes (Id, ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES (700, $p, NULL, 'N', '', $n, $n);", ("$p", alpha), ("$n", Db.Now));
        }

        foreach (var (kind, searchKind) in new[] { (ContentKind.Folder, SearchResultKind.ProjectFolder), (ContentKind.Link, SearchResultKind.Link), (ContentKind.Note, SearchResultKind.Note) })
        {
            await _content.TrashAsync(new ContentRef(kind, 700));
            var resolution = await _search.ResolveAsync(new SearchItemRef(searchKind, 700));
            Assert.Equal(RevealResolveStatus.Trashed, resolution.Status);
            Assert.Null(resolution.Target);
        }

        Assert.Equal(RevealResolveStatus.NotFound, (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Link, 701))).Status);
    }

    // ---- Kinds, fields, terms ----------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryKindAndField_IsFound_WithItsMatchedField()
    {
        var clients = await Folder("Clients");
        var alpha = await Project("Alpha Portal", clients, owner: "Tanaka", manager: "Suzuki", description: "intro\r\nholds the 仕様 documents");
        var specs = await ContentFolder(alpha, "Specs");
        var deck = await Link(alpha, "Deck", @"\\server\share\kickoff.pptx", specs, ProjectLinkType.File);
        var note = await Note(alpha, "Kickoff", "first line\nagenda: scope and dates", specs);

        Assert.Equal(new SearchItemRef(SearchResultKind.WorkspaceFolder, clients), Single(await Search("clients")).Item);
        var owner = Single(await Search("tanaka"));
        Assert.Equal((new SearchItemRef(SearchResultKind.Project, alpha), SearchField.Owner), (owner.Item, owner.MatchedField));
        Assert.Equal(SearchField.ProjectManager, Single(await Search("SUZUKI")).MatchedField);
        var description = Single(await Search("仕様"));
        Assert.Equal((SearchField.Description, "holds the 仕様 documents"), (description.MatchedField, description.Excerpt));
        Assert.Equal(new SearchItemRef(SearchResultKind.ProjectFolder, specs), Single(await Search("specs")).Item);
        var target = Single(await Search("server"));
        Assert.Equal((new SearchItemRef(SearchResultKind.Link, deck), SearchField.Target), (target.Item, target.MatchedField));
        Assert.Equal(@"\\server\share\kickoff.pptx", target.Excerpt);
        Assert.Equal(SearchField.Name, Single(await Search("deck")).MatchedField);
        var content = Single(await Search("agenda"));
        Assert.Equal((new SearchItemRef(SearchResultKind.Note, note), SearchField.Content, "agenda: scope and dates"), (content.Item, content.MatchedField, content.Excerpt));
        Assert.Null(Single(await Search("Alpha Portal")).Excerpt); // name matches carry no excerpt
    }

    [Fact]
    public async Task Terms_MayMatchDifferentFields_ButEveryTermMustMatch()
    {
        var alpha = await Project("Alpha");
        var note = await Note(alpha, "Weekly sync", "budget review");

        Assert.Equal(new SearchItemRef(SearchResultKind.Note, note), Single(await Search("weekly budget")).Item);
        Assert.Empty((await Search("weekly zzz")).Hits);
    }

    [Fact]
    public async Task UnlistedFields_AreNotSearched()
    {
        var alpha = await Project("Alpha");
        await Note(alpha, "n", "c");
        var projectId = (await _workspace.GetProjectAsync(alpha))!.ProjectId;

        Assert.Empty((await Search(projectId)).Hits);
        Assert.Empty((await Search("Active")).Hits);    // status
        Assert.Empty((await Search("2026-")).Hits);     // timestamps
    }

    [Fact]
    public async Task UnicodeWidthAndCase_FollowTheWorkspaceRule()
    {
        var alpha = await Project("ＡＢＣ 販売管理");
        await Note(alpha, "ｶﾀｶﾅ memo", "");

        Assert.Single((await Search("abc")).Hits);
        Assert.Single((await Search("カタカナ")).Hits);
        Assert.Single((await Search("販")).Hits);
    }

    // ---- Locations, ordering, truncation ----------------------------------------------------------------------------

    [Fact]
    public async Task Locations_AreTheLogicalPaths()
    {
        var clients = await Folder("Clients");
        var tokyo = await Folder("東京支社", clients);
        var alpha = await Project("Alpha Portal", tokyo);
        var specs = await ContentFolder(alpha, "Specs");
        var drafts = await ContentFolder(alpha, "Drafts", specs);
        await Note(alpha, "Deep note", "", drafts);
        await Note(alpha, "Root note", "");

        Assert.Equal("Workspace", Single(await Search("clients")).Location);
        Assert.Equal("Clients › 東京支社", Single(await Search("alpha")).Location);
        Assert.Equal("Clients › 東京支社 › Alpha Portal › Specs", Single(await Search("drafts")).Location);
        Assert.Equal("Clients › 東京支社 › Alpha Portal › Specs › Drafts", Single(await Search("deep")).Location);
        Assert.Equal("Clients › 東京支社 › Alpha Portal", Single(await Search("root note")).Location);
    }

    [Fact]
    public async Task BrokenChain_ShowsLocationUnavailable_ForThatHitOnly_ResolveRefuses()
    {
        var a = await Folder("A");
        var b = await Folder("B", a);
        var c = await Folder("C", b);
        var deep = await Project("Deep match", c);
        await Project("Shallow match");
        var store = StoreWith(WorkspaceSearchStoreOptions.Default with { MaxDepth = 2 });

        var result = await store.SearchAsync(WorkspaceSearchService.Parse("match")!, 200, CancellationToken.None);

        Assert.Equal("(location unavailable)", result.Hits.Single(h => h.Name == "Deep match").Location);
        Assert.Equal("Workspace", result.Hits.Single(h => h.Name == "Shallow match").Location);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ResolveAsync(new SearchItemRef(SearchResultKind.Project, deep), CancellationToken.None));
    }

    [Fact]
    public async Task Ordering_NameMatchesFirst_ThenKindRank_ThenName_ThenId()
    {
        var alpha = await Project("Zeta report");               // Project name match
        await Note(alpha, "report", "");                        // Note name match
        await Note(alpha, "Another", "the report is late");     // content match → after all name matches
        await ContentFolder(alpha, "Report drafts");            // ProjectFolder name match
        await Folder("Reports");                                // WorkspaceFolder name match
        await Link(alpha, "B report", "https://x.test/");
        await Link(alpha, "A report", "https://y.test/");

        var names = (await Search("report")).Hits.Select(h => (h.Item.Kind, h.Name)).ToList();

        Assert.Equal(
        [
            (SearchResultKind.WorkspaceFolder, "Reports"),
            (SearchResultKind.Project, "Zeta report"),
            (SearchResultKind.ProjectFolder, "Report drafts"),
            (SearchResultKind.Link, "A report"),
            (SearchResultKind.Link, "B report"),
            (SearchResultKind.Note, "report"),
            (SearchResultKind.Note, "Another"),
        ], names);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(201, true)]
    public async Task Truncation_IsExactAtTheLimit(int count, bool truncated)
    {
        var alpha = await Project("Alpha");
        using (var c = _root.Open())
        using (var tx = c.BeginTransaction())
        {
            for (var i = 0; i < count; i++)
                Db.Exec(c, "INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, NULL, $t, '', $n, $n);", ("$p", alpha), ("$t", $"Bulk {i:D3}"), ("$n", Db.Now));
            tx.Commit();
        }

        var result = await Search("bulk");

        Assert.Equal(200, result.Hits.Count);
        Assert.Equal(truncated, result.Truncated);
        Assert.Equal("Bulk 000", result.Hits[0].Name);
    }

    // ---- Typed resolve ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resolve_SameNumericIdForFolderLinkAndNote_EachKindFindsItsOwnRow()
    {
        var alpha = await Project("Alpha");
        var x = await ContentFolder(alpha, "X");
        var y = await ContentFolder(alpha, "Y");
        using (var c = _root.Open())
        {
            Db.Exec(c, "INSERT INTO ProjectFolders (Id, ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES (500, $p, NULL, 'Same', 'SAME', $n, $n);", ("$p", alpha), ("$n", Db.Now));
            Db.Exec(c, "INSERT INTO ProjectLinks (Id, ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES (500, $p, $f, 'Web', 'Same', 'https://s.test/', $n, $n);", ("$p", alpha), ("$f", x), ("$n", Db.Now));
            Db.Exec(c, "INSERT INTO ProjectNotes (Id, ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES (500, $p, $f, 'Same', '', $n, $n);", ("$p", alpha), ("$f", y), ("$n", Db.Now));
        }

        var hits = (await Search("same")).Hits;
        Assert.Equal([SearchResultKind.ProjectFolder, SearchResultKind.Link, SearchResultKind.Note], hits.Select(h => h.Item.Kind).ToList());
        Assert.All(hits, h => Assert.Equal(500, h.Item.Id));

        var folder = (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.ProjectFolder, 500))).Target!.Content!;
        var link = (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Link, 500))).Target!.Content!;
        var note = (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Note, 500))).Target!.Content!;
        Assert.Equal((SearchResultKind.ProjectFolder, (long?)null), (folder.Kind, folder.ContainerFolderId));
        Assert.Equal((SearchResultKind.Link, (long?)x), (link.Kind, link.ContainerFolderId));
        Assert.Equal((SearchResultKind.Note, (long?)y), (note.Kind, note.ContainerFolderId));

        using (var c = _root.Open())
            Db.Exec(c, "DELETE FROM ProjectLinks WHERE Id = 500;");
        Assert.Equal(RevealResolveStatus.NotFound, (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Link, 500))).Status); // a Note 500 still exists
        Assert.Equal(RevealResolveStatus.Found, (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Note, 500))).Status);
    }

    [Fact]
    public async Task Resolve_WorkspaceKindMustMatchTheNodeType()
    {
        var folder = await Folder("F");
        var project = await Project("P");

        Assert.Equal(RevealResolveStatus.NotFound, (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Project, folder))).Status);
        Assert.Equal(RevealResolveStatus.NotFound, (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.WorkspaceFolder, project))).Status);
        Assert.Equal(RevealResolveStatus.NotFound, (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Note, 999_999))).Status);
    }

    [Fact]
    public async Task Resolve_ReturnsTheCurrentLocation_AfterAMove()
    {
        var alpha = await Project("Alpha");
        var a = await ContentFolder(alpha, "A");
        var b = await ContentFolder(alpha, "B");
        var note = await Note(alpha, "N", "", a);
        Assert.True((await _content.MoveNoteAsync(note, a, b)).Moved);

        var target = (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Note, note))).Target!;

        Assert.Equal(alpha, target.WorkspaceNodeId);
        Assert.Equal(new ContentRevealTarget(alpha, b, SearchResultKind.Note, note), target.Content);
    }

    [Fact]
    public async Task Resolve_BranchesAreTheAncestorChainAndEveryLevelsChildren()
    {
        var top = await Folder("Top");
        var sibling = await Folder("Sibling");
        var middle = await Folder("Middle", top);
        var other = await Project("Other", top);
        var target = await Project("Target", middle);

        var reveal = (await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Project, target))).Target!;

        Assert.Equal([top, middle], reveal.WorkspaceAncestorIds);
        Assert.Equal(target, reveal.WorkspaceNodeId);
        Assert.Null(reveal.Content);
        Assert.Equal([null, top, middle], reveal.Branches.Select(b => b.ParentId).ToList());
        Assert.Equal([top, sibling], reveal.Branches[0].Children.Select(c => c.Id).Order().ToList());
        Assert.Equal([middle, other], reveal.Branches[1].Children.Select(c => c.Id).Order().ToList());
        Assert.True(reveal.Branches[1].Children.Single(c => c.Id == middle).HasChildren);
        Assert.Equal([target], reveal.Branches[2].Children.Select(c => c.Id).ToList());
        Assert.All(reveal.Branches, b => Assert.True(b.Complete));
    }

    [Fact]
    public async Task Resolve_LevelOverTheRowBound_IsIncomplete_ButAlwaysContainsThePathNode()
    {
        await Folder("A");
        await Folder("B");
        await Folder("C");
        var target = await Project("Z target");
        var store = StoreWith(WorkspaceSearchStoreOptions.Default with { MaxRowsPerLevel = 2 });

        var reveal = (await store.ResolveAsync(new SearchItemRef(SearchResultKind.Project, target), CancellationToken.None)).Target!;

        var root = Assert.Single(reveal.Branches);
        Assert.False(root.Complete);
        Assert.Contains(root.Children, c => c.Id == target);
        Assert.Equal(3, root.Children.Count); // two bounded rows + the path node
    }

    // ---- Read-only, exclusions -------------------------------------------------------------------------------------

    [Fact]
    public async Task SearchAndResolve_NeverModifyTheDatabase()
    {
        var alpha = await Project("Alpha");
        var note = await Note(alpha, "Kickoff", "agenda");
        var before = Dump();
        var file = new FileInfo(_root.Paths.DatabaseFile);
        var (size, written) = (file.Length, file.LastWriteTimeUtc);

        await Search("kickoff agenda");
        await Search("zzz");
        await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Note, note));
        await _search.ResolveAsync(new SearchItemRef(SearchResultKind.Project, alpha));

        Assert.Equal(before, Dump());
        file.Refresh();
        Assert.Equal((size, written), (file.Length, file.LastWriteTimeUtc));
    }

    [Fact]
    public async Task SystemsAndOwnerItems_AreNotSearched()
    {
        using (var c = _root.Open())
            Db.InsertSystem(c, "4000000099", "Searchable System Name");

        Assert.Empty((await Search("4000000099")).Hits);
        Assert.Empty((await Search("Searchable System")).Hits);
    }

    private string Dump()
    {
        using var c = _root.Open();
        var text = new StringBuilder();
        foreach (var table in Db.UserTables(c))
        {
            using var command = c.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1, 2;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                text.Append(table);
                for (var i = 0; i < reader.FieldCount; i++)
                    text.Append('|').Append(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i));
                text.Append('\n');
            }
        }

        using var schema = c.CreateCommand();
        schema.CommandText = "SELECT group_concat(sql, ';') FROM sqlite_schema;";
        return text.Append(schema.ExecuteScalar()).ToString();
    }
}
