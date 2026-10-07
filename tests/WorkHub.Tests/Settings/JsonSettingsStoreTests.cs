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

        var repository = result.Settings.Repository;
        Assert.Equal(AppSettings.CurrentSettingsVersion, result.Settings.SettingsVersion);
        Assert.Equal("GeSL_doc_", repository.Svn.SystemPrefix);
        Assert.Equal("GeSL_rel_", repository.Svn.ReleasePrefix);
        Assert.Equal("GeSL_src_", repository.Git.SystemPrefix);
        Assert.Equal(string.Empty, repository.Svn.OraclePrefix);
        Assert.Equal(string.Empty, repository.Git.OraclePrefix);
        Assert.Equal(string.Empty, repository.Svn.Host);
        Assert.Equal(string.Empty, repository.Git.Host);
        Assert.Equal(string.Empty, repository.Svn.TortoiseProcPath);
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
    public void SavedValues_SurviveReload()
    {
        _store.LoadOrCreate();
        var settings = _store.LoadOrCreate().Settings;
        settings.HealthCheck.TimeoutSeconds = 12;
        settings.Repository.Svn.Host = "https://svn.example.test/svn/";
        settings.Repository.Git.OraclePrefix = "ORA_src_";
        _store.Save(settings);

        var result = new JsonSettingsStore(_root.Paths.SettingsFile).LoadOrCreate();

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Null(result.UpgradedFromVersion);
        Assert.Equal(12, result.Settings.HealthCheck.TimeoutSeconds);
        Assert.Equal("https://svn.example.test/svn/", result.Settings.Repository.Svn.Host);
        Assert.Equal("ORA_src_", result.Settings.Repository.Git.OraclePrefix);
    }

    [Fact]
    public void InvalidJson_UsesDefaultsInMemoryAndLeavesFileUntouched()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.SettingsFile, "{ \"repository\": ");

        var result = _store.LoadOrCreate();

        Assert.Equal(SettingsLoadStatus.InvalidFileUsingDefaults, result.Status);
        Assert.NotNull(result.Problem);
        Assert.Equal("GeSL_doc_", result.Settings.Repository.Svn.SystemPrefix);
        Assert.Equal("{ \"repository\": ", File.ReadAllText(_root.Paths.SettingsFile));
    }

    [Fact]
    public void WrongValueType_IsTreatedAsInvalid()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.SettingsFile, """{ "settingsVersion": 2, "repository": { "svn": { "host": 42 } } }""");

        Assert.Equal(SettingsLoadStatus.InvalidFileUsingDefaults, _store.LoadOrCreate().Status);
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
              "settingsVersion": 2,
              "repository": { "svn": { "oraclePrefix": "ORA_" }, "git": null, },
              "ui": null,
            }
            """);

        var result = _store.LoadOrCreate();

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal("ORA_", result.Settings.Repository.Svn.OraclePrefix);
        Assert.Equal("GeSL_doc_", result.Settings.Repository.Svn.SystemPrefix);
        Assert.Equal("GeSL_src_", result.Settings.Repository.Git.SystemPrefix);
        Assert.NotNull(result.Settings.Ui.Window);
        Assert.Equal(5, result.Settings.HealthCheck.TimeoutSeconds);
    }

    [Fact]
    public void Version1File_IsUpgradedInMemoryWithoutRewritingTheFile()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        const string v1 = """
            {
              "settingsVersion": 1,
              "repositories": {
                "tortoiseProcPath": "C:\\Tools\\TortoiseProc.exe",
                "svnSystem": { "baseUrl": "", "prefix": "GeSL_doc_" },
                "svnOracle": { "baseUrl": "https://svn.example.test/svn/", "prefix": "ORA_doc_" },
                "gitSystem": { "baseUrl": "https://git.example.test/root/", "prefix": "GeSL_src_" },
                "gitOracle": { "baseUrl": "", "prefix": "" },
                "release":   { "baseUrl": "", "prefix": "GeSL_rel_" }
              },
              "healthCheck": { "timeoutSeconds": 9 }
            }
            """;
        File.WriteAllText(_root.Paths.SettingsFile, v1);

        var result = _store.LoadOrCreate();

        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(1, result.UpgradedFromVersion);
        var repository = result.Settings.Repository;
        Assert.Equal("https://svn.example.test/svn/", repository.Svn.Host);
        Assert.Equal("ORA_doc_", repository.Svn.OraclePrefix);
        Assert.Equal("GeSL_rel_", repository.Svn.ReleasePrefix);
        Assert.Equal(@"C:\Tools\TortoiseProc.exe", repository.Svn.TortoiseProcPath);
        Assert.Equal("https://git.example.test/root/", repository.Git.Host);
        Assert.Equal(string.Empty, repository.Git.OraclePrefix);
        Assert.Equal(9, result.Settings.HealthCheck.TimeoutSeconds);
        Assert.Equal(v1, File.ReadAllText(_root.Paths.SettingsFile));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFiles()
    {
        _store.LoadOrCreate();
        _store.Save(AppSettings.CreateDefault());

        Assert.Equal(["settings.json"], Directory.GetFiles(_root.Paths.ConfigDirectory).Select(f => Path.GetFileName(f)!));
    }

    [Fact]
    public void Save_OverUnreadableFile_PreservesTheOldFileFirst()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.SettingsFile, "{ broken");

        _store.Save(AppSettings.CreateDefault());

        var backup = Assert.Single(Directory.GetFiles(_root.Paths.ConfigDirectory, "settings.json.invalid-*"));
        Assert.Equal("{ broken", File.ReadAllText(backup));
        Assert.Equal(SettingsLoadStatus.Loaded, _store.LoadOrCreate().Status);
    }
}
