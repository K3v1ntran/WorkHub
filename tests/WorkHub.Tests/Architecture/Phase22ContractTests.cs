using System.Text.Json.Nodes;
using System.Xml.Linq;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 22 structural contracts (proposal Revision 2 + R2a §3.3, §3.5, §3.8, §4.4), read from the shipped files and types as DATA
/// (nothing is launched): no migration, schema version or package; the store change is additive (exactly the Phase 21 tag members
/// plus the two delete members, own types and token format); tag rows are never updated, replaced or upserted; both applies use
/// BEGIN IMMEDIATE; the Phase 21 editor markers are intact; the dialog's delete entry and Confirm wiring are bound and named. The
/// keyboard behaviour itself (Enter / Esc / Space with real focus) is GUI GD5, not provable here.
/// </summary>
public sealed class Phase22ContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string RepoFile(params string[] parts) => Path.Combine([FindRepoRoot(), .. parts]);

    private static XDocument Xaml(string relative) => XDocument.Load(RepoFile("src", "WorkHub.App", relative));

    private static string[] Packages(params string[] project) =>
        XDocument.Load(RepoFile(project)).Descendants("PackageReference")
            .Select(e => $"{(string?)e.Attribute("Include")}@{(string?)e.Attribute("Version")}").Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void TD_C1_Phase22AddsNoMigration_NoSchemaVersion_AndNoPackage()
    {
        Assert.Equal(10, SchemaMigrations.LatestVersion);
        Assert.Equal(Enumerable.Range(1, 10), SchemaMigrations.All.Select(m => m.Version));
        var fingerprints = JsonNode.Parse(File.ReadAllText(RepoFile("tests", "WorkHub.Tests", "Fixtures", "SchemaFingerprints.json")))!.AsObject();
        Assert.DoesNotContain(fingerprints, p => p.Key == "11");
        Assert.Empty(Packages("src", "WorkHub.App", "WorkHub.App.csproj"));
        Assert.Empty(Packages("src", "WorkHub.Core", "WorkHub.Core.csproj"));
        Assert.Empty(Packages("src", "WorkHub.Presentation", "WorkHub.Presentation.csproj"));
        Assert.Equal(["Microsoft.Data.Sqlite@10.0.13"], Packages("src", "WorkHub.Infrastructure", "WorkHub.Infrastructure.csproj"));
    }

    [Fact]
    public void TD_C2_TheStoreChangeIsAdditive_ExactlyTwoDeleteMembers_OwnTypes_Phase21TypesUnchanged()
    {
        var store = typeof(IWorkspaceStore);
        var tagMembers = store.GetMethods().Where(m => m.Name.Contains("Tag", StringComparison.Ordinal)).Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            nameof(IWorkspaceStore.ApplyTagChangeAsync), nameof(IWorkspaceStore.ApplyTagDeleteAsync), nameof(IWorkspaceStore.ListProjectsWithTagAsync), // Phase 24 snapshot (§4.6)
            nameof(IWorkspaceStore.ListTagSuggestionsAsync), nameof(IWorkspaceStore.ListTagsInUseAsync), // ordinal: 'S' (U+0053) < 's' (U+0073)
            nameof(IWorkspaceStore.PrepareTagChangeAsync), nameof(IWorkspaceStore.PrepareTagDeleteAsync),
        }, tagMembers);

        var prepare = store.GetMethod(nameof(IWorkspaceStore.PrepareTagDeleteAsync))!;
        Assert.Equal([typeof(string), typeof(CancellationToken)], prepare.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<TagDeletePreview>), prepare.ReturnType);
        var apply = store.GetMethod(nameof(IWorkspaceStore.ApplyTagDeleteAsync))!;
        Assert.Equal([typeof(string), typeof(string), typeof(CancellationToken)], apply.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<TagDeleteResult>), apply.ReturnType);
        Assert.Equal(new[] { "Deleted", "Stale", "NothingToDelete", "InvalidKey" }, Enum.GetNames<TagDeleteOutcome>());

        // Phase 21 contracts kept byte-for-byte in meaning (D5): no Delete kind or outcome was added to the rename/merge types.
        Assert.Equal(new[] { "Rename", "MergeInto" }, Enum.GetNames<TagChangeKind>());
        Assert.Equal(new[] { "SpellingChange", "Rename", "Merge" }, Enum.GetNames<TagChangeOperation>());
        Assert.Equal(new[] { "SpellingChanged", "Renamed", "Merged", "Stale", "NothingToRename", "Unchanged", "InvalidName" }, Enum.GetNames<TagChangeOutcome>());
    }

    [Fact]
    public void TD_C3_TagRowsAreNeverUpdatedReplacedOrUpserted_BothTokenFormatsPresent_BothAppliesImmediate()
    {
        var source = File.ReadAllText(RepoFile("src", "WorkHub.Infrastructure", "Persistence", "SqliteWorkspaceStore.Tags.cs"));
        Assert.DoesNotContain("OR REPLACE", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ON CONFLICT", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ProjectTags", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE Projects", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE WorkspaceNodes", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"WH-TAGPLAN/1\"", source, StringComparison.Ordinal);
        Assert.Contains("\"WH-TAGDEL/1\"", source, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(source, "BeginTransaction(deferred: false)")); // BEGIN IMMEDIATE for the rename/merge apply and the delete apply
        // Review fix 2: no seam bypasses the eligibility rule; Prepare and Apply share the one plan function and its IsTagEligible rule.
        Assert.DoesNotContain("IgnoreTagDeleteEligibility", source, StringComparison.Ordinal);
        Assert.Contains("new TagDeletePlanRow(s.Project, s.Name, s.CreatedAt, IsTagEligible(lifecycle, s.Project))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TD_C4_TheEditorIsUnchanged_Phase21MarkersStillPresent()
    {
        var source = File.ReadAllText(RepoFile("src", "WorkHub.Presentation", "ViewModels", "WorkspaceEditorViewModels.cs"));
        Assert.Contains("_existing is not null && ChipsEqualBaseline()", source, StringComparison.Ordinal);
        Assert.Contains("_existing is null ? null : _tagBaseline", source, StringComparison.Ordinal);
        Assert.Contains("failure == WorkspaceFailure.TagsChanged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TD_C5_TheDeleteEntry_AndTheConfirmWiring_AreBoundAndNamed_ConfirmNeverDefaultOrCancel()
    {
        var dialog = Xaml(@"Views\ManageTagsWindow.xaml");
        var delete = Button(dialog, "ManageTagsDelete");
        Assert.Equal("{Binding StartDeleteCommand}", (string?)delete.Attribute("Command"));
        Assert.Equal("Delete everywhere…", (string?)delete.Attribute("Content"));
        Assert.Equal("{Binding PreviewCommand}", (string?)Button(dialog, "ManageTagsPreviewDelete").Attribute("Command"));
        Assert.Single(dialog.Descendants(P + "TextBlock"), e => (string?)e.Attribute("AutomationProperties.AutomationId") == "ManageTagsDeletePreview");

        var confirm = Button(dialog, "ManageTagsConfirm");
        Assert.Null(confirm.Attribute("IsDefault"));
        Assert.Null(confirm.Attribute("IsCancel"));
        Assert.Equal("ConfirmButton_PreviewKeyDown", (string?)confirm.Attribute("PreviewKeyDown")); // Enter handler wired (behaviour: GD5)
        var close = Button(dialog, "ManageTagsClose");
        Assert.Equal("True", (string?)close.Attribute("IsCancel"));                                  // Esc = Close

        var codeBehind = File.ReadAllText(RepoFile("src", "WorkHub.App", "Views", "ManageTagsWindow.xaml.cs"));
        Assert.Contains("e.Key == Key.Enter", codeBehind, StringComparison.Ordinal);
        Assert.Contains("IsDeleteMode: true", codeBehind, StringComparison.Ordinal);                // only the delete flow; Rename/Merge unchanged
        Assert.DoesNotContain("Key.Space", codeBehind, StringComparison.Ordinal);                   // Space keeps the button's own activation
    }

    /// <summary>
    /// Review fix 1: the Manage tags window's Closing handler (title-bar X, Alt+F4, owner close) is wired to the view model's
    /// <c>CanClose</c>, which is false only while a delete Apply writes (behaviour: TD-VM13 - TD-VM15). Proves the WIRING only; that a
    /// real X / Alt+F4 is refused is GUI case GD8:
    /// <list type="number">
    /// <item>Isolated profile; seed a tag carried by a live Project; open Manage tags…, select it, Delete everywhere…, Preview.</item>
    /// <item>Hold the delete write: take <c>BEGIN EXCLUSIVE</c> on the isolated DB from a second connection (shorter than the busy
    /// timeout), then click Confirm.</item>
    /// <item>While held: click the title-bar X, then press Alt+F4 (WM_SYSKEYDOWN to the dialog), then Esc: the dialog stays open (UIA: window
    /// still present, ManageTagsClose disabled), the tag is still stored.</item>
    /// <item>Release the lock: the success text appears; press Alt+F4 (or X): the dialog closes and the Workspace details of the selected
    /// Project no longer show the tag. Control: during a held Rename Apply, X closes the dialog as in Phase 21 (Phase 22 snapshot
    /// e4d86a7 only; on Phase 23 this control is GP3-R: the dialog stays open while the Rename writes).</item>
    /// </list>
    /// </summary>
    [Fact]
    public void TD_C6_TheManageTagsWindowRefusesCloseWhileTheDeleteApplyWrites_ClosingWiredToCanClose()
    {
        var source = File.ReadAllText(RepoFile("src", "WorkHub.App", "Services", "DialogService.cs"));
        var start = source.IndexOf("public bool ShowManageTags(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("return viewModel.ChangedAny;", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var body = source[start..end];
        Assert.Contains("window.Closing += (_, e) => e.Cancel |= !viewModel.CanClose;", body, StringComparison.Ordinal);

        // Phase 23 (D2): the flag assertions follow the shared IsWriting flag; the Closing wiring assertion above is unchanged. On the
        // Phase 22 snapshot e4d86a7 this test asserted IsDeleteWriting (Phase 22 evidence only).
        var vm = File.ReadAllText(RepoFile("src", "WorkHub.Presentation", "ViewModels", "ManageTagsViewModel.cs"));
        Assert.Contains("public bool CanClose => !IsWriting;", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDeleteWriting", vm, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(vm, "IsWriting = true;"));                                             // the Rename/Merge and the Delete Apply
    }

    private static XElement Button(XDocument doc, string automationId) =>
        doc.Descendants(P + "Button").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == automationId);

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
