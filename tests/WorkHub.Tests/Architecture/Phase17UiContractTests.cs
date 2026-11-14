using System.Xml.Linq;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 17 structural contracts, read from the shipped XAML as DATA (nothing is launched): the tag box and the suggestions are
/// disabled through <c>CanAddTag</c> at 20 chips, the limit text is bound, chips stay removable, and the Info drawer lists the tags.
/// Behaviour in a running window is left to the GUI checks G1–G9.
/// </summary>
public sealed class Phase17UiContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Xaml(string relative) => XDocument.Load(Path.Combine(FindRepoRoot(), "src", "WorkHub.App", relative));

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void TheTagBoxAndTheSuggestions_AreDisabledThroughCanAddTag_AndTheLimitTextIsShown()
    {
        var editor = Xaml(@"Views\ProjectEditorWindow.xaml");
        var box = Named(editor, "TagBox");
        Assert.Equal("{Binding CanAddTag}", (string?)box.Attribute("IsEnabled"));
        Assert.Equal("{Binding TagInput, UpdateSourceTrigger=PropertyChanged}", (string?)box.Attribute("Text"));
        Assert.Equal("{Binding CanAddTag}", (string?)Named(editor, "TagSuggestions").Attribute("IsEnabled"));
        var hint = Named(editor, "TagLimitHint");
        Assert.Equal("{Binding TagLimitText}", (string?)hint.Attribute("Text"));
        Assert.Contains("TagLimitText", (string?)hint.Attribute("Visibility"));

        // The chip list itself is not disabled: ✕ keeps working at 20.
        var chips = editor.Descendants(P + "ItemsControl").Single(e => (string?)e.Attribute("ItemsSource") == "{Binding Tags}");
        Assert.Null(chips.Attribute("IsEnabled"));
        Assert.Contains(chips.Descendants(P + "Button"), b => (string?)b.Attribute("Command") == "{Binding Command}");
    }

    [Fact]
    public void TheInfoDrawer_ListsTheSelectedProjectsTagsAsChips()
    {
        var main = Xaml(@"Views\MainWindow.xaml");
        var tags = main.Descendants(P + "ItemsControl").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "InfoTags");
        Assert.Equal("{Binding SelectedProjectTags}", (string?)tags.Attribute("ItemsSource"));
        Assert.Contains(tags.Descendants(P + "Button"), b => (string?)b.Attribute("Command") == "{Binding Command}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
