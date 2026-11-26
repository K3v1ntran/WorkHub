using System.Xml.Linq;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 24 structural contracts (proposal Revision 2 + R2a §3.3, §3.6, §3.8, §4.5), read from the shipped files and types as DATA
/// (nothing is launched): no migration, schema version or package; the store's tag members are Phase 21's four, Phase 22's two and
/// the one read <c>ListProjectsWithTagAsync</c>; that read uses one deferred transaction and contains no write statement; the Manage
/// tags window still cancels Closing while <c>CanClose</c> is false and the Confirm Enter handler stays delete-only; Enter /
/// double-click Show in Workspace is wired on the Projects list only. Behaviour: TaggedProjectsStoreTests TG1–TG8,
/// ManageTagsProjectsViewModelTests TGV1–TGV7, ManageTagsRevealTests TGA1–TGA4; real window: GUI GT24-1–3 (not run here).
/// </summary>
public sealed class Phase24ContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string RepoFile(params string[] parts) => Path.Combine([FindRepoRoot(), .. parts]);

    [Fact]
    public void TG_C1_NoMigration_PackagesUnchanged_StoreTagMembersGainTheOneRead()
    {
        Assert.Equal(10, SchemaMigrations.LatestVersion);
        Assert.Equal(Enumerable.Range(1, 10), SchemaMigrations.All.Select(m => m.Version));
        Assert.Equal(["Microsoft.Data.Sqlite@10.0.13"],
            XDocument.Load(RepoFile("src", "WorkHub.Infrastructure", "WorkHub.Infrastructure.csproj")).Descendants("PackageReference")
                .Select(e => $"{(string?)e.Attribute("Include")}@{(string?)e.Attribute("Version")}"));

        var store = typeof(IWorkspaceStore);
        var tagMembers = store.GetMethods().Where(m => m.Name.Contains("Tag", StringComparison.Ordinal))
            .Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            nameof(IWorkspaceStore.ApplyTagChangeAsync), nameof(IWorkspaceStore.ApplyTagDeleteAsync), nameof(IWorkspaceStore.ListProjectsWithTagAsync),
            nameof(IWorkspaceStore.ListTagSuggestionsAsync), nameof(IWorkspaceStore.ListTagsInUseAsync), nameof(IWorkspaceStore.PrepareTagChangeAsync),
            nameof(IWorkspaceStore.PrepareTagDeleteAsync),
        }, tagMembers);

        var read = store.GetMethod(nameof(IWorkspaceStore.ListProjectsWithTagAsync))!;
        Assert.Equal([typeof(string), typeof(int), typeof(CancellationToken)], read.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<TaggedProjectsResult>), read.ReturnType);
        Assert.Equal([typeof(IReadOnlyList<TaggedProject>), typeof(int), typeof(int)],
            typeof(TaggedProjectsResult).GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
        Assert.Equal(["Live", "Archived", "Trashed"], Enum.GetNames<TaggedProjectState>());
        Assert.Equal(500, WorkspaceService.TaggedProjectsLimit);
    }

    [Fact]
    public void TG_C2_TheReadUsesOneDeferredTransaction_AndContainsNoWriteStatement()
    {
        var source = File.ReadAllText(RepoFile("src", "WorkHub.Infrastructure", "Persistence", "SqliteWorkspaceStore.Tags.cs"));
        var start = source.IndexOf("public Task<TaggedProjectsResult> ListProjectsWithTagAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("}, cancellationToken);", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var body = source[start..end];

        Assert.Equal(1, CountOf(body, "BeginTransaction(deferred: true)"));
        Assert.Equal(1, CountOf(body, "BeginTransaction("));
        Assert.DoesNotContain("deferred: false", body, StringComparison.Ordinal);
        foreach (var write in new[] { "INSERT", "UPDATE", "DELETE", "REPLACE", "ExecuteNonQuery" })
            Assert.DoesNotContain(write, body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TaggedProjectOrder.Instance", body, StringComparison.Ordinal); // the §3.3 order, applied before the cap
    }

    [Fact]
    public void TG_C3_ClosingStillWiredToCanClose_ConfirmEnterStillDeleteOnly_ListKeysOnTheProjectsListOnly()
    {
        var dialogs = File.ReadAllText(RepoFile("src", "WorkHub.App", "Services", "DialogService.cs"));
        var start = dialogs.IndexOf("public bool ShowManageTags(", StringComparison.Ordinal);
        var end = dialogs.IndexOf("return viewModel.ChangedAny;", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.Contains("window.Closing += (_, e) => e.Cancel |= !viewModel.CanClose;", dialogs[start..end], StringComparison.Ordinal);

        var dialog = XDocument.Load(RepoFile("src", "WorkHub.App", "Views", "ManageTagsWindow.xaml"));
        XElement ById(string element, string id) =>
            dialog.Descendants(P + element).Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == id);
        var confirm = ById("Button", "ManageTagsConfirm");
        Assert.Equal("ConfirmButton_PreviewKeyDown", (string?)confirm.Attribute("PreviewKeyDown"));
        Assert.Null(confirm.Attribute("IsDefault"));
        var close = ById("Button", "ManageTagsClose");
        Assert.Equal(("True", "True"), ((string?)close.Attribute("IsDefault"), (string?)close.Attribute("IsCancel")));

        var show = ById("Button", "ManageTagsShowInWorkspace");
        Assert.Equal("{Binding ShowInWorkspaceCommand}", (string?)show.Attribute("Command"));
        Assert.Null(show.Attribute("IsDefault"));
        Assert.Equal("{Binding TaggedProjectsHeader}", (string?)ById("TextBlock", "ManageTagsProjectsHeader").Attribute("Text"));
        var projects = ById("ListBox", "ManageTagsProjects");
        Assert.Equal("{Binding TaggedProjects}", (string?)projects.Attribute("ItemsSource"));
        Assert.Equal("{Binding SelectedTaggedProject}", (string?)projects.Attribute("SelectedItem"));
        Assert.Equal("ProjectsList_PreviewKeyDown", (string?)projects.Attribute("PreviewKeyDown"));
        Assert.Equal("ProjectsList_MouseDoubleClick", (string?)projects.Attribute("MouseDoubleClick"));
        // Only the Projects list carries the Show in Workspace keys; the tag list and every other element are unchanged.
        Assert.Equal([projects], dialog.Descendants().Where(e => e.Attribute("PreviewKeyDown") is not null && e != confirm));
        Assert.Equal([projects], dialog.Descendants().Where(e => e.Attribute("MouseDoubleClick") is not null));

        var codeBehind = File.ReadAllText(RepoFile("src", "WorkHub.App", "Views", "ManageTagsWindow.xaml.cs"));
        Assert.Contains("e.Key == Key.Enter && DataContext is ManageTagsViewModel { IsDeleteMode: true }", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Key.Space", codeBehind, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(codeBehind, "ShowInWorkspaceCommand.Execute(null)")); // Enter and double-click on the list, nothing else
    }

    [Fact]
    public void TG_C4_TheViewModelKeepsThePhase23CloseContract_AndVersionsEveryListRead()
    {
        var vm = File.ReadAllText(RepoFile("src", "WorkHub.Presentation", "ViewModels", "ManageTagsViewModel.cs"));
        Assert.Contains("public bool CanClose => !IsWriting;", vm, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(vm, "IsWriting = true;"));
        Assert.Equal(2, CountOf(vm, "await RefreshAfterCommitAsync(success);"));
        Assert.Equal(2, CountOf(vm, "ChangedAny = true;"));                   // Show in Workspace never sets it
        Assert.Contains("var version = ++_listVersion;", vm, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(vm, "version != _listVersion"));              // result and fault, both by version only
        Assert.Contains("SelectedTaggedProject is { CanReveal: true } && !IsWriting && !IsRefreshing", vm, StringComparison.Ordinal);

        var area = File.ReadAllText(RepoFile("src", "WorkHub.Presentation", "ViewModels", "WorkspaceAreaViewModel.cs"));
        var start = area.IndexOf("private async Task ManageTagsAsync()", StringComparison.Ordinal);
        var end = area.IndexOf("private bool TryBeginTagWrite()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var flow = area[start..end];
        var reload = flow.IndexOf("await CurrentDetailsLoad;", StringComparison.Ordinal);
        var reveal = flow.IndexOf("await RevealTaggedProjectAsync(projectNodeId);", StringComparison.Ordinal);
        Assert.True(reload >= 0 && reveal > reload);                            // D7: the details reload completes before the reveal
        Assert.Contains("RevealAsync(new SearchItemRef(SearchResultKind.Project, projectNodeId))", flow, StringComparison.Ordinal);
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
