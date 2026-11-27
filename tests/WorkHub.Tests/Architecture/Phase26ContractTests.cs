using System.Xml.Linq;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Presentation.ViewModels;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 26 structural contracts (proposal Revision 2 §3.1–§3.3, §5.2: TG26-C1–C6), read from the shipped files and types as DATA
/// (nothing is launched): every banner write goes through ShowBanner with Persistent as the default; AutoDismiss only at the reviewed
/// success call sites; the expiry checks the message turn, the timer arm, visibility, pointer and focus; Undo / Details force
/// Persistent; the Undo callers choose the kind from a typed RestoreResultKind; the window only reports banner state; Owner /
/// Repository banners, schema and packages unchanged. Behaviour: BannerAutoDismissTests; real mouse: GUI GT26 (not run here).
/// </summary>
public sealed class Phase26ContractTests
{
    private static string RepoFile(params string[] parts) => Path.Combine([FindRepoRoot(), .. parts]);

    /// <summary>A shipped view-model source with "\n" line endings (the markers below do not depend on the checkout's line endings).</summary>
    private static string ViewModel(string name) => File.ReadAllText(RepoFile("src", "WorkHub.Presentation", "ViewModels", name)).ReplaceLineEndings("\n");

    private static string Between(string text, string start, string end)
    {
        var s = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(s >= 0, start);
        var e = text.IndexOf(end, s + start.Length, StringComparison.Ordinal);
        Assert.True(e > s, end);
        return text[s..e];
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>The source without comment lines (// and ///), so documentation never counts as a call site.</summary>
    private static string Code(string source) =>
        string.Join('\n', source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void TG26_C1_EveryBannerWriteGoesThroughShowBanner_PersistentByDefault()
    {
        Assert.Equal(BannerKind.Persistent, default(BannerKind));
        foreach (var file in new[] { "WorkspaceAreaViewModel.cs", "ProjectContentViewModel.cs" })
        {
            var source = ViewModel(file);
            Assert.Contains("private set => ShowBanner(value, BannerKind.Persistent);", source, StringComparison.Ordinal);
            Assert.Equal(1, CountOf(source, "ref _bannerMessage"));                                  // only ShowBanner writes the field
            Assert.Contains("_bannerAutoDismiss.OnShown(text, kind);", Between(source, "private void ShowBanner(string? text, BannerKind kind)", "\n    }"), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TG26_C2_AutoDismissOnlyAtTheReviewedSuccessCallSites()
    {
        var workspace = ViewModel("WorkspaceAreaViewModel.cs");
        var drop = ViewModel("WorkspaceAreaViewModel.DragDrop.cs");
        var content = ViewModel("ProjectContentViewModel.cs");

        // Workspace: Removed empty folder, the Undo Archive Undone arm, the two typed Undo callers, and the Undo / Details guard.
        Assert.Equal(5, CountOf(Code(workspace), "BannerKind.AutoDismiss"));
        Assert.Contains("ShowBanner($\"Removed the empty folder “{name}”.\", BannerKind.AutoDismiss);",
            Between(workspace, "case FolderRemoveOutcome.Removed:", "break;"), StringComparison.Ordinal);
        Assert.Contains("BannerKind.AutoDismiss);", Between(workspace, "case ArchiveUndoOutcome.Undone:", "break;"), StringComparison.Ordinal);
        Assert.Equal(2, CountOf(workspace, "ShowBanner(notice, kind == RestoreResultKind.Restored ? BannerKind.AutoDismiss : BannerKind.Persistent);"));
        Assert.Contains("if (kind == BannerKind.AutoDismiss && (_undo is not null || IsBulkDetailsAvailable))", workspace, StringComparison.Ordinal);

        // Drop: only the plain "Moved …" result (the tree-not-updated note stays Persistent).
        Assert.Equal(1, CountOf(Code(drop), "BannerKind.AutoDismiss"));
        Assert.Contains("shown ? BannerKind.AutoDismiss : BannerKind.Persistent", drop, StringComparison.Ordinal);

        // Content: only "Restored to …" after a typed Restored outcome.
        Assert.Equal(1, CountOf(Code(content), "BannerKind.AutoDismiss"));
        var undo = Between(content, "private async Task UndoCoreAsync()", "private ");
        var successCheck = undo.IndexOf("if (result.Outcome != RestoreOutcome.Restored)", StringComparison.Ordinal);
        var autoDismiss = undo.IndexOf("ShowBanner($\"Restored to {restored.ContainerPath}.\", BannerKind.AutoDismiss);", StringComparison.Ordinal);
        Assert.True(successCheck >= 0, "the content Undo's typed success check is missing");
        Assert.True(autoDismiss > successCheck, "the content AutoDismiss call is missing or comes before the success check");

        // Owner and Repository banners are unchanged (D15(a)).
        foreach (var file in new[] { "OwnerAreaViewModel.cs", "RepositoryAreaViewModel.cs" })
            Assert.DoesNotContain("BannerKind", ViewModel(file), StringComparison.Ordinal);
    }

    [Fact]
    public void TG26_C3_ExpiryChecksTurnArmVisibilityPointerAndFocus_CancelRetiresTheArm_UndoAndDetailsForcePersistent()
    {
        var timer = ViewModel("BannerAutoDismiss.cs");
        var expired = Between(timer, "private void OnExpired(int turn, int arm)", "\n    }");
        Assert.Contains("turn != Turn || arm != Arm", expired, StringComparison.Ordinal);
        Assert.Contains("!CanCount", expired, StringComparison.Ordinal);
        Assert.Contains("private bool CanCount => !_shutDown && _ownerActive && _visible && !_pointerOver && !_focusWithin;", timer, StringComparison.Ordinal);
        var cancel = Between(timer, "private void Cancel()", "\n    }");
        Assert.Contains("Arm++;", cancel, StringComparison.Ordinal);
        Assert.Contains("_pending?.Dispose();", cancel, StringComparison.Ordinal);
        Assert.Contains("var (turn, arm) = (Turn, ++Arm);", timer, StringComparison.Ordinal);                // a new arm per scheduling
        Assert.Equal(TimeSpan.FromSeconds(5), BannerAutoDismiss.Delay);

        var workspace = ViewModel("WorkspaceAreaViewModel.cs");
        Assert.Contains("_bannerAutoDismiss.MakePersistent();", Between(workspace, "private void OfferUndo(UndoOffer offer)", "\n    }"), StringComparison.Ordinal);
        Assert.Contains("_bannerAutoDismiss.SetOwnerActive(value);", Between(workspace, "public bool IsTabSelected", "public bool IsActivated"), StringComparison.Ordinal);
        Assert.Contains("_bannerAutoDismiss.Shutdown();", ViewModel("ProjectContentViewModel.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void TG26_C4_NoMigration_PackagesUnchanged_NoticeStripsUnchanged()
    {
        Assert.Equal(10, SchemaMigrations.LatestVersion);
        Assert.Equal(["Microsoft.Data.Sqlite@10.0.13"],
            XDocument.Load(RepoFile("src", "WorkHub.Infrastructure", "WorkHub.Infrastructure.csproj")).Descendants("PackageReference")
                .Select(e => $"{(string?)e.Attribute("Include")}@{(string?)e.Attribute("Version")}"));
        var xaml = File.ReadAllText(RepoFile("src", "WorkHub.App", "Views", "MainWindow.xaml"));
        foreach (var strip in new[] { "AutomationProperties.Name=\"Trash notice\"", "AutomationProperties.Name=\"Destination notice\"" })
        {
            var start = xaml.LastIndexOf("<Border", xaml.IndexOf(strip, StringComparison.Ordinal), StringComparison.Ordinal);
            var tag = xaml[start..xaml.IndexOf('>', xaml.IndexOf(strip, StringComparison.Ordinal))];
            Assert.DoesNotContain("Banner_", tag, StringComparison.Ordinal);                         // no Phase 26 hooks on the strips
        }
    }

    [Fact]
    public void TG26_C5_UndoCallersChooseTheKindFromATypedResult_NeverFromTheNoticeOrPlaced()
    {
        var workspace = ViewModel("WorkspaceAreaViewModel.cs");
        var restore = Between(workspace, "RestoreResultKind Kind)> RestoreCoreAsync(long nodeId, string name)", "\n    }\n");
        var clear = Between(workspace, "RestoreResultKind Kind)> ClearFolderCoreAsync(", "\n    }\n");
        Assert.Equal(CountOf(restore, "notice = "), CountOf(restore, "kind = RestoreResultKind."));          // one kind per notice arm
        Assert.Equal(CountOf(clear, "notice = "), CountOf(clear, "resultKind = RestoreResultKind."));
        Assert.Contains("kind = RestoreResultKind.Restored;", Between(restore, "case ProjectRestoreOutcome.Restored:", "break;"), StringComparison.Ordinal);
        Assert.Contains("kind = RestoreResultKind.RestoredArchived;", Between(restore, "case ProjectRestoreOutcome.Restored when", "break;"), StringComparison.Ordinal);
        Assert.Contains("resultKind = RestoreResultKind.Restored;", Between(clear, "case FolderClearOutcome.Cleared:", "break;"), StringComparison.Ordinal);
        Assert.Contains("RestoreResultKind.Failed", restore, StringComparison.Ordinal);
        Assert.Contains("RestoreResultKind.Failed", clear, StringComparison.Ordinal);

        foreach (var caller in new[] { Between(workspace, "private async Task UndoCoreAsync()", "\n    }\n"), Between(workspace, "private async Task UndoFolderCoreAsync()", "\n    }\n") })
        {
            Assert.Contains("ShowBanner(notice, kind == RestoreResultKind.Restored ? BannerKind.AutoDismiss : BannerKind.Persistent);", caller, StringComparison.Ordinal);
            Assert.DoesNotContain("notice ==", caller, StringComparison.Ordinal);
            Assert.DoesNotContain("notice.StartsWith", caller, StringComparison.Ordinal);
            Assert.DoesNotContain("placed is not null ? BannerKind", caller, StringComparison.Ordinal);
        }

        var panel = Between(workspace, "internal async Task<string> RestoreFromTrashAsync(", "\n    }\n");
        Assert.Contains("(await folder).Notice", panel, StringComparison.Ordinal);                     // panels read the notice only
        Assert.Contains("(await operation).Notice", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowBanner", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void TG26_C6_TheWindowReportsBannerStateOnly_AndShutsTheTimersDownOnClose()
    {
        var xaml = XDocument.Load(RepoFile("src", "WorkHub.App", "Views", "MainWindow.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var (name, prefix) in new[] { ("WorkspaceBannerBorder", "WorkspaceBanner_"), ("ContentBannerBorder", "ContentBanner_") })
        {
            var border = xaml.Descendants(p + "Border").Single(e => (string?)e.Attribute(x + "Name") == name);
            Assert.Equal(prefix + "StateChanged", (string?)border.Attribute("IsVisibleChanged"));
            Assert.Equal(prefix + "StateChanged", (string?)border.Attribute("IsKeyboardFocusWithinChanged"));
            Assert.Equal(prefix + "MouseChanged", (string?)border.Attribute("MouseEnter"));
            Assert.Equal(prefix + "MouseChanged", (string?)border.Attribute("MouseLeave"));
        }

        var code = File.ReadAllText(RepoFile("src", "WorkHub.App", "Views", "MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        Assert.Contains("Main?.Workspace.ReportBannerView(WorkspaceBannerBorder.IsVisible, pointerOver, WorkspaceBannerBorder.IsKeyboardFocusWithin);", code, StringComparison.Ordinal);
        Assert.Contains("?.ReportBannerView(ContentBannerBorder.IsVisible, pointerOver, ContentBannerBorder.IsKeyboardFocusWithin);", code, StringComparison.Ordinal);
        Assert.Contains("Main?.Workspace.ShutdownBanners();", Between(code, "protected override void OnClosed(EventArgs e)", "\n    }"), StringComparison.Ordinal);
        Assert.DoesNotContain("BannerMessage =", code, StringComparison.Ordinal);                     // the view never clears a banner
        Assert.DoesNotContain("DismissBannerCommand", code, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
