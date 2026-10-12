using Microsoft.Data.Sqlite;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.LegacyImport;
using WorkHub.Infrastructure.LegacyImport;
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

    public LegacyImportService CreateLegacyImportService(IAppLog? log = null) =>
        new(new LegacyXmlParser(), new SqliteLegacyImportStore(new SqliteConnectionFactory(Paths.DatabaseFile)), log ?? NullAppLog.Instance);

    public void Dispose()
    {
        new SqliteConnectionFactory(Paths.DatabaseFile).ClearPools();
        new SqliteConnectionFactory(Paths.DatabaseFile, SqliteWorkspaceSearchStore.BusyTimeoutSeconds).ClearPools(); // search/resolve pool
        var testDirectory = Directory.GetParent(Paths.Root)!.FullName;
        for (var attempt = 1; Directory.Exists(testDirectory); attempt++)
        {
            try
            {
                Directory.Delete(testDirectory, recursive: true);
            }
            catch (IOException) when (attempt < 50)
            {
                // A read a view model superseded (cancelled, never awaited) may still hold its pooled connection briefly.
                Thread.Sleep(20);
                new SqliteConnectionFactory(Paths.DatabaseFile).ClearPools();
            }
        }
    }
}
