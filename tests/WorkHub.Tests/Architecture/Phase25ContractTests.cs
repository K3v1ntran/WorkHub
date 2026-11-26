using System.Xml.Linq;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 25 structural contracts (proposal Revision 2 + R2a §2.1–§2.3, §4.2), read from the shipped files and types as DATA (nothing is
/// launched): no migration, schema version or package; <see cref="IWorkspaceStore"/> unchanged; the tree has <c>AllowDrop</c> and the
/// drag handlers; the drop path uses <c>WorkspaceService.MoveAsync</c>; in <c>MoveByDropAsync</c> the slot is taken before the first
/// await and before <c>BeginWorkspaceWrite()</c> and released in a finally; "Nothing was changed" only on the write-failure branch;
/// the hit test names Row, Expander and ScrollBar (never "any ancestor TreeViewItem"); Esc unchanged (D6(b)); Move to… unchanged.
/// Behaviour: WorkspaceDragDropTests, WorkspaceBackgroundSelectionTests; real mouse: GUI GT25-1–11 (not run here).
/// </summary>
public sealed class Phase25ContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoFile(params string[] parts) => Path.Combine([FindRepoRoot(), .. parts]);

    private static string DragDropSource() =>
        File.ReadAllText(RepoFile("src", "WorkHub.Presentation", "ViewModels", "WorkspaceAreaViewModel.DragDrop.cs"));

    private static string Between(string text, string start, string end)
    {
        var s = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(s >= 0, start);
        var e = text.IndexOf(end, s + start.Length, StringComparison.Ordinal);
        Assert.True(e > s, end);
        return text[s..e];
    }

    [Fact]
    public void TG25_C1_NoMigration_PackagesUnchanged_StoreUnchanged()
    {
        Assert.Equal(10, SchemaMigrations.LatestVersion);
        Assert.Equal(Enumerable.Range(1, 10), SchemaMigrations.All.Select(m => m.Version));
        Assert.Equal(["Microsoft.Data.Sqlite@10.0.13"],
            XDocument.Load(RepoFile("src", "WorkHub.Infrastructure", "WorkHub.Infrastructure.csproj")).Descendants("PackageReference")
                .Select(e => $"{(string?)e.Attribute("Include")}@{(string?)e.Attribute("Version")}"));
        // The store gains nothing: the drop reuses MoveAsync.
        Assert.DoesNotContain(typeof(IWorkspaceStore).GetMethods(), m => m.Name.Contains("Drop", StringComparison.Ordinal) || m.Name.Contains("Drag", StringComparison.Ordinal));
        Assert.Equal([typeof(long), typeof(long?), typeof(CancellationToken)],
            typeof(IWorkspaceStore).GetMethod(nameof(IWorkspaceStore.MoveAsync))!.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void TG25_C2_TheDropHoldsTheSlot_BeginWorkspaceWriteOnlyAfter_ReleasedInFinally_OutcomesSeparated()
    {
        var source = DragDropSource();
        var entry = Between(source, "public Task<WorkspaceDropOutcome> MoveByDropAsync(", "private async Task<WorkspaceDropOutcome> MoveByDropCoreAsync(");
        Assert.DoesNotContain("await", entry, StringComparison.Ordinal);                         // the entry is synchronous
        var resolve = entry.IndexOf("TryResolveDrop(", StringComparison.Ordinal);
        var slot = entry.IndexOf("IsLifecycleBusy = true;", StringComparison.Ordinal);
        var begin = entry.IndexOf("BeginWorkspaceWrite();", StringComparison.Ordinal);
        Assert.True(resolve >= 0 && slot > resolve && begin > slot);                              // re-check → slot → BeginWorkspaceWrite

        var core = Between(source, "private async Task<WorkspaceDropOutcome> MoveByDropCoreAsync(", "/// <summary>§2.1 step 5;");
        Assert.Contains("await _workspace.MoveAsync(sourceId, targetId);", core, StringComparison.Ordinal);
        var lastFinally = core.LastIndexOf("finally", StringComparison.Ordinal);
        Assert.True(lastFinally > core.IndexOf("await ApplyMovedAsync(source, targetId);", StringComparison.Ordinal));
        Assert.Contains("IsLifecycleBusy = false;", core[lastFinally..], StringComparison.Ordinal);
        Assert.Equal(1, CountOf(source, "IsLifecycleBusy = true;"));
        Assert.Equal(1, CountOf(source, "IsLifecycleBusy = false;"));

        // "Nothing was changed" only on the write-failure branch: CouldNotMoveText is used once, inside the MoveAsync catch.
        Assert.Equal(1, CountOf(core, "CouldNotMoveText(name)"));
        var writeCatch = Between(core, "result = await _workspace.MoveAsync(sourceId, targetId);", "if (!result.Succeeded)");
        Assert.Contains("CouldNotMoveText(name)", writeCatch, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was changed", core, StringComparison.Ordinal);
        Assert.Contains("internal const string TreeNotUpdatedNote = \" The Workspace tree could not be updated.\";", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(core, "_workspace.MoveAsync("));                                  // never a second Move
    }

    [Fact]
    public void TG25_C3_TreeWiring_HitTestNamesRowExpanderScrollBar_BackgroundClearsOnlyOnBackground()
    {
        var main = XDocument.Load(RepoFile("src", "WorkHub.App", "Views", "MainWindow.xaml"));
        var tree = main.Descendants(P + "TreeView").Single(e => (string?)e.Attribute(X + "Name") == "WorkspaceTree");
        Assert.Equal("True", (string?)tree.Attribute("AllowDrop"));
        Assert.Equal("WorkspaceTree_DragOver", (string?)tree.Attribute("DragEnter"));
        Assert.Equal("WorkspaceTree_DragOver", (string?)tree.Attribute("DragOver"));
        Assert.Equal("WorkspaceTree_DragLeave", (string?)tree.Attribute("DragLeave"));
        Assert.Equal("WorkspaceTree_Drop", (string?)tree.Attribute("Drop"));
        Assert.Equal("WorkspaceTree_PreviewMouseMove", (string?)tree.Attribute("PreviewMouseMove"));
        var strip = main.Descendants(P + "Border").Single(e => (string?)e.Attribute(X + "Name") == "RootDropStrip");
        Assert.Equal("True", (string?)strip.Attribute("AllowDrop"));
        Assert.Equal("{Binding IsDragActive, Converter={StaticResource BoolToVisibility}}", (string?)strip.Attribute("Visibility"));
        // The item template still has the parts the hit test names.
        var template = main.Descendants(P + "Style").Single(e => (string?)e.Attribute(X + "Key") == "WorkspaceTreeItem");
        Assert.Single(template.Descendants(P + "Border"), e => (string?)e.Attribute(X + "Name") == "Row");
        Assert.Single(template.Descendants(P + "ToggleButton"), e => (string?)e.Attribute(X + "Name") == "Expander");
        // D6(b): no new Esc binding on the tree.
        Assert.DoesNotContain(tree.Descendants(P + "KeyBinding"), e => (string?)e.Attribute("Key") == "Escape");

        var code = File.ReadAllText(RepoFile("src", "WorkHub.App", "Views", "MainWindow.xaml.cs"));
        var chain = Between(code, "private IEnumerable<TreeHitElement> TreeHitChain(", "private void WorkspaceTree_PreviewMouseRightButtonDown(");
        Assert.Contains("ScrollBar => new TreeHitElement(TreeHitElementKind.ScrollBar)", chain, StringComparison.Ordinal);
        Assert.Contains("ToggleButton { Name: \"Expander\", TemplatedParent: TreeViewItem item }", chain, StringComparison.Ordinal);
        Assert.Contains("Border { Name: \"Row\", TemplatedParent: TreeViewItem item }", chain, StringComparison.Ordinal);
        Assert.Contains("TreeViewItem item => new TreeHitElement(TreeHitElementKind.Item,", chain, StringComparison.Ordinal);
        var right = Between(code, "private void WorkspaceTree_PreviewMouseRightButtonDown(", "private void WorkspaceTree_PreviewMouseLeftButtonDown(");
        var left = Between(code, "private void WorkspaceTree_PreviewMouseLeftButtonDown(", "private void WorkspaceTree_PreviewMouseLeftButtonUp(");
        foreach (var handler in new[] { right, left })
        {
            Assert.Contains("ClassifyTreePoint(e.OriginalSource)", handler, StringComparison.Ordinal);
            Assert.Equal(1, CountOf(handler, "workspace.ClearSelection();"));
            Assert.DoesNotContain("d is TreeViewItem", handler, StringComparison.Ordinal);         // never "any ancestor TreeViewItem"
        }

        Assert.Contains("TreeHitKind.Background", right, StringComparison.Ordinal);
        Assert.Contains("case TreeHitKind.Background:", left, StringComparison.Ordinal);
        Assert.Contains("if (workspace.IsTrashOpen)", left, StringComparison.Ordinal);            // the Trash case, on a row area only
        Assert.Contains("SystemParameters.MinimumHorizontalDragDistance", code, StringComparison.Ordinal);
        Assert.Contains("new DataObject(WorkspaceNodeDragFormat, candidate.NodeId)", code, StringComparison.Ordinal); // the Id only
        Assert.Equal(2, CountOf(code, "workspace.MoveByDropAsync("));                                // the tree and the root strip
    }

    [Fact]
    public void TG25_C4_MoveToDialogUnchanged_HoverKeyedBySessionAndFolderId()
    {
        var area = File.ReadAllText(RepoFile("src", "WorkHub.Presentation", "ViewModels", "WorkspaceAreaViewModel.cs"));
        var move = Between(area, "private async Task MoveAsync()", "// ---- Workspace lifecycle:");
        Assert.Contains("var editor = new WorkspaceMoveViewModel(_workspace, node.Id, node.Name, node.Parent?.Id, targets);", move, StringComparison.Ordinal);
        Assert.Contains("await ApplyMovedAsync(node, editor.MovedTo);", move, StringComparison.Ordinal);
        Assert.DoesNotContain("IsLifecycleBusy", move, StringComparison.Ordinal);                  // Move to… still takes no slot (§2.4)

        var source = DragDropSource();
        Assert.Contains("private (int Session, long FolderId)? _hover;", source, StringComparison.Ordinal);
        Assert.Contains("HoverClock.Schedule(HoverExpandDelay, () => OnDragHoverElapsed(hoverSession, folderId));", source, StringComparison.Ordinal);
        var elapsed = Between(source, "private void OnDragHoverElapsed(", "// ---- R1: the drop");
        Assert.Contains("session != _dragSession", elapsed, StringComparison.Ordinal);
        Assert.Contains("current.FolderId != folderId", elapsed, StringComparison.Ordinal);
        Assert.Contains("_nodes.TryGetValue(folderId, out var folder)", elapsed, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMilliseconds(800), WorkHub.Presentation.ViewModels.WorkspaceAreaViewModel.HoverExpandDelay);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
