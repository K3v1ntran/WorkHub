using System.Text.Json.Nodes;
using System.Xml.Linq;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Tests.Architecture;

/// <summary>
/// Phase 18 structural contracts, read from the shipped files as DATA (nothing is launched): the Settings checkbox and the
/// note editor's status text are reachable through UI Automation (A18), and Phase 18 adds no migration, no schema version and
/// no package (A17). Behaviour in a running window is left to the GUI checks N1–N9.
/// </summary>
public sealed class Phase18UiContractTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string RepoFile(params string[] parts) => Path.Combine([FindRepoRoot(), .. parts]);

    private static XDocument Xaml(string relative) => XDocument.Load(RepoFile("src", "WorkHub.App", relative));

    [Fact]
    public void A18_TheSettingsCheckbox_IsBoundAndNamed_AndTheEditorStatusTextHasAnAutomationId()
    {
        var settings = Xaml(@"Views\SettingsWindow.xaml");
        var box = settings.Descendants(P + "CheckBox").Single(e => (string?)e.Attribute("AutomationProperties.Name") == "Save notes automatically");
        Assert.Equal("{Binding SaveNotesAutomatically}", (string?)box.Attribute("IsChecked"));
        Assert.Equal("Save notes automatically", (string?)box.Attribute("Content"));

        var editor = Xaml(@"Views\NoteEditorWindow.xaml");
        var status = editor.Descendants(P + "TextBlock").Single(e => (string?)e.Attribute("AutomationProperties.AutomationId") == "NoteStatus");
        Assert.Equal("{Binding StatusText}", (string?)status.Attribute("Text"));
    }

    [Fact]
    public void A17_Phase18AddsNoMigration_NoSchemaVersion_AndNoPackage()
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
        Assert.Equal(["Microsoft.NET.Test.Sdk@17.14.1", "coverlet.collector@6.0.4", "xunit.runner.visualstudio@3.1.4", "xunit@2.9.3"],
            Packages("tests", "WorkHub.Tests", "WorkHub.Tests.csproj"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
