using WorkHub.Core.Settings;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Infrastructure.Settings;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Startup;

public sealed class FirstRunInitializationTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void FreshRun_CreatesRequiredFoldersAndFiles()
    {
        Assert.False(Directory.Exists(_root.Paths.Root));

        var result = _root.CreateInitializer().Initialize();

        foreach (var folder in new[] { "Data", "Config", "Logs", "Backups" })
            Assert.True(Directory.Exists(Path.Combine(_root.Paths.Root, folder)), $"{folder} missing");

        Assert.True(File.Exists(Path.Combine(_root.Paths.Root, "Data", "workhub.db")));
        Assert.True(File.Exists(Path.Combine(_root.Paths.Root, "Config", "settings.json")));
        Assert.Equal(DatabaseInitializationOutcome.Created, result.Database.Outcome);
        Assert.Equal(SettingsLoadStatus.Created, result.Settings.Status);
    }

    [Fact]
    public void FreshRun_DoesNotCreateVaultOrSecurityFolder()
    {
        _root.CreateInitializer().Initialize();

        Assert.False(File.Exists(_root.Paths.VaultFile));
        Assert.False(Directory.Exists(_root.Paths.SecurityDirectory));
        Assert.Empty(Directory.GetFiles(_root.Paths.Root, "*.whvault", SearchOption.AllDirectories));
    }

    [Fact]
    public void FreshRun_CreatesOnlyTheExpectedTopLevelFolders()
    {
        _root.CreateInitializer().Initialize();

        var topLevel = Directory.GetDirectories(_root.Paths.Root).Select(d => Path.GetFileName(d)!).Order().ToArray();
        Assert.Equal(["Backups", "Config", "Data", "Logs"], topLevel);
        Assert.Empty(Directory.GetFiles(_root.Paths.Root));
        Assert.Empty(Directory.GetFileSystemEntries(_root.Paths.BackupsDirectory));
    }

    [Fact]
    public void FreshRun_HasNoUserDataAndNoSampleRows()
    {
        _root.CreateInitializer().Initialize();

        using var connection = _root.Open();
        foreach (var table in Db.UserTables(connection))
            Assert.True(Db.Count(connection, table) == 0, $"{table} is not empty");
    }

    [Fact]
    public void FreshRun_HasOnlyTheWorkspaceFoundation_NoDeferredOrCredentialTablesYet()
    {
        _root.CreateInitializer().Initialize();

        using var connection = _root.Open();
        var tables = Db.UserTables(connection);
        Assert.Equal(["Projects", "WorkspaceNodes"], tables.Where(name =>
            name.StartsWith("Workspace", StringComparison.Ordinal) || name.StartsWith("Project", StringComparison.Ordinal)));
        Assert.DoesNotContain(tables,
            name => name.Contains("Tag", StringComparison.Ordinal)
                    || name.Contains("Credential", StringComparison.Ordinal)
                    || name.Contains("Trash", StringComparison.Ordinal)
                    || name.Contains("Link", StringComparison.Ordinal)
                    || name.Contains("Note", StringComparison.Ordinal)
                    || name.Contains("Alias", StringComparison.Ordinal));
    }

    [Fact]
    public void SecondRun_PreservesDatabaseContent()
    {
        _root.CreateInitializer().Initialize();
        using (var connection = _root.Open())
        {
            var id = Db.InsertSystem(connection, "ABC");
            Db.InsertSystemRepository(connection, id, "SVN_SYSTEM");
        }

        var second = _root.CreateInitializer().Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Opened, second.Database.Outcome);
        Assert.Equal(SchemaMigrations.LatestVersion, second.Database.PreviousSchemaVersion);
        using var reopened = _root.Open();
        Assert.Equal(1, Db.Count(reopened, "Systems"));
        Assert.Equal(1, Db.Count(reopened, "SystemRepositories"));
    }

    [Fact]
    public void SecondRun_PreservesSettingsFileExactly()
    {
        _root.CreateInitializer().Initialize();
        var store = new JsonSettingsStore(_root.Paths.SettingsFile);
        var settings = store.LoadOrCreate().Settings;
        settings.Repository.Svn.OraclePrefix = "Custom_";
        store.Save(settings);
        var before = File.ReadAllBytes(_root.Paths.SettingsFile);
        var writeTime = File.GetLastWriteTimeUtc(_root.Paths.SettingsFile);

        var second = _root.CreateInitializer().Initialize();

        Assert.Equal(SettingsLoadStatus.Loaded, second.Settings.Status);
        Assert.Equal("Custom_", second.Settings.Settings.Repository.Svn.OraclePrefix);
        Assert.Equal(before, File.ReadAllBytes(_root.Paths.SettingsFile));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(_root.Paths.SettingsFile));
    }

    [Fact]
    public void SecondRun_StillDoesNotCreateVault()
    {
        _root.CreateInitializer().Initialize();
        _root.CreateInitializer().Initialize();

        Assert.False(Directory.Exists(_root.Paths.SecurityDirectory));
    }

    [Fact]
    public void MissingFolderIsRecreatedWithoutTouchingData()
    {
        _root.CreateInitializer().Initialize();
        using (var connection = _root.Open())
            Db.InsertSystem(connection, "KEEP");
        Directory.Delete(_root.Paths.LogsDirectory, recursive: true);

        _root.CreateInitializer().Initialize();

        Assert.True(Directory.Exists(_root.Paths.LogsDirectory));
        using var reopened = _root.Open();
        Assert.Equal(1, Db.Count(reopened, "Systems"));
    }

    [Fact]
    public void InvalidSettingsFile_DoesNotBlockDatabaseInitialization()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.SettingsFile, "{ not json");

        var result = _root.CreateInitializer().Initialize();

        Assert.Equal(SettingsLoadStatus.InvalidFileUsingDefaults, result.Settings.Status);
        Assert.Equal(DatabaseInitializationOutcome.Created, result.Database.Outcome);
        Assert.Equal("{ not json", File.ReadAllText(_root.Paths.SettingsFile));
    }
}
