using System.Text.Json.Nodes;
using System.Xml.Linq;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 20 structural contracts (proposal R5), read from the shipped files and types as DATA (nothing is launched): no migration,
/// schema version or package; the store change is additive (exactly two Bulk Unarchive members, the single Unarchive keeps its
/// signature, <see cref="ArchivedProjectEntry"/> is unchanged); the .NET 8 deliverables are untouched; the checklist and its entry
/// points are bound and named for UI Automation, with Cancel as the default. Behaviour in a running window is left to GU1–GU10.
/// </summary>
public sealed class Phase20ContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string RepoFile(params string[] parts) => Path.Combine([FindRepoRoot(), .. parts]);

    private static XDocument Xaml(string relative) => XDocument.Load(RepoFile("src", "WorkHub.App", relative));

    [Fact]
    public void R5_Phase20AddsNoMigration_NoSchemaVersion_AndNoPackage()
    {
        Assert.Equal(10, SchemaMigrations.LatestVersion);
        Assert.Equal(Enumerable.Range(1, 10), SchemaMigrations.All.Select(m => m.Version));

        var fingerprints = JsonNode.Parse(File.ReadAllText(RepoFile("tests", "WorkHub.Tests", "Fixtures", "SchemaFingerprints.json")))!.AsObject();
        Assert.DoesNotContain(fingerprints, p => p.Key == "11");

        string[] Packages(params string[] project) =>
            XDocument.Load(RepoFile(project)).Descendants("PackageReference")
                .Select(e => $"{(string?)e.Attribute("Include")}@{(string?)e.Attribute("Version")}").Order(StringComparer.Ordinal).ToArray();
        Assert.Empty(Packages("src", "WorkHub.App", "WorkHub.App.csproj"));
        Assert.Empty(Packages("src", "WorkHub.Core", "WorkHub.Core.csproj"));
        Assert.Empty(Packages("src", "WorkHub.Presentation", "WorkHub.Presentation.csproj"));
        Assert.Equal(["Microsoft.Data.Sqlite@10.0.13"], Packages("src", "WorkHub.Infrastructure", "WorkHub.Infrastructure.csproj"));
    }

    [Fact]
    public void R5_TheStoreChangeIsAdditive_ExactlyTwoBulkUnarchiveMembers()
    {
        var store = typeof(IWorkspaceStore);
        var bulk = store.GetMethods().Where(m => m.Name.Contains("BulkUnarchive", StringComparison.Ordinal) || m.Name == nameof(IWorkspaceStore.UnarchiveProjectBoundAsync))
            .Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { nameof(IWorkspaceStore.ListBulkUnarchiveCandidatesAsync), nameof(IWorkspaceStore.UnarchiveProjectBoundAsync) }, bulk);

        var single = store.GetMethod(nameof(IWorkspaceStore.UnarchiveProjectAsync))!;
        Assert.Equal([typeof(long), typeof(ProjectStatus), typeof(CancellationToken)], single.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<ProjectUnarchiveResult>), single.ReturnType);
        var bound = store.GetMethod(nameof(IWorkspaceStore.UnarchiveProjectBoundAsync))!;
        Assert.Equal([typeof(BulkUnarchiveTarget), typeof(ProjectStatus), typeof(CancellationToken)], bound.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<BulkUnarchiveResult>), bound.ReturnType);
        Assert.Equal(typeof(Task<BulkUnarchiveCandidates>), store.GetMethod(nameof(IWorkspaceStore.ListBulkUnarchiveCandidatesAsync))!.ReturnType);
        Assert.Equal(new[] { "Unarchived", "NotFound", "NotAProject", "NotArchived", "ProjectTrashed", "ParentArchived", "ParentTrashed" }, Enum.GetNames<ProjectUnarchiveOutcome>());
        Assert.Equal(new[] { "Unarchived", "NotFound", "NotAProject", "ProjectTrashed", "NotArchived", "ArchivedAgain", "ParentArchived", "ParentTrashed" }, Enum.GetNames<BulkUnarchiveOutcome>());

        var entry = typeof(ArchivedProjectEntry).GetConstructors().Single(c => c.GetParameters().Length == 5);
        Assert.Equal([typeof(long), typeof(string), typeof(long?), typeof(string), typeof(DateTimeOffset)], entry.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void R5_TheNet8DeliverablesAreUntouched()
    {
        var legacy = Directory.GetFiles(RepoFile("src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("net8.0", StringComparison.Ordinal)).ToList();
        Assert.All(legacy, p => Assert.DoesNotContain("BulkUnarchive", File.ReadAllText(p), StringComparison.Ordinal));
    }

    [Fact]
    public void GU1_GU3_EntryPointsAndTheChecklist_AreBoundAndNamed()
    {
        var main = Xaml(@"Views\MainWindow.xaml");
        var panel = main.Descendants(P + "DockPanel").Single(e => (string?)e.Attribute("AutomationProperties.Name") == "Archived projects");
        Assert.Single(panel.Descendants(P + "Button"), e => (string?)e.Attribute("Command") == "{Binding UnarchiveProjectsCommand}"
            && (string?)e.Attribute("AutomationProperties.AutomationId") == "ArchivedUnarchiveProjects");
        Assert.Single(panel.Descendants(P + "MenuItem"), e => (string?)e.Attribute("Command") == "{Binding UnarchiveProjectsCommand}"
            && (string?)e.Attribute("Header") == "Unarchive projects…");
        Assert.Single(panel.Descendants(P + "Button"), e => (string?)e.Attribute("Command") == "{Binding ShowBulkDetailsCommand}"
            && (string?)e.Attribute("AutomationProperties.AutomationId") == "ArchivedBulkDetails");
        Assert.Single(panel.Descendants(P + "Button"), e => (string?)e.Attribute("Command") == "{Binding UnarchiveCommand}"); // single Unarchive kept

        var dialog = Xaml(@"Views\BulkUnarchiveWindow.xaml");
        var cancel = dialog.Descendants(P + "Button").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "BulkUnarchiveCancel");
        Assert.Equal("True", (string?)cancel.Attribute("IsDefault"));
        Assert.Equal("True", (string?)cancel.Attribute("IsCancel"));
        var confirm = dialog.Descendants(P + "Button").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "BulkUnarchiveConfirm");
        Assert.Null(confirm.Attribute("IsDefault"));
        Assert.Equal("{Binding UnarchiveCommand}", (string?)confirm.Attribute("Command"));
        Assert.Contains(dialog.Descendants(P + "KeyBinding"), e => (string?)e.Attribute("Key") == "A" && (string?)e.Attribute("Command") == "{Binding SelectAllCommand}");
        Assert.Single(dialog.Descendants(P + "ComboBox"), e => (string?)e.Attribute("AutomationProperties.AutomationId") == "BulkUnarchiveStatus");
        Assert.Single(dialog.Descendants(P + "ListBox"), e => (string?)e.Attribute("AutomationProperties.Name") == "Archived projects to unarchive");

        var phase19 = Xaml(@"Views\BulkArchiveWindow.xaml"); // D2: the Phase 19 dialog is not modified
        Assert.Single(phase19.Descendants(P + "ListBox"), e => (string?)e.Attribute("AutomationProperties.Name") == "Projects to archive");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
