using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 13A structural contracts, read from the shipped XAML, project file, icon file and built executable as DATA (nothing
/// is launched or rendered). These prove structure only; the visual appearance still needs the user's review (§10).
/// </summary>
public sealed class Phase13AUiContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 256];

    private static string AppFile(string relative) => Path.Combine(RepoRoot, "src", "WorkHub.App", relative);

    private static XDocument Xaml(string relative) => XDocument.Load(AppFile(relative));

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static XElement Keyed(XDocument doc, string key) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Key") == key);

    // ---- Icons -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Ico_HasEveryRequiredFrame_At32BitsPerPixel()
    {
        var bytes = File.ReadAllBytes(AppFile("WorkHub.ico"));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2)); // icon
        var count = BitConverter.ToUInt16(bytes, 4);
        var sizes = Enumerable.Range(0, count).Select(i => bytes[6 + i * 16] == 0 ? 256 : bytes[6 + i * 16]).Order().ToArray();
        Assert.Equal(IconSizes, sizes);
        Assert.All(Enumerable.Range(0, count), i => Assert.Equal(32, BitConverter.ToUInt16(bytes, 6 + i * 16 + 6)));
    }

    [Fact]
    public void Project_UsesTheIconForTheExecutable_AndShipsItForTheWindow()
    {
        var csproj = XDocument.Load(AppFile("WorkHub.App.csproj"));
        Assert.Equal("WorkHub.ico", csproj.Descendants("ApplicationIcon").Single().Value);
        Assert.Contains(csproj.Descendants("Resource"), r => (string?)r.Attribute("Include") == "WorkHub.ico");
        var window = Xaml(@"Views\MainWindow.xaml").Root!;
        Assert.Equal("pack://application:,,,/WorkHub;component/WorkHub.ico", (string?)window.Attribute("Icon"));
    }

    [Fact]
    public void BuiltExecutable_CarriesTheSameIconFrames()
    {
        var configuration = typeof(Phase13AUiContractTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var exe = Path.Combine(RepoRoot, "src", "WorkHub.App", "bin", configuration, "net10.0-windows", "WorkHub.exe");
        Assert.True(File.Exists(exe), $"Build the solution first ({exe}).");

        var sizes = ExeIconGroupSizes(exe);

        Assert.Equal(IconSizes, sizes.Order().ToArray());
    }

    // ---- Templates and states --------------------------------------------------------------------------------------------

    private static void AssertFocusRing(XElement style)
    {
        var template = style.Descendants(P + "ControlTemplate").Single();
        var ring = template.Descendants(P + "Border").Single(b => (string?)b.Attribute(X + "Name") == "FocusRing");
        Assert.Equal("2", (string?)ring.Attribute("BorderThickness"));
        Assert.Equal("{StaticResource Brush.Accent}", (string?)ring.Attribute("BorderBrush"));
        Assert.Equal("Collapsed", (string?)ring.Attribute("Visibility"));
        var triggers = template.Descendants(P + "Trigger").ToList();
        var focus = triggers.Single(t => (string?)t.Attribute("Property") == "IsKeyboardFocused");
        Assert.Contains(focus.Elements(P + "Setter"), s => (string?)s.Attribute("TargetName") == "FocusRing" && (string?)s.Attribute("Value") == "Visible");
        var hover = triggers.Single(t => (string?)t.Attribute("Property") == "IsMouseOver");
        Assert.DoesNotContain(hover.Elements(P + "Setter"), s => (string?)s.Attribute("TargetName") == "FocusRing"); // hover ≠ focus
        Assert.Contains(triggers, t => (string?)t.Attribute("Property") == "IsPressed");
        Assert.Contains(triggers, t => (string?)t.Attribute("Property") == "IsEnabled" && (string?)t.Attribute("Value") == "False");
    }

    [Fact]
    public void SharedButtonStyles_HaveASeparateKeyboardFocusRing_AndPressedAndDisabledStates()
    {
        var app = Xaml("App.xaml");
        AssertFocusRing(app.Descendants(P + "Style").Single(s => (string?)s.Attribute("TargetType") == "Button" && s.Attribute(X + "Key") is null));
        AssertFocusRing(Keyed(app, "ToolButton"));
        AssertFocusRing(Keyed(Xaml(@"Views\MainWindow.xaml"), "LibraryRow"));
        Assert.Equal("{StaticResource ToolButton}", (string?)Keyed(app, "IconPillButton").Attribute("BasedOn"));
        Assert.Equal("{StaticResource ToolButton}", (string?)Keyed(app, "PillButton").Attribute("BasedOn"));
        Assert.Equal("{StaticResource PillButton}", (string?)Keyed(app, "TogglePillButton").Attribute("BasedOn"));
    }

    [Fact]
    public void TreeRows_KeepTheFullWidthRowAndTheExistingIndentation()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var item = Keyed(window, "WorkspaceTreeItem");
        var row = item.Descendants(P + "Border").Single(b => (string?)b.Attribute(X + "Name") == "Row");
        Assert.Equal("2,0,0,0", (string?)row.Attribute("BorderThickness")); // full-width row with the left accent bar
        Assert.Contains("Converter={StaticResource WorkspaceIndent}", (string?)row.Element(P + "Grid")!.Attribute("Margin"));
        Assert.Equal("18", (string?)Keyed(window, "WorkspaceIndent").Attribute("Indent"));
        Assert.Contains("ItemsPresenter", item.Descendants().Select(e => e.Name.LocalName)); // children render full-width below
    }

    // ---- Layout -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Strip_WrapsInsteadOfTrimmingTheDates()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        Assert.Equal("WrapPanel", Named(window, "StripItems").Name.LocalName);
        foreach (var name in new[] { "StripCreated", "StripUpdated" })
        {
            var date = Named(window, name);
            Assert.Equal("None", (string?)date.Attribute("TextTrimming"));
            Assert.Equal("NoWrap", (string?)date.Attribute("TextWrapping"));
            Assert.Null(date.Attribute("Width"));
            Assert.Null(date.Attribute("MaxWidth"));
        }
    }

    [Fact]
    public void StatusCreatedAndUpdated_AreSeparateWrappingUnits_EachKeepingItsLabelWithItsValue()
    {
        // Codex finding: the facts shared one StackPanel, an indivisible WrapPanel child, so they could not wrap separately.
        var window = Xaml(@"Views\MainWindow.xaml");
        var strip = Named(window, "StripItems");
        var units = new[] { "StripStatusUnit", "StripCreatedUnit", "StripUpdatedUnit" }.Select(n => Named(window, n)).ToArray();
        Assert.All(units, unit => Assert.Same(strip, unit.Parent)); // direct children: each wraps on its own
        Assert.Equal("SelectedProjectStatus", (string?)units[0].Descendants(P + "TextBlock").Single().Attribute("AutomationProperties.AutomationId"));
        foreach (var (unit, label, value) in new[] { (units[1], "Created", "StripCreated"), (units[2], "Updated", "StripUpdated") })
        {
            var texts = unit.Elements(P + "TextBlock").ToList();
            Assert.Equal(2, texts.Count);
            Assert.Equal(label, (string?)texts[0].Attribute("Text"));
            Assert.Equal(value, (string?)texts[1].Attribute(X + "Name")); // the value stays in the label's unit
            Assert.NotNull(texts[1].Attribute("ToolTip"));
        }

        Assert.Equal(1, window.Descendants().Count(e => (string?)e.Attribute(X + "Name") == "StripCreated"));
    }

    [Fact]
    public void BreadcrumbAndContentActions_ShareOneWrappingRow_AndEveryActionStaysAccessible()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var row = Named(window, "ContentHeaderRow");
        Assert.Equal("WrapPanel", row.Name.LocalName);
        Assert.Same(row, Named(window, "ContentBreadcrumb").Parent);
        var toolbar = Named(window, "ContentToolbar");
        Assert.Same(row, toolbar.Parent);
        var names = toolbar.Elements(P + "Button").Select(b => (string?)b.Attribute("AutomationProperties.Name")).ToList();
        Assert.Equal(["Up one level", "New content folder", "New link", "New note", "Open", "Edit content item", "Move content item",
            "Move to Trash", "Check link", "Check all links", "Stop checking", "Open Trash"], names);
        Assert.All(toolbar.Elements(P + "Button"), b => Assert.False(string.IsNullOrWhiteSpace((string?)b.Attribute("ToolTip"))));
    }

    [Fact]
    public void OnlyTheActionGroupIsHideable_MoreAndInfoAlwaysStay()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var toolbars = new[] { Named(window, "ProjectSelectionToolbar"), Named(window, "FolderSelectionToolbar") };
        foreach (var toolbar in toolbars)
            Assert.Contains(toolbar.Descendants(P + "Condition"), c => (string?)c.Attribute("Binding") == "{Binding ShowSelectionToolbar}");
        foreach (var always in new[] { Named(window, "SelectionMoreButton"), Named(window, "InfoToggle") })
        {
            Assert.DoesNotContain(toolbars, t => t.Descendants().Contains(always));
            Assert.DoesNotContain(always.DescendantsAndSelf().Attributes(), a => a.Value.Contains("ShowSelectionToolbar"));
        }

        Assert.Equal("{StaticResource WorkspaceSelectionMenu}", (string?)Named(window, "SelectionMoreButton").Attribute("ContextMenu"));
        Assert.Contains(window.Descendants(P + "StaticResource"), r => (string?)r.Attribute("ResourceKey") == "WorkspaceSelectionMenu"); // the tree menu
    }

    private static bool References(XElement element, string text) =>
        element.DescendantsAndSelf().Attributes().Any(a => a.Value.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void ThreeToolbars_EachFollowOnlyTheirOwnPreference_AndNothingElseIsHidden()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var tree = Named(window, "TreeToolbar");
        var content = Named(window, "ContentToolbar");
        var selection = new[] { Named(window, "ProjectSelectionToolbar"), Named(window, "FolderSelectionToolbar") };

        Assert.Equal("{Binding ShowTreeToolbar, Converter={StaticResource BoolToVisibility}}", (string?)tree.Attribute("Visibility"));
        Assert.Contains(content.Descendants(P + "Condition"), c => (string?)c.Attribute("Binding") == "{Binding DataContext.ShowContentToolbar, ElementName=WorkspaceTree}");
        // No toolbar follows another toolbar's preference.
        Assert.False(References(tree, "ShowSelectionToolbar") || References(tree, "ShowContentToolbar"));
        Assert.False(References(content, "ShowTreeToolbar") || References(content, "ShowSelectionToolbar"));
        Assert.All(selection, s => Assert.False(References(s, "ShowTreeToolbar") || References(s, "ShowContentToolbar")));

        // Search, the tree, LIBRARY, the breadcrumb, the facts, Info and More are outside every toolbar and follow no switch.
        var toolbars = selection.Append(tree).Append(content).ToArray();
        foreach (var name in new[] { "WorkspaceSearchBox", "WorkspaceTree", "LibrarySection", "ContentBreadcrumb", "StripStatusUnit",
                     "StripCreatedUnit", "StripUpdatedUnit", "InfoToggle", "SelectionMoreButton" })
        {
            var always = Named(window, name);
            Assert.DoesNotContain(toolbars, t => t.DescendantsAndSelf().Contains(always));
            Assert.False(References(always, "ShowTreeToolbar") || References(always, "ShowSelectionToolbar")
                         || References(always, "ShowContentToolbar"), name);
        }
    }

    [Fact]
    public void HiddenTreeOrContentToolbar_LeavesACompactMore_WithEveryActionOfThatToolbar()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var treeMore = Named(window, "TreeMoreButton");
        Assert.Equal("{Binding ShowTreeToolbar, Converter={StaticResource InverseBoolToVisibility}}", (string?)treeMore.Attribute("Visibility"));
        Assert.Equal("{StaticResource WorkspaceSelectionMenu}", (string?)treeMore.Attribute("ContextMenu"));
        var treeCommands = Named(window, "TreeToolbar").Descendants(P + "Button").Select(b => (string?)b.Attribute("Command")).ToHashSet();
        var selectionMenu = Keyed(window, "WorkspaceSelectionMenu").Descendants(P + "MenuItem").Select(m => (string?)m.Attribute("Command")).ToHashSet();
        Assert.Subset(selectionMenu, treeCommands);

        var contentMore = Named(window, "ContentMoreButton");
        Assert.Contains(contentMore.Descendants(P + "Condition"),
            c => (string?)c.Attribute("Binding") == "{Binding DataContext.ShowContentToolbar, ElementName=WorkspaceTree}" && (string?)c.Attribute("Value") == "False");
        Assert.DoesNotContain(contentMore, Named(window, "ContentToolbar").DescendantsAndSelf());
        var contentCommands = Named(window, "ContentToolbar").Descendants(P + "Button").Select(b => (string?)b.Attribute("Command")).ToHashSet();
        var contentMenu = contentMore.Descendants(P + "MenuItem").Select(m => (string?)m.Attribute("Command")).ToHashSet();
        Assert.Subset(contentMenu, contentCommands);
        Assert.False(string.IsNullOrWhiteSpace((string?)treeMore.Attribute("AutomationProperties.Name")));
        Assert.False(string.IsNullOrWhiteSpace((string?)contentMore.Attribute("AutomationProperties.Name")));
    }

    [Fact]
    public void ContextualShowAndHide_AffectOnlyTheToolbarOfThatMenu()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var menus = window.Descendants(P + "MenuItem")
            .Select(m => (Header: (string?)m.Attribute("Header") ?? "", Command: (string?)m.Attribute("Command") ?? ""))
            .Where(m => m.Header.Contains("toolbar", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(menus);
        foreach (var (header, command) in menus)
        {
            var toolbar = header.Split(' ')[1]; // "Show tree toolbar" → "tree"
            Assert.Contains($"{char.ToUpperInvariant(toolbar[0])}{toolbar[1..]}ToolbarCommand", command);
            Assert.StartsWith(header.Split(' ')[0], command.Split("Preferences.")[1]); // Show → Show…Command, Hide → Hide…Command
        }

        Assert.Contains(menus, m => m.Header == "Hide tree toolbar");
        Assert.Contains(menus, m => m.Header == "Hide selection toolbar");
        Assert.Contains(menus, m => m.Header == "Hide content toolbar");
    }

    [Fact]
    public void SettingsWorkspacePage_HasOneClearlyNamedCheckboxPerToolbar()
    {
        var settings = Xaml(@"Views\SettingsWindow.xaml");
        var boxes = settings.Descendants(P + "CheckBox")
            .Select(c => ((string?)c.Attribute("AutomationProperties.Name"), (string?)c.Attribute("IsChecked")))
            .ToList();
        Assert.Contains(("Show the tree toolbar", "{Binding ShowTreeToolbar}"), boxes);
        Assert.Contains(("Show the selection toolbar", "{Binding ShowSelectionToolbar}"), boxes);
        Assert.Contains(("Show the content toolbar", "{Binding ShowContentToolbar}"), boxes);
    }

    [Fact]
    public void Library_IsDockedAtTheBottom_BeforeTheTree()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var library = Named(window, "LibrarySection");
        Assert.Equal("Bottom", (string?)library.Attribute("DockPanel.Dock"));
        var siblings = library.Parent!.Elements().ToList();
        var treeHost = siblings.Last(); // the DockPanel's fill child (tree and search results)
        Assert.True(siblings.IndexOf(library) < siblings.IndexOf(treeHost));
        Assert.Contains(treeHost.Descendants(P + "TreeView"), t => (string?)t.Attribute(X + "Name") == "WorkspaceTree");
        Assert.Equal(["OpenArchivedProjects", "OpenWorkspaceTrash"],
            library.Elements(P + "Button").Select(b => (string?)b.Attribute("AutomationProperties.AutomationId") ?? string.Empty).ToArray());
    }

    [Fact]
    public void Header_HidesTheRoutineStatus_AndKeepsTheTestRootIndicator()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var status = window.Descendants(P + "TextBlock").Single(t => (string?)t.Attribute("AutomationProperties.AutomationId") == "StatusText");
        var trigger = status.Descendants(P + "DataTrigger").Single();
        Assert.Equal("{Binding IsReady}", (string?)trigger.Attribute("Binding"));
        Assert.Contains(trigger.Elements(P + "Setter"), s => (string?)s.Attribute("Property") == "Visibility" && (string?)s.Attribute("Value") == "Collapsed");
        Assert.Contains(window.Descendants(P + "TextBlock"), t => (string?)t.Attribute("AutomationProperties.AutomationId") == "CustomDataRoot");
        Assert.Equal("{Binding BrandToolTip}", (string?)Named(window, "Brand").Attribute("ToolTip"));
        Assert.DoesNotContain(window.Descendants(P + "ScrollViewer"), s => s.Descendants().Any(d => (string?)d.Attribute("Value") == "300")); // the old details block
    }

    [Fact]
    public void IconOnlyControls_HaveAccessibleNamesAndTooltips()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var regions = new[]
            {
                Named(window, "SelectionStrip"), Named(window, "LibrarySection"), Named(window, "InfoDrawer"), Named(window, "TreeToolbar"),
                Named(window, "ContentHeaderRow"), Named(window, "TreeMoreButton"),
            };
        var buttons = regions.SelectMany(r => r.DescendantsAndSelf()).Where(e => e.Name.LocalName is "Button" or "ToggleButton").ToList();
        Assert.NotEmpty(buttons);
        foreach (var button in buttons)
        {
            var name = (string?)button.Attribute("AutomationProperties.Name")
                       ?? button.Descendants(P + "Setter").FirstOrDefault(s => (string?)s.Attribute("Property") == "AutomationProperties.Name")?.Attribute("Value")?.Value;
            Assert.False(string.IsNullOrWhiteSpace(name), $"A button without an accessible name: {button}");
            var iconOnly = button.Attribute("Content") is null && !button.Elements().Where(e => !e.Name.LocalName.Contains('.')).DescendantsAndSelf(P + "TextBlock").Any();
            if (iconOnly)
                Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("ToolTip")), $"Icon-only button without a tooltip: {name}");
        }
    }

    // ---- Graphical icons (§0d C1) ------------------------------------------------------------------------------------

    private static IEnumerable<string> AppXamlFiles() =>
        Directory.GetFiles(AppFile("Views"), "*.xaml").Append(AppFile("App.xaml"));

    private static bool IsPrivateUseGlyph(char c) => c is >= '' and <= '';

    [Fact]
    public void NoFontGlyphIcons_RemainInTheApp()
    {
        foreach (var file in AppXamlFiles())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Segoe Fluent Icons", text);
            Assert.DoesNotContain("Segoe MDL2", text);
            Assert.DoesNotContain("{StaticResource Icon}", text);
            Assert.DoesNotMatch(@"&#x[Ee][0-9A-Fa-f]{3};", text); // private-use code points written as entities
            var doc = XDocument.Load(file);
            Assert.DoesNotContain(doc.Descendants().Attributes(), a => a.Value.Any(IsPrivateUseGlyph));
        }
    }

    private static XDocument IconDictionary() => Xaml(@"Resources\Icons.xaml");

    [Fact]
    public void IconDictionary_HoldsOnlyVectorDrawings()
    {
        var icons = IconDictionary();
        var resources = icons.Root!.Elements().ToList();
        Assert.True(resources.Count >= 30);
        foreach (var image in resources)
        {
            Assert.Equal("DrawingImage", image.Name.LocalName);
            var key = (string?)image.Attribute(X + "Key");
            Assert.StartsWith("Img.", key);
            // Drawn shapes with fills or outlines: no text, glyph runs, fonts or bitmaps.
            Assert.DoesNotContain(image.Descendants(), e => e.Name.LocalName is "GlyphRunDrawing" or "ImageDrawing" or "VideoDrawing" or "GlyphRun");
            Assert.DoesNotContain(image.Descendants().Attributes(), a => a.Name.LocalName == "FontFamily");
            var painted = image.Descendants(P + "GeometryDrawing").Where(g => g.Descendants().Any(d => d.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush")).ToList();
            Assert.True(painted.Count >= 1, $"{key} draws nothing");
        }

        foreach (var required in new[] { "Folder", "Project", "Link", "Note", "Add", "Edit", "Move", "Archive", "Trash", "Info" })
            Assert.Contains(resources, r => (string?)r.Attribute(X + "Key") == $"Img.{required}");
    }

    [Fact]
    public void EveryReferencedIcon_Exists()
    {
        var keys = IconDictionary().Root!.Elements().Select(e => (string?)e.Attribute(X + "Key")).ToHashSet();
        foreach (var file in AppXamlFiles())
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"StaticResource (Img\.\w+)\}"))
                Assert.Contains(m.Groups[1].Value, keys);
        }
    }

    private static string? IconOf(XElement commandSource) =>
        commandSource.Descendants(P + "Image").Select(i => (string?)i.Attribute("Source")).FirstOrDefault();

    [Fact]
    public void EachCommand_UsesOneIcon_AcrossToolbarsAndMenus()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var workspaceScope = new[] { Keyed(window, "WorkspaceSelectionMenu"), Named(window, "TreeToolbar"), Named(window, "SelectionStrip") };
        var contentScope = new[] { Named(window, "ContentHeaderRow"), Named(window, "ContentList") };
        foreach (var scope in new[] { workspaceScope, contentScope })
        {
            var byCommand = scope.SelectMany(s => s.Descendants())
                .Where(e => e.Name.LocalName is "Button" or "MenuItem" && e.Attribute("Command") is not null && IconOf(e) is not null)
                .GroupBy(e => (string)e.Attribute("Command")!);
            foreach (var group in byCommand)
            {
                var icons = group.Select(IconOf).Distinct().ToList();
                Assert.True(icons.Count == 1, $"{group.Key} uses {string.Join(", ", icons)}");
            }
        }

        // Rows: the tree, search results and lists use the type icons.
        var tree = Named(window, "WorkspaceTree");
        Assert.Contains(tree.Descendants(P + "Image"), i => (string?)i.Attribute("Source") == "{StaticResource Img.Folder}");
        Assert.Contains(tree.Descendants(P + "Setter"), s => (string?)s.Attribute("Value") == "{StaticResource Img.Project}");
    }

    [Fact]
    public void TreeExpander_IsADrawnChevron()
    {
        var window = Xaml(@"Views\MainWindow.xaml");
        var expander = Keyed(window, "WorkspaceTreeExpander");
        var chevron = expander.Descendants(P + "Path").Single(p => (string?)p.Attribute(X + "Name") == "Chevron");
        Assert.False(string.IsNullOrWhiteSpace((string?)chevron.Attribute("Data")));
    }

    [Fact]
    public void NewShortcuts_DoNotCollideWithExistingBindings()
    {
        var xamlFiles = Directory.GetFiles(AppFile("Views"), "*.xaml");
        foreach (var file in xamlFiles)
        {
            var doc = XDocument.Load(file);
            foreach (var binding in doc.Descendants(P + "KeyBinding"))
            {
                var key = (string?)binding.Attribute("Key");
                var modifiers = (string?)binding.Attribute("Modifiers") ?? string.Empty;
                Assert.False(modifiers.Contains("Control") && key is "I" or "A" or "T", $"{Path.GetFileName(file)}: Ctrl+{key} is already bound");
            }
        }

        var codeBehind = File.ReadAllText(AppFile(@"Views\MainWindow.xaml.cs"));
        Assert.Equal(1, CountOf(codeBehind, "Key.I,"));
        Assert.Equal(1, CountOf(codeBehind, "Key.A,"));
        Assert.Equal(1, CountOf(codeBehind, "Key.T,"));
    }

    private static int CountOf(string text, string value) => (text.Length - text.Replace(value, string.Empty).Length) / value.Length;

    // ---- Helpers ------------------------------------------------------------------------------------------------------------

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WorkHub.sln")))
                return directory.FullName;
        }

        throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }

    private const uint LoadLibraryAsDatafile = 0x2;
    private const uint LoadLibraryAsImageResource = 0x20;
    private static readonly IntPtr RtGroupIcon = 14;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr data);

    [DllImport("kernel32.dll")]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    /// <summary>The frame sizes of the executable's first RT_GROUP_ICON, read with a data-file load (no code runs).</summary>
    private static List<int> ExeIconGroupSizes(string exe)
    {
        var module = LoadLibraryEx(exe, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
        Assert.NotEqual(IntPtr.Zero, module);
        try
        {
            var group = IntPtr.Zero;
            EnumResourceNames(module, RtGroupIcon, (_, _, name, _) => { group = name; return false; }, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, group);
            var resource = FindResource(module, group, RtGroupIcon);
            var size = (int)SizeofResource(module, resource);
            var bytes = new byte[size];
            Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, size);
            var count = BitConverter.ToUInt16(bytes, 4);
            return Enumerable.Range(0, count).Select(i => bytes[6 + i * 14] == 0 ? 256 : bytes[6 + i * 14]).ToList();
        }
        finally
        {
            FreeLibrary(module);
        }
    }
}
