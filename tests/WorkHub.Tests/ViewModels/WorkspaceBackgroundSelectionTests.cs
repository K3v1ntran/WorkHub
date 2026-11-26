using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 25 R2 acceptance (proposal Revision 2 + R2a §2.2, §2.3, §4.1: BG1–BG7; mouse-only, D6(b) keeps Esc unchanged so BG-E1 does
/// not apply). The window classifies a pointer with <see cref="WorkspaceTreeHitTest"/> and calls
/// <see cref="WorkspaceAreaViewModel.ClearSelection"/> for a background click; these cases drive the same view-model calls and feed the
/// classifier the element chains the window builds. Real mouse input is GUI GT25-4–11 (not run here). Fake names only.
/// </summary>
public sealed class WorkspaceBackgroundSelectionTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly GatedWorkspaceStore _treeStore;
    private readonly WorkspaceService _other;
    private readonly List<WorkspaceAreaViewModel> _areas = [];

    public WorkspaceBackgroundSelectionTests()
    {
        _root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _treeStore = new GatedWorkspaceStore(new SqliteWorkspaceStore(connections));
        _other = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose()
    {
        _treeStore.ReleaseAll();
        foreach (var area in _areas)
            Task.WhenAll(area.CurrentDetailsLoad).ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(10));
        _root.Dispose();
    }

    private async Task<WorkspaceAreaViewModel> AreaAsync()
    {
        var area = new WorkspaceAreaViewModel(new WorkspaceService(_treeStore), _dialogs, NullAppLog.Instance);
        _areas.Add(area);
        area.IsTabSelected = true;
        await area.RootLoad;
        await area.Trash.CurrentCount;
        await area.Archived.CurrentCount;
        return area;
    }

    private async Task<long> Folder(string name, long? parent = null) => (await _other.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null) =>
        (await _other.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;

    private static WorkspaceNodeViewModel Node(IEnumerable<WorkspaceNodeViewModel> nodes, string name) => nodes.Single(n => !n.IsPlaceholder && n.Name == name);

    private static async Task Expand(WorkspaceAreaViewModel area, WorkspaceNodeViewModel folder)
    {
        folder.IsExpanded = true;
        Assert.True(await area.EnsureChildrenLoadedAsync(folder));
    }

    // ---- BG1–BG5: the view-model side of background clicks ------------------------------------------------------------------------

    [Fact]
    public async Task BG1_BackgroundLeftClick_ClearsTheSelection_ClosesInfo_StartsNoDetailsLoad()
    {
        var p = await Project("P");
        var area = await AreaAsync();
        area.SelectedNode = Node(area.Roots, "P");
        await area.CurrentDetailsLoad;
        area.ToggleInfoCommand.Execute(null);
        Assert.True(area.IsInfoOpen);
        var details = _treeStore.Calls($"details:{p}");

        area.ClearSelection();

        Assert.Null(area.SelectedNode);
        Assert.False(area.IsInfoOpen);
        Assert.Null(area.SelectedProject);
        await area.CurrentDetailsLoad;
        Assert.Equal(details, _treeStore.Calls($"details:{p}"));
    }

    [Fact]
    public async Task BG2_FolderSelected_BackgroundRightClick_NewFolder_CreatesAtTheTopLevel()
    {
        await Folder("F");
        var area = await AreaAsync();
        area.SelectedNode = Node(area.Roots, "F");
        string? location = null;
        WorkspaceNode? created = null;
        _dialogs.OnWorkspaceNameEditor = editor =>
        {
            location = editor.LocationText;
            editor.Name = "New";
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10));
            created = editor.CreatedNode;
            return created is not null;
        };

        area.ClearSelection();                                              // the window does this before the menu opens
        await area.NewFolderCommand.ExecuteAsync();

        Assert.Equal("Location: Workspace (top level)", location);
        Assert.NotNull(created);
        Assert.Null(created!.ParentId);
        Assert.Contains(area.Roots, n => n.Id == created.Id);
    }

    [Fact]
    public async Task BG3_RightClickOnAFolderRow_NewFolder_CreatesInsideIt()
    {
        var f = await Folder("F");
        var area = await AreaAsync();
        string? location = null;
        WorkspaceNode? created = null;
        _dialogs.OnWorkspaceNameEditor = editor =>
        {
            location = editor.LocationText;
            editor.Name = "Inner";
            _dialogs.WaitModal(editor.SaveCommand.ExecuteAsync(), TimeSpan.FromSeconds(10));
            created = editor.CreatedNode;
            return created is not null;
        };

        Node(area.Roots, "F").IsSelected = true;                            // the window selects the right-clicked row
        await area.NewFolderCommand.ExecuteAsync();

        Assert.Equal("Location: F", location);
        Assert.Equal(f, created!.ParentId);
    }

    [Fact]
    public async Task BG4_ProjectInAFolderSelected_BackgroundRightClick_NewProject_IsAtTheTopLevel()
    {
        var f = await Folder("F");
        await Project("P", f);
        var area = await AreaAsync();
        await Expand(area, Node(area.Roots, "F"));
        area.SelectedNode = Node(Node(area.Roots, "F").Children, "P");
        await area.CurrentDetailsLoad;
        string? location = null;
        _dialogs.OnProjectEditor = editor =>
        {
            location = editor.LocationText;
            return false;
        };

        area.ClearSelection();
        await area.NewProjectCommand.ExecuteAsync();

        Assert.Equal("Location: Workspace (top level)", location);
    }

    [Fact]
    public async Task BG5_AfterABackgroundRightClick_SelectionItemsAreDisabled_CreationItemsEnabled()
    {
        await Folder("F");
        var area = await AreaAsync();
        area.SelectedNode = Node(area.Roots, "F");

        area.ClearSelection();

        Assert.False(area.RenameCommand.CanExecute(null));
        Assert.False(area.MoveCommand.CanExecute(null));
        Assert.False(area.ArchiveFolderCommand.CanExecute(null));
        Assert.False(area.TrashFolderCommand.CanExecute(null));
        Assert.False(area.TrashProjectCommand.CanExecute(null));
        Assert.False(area.ToggleInfoCommand.CanExecute(null));
        Assert.True(area.NewFolderCommand.CanExecute(null));
        Assert.True(area.NewProjectCommand.CanExecute(null));
        Assert.True(area.ManageTagsCommand.CanExecute(null));
    }

    [Fact]
    public async Task BG1_WithTheWorkspaceTrashOpen_ABackgroundClickClearsTheSelection_AndKeepsTheTrashOpen()
    {
        await Folder("F");
        var area = await AreaAsync();
        area.SelectedNode = Node(area.Roots, "F");
        await area.OpenTrashCommand.ExecuteAsync();
        Assert.True(area.IsTrashOpen);

        area.ClearSelection();

        Assert.Null(area.SelectedNode);
        Assert.True(area.IsTrashOpen);
    }

    // ---- BG6, BG7: the §2.3 classifier -------------------------------------------------------------------------------------------

    private static TreeHitElement E(TreeHitElementKind kind, WorkspaceNodeViewModel? node = null) => new(kind, node);

    [Fact]
    public async Task BG6_HitTestDecisionTable()
    {
        await Folder("F");
        await Folder("G");
        var area = await AreaAsync();
        var (f, g) = (Node(area.Roots, "F"), Node(area.Roots, "G"));
        TreeHit Classify(params TreeHitElement[] chain) => WorkspaceTreeHitTest.Classify(chain);
        var other = E(TreeHitElementKind.Other);
        var tree = E(TreeHitElementKind.Tree);

        // A scroll bar part (track, thumb) inside the tree's template.
        Assert.Equal(new TreeHit(TreeHitKind.ScrollBar), Classify(other, E(TreeHitElementKind.ScrollBar), other, tree));
        // The expander of F (its glyph first).
        Assert.Equal(new TreeHit(TreeHitKind.Expander, f), Classify(other, E(TreeHitElementKind.Expander, f), E(TreeHitElementKind.Row, f), E(TreeHitElementKind.Item, f), tree));
        // F's label text, its icon, and the empty space right of a short label (the Row border itself): all F's row area.
        Assert.Equal(new TreeHit(TreeHitKind.Row, f), Classify(other, other, E(TreeHitElementKind.Row, f), E(TreeHitElementKind.Item, f), tree));
        Assert.Equal(new TreeHit(TreeHitKind.Row, f), Classify(E(TreeHitElementKind.Row, f), E(TreeHitElementKind.Item, f), tree));
        // A child G's row inside F's ItemsHost: G, never F.
        Assert.Equal(new TreeHit(TreeHitKind.Row, g), Classify(other, E(TreeHitElementKind.Row, g), E(TreeHitElementKind.Item, g), other, E(TreeHitElementKind.Item, f), tree));
        // A point in F's ItemsHost that is not on a child row: F's TreeViewItem is met WITHOUT its Row → not F's row → background.
        Assert.Equal(new TreeHit(TreeHitKind.Background), Classify(other, E(TreeHitElementKind.Item, f), other, tree));
        // Below the last row: the tree only.
        Assert.Equal(new TreeHit(TreeHitKind.Background), Classify(other, tree));
        Assert.Equal(new TreeHit(TreeHitKind.Background), Classify(tree));
        // Not a tree point at all.
        Assert.Equal(new TreeHit(TreeHitKind.Outside), Classify(other, other));
        Assert.True(Classify(E(TreeHitElementKind.Row, f), tree).IsNodeRow);
    }

    [Fact]
    public async Task BG7_ExpanderAndScrollBar_AreNeitherRowNorBackground_SelectionKept()
    {
        await Folder("F");
        await Folder("G");
        var area = await AreaAsync();
        var (f, g) = (Node(area.Roots, "F"), Node(area.Roots, "G"));
        area.SelectedNode = f;

        var expander = WorkspaceTreeHitTest.Classify([E(TreeHitElementKind.Expander, g), E(TreeHitElementKind.Row, g), E(TreeHitElementKind.Tree)]);
        var scrollBar = WorkspaceTreeHitTest.Classify([E(TreeHitElementKind.ScrollBar), E(TreeHitElementKind.Tree)]);

        // The window clears the selection only for Background and selects only for a node row (contract TG25-C3).
        Assert.Equal(TreeHitKind.Expander, expander.Kind);
        Assert.False(expander.IsNodeRow);
        Assert.Equal(TreeHitKind.ScrollBar, scrollBar.Kind);
        Assert.False(scrollBar.IsNodeRow);
        g.IsExpanded = true;                                                // the expander toggles G only
        await area.EnsureChildrenLoadedAsync(g);
        Assert.Same(f, area.SelectedNode);
    }
}
