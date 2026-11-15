using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 19 structural contracts (proposal R6 and §4.5–4.6), read from the shipped files and types as DATA (nothing is
/// launched): no migration, schema version or package; the store change is additive; the checklist and its entry points are
/// bound and named for UI Automation. Behaviour in a running window is left to the GUI checks G1–G9.
/// </summary>
public sealed class Phase19ContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoFile(params string[] parts) => Path.Combine([FindRepoRoot(), .. parts]);

    private static XDocument Xaml(string relative) => XDocument.Load(RepoFile("src", "WorkHub.App", relative));

    [Fact]
    public void R6_Phase19AddsNoMigration_NoSchemaVersion_AndNoPackage()
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
    public void R6_TheStoreChangeIsAdditive_TheSingleArchiveKeepsItsSignature_AndNodeStateKeepsItsConstructor()
    {
        var store = typeof(IWorkspaceStore);
        var archive = store.GetMethod(nameof(IWorkspaceStore.ArchiveProjectAsync))!;
        Assert.Equal([typeof(long), typeof(CancellationToken)], archive.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<ProjectArchiveResult>), archive.ReturnType);
        Assert.Equal(typeof(Task<BulkArchiveResult>), store.GetMethod(nameof(IWorkspaceStore.ArchiveProjectInPlaceAsync))!.ReturnType);
        Assert.Equal(typeof(Task<BulkArchiveCandidates>), store.GetMethod(nameof(IWorkspaceStore.ListBulkArchiveCandidatesAsync))!.ReturnType);
        Assert.Equal(new[] { "Archived", "NotFound", "NotAProject", "AlreadyArchived", "ProjectTrashed", "AncestorNotLive" }, Enum.GetNames<ProjectArchiveOutcome>());

        var ctor = Assert.Single(typeof(WorkspaceNodeState).GetConstructors(BindingFlags.Public | BindingFlags.Instance), c => c.GetParameters().Length == 4);
        Assert.Equal([typeof(long), typeof(WorkspaceNodeType), typeof(long?), typeof(bool)], ctor.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(WorkspaceNode), typeof(WorkspaceNodeState).GetProperty(nameof(WorkspaceNodeState.Node))!.PropertyType);
    }

    [Fact]
    public void G1_G3_EntryPointsAndTheChecklist_AreBoundAndNamed()
    {
        var main = Xaml(@"Views\MainWindow.xaml");
        var menu = main.Descendants(P + "ContextMenu").Single(e => (string?)e.Attribute(X + "Key") == "WorkspaceSelectionMenu");
        var items = menu.Descendants(P + "MenuItem").ToList();
        Assert.Contains(items, e => (string?)e.Attribute("Command") == "{Binding ArchiveProjectsInFolderCommand}" && (string?)e.Attribute("Header") == "Archive projects…");
        Assert.Contains(items, e => (string?)e.Attribute("Command") == "{Binding ArchiveTopLevelProjectsCommand}" && (string?)e.Attribute("Header") == "Archive projects at the top level…");

        var dialog = Xaml(@"Views\BulkArchiveWindow.xaml");
        var cancel = dialog.Descendants(P + "Button").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "BulkArchiveCancel");
        Assert.Equal("True", (string?)cancel.Attribute("IsDefault"));
        Assert.Equal("True", (string?)cancel.Attribute("IsCancel"));
        var confirm = dialog.Descendants(P + "Button").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "BulkArchiveConfirm");
        Assert.Null(confirm.Attribute("IsDefault"));
        Assert.Equal("{Binding ArchiveCommand}", (string?)confirm.Attribute("Command"));
        Assert.Contains(dialog.Descendants(P + "KeyBinding"), e => (string?)e.Attribute("Key") == "A" && (string?)e.Attribute("Command") == "{Binding SelectAllCommand}");
        Assert.Single(dialog.Descendants(P + "ListBox"), e => (string?)e.Attribute("AutomationProperties.Name") == "Projects to archive");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
