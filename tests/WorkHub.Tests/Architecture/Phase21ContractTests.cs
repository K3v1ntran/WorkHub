using System.Text.Json.Nodes;
using System.Xml.Linq;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 21 structural contracts (proposal Revision 2 + R2a, R3), read from the shipped files and types as DATA (nothing is
/// launched): no migration, schema version or package; the store change is additive (exactly three tag-change members, the editor
/// members keep their signatures, <see cref="ProjectDraft.TagsBaseline"/> is optional and last); tag rows are never updated,
/// replaced or upserted; the .NET 8 deliverables are untouched; the dialog and its entry point are bound and named, with Close as
/// the default and Confirm never the default. Behaviour in a running window is left to GT1-GT6.
/// </summary>
public sealed class Phase21ContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string RepoFile(params string[] parts) => Path.Combine([FindRepoRoot(), .. parts]);

    private static XDocument Xaml(string relative) => XDocument.Load(RepoFile("src", "WorkHub.App", relative));

    [Fact]
    public void R3_Phase21AddsNoMigration_NoSchemaVersion_AndNoPackage()
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
    public void R3_TheStoreChangeIsAdditive_ExactlyThreeTagChangeMembers_EditorSignaturesKept()
    {
        var store = typeof(IWorkspaceStore);
        var tagMembers = store.GetMethods().Where(m => m.Name.Contains("Tag", StringComparison.Ordinal)).Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            nameof(IWorkspaceStore.ApplyTagChangeAsync), nameof(IWorkspaceStore.ListTagsInUseAsync),
            nameof(IWorkspaceStore.ListTagSuggestionsAsync), nameof(IWorkspaceStore.PrepareTagChangeAsync),
        }, tagMembers);
        Assert.Equal([typeof(CancellationToken)], store.GetMethod(nameof(IWorkspaceStore.ListTagsInUseAsync))!.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<IReadOnlyList<TagInUse>>), store.GetMethod(nameof(IWorkspaceStore.ListTagsInUseAsync))!.ReturnType);
        Assert.Equal([typeof(TagChangeRequest), typeof(CancellationToken)], store.GetMethod(nameof(IWorkspaceStore.PrepareTagChangeAsync))!.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<TagChangePreview>), store.GetMethod(nameof(IWorkspaceStore.PrepareTagChangeAsync))!.ReturnType);
        Assert.Equal([typeof(TagChangeRequest), typeof(string), typeof(CancellationToken)], store.GetMethod(nameof(IWorkspaceStore.ApplyTagChangeAsync))!.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<TagChangeResult>), store.GetMethod(nameof(IWorkspaceStore.ApplyTagChangeAsync))!.ReturnType);

        var update = store.GetMethod(nameof(IWorkspaceStore.UpdateProjectAsync))!;
        Assert.Equal([typeof(long), typeof(ProjectDraft), typeof(CancellationToken)], update.GetParameters().Select(p => p.ParameterType));
        var draft = typeof(ProjectDraft).GetConstructors().Single().GetParameters();
        Assert.Equal(nameof(ProjectDraft.TagsBaseline), draft[^1].Name, ignoreCase: true);
        Assert.Equal(typeof(IReadOnlyList<ProjectTag>), draft[^1].ParameterType);
        Assert.True(draft[^1].HasDefaultValue);
        Assert.Equal(nameof(ProjectDraft.Tags), draft[^2].Name, ignoreCase: true);

        Assert.Equal(new[] { "Rename", "MergeInto" }, Enum.GetNames<TagChangeKind>());
        Assert.Equal(new[] { "SpellingChange", "Rename", "Merge" }, Enum.GetNames<TagChangeOperation>());
        Assert.Equal(new[] { "SpellingChanged", "Renamed", "Merged", "Stale", "NothingToRename", "Unchanged", "InvalidName" }, Enum.GetNames<TagChangeOutcome>());
        Assert.Equal("TagsChanged", Enum.GetNames<WorkspaceFailure>()[^1]);
    }

    [Fact]
    public void R3_TagRowsAreNeverUpdatedReplacedOrUpserted_AndTheTokenFormatIsFixed()
    {
        var source = File.ReadAllText(RepoFile("src", "WorkHub.Infrastructure", "Persistence", "SqliteWorkspaceStore.Tags.cs"));
        Assert.DoesNotContain("OR REPLACE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ON CONFLICT", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ProjectTags", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE Projects", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE WorkspaceNodes", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"WH-TAGPLAN/1\"", source, StringComparison.Ordinal);
        Assert.Contains("BeginTransaction(deferred: false)", source, StringComparison.Ordinal); // BEGIN IMMEDIATE for the apply
    }

    [Fact]
    public void R3_TheEditorSendsTheBaselineWheneverItSendsTagsOfAnExistingProject()
    {
        var source = File.ReadAllText(RepoFile("src", "WorkHub.Presentation", "ViewModels", "WorkspaceEditorViewModels.cs"));
        Assert.Contains("_existing is not null && ChipsEqualBaseline()", source, StringComparison.Ordinal);
        Assert.Contains("_existing is null ? null : _tagBaseline", source, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_TheNet8DeliverablesAreUntouched()
    {
        var legacy = Directory.GetFiles(RepoFile("src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("net8.0", StringComparison.Ordinal)).ToList();
        Assert.All(legacy, p => Assert.DoesNotContain("ManageTags", File.ReadAllText(p), StringComparison.Ordinal));
    }

    [Fact]
    public void GT1_GT2_TheEntryPointAndTheDialog_AreBoundAndNamed_CloseIsTheDefault_ConfirmNever()
    {
        var main = Xaml(@"Views\MainWindow.xaml");
        var menu = main.Descendants(P + "ContextMenu").Single(e => (string?)e.Attribute(X + "Key") == "WorkspaceSelectionMenu"); // tree right-click and More
        Assert.Single(menu.Descendants(P + "MenuItem"), e => (string?)e.Attribute("Command") == "{Binding ManageTagsCommand}"
            && (string?)e.Attribute("Header") == "Manage tags…" && (string?)e.Attribute("AutomationProperties.AutomationId") == "ManageTags");

        var dialog = Xaml(@"Views\ManageTagsWindow.xaml");
        Button(dialog, "ManageTagsClose", out var close);
        Assert.Equal("True", (string?)close.Attribute("IsDefault"));
        Assert.Equal("True", (string?)close.Attribute("IsCancel"));
        Button(dialog, "ManageTagsConfirm", out var confirm);
        Assert.Null(confirm.Attribute("IsDefault"));
        Assert.Equal("{Binding ConfirmCommand}", (string?)confirm.Attribute("Command"));
        Assert.Single(dialog.Descendants(P + "ListBox"), e => (string?)e.Attribute("AutomationProperties.AutomationId") == "ManageTagsList");
        Assert.Single(dialog.Descendants(P + "TextBox"), e => (string?)e.Attribute("AutomationProperties.AutomationId") == "ManageTagsFilter");
        var nameBox = dialog.Descendants(P + "TextBox").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "ManageTagsNewName");
        Assert.Single(nameBox.Descendants(P + "KeyBinding"), e => (string?)e.Attribute("Key") == "Enter" && (string?)e.Attribute("Command") == "{Binding PreviewCommand}"); // Enter previews, never confirms

        var editor = Xaml(@"Views\ProjectEditorWindow.xaml");
        Button(editor, "UseCurrentTags", out var use);
        Assert.Equal("{Binding UseCurrentTagsCommand}", (string?)use.Attribute("Command"));
        Button(editor, "KeepMyTags", out var keep);
        Assert.Equal("{Binding KeepMyTagsCommand}", (string?)keep.Attribute("Command"));
    }

    private static void Button(XDocument doc, string automationId, out XElement button) =>
        button = doc.Descendants(P + "Button").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == automationId);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
