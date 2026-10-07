using WorkHub.Core.Diagnostics;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.FileSystem;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Logging;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;

namespace WorkHub.Infrastructure;

/// <summary>
/// Hand-written composition of services (no DI container dependency). Constructing services does no I/O;
/// <see cref="Initializer"/> must complete before the database-backed services are used.
/// </summary>
public sealed class WorkHubServices
{
    private WorkHubServices(WorkHubPaths paths)
    {
        Paths = paths;
        Log = new FileAppLog(paths.LogsDirectory);
        Connections = new SqliteConnectionFactory(paths.DatabaseFile);

        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        Initializer = new AppInitializer(paths, new LocalStorage(paths), settingsStore, new DatabaseInitializer(paths, Log), Log);
        Settings = new SettingsService(settingsStore);
        Systems = new SystemService(new SqliteSystemStore(Connections));
        RepositoryOpener = new RepositoryOpenService(Settings, new ProcessRepositoryLauncher());
    }

    public static WorkHubServices Create(WorkHubPaths paths) => new(paths);

    public WorkHubPaths Paths { get; }

    public IAppLog Log { get; }

    public SqliteConnectionFactory Connections { get; }

    public AppInitializer Initializer { get; }

    public SettingsService Settings { get; }

    public SystemService Systems { get; }

    public RepositoryOpenService RepositoryOpener { get; }
}
