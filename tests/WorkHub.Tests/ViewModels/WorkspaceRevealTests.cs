using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Search reveal (Phase 6 proposal §4.5, §4.6, §8.3): typed (Kind, Id) identity, targeted reconciliation of cached
/// Workspace branches after external changes, version/stamp guards, bounded retry, content-row outcomes. A second set of
/// services on the same database plays the other window.
/// </summary>
public sealed class WorkspaceRevealTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly GatedWorkspaceStore _treeStore;
    private readonly GatedProjectContentStore _contentStore;
    private readonly RecordingProcessStarter _starter = new();
    private readonly WorkspaceService _other;
    private readonly ProjectContentService _otherContent;

    public WorkspaceRevealTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var real = new SqliteWorkspaceStore(_connections);
        _treeStore = new GatedWorkspaceStore(real);
        _other = new WorkspaceService(real);
        _contentStore = new GatedProjectContentStore(new SqliteProjectContentStore(_connections));
        _otherContent = new ProjectContentService(new SqliteProjectContentStore(_connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));
        _dialogs.OnNoteEditor = _ => throw new InvalidOperationException("a reveal must never open the note editor");
    }

    private readonly List<WorkspaceAreaViewModel> _areas = [];

    /// <summary>Background reads started by selections (details, content listing, preview) finish before the folder is deleted.</summary>
    public void Dispose()
    {
        foreach (var area in _areas)
        {
            var pending = new List<Task> { area.CurrentDetailsLoad, area.CurrentReveal };
            if (area.SelectedContent is { } content)
                pending.AddRange([content.CurrentLoad, content.CurrentPreviewLoad]);
            Task.WhenAll(pending).ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(10));
        }

        _root.Dispose();
    }

    private GatedSearchStore SearchStore { get; set; } = null!;

    private async Task<WorkspaceAreaViewModel> Area(WorkspaceSearchStoreOptions? options = null)
    {
        SearchStore = new GatedSearchStore(new SqliteWorkspaceSearchStore(_connections, options ?? WorkspaceSearchStoreOptions.Default));
        var content = new ProjectContentService(_contentStore, new ProcessProjectLinkLauncher(_starter, _ => true, _ => true));
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance, content, new ScriptedLinkChecker(),
            new WorkspaceSearchService(SearchStore), searchDelayMs: 0);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        return area;
    }

    private async Task<long> Folder(string name, long? parent = null) => (await _other.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null) => (await _other.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) => nodes.Single(n => !n.IsPlaceholder && n.Name == name);

    private static List<string> Names(IEnumerable<WorkspaceNodeViewModel> nodes) => nodes.Where(n => !n.IsPlaceholder).Select(n => n.Name).ToList();

    private static async Task Expand(WorkspaceAreaViewModel area, WorkspaceNodeViewModel folder)
    {
        folder.IsExpanded = true;
        Assert.True(await area.EnsureChildrenLoadedAsync(folder));
    }

    /// <summary>Every placement of the node Id in the tree's collections (must be exactly one once revealed).</summary>
    private static List<WorkspaceNodeViewModel> Placements(WorkspaceAreaViewModel area, long id)
    {
        var found = new List<WorkspaceNodeViewModel>();
        void Walk(IEnumerable<WorkspaceNodeViewModel> nodes)
        {
            foreach (var node in nodes.Where(n => !n.IsPlaceholder))
            {
                if (node.Id == id)
                    found.Add(node);
                Walk(node.Children);
            }
        }

        Walk(area.Roots);
        return found;
    }

    private static Task<WorkspaceRevealOutcome> Reveal(WorkspaceAreaViewModel area, SearchResultKind kind, long id) => area.RevealAsync(new SearchItemRef(kind, id));

    // ---- Branch reconciliation ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_ExternalProjectMove_SourceAndDestinationLoaded_AppearsOnceUnderTheDestination_SameInstance()
    {
        var source = await Folder("Source");
        var destination = await Folder("Destination");
        var target = await Project("Target", source);
        await Project("Stays", source);
        await Project("Already there", destination);
        var area = await Area();
        await Expand(area, Node(area.Roots, "Source"));
        await Expand(area, Node(area.Roots, "Destination"));
        var instance = Node(Node(area.Roots, "Source").Children, "Target");
        await _other.MoveAsync(target, destination);

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, target));

        Assert.Equal(["Stays"], Names(Node(area.Roots, "Source").Children));
        Assert.Equal(["Already there", "Target"], Names(Node(area.Roots, "Destination").Children));
        Assert.Same(instance, Assert.Single(Placements(area, target)));
        Assert.Same(instance, area.SelectedNode);
        Assert.Same(Node(area.Roots, "Destination"), instance.Parent);
        Assert.True(instance.IsSelected);
    }

    [Fact]
    public async Task B_AncestorMovedExternally_RelocatesWithItsLoadedSubtreeAndExpansion()
    {
        var a = await Folder("A");
        var b = await Folder("B", a);
        var target = await Project("Target", b);
        var z = await Folder("Z");
        var area = await Area();
        var aNode = Node(area.Roots, "A");
        await Expand(area, aNode);
        var bNode = Node(aNode.Children, "B");
        await Expand(area, bNode);
        await Expand(area, Node(area.Roots, "Z"));
        await _other.MoveAsync(a, z);

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, target));

        Assert.Equal(["Z"], Names(area.Roots));
        Assert.Same(aNode, Assert.Single(Placements(area, a)));
        Assert.Same(Node(area.Roots, "Z"), aNode.Parent);
        Assert.Same(bNode, Assert.Single(aNode.Children)); // loaded subtree kept
        Assert.True(bNode.IsExpanded && aNode.IsExpanded);
        Assert.Equal(target, area.SelectedNode!.Id);
        Assert.Single(Placements(area, b));
    }

    [Fact]
    public async Task C_TargetCreatedExternallyUnderALoadedParent_AppearsOnceSorted()
    {
        var parent = await Folder("Parent");
        await Folder("Sub", parent);
        await Project("Zulu", parent);
        var area = await Area();
        var parentNode = Node(area.Roots, "Parent");
        await Expand(area, parentNode);
        var sub = Node(parentNode.Children, "Sub");
        var created = await Project("Mike", parent);

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, created));

        Assert.Equal(["Sub", "Mike", "Zulu"], Names(parentNode.Children)); // folders first, then by name
        Assert.Same(sub, Node(parentNode.Children, "Sub"));
        Assert.Single(Placements(area, created));
        Assert.Equal(created, area.SelectedNode!.Id);
    }

    [Fact]
    public async Task D_ExternalMovesToAndFromTheRoot()
    {
        var folder = await Folder("Folder");
        var roaming = await Project("Roaming");
        var area = await Area();
        await Expand(area, Node(area.Roots, "Folder"));
        var instance = Node(area.Roots, "Roaming");

        await _other.MoveAsync(roaming, folder);
        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, roaming));
        Assert.Equal(["Folder"], Names(area.Roots));
        Assert.Same(instance, Assert.Single(Node(area.Roots, "Folder").Children));

        await _other.MoveAsync(roaming, null);
        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, roaming));
        Assert.Equal(["Folder", "Roaming"], Names(area.Roots));
        Assert.Empty(Names(Node(area.Roots, "Folder").Children));
        Assert.Same(instance, Assert.Single(Placements(area, roaming)));
        Assert.Null(instance.Parent);
    }

    [Fact]
    public async Task D_RootLevelExternalCreate_IsPlaced_AndARootNodeRemovedElsewhereIsDropped()
    {
        await Folder("Keep");
        var gone = await Folder("Gone");
        var area = await Area();
        using (var c = _root.Open())
            Db.Exec(c, "DELETE FROM WorkspaceNodes WHERE Id = $id;", ("$id", gone));
        var created = await Project("New at root");

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, created));

        Assert.Equal(["Keep", "New at root"], Names(area.Roots)); // a complete root snapshot drops what is no longer there
        Assert.Empty(Placements(area, gone));
    }

    [Theory]
    [InlineData("manual selection")]
    [InlineData("newer activation")]
    [InlineData("write command")]
    [InlineData("tab deactivated")]
    public async Task E_PendingRevealIsInvalidated_AppliesNothing(string invalidation)
    {
        var source = await Folder("Source");
        var destination = await Folder("Destination");
        var target = await Project("Target", source);
        var other = await Project("Other");
        var area = await Area();
        await Expand(area, Node(area.Roots, "Source"));
        var otherNode = Node(area.Roots, "Other");
        if (invalidation == "write command")
            area.SelectedNode = otherNode; // Rename needs a selection; selected BEFORE the reveal starts
        await _other.MoveAsync(target, destination);
        var gate = SearchStore.Hold("resolve");

        var reveal = Reveal(area, SearchResultKind.Project, target);
        gate.WaitEntered();
        Task<WorkspaceRevealOutcome>? newer = null;
        switch (invalidation)
        {
            case "manual selection": area.SelectedNode = otherNode; break;
            case "newer activation": newer = Reveal(area, SearchResultKind.Project, other); break;
            case "write command": await area.RenameCommand.ExecuteAsync(); break; // the dialog is cancelled; the command still starts
            case "tab deactivated": area.IsTabSelected = false; break;
        }

        gate.Release();
        Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal);
        Assert.Equal(["Target"], Names(Node(area.Roots, "Source").Children)); // the stale reveal reconciled nothing
        if (newer is not null)
            Assert.Equal(WorkspaceRevealOutcome.Selected, await newer);
        Assert.NotEqual(target, area.SelectedNode?.Id);
    }

    [Fact]
    public async Task F_LocalChangeOfAPathBranchDuringResolve_RetriesOnce_ThenSucceeds()
    {
        var folder = await Folder("Folder");
        var target = await Project("Target", folder);
        var area = await Area();
        var gate = SearchStore.Hold("resolve");

        var reveal = Reveal(area, SearchResultKind.Project, target);
        gate.WaitEntered();
        await Expand(area, Node(area.Roots, "Folder")); // a lazy load changes the path branch during the read
        gate.Release();

        Assert.Equal(WorkspaceRevealOutcome.Selected, await reveal);
        Assert.Equal(2, SearchStore.Resolves);
        Assert.Single(Placements(area, target));
    }

    [Fact]
    public async Task F_PathBranchChangesDuringBothResolves_ReportsWorkspaceChanged_AndChangesNothing()
    {
        var outer = await Folder("Outer");
        var inner = await Folder("Inner", outer);
        var target = await Project("Target", inner);
        var area = await Area();
        var first = SearchStore.Hold("resolve");

        var reveal = Reveal(area, SearchResultKind.Project, target);
        first.WaitEntered();
        await Expand(area, Node(area.Roots, "Outer"));
        var second = SearchStore.Hold("resolve");
        first.Release();
        second.WaitEntered();
        await Expand(area, Node(Node(area.Roots, "Outer").Children, "Inner"));
        var before = Names(Node(Node(area.Roots, "Outer").Children, "Inner").Children);
        second.Release();

        Assert.Equal(WorkspaceRevealOutcome.WorkspaceChanged, await reveal);
        Assert.Null(area.SelectedNode);
        Assert.Equal(before, Names(Node(Node(area.Roots, "Outer").Children, "Inner").Children));
    }

    [Fact]
    public async Task G_OlderLazyLoadLandingAfterAReveal_CannotPullTheNodeBack()
    {
        var oldParent = await Folder("Old parent");
        var newParent = await Folder("New parent");
        var target = await Project("Target", oldParent);
        await Project("Sibling", oldParent);
        var area = await Area();
        var oldNode = Node(area.Roots, "Old parent");
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(oldParent));
        oldNode.IsExpanded = true; // the lazy load reads NOW (Target still under Old parent) and lands later
        load.WaitEntered();
        await _other.MoveAsync(target, newParent);

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, target));
        load.Release();
        Assert.True(await area.EnsureChildrenLoadedAsync(oldNode));

        Assert.Equal(["Sibling"], Names(oldNode.Children));
        var placed = Assert.Single(Placements(area, target));
        Assert.Same(Node(area.Roots, "New parent"), placed.Parent);
        Assert.Same(placed, area.SelectedNode);
    }

    [Fact]
    public async Task G_OlderLazyLoadOfARevealedBranch_IsDiscarded()
    {
        var folder = await Folder("Folder");
        var target = await Project("Target", folder);
        var leaving = await Project("Leaving", folder);
        var elsewhere = await Folder("Elsewhere");
        var area = await Area();
        var folderNode = Node(area.Roots, "Folder");
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(folder));
        folderNode.IsExpanded = true;
        load.WaitEntered();       // snapshot: Target + Leaving
        await _other.MoveAsync(leaving, elsewhere);

        var reveal = Reveal(area, SearchResultKind.Project, target); // waits for the in-flight load of its path branch
        load.Release();
        Assert.Equal(WorkspaceRevealOutcome.Selected, await reveal);

        Assert.Equal(["Target"], Names(folderNode.Children));
        Assert.Empty(Placements(area, leaving));
    }

    [Fact]
    public async Task H_UnrelatedLoadedBranches_AreNotTouched()
    {
        var unrelated = await Folder("Unrelated");
        await Project("U1", unrelated);
        await Project("U2", unrelated);
        var folder = await Folder("Folder");
        var target = await Project("Target", folder);
        var area = await Area();
        var unrelatedNode = Node(area.Roots, "Unrelated");
        await Expand(area, unrelatedNode);
        var children = unrelatedNode.Children.ToList();
        var stamp = unrelatedNode.BranchStamp;
        using (var c = _root.Open())
            Db.Exec(c, "UPDATE WorkspaceNodes SET Name = 'U1 renamed elsewhere', NameKey = 'U1 RENAMED ELSEWHERE' WHERE Name = 'U1';");

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, target));

        Assert.Equal(children, unrelatedNode.Children.ToList());
        Assert.Equal(stamp, unrelatedNode.BranchStamp);
        Assert.Equal(["U1", "U2"], Names(unrelatedNode.Children)); // stays stale: only the revealed path is reconciled
    }

    [Fact]
    public async Task I_TruncatedLevel_RemovesNothing_ButPlacesThePathNode()
    {
        await Folder("A");
        await Folder("B");
        var c = await Folder("C");
        var area = await Area(WorkspaceSearchStoreOptions.Default with { MaxRowsPerLevel = 2 });
        using (var connection = _root.Open())
            Db.Exec(connection, "DELETE FROM WorkspaceNodes WHERE Id = $id;", ("$id", c));
        var target = await Project("Target");

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, target));

        Assert.Equal(["A", "B", "C", "Target"], Names(area.Roots)); // C is gone in the database, but the level was incomplete
        Assert.Equal(target, area.SelectedNode!.Id);
    }

    // ---- Retained descendants of an obsolete cached ancestor ----------------------------------------------------------

    [Fact]
    public async Task RetainedDescendantOfAForgottenAncestor_KeepsItsInstanceSubtreeAndExpansion()
    {
        var a = await Folder("A");
        var z = await Folder("Z");
        var b = await Folder("B", a);
        var child = await Project("Child", b);
        var area = await Area();
        var aNode = Node(area.Roots, "A");
        await Expand(area, aNode);
        var bNode = Node(aNode.Children, "B");
        await Expand(area, bNode);
        var childNode = Node(bNode.Children, "Child");

        await _other.MoveAsync(b, null); // B to the root
        await _other.MoveAsync(a, z);    // A beneath Z: A is no longer at the root

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.WorkspaceFolder, b));

        Assert.Same(bNode, Assert.Single(Placements(area, b)));
        Assert.Same(childNode, Assert.Single(Placements(area, child)));
        Assert.True(bNode.IsExpanded);
        Assert.Same(childNode, Assert.Single(bNode.Children)); // loaded subtree kept, no "Loading…" placeholder
        Assert.Null(bNode.Parent);
        Assert.Same(bNode, childNode.Parent);
        Assert.Equal((0, 1), (bNode.Depth, childNode.Depth));
        Assert.Equal(["B", "Z"], Names(area.Roots));
        Assert.Empty(Placements(area, a));
        Assert.Same(bNode, area.SelectedNode);
        Assert.True(bNode.IsSelected);
    }

    [Fact]
    public async Task DeeperRetainedDescendant_RelocatedToAnotherPathLevel_KeepsItsInstance()
    {
        var a = await Folder("A");
        var y = await Folder("Y");
        var z = await Folder("Z");
        var b = await Folder("B", a);
        var c = await Folder("C", b);
        var deep = await Project("Deep", c);
        var area = await Area();
        var aNode = Node(area.Roots, "A");
        await Expand(area, aNode);
        var bNode = Node(aNode.Children, "B");
        await Expand(area, bNode);
        var cNode = Node(bNode.Children, "C");
        await Expand(area, cNode);
        var deepNode = Node(cNode.Children, "Deep");

        await _other.MoveAsync(c, z); // C (with Deep) beneath Z, a different path level
        await _other.MoveAsync(a, y); // A (with B) beneath Y: A leaves the root

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.WorkspaceFolder, c));

        var zNode = Node(area.Roots, "Z");
        Assert.Same(cNode, Assert.Single(Placements(area, c)));
        Assert.Same(deepNode, Assert.Single(Placements(area, deep)));
        Assert.Same(zNode, cNode.Parent);
        Assert.True(cNode.IsExpanded);
        Assert.Same(deepNode, Assert.Single(cNode.Children));
        Assert.Equal((0, 1, 2), (zNode.Depth, cNode.Depth, deepNode.Depth));
        Assert.Equal(["Y", "Z"], Names(area.Roots));
        Assert.Empty(Placements(area, a));
        Assert.Empty(Placements(area, b)); // B left with A, beneath Y (not loaded): genuinely gone from the visible tree
        Assert.Same(cNode, area.SelectedNode);
    }

    [Fact]
    public async Task GenuinelyAbsentSubtree_IsForgotten_SoALaterLoadCreatesFreshInstances()
    {
        var a = await Folder("A");
        var z = await Folder("Z");
        var b = await Folder("B", a);
        await Project("Leaf", b);
        var other = await Project("Other");
        var area = await Area();
        var aNode = Node(area.Roots, "A");
        await Expand(area, aNode);
        var bNode = Node(aNode.Children, "B");
        await Expand(area, bNode);

        await _other.MoveAsync(a, z); // A leaves the root; nothing of its subtree is on the reveal path
        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Project, other));

        Assert.Equal(["Z", "Other"], Names(area.Roots));
        Assert.Empty(Placements(area, a));
        Assert.Empty(Placements(area, b));

        await Expand(area, Node(area.Roots, "Z")); // the subtree is found again by a normal load...
        var reloadedA = Assert.Single(Placements(area, a));
        Assert.NotSame(aNode, reloadedA);           // ...as fresh instances: the old ones were unregistered
        await Expand(area, reloadedA);
        Assert.NotSame(bNode, Assert.Single(Placements(area, b)));
    }

    [Fact]
    public async Task OlderLazyLoadOfAnObsoleteAncestor_CannotResurrectTheRelocatedNode()
    {
        var a = await Folder("A");
        var z = await Folder("Z");
        var b = await Folder("B", a);
        var area = await Area();
        var aNode = Node(area.Roots, "A");
        var load = _treeStore.Hold(GatedWorkspaceStore.Children(a));
        aNode.IsExpanded = true; // reads now (B under A), lands later
        load.WaitEntered();
        await _other.MoveAsync(b, null);
        await _other.MoveAsync(a, z);

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.WorkspaceFolder, b));
        var bNode = Assert.Single(Placements(area, b));
        load.Release();
        await area.EnsureChildrenLoadedAsync(aNode);

        Assert.Same(bNode, Assert.Single(Placements(area, b))); // still exactly once, at the root
        Assert.Null(bNode.Parent);
        Assert.Empty(Names(aNode.Children));                  // the obsolete A never regains B
        Assert.Equal(["B", "Z"], Names(area.Roots));
    }

    [Fact]
    public async Task NotFound_LeavesTheTreeAlone()
    {
        var project = await Project("Gone soon");
        var area = await Area();
        using (var c = _root.Open())
            Db.Exec(c, "DELETE FROM Projects WHERE NodeId = $id; DELETE FROM WorkspaceNodes WHERE Id = $id;", ("$id", project));

        Assert.Equal(WorkspaceRevealOutcome.NotFound, await Reveal(area, SearchResultKind.Project, project));
        Assert.Equal(["Gone soon"], Names(area.Roots));
        Assert.Null(area.SelectedNode);
    }

    // ---- Content rows ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SameNumericIdForFolderLinkAndNote_InOneContainer_EachRevealSelectsExactlyItsOwnRow()
    {
        var project = await Project("Alpha");
        using (var c = _root.Open())
        {
            Db.Exec(c, "INSERT INTO ProjectFolders (Id, ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES (700, $p, NULL, 'Same', 'SAME', $n, $n);", ("$p", project), ("$n", Db.Now));
            Db.Exec(c, "INSERT INTO ProjectLinks (Id, ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES (700, $p, NULL, 'Web', 'Same', 'https://same.test/', $n, $n);", ("$p", project), ("$n", Db.Now));
            Db.Exec(c, "INSERT INTO ProjectNotes (Id, ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES (700, $p, NULL, 'Same', 'note body', $n, $n);", ("$p", project), ("$n", Db.Now));
        }

        var area = await Area();
        foreach (var (kind, rowKind) in new[] { (SearchResultKind.ProjectFolder, ProjectContentItemKind.Folder), (SearchResultKind.Link, ProjectContentItemKind.Link), (SearchResultKind.Note, ProjectContentItemKind.Note) })
        {
            Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, kind, 700));
            var content = area.SelectedContent!;
            Assert.Null(content.CurrentFolderId);
            Assert.Equal(3, content.Items.Count(i => i.Id == 700));
            Assert.Equal((rowKind, 700L), (content.SelectedItem!.Kind, content.SelectedItem.Id));
            await content.CurrentPreviewLoad;
            Assert.Equal(rowKind == ProjectContentItemKind.Note, content.IsPreviewVisible);
            if (rowKind == ProjectContentItemKind.Note)
                Assert.Equal("note body", content.PreviewNote!.Content);
        }

        Assert.Empty(_starter.Started); // nothing launched
    }

    [Fact]
    public async Task ProjectFolderResult_IsSelectedAsARowInItsParentContainer()
    {
        var project = await Project("Alpha");
        var specs = (await _otherContent.CreateFolderAsync(project, null, "Specs")).Value!.Id;
        var drafts = (await _otherContent.CreateFolderAsync(project, specs, "Drafts")).Value!.Id;
        var area = await Area();

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.ProjectFolder, drafts));

        Assert.Equal(specs, area.SelectedContent!.CurrentFolderId);
        Assert.Equal((ProjectContentItemKind.Folder, drafts), (area.SelectedContent.SelectedItem!.Kind, area.SelectedContent.SelectedItem.Id));
    }

    [Fact]
    public async Task NoteInANestedFolderUnderANeverLoadedWorkspaceFolder_IsRevealed()
    {
        var clients = await Folder("Clients");
        var tokyo = await Folder("Tokyo", clients);
        var project = await Project("Alpha", tokyo);
        var specs = (await _otherContent.CreateFolderAsync(project, null, "Specs")).Value!.Id;
        var note = (await _otherContent.CreateNoteAsync(project, specs, new ProjectNoteDraft("Kickoff", "agenda"))).Note!.Id;
        var area = await Area();
        Assert.False(Node(area.Roots, "Clients").IsExpanded);

        Assert.Equal(WorkspaceRevealOutcome.Selected, await Reveal(area, SearchResultKind.Note, note));

        Assert.True(Node(area.Roots, "Clients").IsExpanded);
        Assert.Equal("Alpha", area.SelectedNode!.Name);
        Assert.Equal(specs, area.SelectedContent!.CurrentFolderId);
        Assert.Equal((ProjectContentItemKind.Note, note), (area.SelectedContent.SelectedItem!.Kind, area.SelectedContent.SelectedItem.Id));
    }

    [Fact]
    public async Task ContentItemMovedAfterResolve_IsReportedAsMovedMeanwhile()
    {
        var project = await Project("Alpha");
        var x = (await _otherContent.CreateFolderAsync(project, null, "X")).Value!.Id;
        var y = (await _otherContent.CreateFolderAsync(project, null, "Y")).Value!.Id;
        var note = (await _otherContent.CreateNoteAsync(project, x, new ProjectNoteDraft("N", ""))).Note!.Id;
        var area = await Area();
        var gate = SearchStore.Hold("resolve");

        var reveal = Reveal(area, SearchResultKind.Note, note);
        gate.WaitEntered(); // resolved: in X
        await _otherContent.MoveNoteAsync(note, x, y);
        gate.Release();

        Assert.Equal(WorkspaceRevealOutcome.MovedMeanwhile, await reveal);
        Assert.Equal(x, area.SelectedContent!.CurrentFolderId);
        Assert.Null(area.SelectedContent.SelectedItem);
    }

    [Fact]
    public async Task ContainerRemovedAfterResolve_IsReportedAsContainerMissing()
    {
        var project = await Project("Alpha");
        var x = (await _otherContent.CreateFolderAsync(project, null, "X")).Value!.Id;
        var note = (await _otherContent.CreateNoteAsync(project, x, new ProjectNoteDraft("N", ""))).Note!.Id;
        var area = await Area();
        var gate = SearchStore.Hold("resolve");

        var reveal = Reveal(area, SearchResultKind.Note, note);
        gate.WaitEntered();
        await _otherContent.MoveNoteAsync(note, x, null);
        using (var c = _root.Open())
            Db.Exec(c, "DELETE FROM ProjectFolders WHERE Id = $id;", ("$id", x));
        gate.Release();

        Assert.Equal(WorkspaceRevealOutcome.ContainerMissing, await reveal);
    }

    [Fact]
    public async Task ManualSelectionWhileTheContentLoads_SupersedesTheContentReveal()
    {
        var project = await Project("Alpha");
        var other = await Project("Other");
        var x = (await _otherContent.CreateFolderAsync(project, null, "X")).Value!.Id;
        var note = (await _otherContent.CreateNoteAsync(project, x, new ProjectNoteDraft("N", ""))).Note!.Id;
        var area = await Area();
        var listing = _contentStore.Hold(GatedProjectContentStore.List(x));

        var reveal = Reveal(area, SearchResultKind.Note, note);
        listing.WaitEntered();
        area.SelectedNode = Node(area.Roots, "Other");
        listing.Release();

        Assert.Equal(WorkspaceRevealOutcome.Superseded, await reveal);
        Assert.Equal(other, area.SelectedNode!.Id);
    }

    [Fact]
    public async Task ResolveFailure_IsReportedAsFailed_BusyAsBusy()
    {
        var project = await Project("Alpha");
        var area = await Area();

        SearchStore.Hold("resolve").Fail(new IOException("disk"));
        Assert.Equal(WorkspaceRevealOutcome.Failed, await Reveal(area, SearchResultKind.Project, project));
        SearchStore.Hold("resolve").Fail(new WorkspaceSearchBusyException(TimeSpan.FromSeconds(30)));
        Assert.Equal(WorkspaceRevealOutcome.Busy, await Reveal(area, SearchResultKind.Project, project));
        Assert.Null(area.SelectedNode);
    }
}
