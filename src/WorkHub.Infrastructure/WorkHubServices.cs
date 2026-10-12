using WorkHub.Core.Diagnostics;
using WorkHub.Core.LegacyImport;
using WorkHub.Core.Owner;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Core.Startup;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.FileSystem;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.LegacyImport;
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
        Initializer = new AppInitializer(paths, new LocalStorage(paths), settingsStore, new DatabaseInitializer(paths, Log, InitialSystemsSeed.Apply), Log);
        Settings = new SettingsService(settingsStore);
        Systems = new SystemService(new SqliteSystemStore(Connections));
        RepositoryOpener = new RepositoryOpenService(Settings, new ProcessRepositoryLauncher());
        Owners = new OwnerService(new SqliteOwnerStore(Connections), new ProcessOwnerLauncher());
        LegacyImport = new LegacyImportService(new LegacyXmlParser(), new SqliteLegacyImportStore(Connections), Log);
        Workspace = new WorkspaceService(new SqliteWorkspaceStore(Connections));
        ProjectContent = new ProjectContentService(new SqliteProjectContentStore(Connections), new ProcessProjectLinkLauncher());
        WorkspaceSearch = new WorkspaceSearchService(new SqliteWorkspaceSearchStore(Connections));
        LinkChecker = new LinkHealthChecker(Log);
    }

    public static WorkHubServices Create(WorkHubPaths paths) => new(paths);

    public WorkHubPaths Paths { get; }

    public IAppLog Log { get; }

    public SqliteConnectionFactory Connections { get; }

    public AppInitializer Initializer { get; }

    public SettingsService Settings { get; }

    public SystemService Systems { get; }

    public RepositoryOpenService RepositoryOpener { get; }

    public OwnerService Owners { get; }

    public LegacyImportService LegacyImport { get; }

    public WorkspaceService Workspace { get; }

    public ProjectContentService ProjectContent { get; }

    /// <summary>Read-only Workspace search; its own connections use a short busy timeout with bounded retries.</summary>
    public WorkspaceSearchService WorkspaceSearch { get; }

    /// <summary>The one application-wide manual link checker (bounded; never polls).</summary>
    public ILinkHealthChecker LinkChecker { get; }
}
