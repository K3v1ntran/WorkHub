using System.Xml.Linq;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 14 structural contracts, read from the shipped XAML and code-behind as DATA (nothing is launched): the purge
/// confirmation's safe defaults, Shift+Del scoped to the Project Trash list, and the Trash panel's commands. Appearance and
/// behaviour in a running window are left to the isolated manual verification.
/// </summary>
public sealed class Phase14UiContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string RepoRoot = FindRepoRoot();

    private static string AppFile(string relative) => Path.Combine(RepoRoot, "src", "WorkHub.App", relative);

    private static XDocument Xaml(string relative) => XDocument.Load(AppFile(relative));

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Fact]
    public void PurgeConfirmation_CancelIsFocused_AndIsBothTheEnterAndTheEscDefault()
    {
        var window = Xaml(@"Views\PurgeConfirmationWindow.xaml");
        var cancel = Named(window, "CancelButton");

        Assert.Equal("True", (string?)cancel.Attribute("IsDefault"));
        Assert.Equal("True", (string?)cancel.Attribute("IsCancel"));
        Assert.Equal("{Binding ElementName=CancelButton}", (string?)window.Root!.Attribute("FocusManager.FocusedElement"));
        var confirm = window.Descendants(P + "Button").Single(b => (string?)b.Attribute("Command") == "{Binding ConfirmCommand}");
        Assert.Null(confirm.Attribute("IsDefault"));
        Assert.Contains(window.Descendants(), e => (string?)e.Attribute("Text") == "{Binding ExplanationText}");
        Assert.Contains(window.Descendants(), e => (string?)e.Attribute("Text") == "{Binding TotalsText}");
    }

    [Fact]
    public void ProjectTrashList_AllowsExtendedSelection_AndOffersBothCommands()
    {
        var main = Xaml(@"Views\MainWindow.xaml");
        var list = Named(main, "TrashList");

        Assert.Equal("Extended", (string?)list.Attribute("SelectionMode"));
        Assert.Equal("TrashList_SelectionChanged", (string?)list.Attribute("SelectionChanged"));
        var menu = list.Descendants(P + "MenuItem").Select(m => ((string?)m.Attribute("Header"), (string?)m.Attribute("Command"), (string?)m.Attribute("InputGestureText"))).ToList();
        Assert.Contains(("Delete permanently…", "{Binding DeletePermanentlyCommand}", "Shift+Del"), menu);
        Assert.Contains(("Empty Trash…", "{Binding EmptyTrashCommand}", null), menu);
        var panel = Named(main, "TrashPanel");
        var commands = panel.Descendants(P + "Button").Select(b => (string?)b.Attribute("Command")).ToList();
        Assert.Contains("{Binding DeletePermanentlyCommand}", commands);
        Assert.Contains("{Binding EmptyTrashCommand}", commands);
        Assert.Contains("{Binding CancelPurgeCommand}", commands);
    }

    [Fact]
    public void ShiftDelete_IsHandledOnlyByTheProjectTrashList()
    {
        var code = File.ReadAllText(AppFile(@"Views\MainWindow.xaml.cs"));
        var handler = code[code.IndexOf("private void TrashList_PreviewKeyDown", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("private void", 20, StringComparison.Ordinal)];

        Assert.Contains("Key.Delete when Keyboard.Modifiers == ModifierKeys.Shift => content.Trash.DeletePermanentlyCommand", handler);
        Assert.Equal(1, code.Split("DeletePermanentlyCommand").Length - 1); // nowhere else in the window
        Assert.Contains("Key.Delete when Keyboard.Modifiers == ModifierKeys.None => content.MoveToTrashCommand", code); // Del is unchanged
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WorkHub.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("WorkHub.sln not found above the test output.");
    }
}
