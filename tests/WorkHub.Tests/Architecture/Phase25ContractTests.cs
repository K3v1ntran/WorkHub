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
        var entry = Between(source, "public WorkspaceDropEffect Drop(", "/// <summary>Tests: <see cref=\"Drop\"/>");
        Assert.DoesNotContain("await", entry, StringComparison.Ordinal);                         // the entry is synchronous
        var resolve = entry.IndexOf("TryResolveDrop(", StringComparison.Ordinal);
        var slot = entry.IndexOf("IsLifecycleBusy = true;", StringComparison.Ordinal);
        var begin = entry.IndexOf("BeginWorkspaceWrite();", StringComparison.Ordinal);
        var start = entry.IndexOf("CurrentDrop = MoveByDropCoreAsync(source, target);", StringComparison.Ordinal);
        var move = entry.IndexOf("return WorkspaceDropEffect.Move;", StringComparison.Ordinal);
        Assert.True(resolve >= 0 && slot > resolve && begin > slot && start > begin && move > start); // re-check → slot → Begin → core → Move
        Assert.Equal(1, CountOf(entry, "return WorkspaceDropEffect.Move;"));                     // Move only on the accepted path

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
        // Banner text only (string literals; comments are not banner text): the core writes no "Nothing was changed" of its own, and the
        // only drop banner literal carrying it is CouldNotMoveText's.
        Assert.DoesNotContain(StringLiterals(core), l => l.Contains("Nothing was changed", StringComparison.Ordinal));
        Assert.Equal(["Could not move “{name}”. Nothing was changed."],
            StringLiterals(source).Where(l => l.Contains("Nothing was changed", StringComparison.Ordinal)));
        Assert.Contains("internal static string CouldNotMoveText(string name) => $\"Could not move “{name}”. Nothing was changed.\";", source, StringComparison.Ordinal);
        // After the commit the banner is the moved text, plus TreeNotUpdatedNote when the tree could not be updated.
        // Phase 26: the same banner text, shown through ShowBanner (auto-hide only when the tree was updated).
        Assert.Contains("ShowBanner(MovedText(name, targetName) + (shown ? string.Empty : TreeNotUpdatedNote), shown ? BannerKind.AutoDismiss : BannerKind.Persistent);", core, StringComparison.Ordinal);
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
        // Review fix: both drop handlers report the view model's synchronous acceptance; nothing reports Move on its own at drop time.
        Assert.DoesNotContain("workspace.MoveByDropAsync(", code, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(code, "e.Effects = ToWpf(workspace.Drop("));
        Assert.Contains("private void WorkspaceTree_Drop(object sender, DragEventArgs e)", code, StringComparison.Ordinal);   // synchronous handlers
        Assert.Contains("private void RootDropStrip_Drop(object sender, DragEventArgs e)", code, StringComparison.Ordinal);
        foreach (var drop in new[]
                 {
                     Between(code, "private void WorkspaceTree_Drop(", "private static DragDropEffects ToWpf("),
                     Between(code, "private void RootDropStrip_Drop(", "\n    }"),
                 })
        {
            Assert.Equal(1, CountOf(drop, "e.Effects = ToWpf(workspace.Drop("));                      // the effect is the view model's acceptance
            Assert.DoesNotContain("e.Effects = DragDropEffects.Move;", drop, StringComparison.Ordinal);
        }

        Assert.Contains("workspace.DragHoverEnter(drag.Session, hover, FolderRowUnderPointer);", code, StringComparison.Ordinal);
        var probe = Between(code, "private long? FolderRowUnderPointer()", "\n    }");
        Assert.Contains("VisualTreeHelper.HitTest(WorkspaceTree, p)", probe, StringComparison.Ordinal);
        Assert.Contains("ClassifyTreePoint(visual)", probe, StringComparison.Ordinal);
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
        Assert.Contains("HoverClock.Schedule(HoverExpandDelay, () => OnDragHoverElapsed(hoverSession, folderId, turn));", source, StringComparison.Ordinal);
        var elapsed = Between(source, "private void OnDragHoverElapsed(", "// ---- R1: the drop");
        Assert.Contains("session != _dragSession", elapsed, StringComparison.Ordinal);
        Assert.Contains("current.FolderId != folderId", elapsed, StringComparison.Ordinal);
        Assert.Contains("turn != _hoverTurn", elapsed, StringComparison.Ordinal);                         // a late callback of an earlier hover
        Assert.Contains("_nodes.TryGetValue(folderId, out var folder)", elapsed, StringComparison.Ordinal);
        Assert.Contains("var probe = _hoverProbe;", elapsed, StringComparison.Ordinal);
        Assert.Contains("CancelDragHover();", elapsed, StringComparison.Ordinal);                         // the expired hover is cleared
        Assert.Contains("probe?.Invoke() != folderId", elapsed, StringComparison.Ordinal);                // the row under the pointer NOW
        Assert.Contains("!CanDrop(session, _dragSourceId, folderId)", elapsed, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromMilliseconds(800), WorkHub.Presentation.ViewModels.WorkspaceAreaViewModel.HoverExpandDelay);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>The contents of the regular and interpolated string literals in C# source, skipping comments and char literals.</summary>
    private static List<string> StringLiterals(string code)
    {
        var literals = new List<string>();
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n')
                    i++;
            }
            else if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                var close = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? code.Length : close + 1;
            }
            else if (code[i] == '\'')
            {
                for (i++; i < code.Length && code[i] != '\''; i++)
                    if (code[i] == '\\')
                        i++;
            }
            else if (code[i] == '"')
            {
                var text = new System.Text.StringBuilder();
                for (i++; i < code.Length && code[i] != '"'; i++)
                {
                    if (code[i] == '\\' && i + 1 < code.Length)
                        text.Append(code[i++]);
                    text.Append(code[i]);
                }

                literals.Add(text.ToString());
            }
        }

        return literals;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
