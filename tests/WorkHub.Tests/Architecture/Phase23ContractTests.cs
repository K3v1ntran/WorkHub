using System.Xml.Linq;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 23 structural contracts (proposal Revision 2 §3.2–§3.4, §4.4), read from the shipped files and types as DATA (nothing is
/// launched): no migration, schema version or package; the store's tag members are unchanged; the Manage tags window still cancels
/// Closing while <c>CanClose</c> is false; both confirm paths (Rename / Merge and Delete) set the shared <c>IsWriting</c> flag and use
/// the one post-commit refresh step; the keyboard wiring of Phase 21 / 22 is unchanged. Behaviour: ManageTagsCloseRefreshTests
/// TP1–TP7 and ManageTagsDeleteViewModelTests; real X / Alt+F4: GUI GP1, GP2, GP3-R (not run here).
/// </summary>
public sealed class Phase23ContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string RepoFile(params string[] parts) => Path.Combine([FindRepoRoot(), .. parts]);

    private static string ViewModel() => File.ReadAllText(RepoFile("src", "WorkHub.Presentation", "ViewModels", "ManageTagsViewModel.cs"));

    [Fact]
    public void TP_C1_NoMigration_NoSchemaVersion_StoreTagMembersUnchanged()
    {
        Assert.Equal(10, SchemaMigrations.LatestVersion);
        Assert.Equal(Enumerable.Range(1, 10), SchemaMigrations.All.Select(m => m.Version));
        var tagMembers = typeof(IWorkspaceStore).GetMethods().Where(m => m.Name.Contains("Tag", StringComparison.Ordinal))
            .Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            nameof(IWorkspaceStore.ApplyTagChangeAsync), nameof(IWorkspaceStore.ApplyTagDeleteAsync), nameof(IWorkspaceStore.ListProjectsWithTagAsync), // Phase 24 snapshot (§4.6)
            nameof(IWorkspaceStore.ListTagSuggestionsAsync), nameof(IWorkspaceStore.ListTagsInUseAsync), // ordinal: 'S' (U+0053) < 's' (U+0073)
            nameof(IWorkspaceStore.PrepareTagChangeAsync), nameof(IWorkspaceStore.PrepareTagDeleteAsync),
        }, tagMembers);
        Assert.Equal(["Microsoft.Data.Sqlite@10.0.13"],
            XDocument.Load(RepoFile("src", "WorkHub.Infrastructure", "WorkHub.Infrastructure.csproj")).Descendants("PackageReference")
                .Select(e => $"{(string?)e.Attribute("Include")}@{(string?)e.Attribute("Version")}"));
    }

    [Fact]
    public void TP_C2_ClosingWiredToCanClose_BothConfirmPathsSetIsWriting_AndShareTheRefreshStep()
    {
        var dialogs = File.ReadAllText(RepoFile("src", "WorkHub.App", "Services", "DialogService.cs"));
        var start = dialogs.IndexOf("public bool ShowManageTags(", StringComparison.Ordinal);
        var end = dialogs.IndexOf("return viewModel.ChangedAny;", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.Contains("window.Closing += (_, e) => e.Cancel |= !viewModel.CanClose;", dialogs[start..end], StringComparison.Ordinal);

        var vm = ViewModel();
        Assert.Contains("public bool CanClose => !IsWriting;", vm, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(vm, "IsWriting = true;"));                 // ConfirmAsync (Rename / Merge) and ConfirmDeleteAsync
        Assert.Equal(2, CountOf(vm, "IsWriting = false;"));
        Assert.Equal(2, CountOf(vm, "await RefreshAfterCommitAsync(success);")); // one post-commit refresh step for every committed Apply
        Assert.Equal(1, CountOf(vm, "private async Task RefreshAfterCommitAsync(string success)"));
        Assert.Equal(2, CountOf(vm, "ChangedAny = true;"));                // set right after a committed Apply, in both paths
        Assert.DoesNotContain("IsDeleteWriting", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void TP_C3_KeyboardWiringUnchanged_EnterHandlerStillDeleteOnly_CloseStillDefaultAndCancel()
    {
        var dialog = XDocument.Load(RepoFile("src", "WorkHub.App", "Views", "ManageTagsWindow.xaml"));
        XElement Button(string id) => dialog.Descendants(P + "Button").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == id);
        var close = Button("ManageTagsClose");
        Assert.Equal("True", (string?)close.Attribute("IsCancel"));
        Assert.Equal("True", (string?)close.Attribute("IsDefault"));
        var confirm = Button("ManageTagsConfirm");
        Assert.Null(confirm.Attribute("IsDefault"));
        Assert.Null(confirm.Attribute("IsCancel"));
        Assert.Equal("ConfirmButton_PreviewKeyDown", (string?)confirm.Attribute("PreviewKeyDown"));

        var codeBehind = File.ReadAllText(RepoFile("src", "WorkHub.App", "Views", "ManageTagsWindow.xaml.cs"));
        Assert.Contains("e.Key == Key.Enter && DataContext is ManageTagsViewModel { IsDeleteMode: true }", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Key.Space", codeBehind, StringComparison.Ordinal);
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
