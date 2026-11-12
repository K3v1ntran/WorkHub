using System.Xml.Linq;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 15 UI contract (acceptance U4, U7; D-15.5): the Copy/Move dialog's Cancel is focused and is both the Enter and the Esc
/// default; the commands sit next to Move to… in the content toolbar, the More menu and the context menu; no keyboard shortcut
/// is added; and the destination notice offers Open destination and Dismiss only — never an Undo.
/// </summary>
public sealed class Phase15UiContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string RepoRoot = FindRepoRoot();

    private static string AppFile(string relative) => Path.Combine(RepoRoot, "src", "WorkHub.App", relative);

    private static XDocument Xaml(string relative) => XDocument.Load(AppFile(relative));

    [Fact]
    public void CrossProjectDialog_CancelIsFocused_AndIsBothTheEnterAndTheEscDefault_ConfirmIsNot()
    {
        var window = Xaml(@"Views\CrossProjectWindow.xaml");
        var cancel = window.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "CancelButton");

        Assert.Equal("True", (string?)cancel.Attribute("IsDefault"));
        Assert.Equal("True", (string?)cancel.Attribute("IsCancel"));
        Assert.Equal("{Binding ElementName=CancelButton}", (string?)window.Root!.Attribute("FocusManager.FocusedElement"));
        var confirm = window.Descendants(P + "Button").Single(b => (string?)b.Attribute("Command") == "{Binding ConfirmCommand}");
        Assert.Null(confirm.Attribute("IsDefault"));
        Assert.Null(confirm.Attribute("IsCancel"));
        foreach (var binding in new[] { "{Binding CountsText}", "{Binding SkippedText}", "{Binding PathText}", "{Binding MoveNote}", "{Binding Message}" })
            Assert.Contains(window.Descendants(), e => (string?)e.Attribute("Text") == binding);
    }

    /// <summary>
    /// Found in the Phase 15 GUI check G4: with a binding Delay, Confirm stayed enabled on the previous preview while the box already
    /// showed an edited name, and the older name was committed. The name must reach the view model at once (a new preview disables
    /// Confirm until it is applied).
    /// </summary>
    [Fact]
    public void CrossProjectDialog_TheDestinationNameBindsImmediately_WithoutADelay()
    {
        var window = Xaml(@"Views\CrossProjectWindow.xaml");
        var name = window.Descendants(P + "TextBox").Single(t => (string?)t.Attribute("AutomationProperties.AutomationId") == "CrossRootName");
        var binding = (string?)name.Attribute("Text");

        Assert.Equal("{Binding RootName, UpdateSourceTrigger=PropertyChanged}", binding);
        Assert.DoesNotContain(window.Descendants().Attributes(), a => a.Value.Contains("Delay=", StringComparison.Ordinal));
    }

    [Fact]
    public void CopyAndMove_SitNextToMoveTo_InTheToolbarTheMoreMenuAndTheContextMenu()
    {
        var main = Xaml(@"Views\MainWindow.xaml");
        foreach (var command in new[] { "{Binding CopyToProjectCommand}", "{Binding MoveToProjectCommand}" })
        {
            Assert.Single(main.Descendants(P + "Button"), b => (string?)b.Attribute("Command") == command);       // content toolbar
            Assert.Equal(2, main.Descendants(P + "MenuItem").Count(m => (string?)m.Attribute("Command") == command)); // More menu + context menu
        }

        var contentList = main.Descendants(P + "ListView").Single(e => (string?)e.Attribute(X + "Name") == "ContentList");
        var items = contentList.Descendants(P + "MenuItem").Select(m => (string?)m.Attribute("Command")).ToList();
        Assert.Equal(items.IndexOf("{Binding MoveCommand}") + 1, items.IndexOf("{Binding CopyToProjectCommand}"));
        Assert.Equal(items.IndexOf("{Binding MoveCommand}") + 2, items.IndexOf("{Binding MoveToProjectCommand}"));
    }

    [Fact]
    public void NoKeyboardShortcutIsAdded_ForCopyOrMoveToAnotherProject()
    {
        var main = Xaml(@"Views\MainWindow.xaml");
        Assert.DoesNotContain(main.Descendants(P + "KeyBinding"), k => ((string?)k.Attribute("Command") ?? string.Empty).Contains("ToProject", StringComparison.Ordinal));
        Assert.DoesNotContain(main.Descendants(P + "MenuItem"), m => ((string?)m.Attribute("Command") ?? string.Empty).Contains("ToProject", StringComparison.Ordinal) && m.Attribute("InputGestureText") is not null);
        var codeBehind = File.ReadAllText(AppFile(@"Views\MainWindow.xaml.cs"));
        Assert.DoesNotContain("CopyToProjectCommand", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToProjectCommand", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDestinationNotice_OffersOpenDestinationAndDismiss_AndNoUndo()
    {
        var main = Xaml(@"Views\MainWindow.xaml");
        var notice = main.Descendants(P + "Border").Single(b => (string?)b.Attribute("AutomationProperties.Name") == "Destination notice");
        var commands = notice.Descendants(P + "Button").Select(b => (string?)b.Attribute("Command")).ToList();

        Assert.Equal(new[] { "{Binding DismissDestinationCommand}", "{Binding OpenDestinationCommand}" }, commands);
        Assert.DoesNotContain(notice.Descendants(), e => e.Attributes().Any(a => a.Value.Contains("Undo", StringComparison.Ordinal)));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkHub.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output.");
    }
}
