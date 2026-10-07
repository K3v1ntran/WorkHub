using Microsoft.Data.Sqlite;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.FileSystem;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Infrastructure.Settings;

namespace WorkHub.Tests.TestSupport;

/// <summary>Isolated stand-in for %LOCALAPPDATA%\WorkHub. Never touches the real user profile.</summary>
public sealed class TempWorkHubRoot : IDisposable
{
    public TempWorkHubRoot()
    {
        Paths = new WorkHubPaths(Path.Combine(Path.GetTempPath(), "WorkHubTests", Guid.NewGuid().ToString("N"), "WorkHub"));
    }

    public WorkHubPaths Paths { get; }

    public AppInitializer CreateInitializer() =>
        new(Paths, new LocalStorage(Paths), new JsonSettingsStore(Paths.SettingsFile), new DatabaseInitializer(Paths, NullAppLog.Instance), NullAppLog.Instance);

    public DatabaseInitializer CreateDatabaseInitializer(params ISchemaMigration[] migrations) =>
        new(Paths, migrations, TimeProvider.System, NullAppLog.Instance);

    public SqliteConnection Open() => new SqliteConnectionFactory(Paths.DatabaseFile).OpenExisting();

    public void Dispose()
    {
        new SqliteConnectionFactory(Paths.DatabaseFile).ClearPools();
        var testDirectory = Directory.GetParent(Paths.Root)!.FullName;
        if (Directory.Exists(testDirectory))
            Directory.Delete(testDirectory, recursive: true);
    }
}
