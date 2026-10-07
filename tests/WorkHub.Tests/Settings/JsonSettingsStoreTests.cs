using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Settings;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly JsonSettingsStore _store;

    public JsonSettingsStoreTests()
    {
        _store = new JsonSettingsStore(_root.Paths.SettingsFile);
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void MissingFile_IsCreatedWithSafeDefaults()
    {
        var result = _store.LoadOrCreate();

        Assert.Equal(SettingsLoadStatus.Created, result.Status);
        Assert.True(File.Exists(_root.Paths.SettingsFile));

        var repositories = result.Settings.Repositories;
        Assert.Equal("GeSL_doc_", repositories.SvnSystem.Prefix);
        Assert.Equal("GeSL_src_", repositories.GitSystem.Prefix);
        Assert.Equal("GeSL_rel_", repositories.Release.Prefix);
        Assert.Equal(string.Empty, repositories.SvnOracle.Prefix);
        Assert.Equal(string.Empty, repositories.GitOracle.Prefix);
        Assert.Equal(string.Empty, repositories.TortoiseProcPath);
        Assert.All(RepositoryCatalog.All, info => Assert.Equal(string.Empty, repositories.For(info.Type).BaseUrl));
    }

    [Fact]
    public void DefaultFile_ContainsNoHostsOrSecrets()
    {
        _store.LoadOrCreate();
        var json = File.ReadAllText(_root.Paths.SettingsFile);

        Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}", json);
    }

    [Fact]
    public void ExistingFile_IsLoadedNotOverwritten()
    {
        _store.LoadOrCreate();
        var settings = _store.LoadOrCreate().Settings;
        settings.HealthCheck.TimeoutSeconds = 12;
        _store.Save(settings);

        var result = _store.LoadOrCreate();

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(12, result.Settings.HealthCheck.TimeoutSeconds);
    }

    [Fact]
    public void InvalidJson_UsesDefaultsInMemoryAndLeavesFileUntouched()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.SettingsFile, "{ \"repositories\": ");

        var result = _store.LoadOrCreate();

        Assert.Equal(SettingsLoadStatus.InvalidFileUsingDefaults, result.Status);
        Assert.NotNull(result.Problem);
        Assert.Equal("GeSL_doc_", result.Settings.Repositories.SvnSystem.Prefix);
        Assert.Equal("{ \"repositories\": ", File.ReadAllText(_root.Paths.SettingsFile));
    }

    [Fact]
    public void JsonNull_IsTreatedAsInvalid()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.SettingsFile, "null");

        Assert.Equal(SettingsLoadStatus.InvalidFileUsingDefaults, _store.LoadOrCreate().Status);
    }

    [Fact]
    public void PartialFile_FillsMissingAndNullSectionsWithDefaults()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.SettingsFile, """
            {
              // comments and trailing commas are tolerated for hand edits
              "repositories": { "svnOracle": { "prefix": "ORA_" }, "gitOracle": null, },
              "ui": null,
            }
            """);

        var result = _store.LoadOrCreate();

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal("ORA_", result.Settings.Repositories.SvnOracle.Prefix);
        Assert.Equal(string.Empty, result.Settings.Repositories.SvnOracle.BaseUrl);
        Assert.Equal("GeSL_doc_", result.Settings.Repositories.SvnSystem.Prefix);
        Assert.NotNull(result.Settings.Repositories.GitOracle);
        Assert.NotNull(result.Settings.Ui.Window);
        Assert.Equal(5, result.Settings.HealthCheck.TimeoutSeconds);
    }

    [Fact]
    public void Save_LeavesNoTemporaryFiles()
    {
        _store.LoadOrCreate();
        _store.Save(AppSettings.CreateDefault());

        Assert.Equal(["settings.json"], Directory.GetFiles(_root.Paths.ConfigDirectory).Select(f => Path.GetFileName(f)!));
    }
}
