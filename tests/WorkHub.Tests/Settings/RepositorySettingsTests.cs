using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Settings;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Settings;

public sealed class RepositorySettingsTests : IDisposable
{
    private const string Tortoise = @"C:\Tools\TortoiseSVN\bin\TortoiseProc.exe";
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static RepositoryUsage Usage(params RepositoryType[] generated) =>
        new(new HashSet<RepositoryType>(generated), new HashSet<RepositoryType>(generated));

    private static IReadOnlyList<SettingsIssue> Validate(RepositorySettings settings, RepositoryUsage usage) =>
        RepositorySettingsValidator.Validate(settings, usage, path => path == Tortoise);

    private SettingsService CreateService()
    {
        var store = new JsonSettingsStore(_root.Paths.SettingsFile);
        var service = new SettingsService(store, path => path == Tortoise);
        service.Initialize(store.LoadOrCreate());
        return service;
    }

    [Fact]
    public void Defaults_HaveNoIssuesWhenNothingIsInUse()
    {
        Assert.Empty(Validate(new RepositorySettings(), RepositoryUsage.None));
    }

    [Fact]
    public void MissingValuesForTypesInUse_AreWarningsNotErrors()
    {
        var issues = Validate(new RepositorySettings(), Usage(RepositoryType.SvnOracle, RepositoryType.GitSystem));

        Assert.All(issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity));
        Assert.Contains(issues, i => i.Message == "SVN host is not configured.");
        Assert.Contains(issues, i => i.Message == "SVN ORACLE prefix is not configured.");
        Assert.Contains(issues, i => i.Message == "GitBucket host is not configured.");
        Assert.Contains(issues, i => i.Field == RepositorySettingsValidator.Fields.TortoiseProcPath);
        Assert.DoesNotContain(issues, i => i.Message.Contains("GIT ORACLE", StringComparison.Ordinal));
    }

    [Fact]
    public void UnusedTypes_DoNotProduceIssues()
    {
        var issues = Validate(new RepositorySettings { Git = { Host = "https://git.example.test/" } }, Usage(RepositoryType.GitSystem));

        Assert.Empty(issues);
    }

    [Fact]
    public void CustomTargetOnlyUsage_DoesNotRequireHostOrPrefix()
    {
        var usage = new RepositoryUsage(new HashSet<RepositoryType> { RepositoryType.GitOracle }, new HashSet<RepositoryType>());

        Assert.Empty(Validate(new RepositorySettings(), usage));
    }

    [Theory]
    [InlineData("Svn.Host", "svn.example.test")]
    [InlineData("Svn.Host", "https://svn.example.test/?q=1")]
    [InlineData("Git.Host", "svn://git.example.test/")]
    [InlineData("Svn.SystemPrefix", "Ge SL")]
    [InlineData("Git.OraclePrefix", "a/b")]
    [InlineData("Svn.TortoiseProcPath", "TortoiseProc.exe")]
    [InlineData("Svn.TortoiseProcPath", @"C:\missing\TortoiseProc.exe")]
    public void MalformedValues_AreErrors(string field, string value)
    {
        var settings = new RepositorySettings();
        switch (field)
        {
            case "Svn.Host": settings.Svn.Host = value; break;
            case "Git.Host": settings.Git.Host = value; break;
            case "Svn.SystemPrefix": settings.Svn.SystemPrefix = value; break;
            case "Git.OraclePrefix": settings.Git.OraclePrefix = value; break;
            case "Svn.TortoiseProcPath": settings.Svn.TortoiseProcPath = value; break;
        }

        var issue = Assert.Single(Validate(settings, RepositoryUsage.None));
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(field, issue.Field);
    }

    [Fact]
    public void MissingTortoiseExecutable_HasTheExpectedMessage()
    {
        var settings = new RepositorySettings { Svn = { TortoiseProcPath = @"C:\missing\TortoiseProc.exe" } };

        Assert.Equal("TortoiseSVN executable was not found.", Assert.Single(Validate(settings, RepositoryUsage.None)).Message);
    }

    [Fact]
    public async Task Save_PersistsAndUpdatesCurrent_AndSurvivesReload()
    {
        var service = CreateService();
        var draft = service.CreateRepositoryDraft();
        draft.Svn.Host = "  https://svn.example.test/svn/  ";
        draft.Svn.OraclePrefix = "ORA_doc_";
        draft.Svn.TortoiseProcPath = $"\"{Tortoise}\"";
        draft.Git.Host = "https://git.example.test/gitbucket/root/";
        var changed = 0;
        service.Changed += (_, _) => changed++;

        var result = await service.SaveRepositoryAsync(draft, RepositoryUsage.None);

        Assert.True(result.Saved);
        Assert.Equal(1, changed);
        Assert.Equal("https://svn.example.test/svn/", service.Current.Repository.Svn.Host);
        Assert.Equal(Tortoise, service.Current.Repository.Svn.TortoiseProcPath);

        var reloaded = CreateService().Current.Repository;
        Assert.Equal("https://svn.example.test/svn/", reloaded.Svn.Host);
        Assert.Equal("ORA_doc_", reloaded.Svn.OraclePrefix);
        Assert.Equal(Tortoise, reloaded.Svn.TortoiseProcPath);
        Assert.Equal("https://git.example.test/gitbucket/root/", reloaded.Git.Host);
    }

    [Fact]
    public async Task Save_WithErrors_PersistsNothing()
    {
        var service = CreateService();
        var before = File.ReadAllBytes(_root.Paths.SettingsFile);
        var draft = service.CreateRepositoryDraft();
        draft.Git.Host = "https://git.example.test/";    // valid
        draft.Svn.Host = "not a url";                    // invalid

        var result = await service.SaveRepositoryAsync(draft, RepositoryUsage.None);

        Assert.False(result.Saved);
        Assert.Contains(result.Issues, i => i.Severity == IssueSeverity.Error && i.Field == RepositorySettingsValidator.Fields.SvnHost);
        Assert.Equal(before, File.ReadAllBytes(_root.Paths.SettingsFile));
        Assert.Equal(string.Empty, service.Current.Repository.Git.Host);
    }

    [Fact]
    public async Task Save_WithOnlyWarnings_Succeeds()
    {
        var service = CreateService();

        var result = await service.SaveRepositoryAsync(service.CreateRepositoryDraft(), Usage(RepositoryType.SvnSystem));

        Assert.True(result.Saved);
        Assert.NotEmpty(result.Issues);
    }

    [Fact]
    public void DraftEdits_DoNotTouchCurrentOrDisk_UntilSaved()
    {
        var service = CreateService();
        var before = File.ReadAllBytes(_root.Paths.SettingsFile);

        var draft = service.CreateRepositoryDraft();
        draft.Svn.Host = "https://svn.example.test/";
        draft.Svn.SystemPrefix = "X_";

        Assert.Equal(string.Empty, service.Current.Repository.Svn.Host);
        Assert.Equal("GeSL_doc_", service.Current.Repository.Svn.SystemPrefix);
        Assert.Equal(before, File.ReadAllBytes(_root.Paths.SettingsFile));
    }
}
