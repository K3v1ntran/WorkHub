using System.Xml.Linq;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 13B structural contracts, read from the shipped XAML as DATA (nothing is launched): the bounded, Cancel-default Folder
/// lifecycle confirmation (§7.4 "Dialog"; B13), and the Folder actions wired into the menu and both toolbars that hold Folder
/// actions, with Del routing and the 13A toolbar independence unchanged. Visual appearance still needs the user's review.
/// </summary>
public sealed class Phase13BUiContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Xaml(string relative) => XDocument.Load(Path.Combine(FindRepoRoot(), "src", "WorkHub.App", relative));

    private static XElement Named(XDocument doc, string name) => doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static XElement Keyed(XDocument doc, string key) => doc.Descendants().Single(e => (string?)e.Attribute(X + "Key") == key);

    [Fact]
    public void TheFolderConfirmation_HasAFixedMaximumSize_CancelIsTheDefaultForEnterAndEsc_AndItNeverListsTheSubtree()
    {
        var window = Xaml(@"Views\FolderLifecycleWindow.xaml");
        var root = window.Root!;
        Assert.Equal("440", (string?)root.Attribute("MaxHeight"));
        Assert.Equal("480", (string?)root.Attribute("Width"));
        Assert.Equal("NoResize", (string?)root.Attribute("ResizeMode"));
        Assert.Equal("{Binding ElementName=CancelButton}", (string?)root.Attribute("FocusManager.FocusedElement"));

        var cancel = Named(window, "CancelButton");
        Assert.Equal(("True", "True", "{Binding CancelCommand}"), ((string?)cancel.Attribute("IsCancel"), (string?)cancel.Attribute("IsDefault"), (string?)cancel.Attribute("Command")));
        var buttons = window.Descendants(P + "Button").ToList();
        Assert.Equal(2, buttons.Count);
        Assert.Single(buttons, b => (string?)b.Attribute("IsDefault") == "True"); // the action button is never the default
        Assert.Single(buttons, b => (string?)b.Attribute("Command") == "{Binding ConfirmCommand}");

        // The only list is the (at most three) shown children; there is no scrolling list of the subtree.
        var lists = window.Descendants().Where(e => e.Name.LocalName is "ItemsControl" or "ListBox" or "ListView" or "DataGrid" or "TreeView" or "ScrollViewer").ToList();
        Assert.Equal("{Binding ShownChildren}", (string?)Assert.Single(lists).Attribute("ItemsSource"));
        Assert.Contains(window.Descendants(P + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding MoreText}");
        Assert.Contains(window.Descendants(P + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding StaleText}");
    }

    [Fact]
    public void FolderActions_AreInTheMenu_TheTreeToolbar_AndTheFolderSelectionToolbar_DelStillRoutesThroughDeleteCommand()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        foreach (var command in new[] { "{Binding ArchiveFolderCommand}", "{Binding TrashFolderCommand}" })
        {
            Assert.Contains(Keyed(window, "WorkspaceSelectionMenu").Descendants(P + "MenuItem"), m => (string?)m.Attribute("Command") == command);
            Assert.Contains(Named(window, "TreeToolbar").Descendants(P + "Button"), b => (string?)b.Attribute("Command") == command);
            Assert.Contains(Named(window, "FolderSelectionToolbar").Descendants(P + "Button"), b => (string?)b.Attribute("Command") == command);
            Assert.DoesNotContain(Named(window, "ProjectSelectionToolbar").Descendants(P + "Button"), b => (string?)b.Attribute("Command") == command);
        }

        Assert.All(Named(window, "FolderSelectionToolbar").Descendants(P + "Button"), b =>
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)b.Attribute("AutomationProperties.Name")));
            Assert.False(string.IsNullOrWhiteSpace((string?)b.Attribute("ToolTip")));
        });

        var tree = Named(window, "WorkspaceTree");
        var bindings = tree.Descendants(P + "KeyBinding").Select(k => ((string?)k.Attribute("Key"), (string?)k.Attribute("Command"))).ToList();
        Assert.Equal([("F2", "{Binding RenameCommand}"), ("Delete", "{Binding DeleteCommand}")], bindings); // no shortcut changed
    }

    [Fact]
    public void TheLibraryRows_KeepTheirAutomationIds_AndBothPanelsShowAFolderIconForFolderRoots()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var library = Named(window, "LibrarySection");
        Assert.Equal(["OpenArchivedProjects", "OpenWorkspaceTrash"],
            library.Descendants(P + "Button").Select(b => (string?)b.Attribute("AutomationProperties.AutomationId")));
        foreach (var list in new[] { "WorkspaceTrashList", "ArchivedProjectsList" })
        {
            Assert.Contains(Named(window, list).Descendants(P + "DataTrigger"),
                t => (string?)t.Attribute("Binding") == "{Binding IsFolder}" && t.Descendants(P + "Setter").Any(s => (string?)s.Attribute("Value") == "{StaticResource Img.Folder}"));
        }
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WorkHub.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
