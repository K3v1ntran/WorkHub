using WorkHub.Core.Diagnostics;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.FileSystem;
using WorkHub.Infrastructure.Logging;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;

namespace WorkHub.Infrastructure;

/// <summary>
/// Hand-written composition of infrastructure services (no DI container dependency).
/// </summary>
public sealed class WorkHubServices
{
    private WorkHubServices(WorkHubPaths paths)
    {
        Paths = paths;
        Log = new FileAppLog(paths.LogsDirectory);
        Connections = new SqliteConnectionFactory(paths.DatabaseFile);
        Initializer = new AppInitializer(
            paths,
            new LocalStorage(paths),
            new JsonSettingsStore(paths.SettingsFile),
            new DatabaseInitializer(paths, Log),
            Log);
    }

    public static WorkHubServices Create(WorkHubPaths paths) => new(paths);

    public WorkHubPaths Paths { get; }

    public IAppLog Log { get; }

    public SqliteConnectionFactory Connections { get; }

    public AppInitializer Initializer { get; }
}
