using System.Xml.Linq;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 16 structural contracts, read from the shipped XAML and code as DATA (nothing is launched): the Favorites entry,
/// buttons and menu items (acceptance F15), and the App wiring of the window-state save and the start-up peek (§4.2, §4.6).
/// Behaviour in a running window is left to the GUI checks W1–W11.
/// </summary>
public sealed class Phase16UiContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string RepoRoot = FindRepoRoot();

    private static string AppFile(string relative) => Path.Combine(RepoRoot, "src", "WorkHub.App", relative);

    private static XDocument Xaml(string relative) => XDocument.Load(AppFile(relative));

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string? IconOf(XElement element) =>
        element.Descendants(P + "Image").Select(i => (string?)i.Attribute("Source")).FirstOrDefault();

    [Fact]
    public void F15_TheFavoritesEntry_IsAboveTheGroups_NamedForAutomation_WithATooltipAndTheFavoriteIcon()
    {
        var main = Xaml(@"Views\MainWindow.xaml");
        var favorites = Named(main, "OwnerFavoritesList");
        var groups = Named(main, "OwnerGroupList");

        Assert.Equal("Owner favorites", (string?)favorites.Attribute("AutomationProperties.Name"));
        Assert.False(string.IsNullOrWhiteSpace((string?)favorites.Attribute("ToolTip")));
        Assert.Equal("{Binding Owner.FavoritesEntries}", (string?)favorites.Attribute("ItemsSource"));
        Assert.Equal("{Binding Owner.SelectedFavoritesEntry}", (string?)favorites.Attribute("SelectedItem"));
        Assert.Contains("Owner.HasFavorites", (string?)favorites.Attribute("Visibility"));
        Assert.Equal("{StaticResource Img.Favorite}", IconOf(favorites));
        Assert.Equal("{Binding Owner.SelectedOwnerGroup}", (string?)groups.Attribute("SelectedItem"));
        var siblings = favorites.Parent!.Elements().ToList();
        Assert.True(siblings.IndexOf(favorites) < siblings.IndexOf(groups), "Favorites must sit above the Owner groups");
    }

    [Fact]
    public void F15_TheFavoriteButtonAndMenuItems_AreNamed_HaveTooltips_AndUseOneIcon()
    {
        var main = Xaml(@"Views\MainWindow.xaml");
        var button = Named(main, "OwnerFavoriteButton");
        Assert.Equal("{Binding ToggleFavoriteCommand}", (string?)button.Attribute("Command"));
        Assert.Equal("{Binding FavoriteActionText}", (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding FavoriteActionText}", (string?)button.Attribute("ToolTip"));
        Assert.Equal("{StaticResource Img.Favorite}", IconOf(button));

        foreach (var (command, header) in new[] { ("{Binding AddToFavoritesCommand}", "Add to Favorites"), ("{Binding RemoveFromFavoritesCommand}", "Remove from Favorites") })
        {
            var items = main.Descendants(P + "MenuItem").Where(m => (string?)m.Attribute("Command") == command).ToList();
            Assert.Equal(2, items.Count); // the Owner More (⋯) menu and the launcher list's context menu
            foreach (var item in items)
            {
                Assert.Equal(header, (string?)item.Attribute("Header"));
                Assert.Equal(header, (string?)item.Attribute("AutomationProperties.Name"));
                Assert.False(string.IsNullOrWhiteSpace((string?)item.Attribute("ToolTip")));
                Assert.Equal("{StaticResource Img.Favorite}", IconOf(item));
            }
        }

        Assert.Contains(Named(main, "OwnerItemList").Descendants(P + "MenuItem"), m => (string?)m.Attribute("Command") == "{Binding AddToFavoritesCommand}");
        Assert.Contains(Named(main, "OwnerMoreButton").Descendants(P + "MenuItem"), m => (string?)m.Attribute("Command") == "{Binding RemoveFromFavoritesCommand}");
    }

    [Fact]
    public void F15_TheFavoriteIcon_IsAnOriginalVectorDrawing_InTheIconDictionary()
    {
        var icon = Xaml(@"Resources\Icons.xaml").Root!.Elements().Single(e => (string?)e.Attribute(X + "Key") == "Img.Favorite");

        Assert.Equal("DrawingImage", icon.Name.LocalName);
        Assert.DoesNotContain(icon.Descendants(), e => e.Name.LocalName is "GlyphRunDrawing" or "ImageDrawing" or "GlyphRun");
        Assert.Contains(icon.Descendants(), e => e.Name.LocalName == "LinearGradientBrush");
    }

    [Fact]
    public void TheMainWindow_SavesOnlyAfterEveryClosingHandler_WithTheFinalCancelValue()
    {
        var code = File.ReadAllText(AppFile(@"Views\MainWindow.xaml.cs"));
        var onClosing = code.IndexOf("protected override void OnClosing(CancelEventArgs e)", StringComparison.Ordinal);
        Assert.True(onClosing > 0);
        var body = code[onClosing..code.IndexOf('}', onClosing)];
        Assert.True(body.IndexOf("base.OnClosing(e);", StringComparison.Ordinal) < body.IndexOf("OnWindowClosing(e.Cancel)", StringComparison.Ordinal));
        Assert.Contains("e.Cancel |= Main?.IsCloseBlocked == true", code); // the Phase 10 close guard is still a Closing handler
    }

    [Fact]
    public void TheApp_PeeksBeforeShowing_AppliesThePlacementBeforeShow_AndWrapsTheLifetime()
    {
        var code = File.ReadAllText(AppFile("App.xaml.cs"));

        Assert.Contains("new SavingAppLifetime(new AppLifetime(options.Options), saver)", code);
        var peek = code.IndexOf("PeekUi()", StringComparison.Ordinal);
        var apply = code.IndexOf("WindowPlacement.Apply(", StringComparison.Ordinal);
        var show = code.IndexOf("window.Show();", StringComparison.Ordinal);
        var initialize = code.IndexOf("await viewModel.InitializeAsync();", StringComparison.Ordinal);
        Assert.True(peek > 0 && peek < apply && apply < show && show < initialize, "peek → apply → Show → initialize");
        Assert.DoesNotContain("LoadOrCreate", code); // the peek never creates the settings file
    }

    [Fact]
    public void TheRelaunchOrder_IsUnchanged_RelaunchIsPassedThrough()
    {
        var lifetime = File.ReadAllText(AppFile(@"Services\AppLifetime.cs"));
        Assert.Contains("public void Shutdown() => Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());", lifetime);

        var saving = File.ReadAllText(Path.Combine(RepoRoot, "src", "WorkHub.Presentation", "Services", "WindowStateSaver.cs"));
        Assert.Contains("public bool Relaunch(string? restoreOp) => inner.Relaunch(restoreOp);", saving);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
