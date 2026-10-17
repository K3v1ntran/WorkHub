using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.LegacyImport;
using WorkHub.Core.Owner;
using WorkHub.Core.Repositories;
using WorkHub.Core.Restore;
using WorkHub.Core.Settings;
using WorkHub.Core.Startup;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.FileSystem;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.LegacyImport;
using WorkHub.Infrastructure.Logging;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Restore;
using WorkHub.Infrastructure.Settings;

namespace WorkHub.Infrastructure;

/// <summary>
/// Hand-written composition of services (no DI container dependency). Constructing services does no I/O;
/// <see cref="Initializer"/> must complete before the database-backed services are used.
/// </summary>
public sealed class WorkHubServices : IDisposable
{
    private WorkHubServices(WorkHubPaths paths, string? restoreContinueOp)
    {
        Paths = paths;
        Log = new FileAppLog(paths.LogsDirectory);
        var restore = _restore = new RestoreCoordinator(paths, Log, restoreContinueOp);
        RestoreOwner = restore;
        RestoreRequester = new RestoreRequester(paths, Log);
        Connections = new SqliteConnectionFactory(paths.DatabaseFile);

        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        Initializer = new AppInitializer(paths, new LocalStorage(paths), settingsStore, new DatabaseInitializer(paths, Log, InitialSystemsSeed.Apply), Log, new DailyBackupService(paths, Log), restore);
        Backups = new BackupCatalog(paths);
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

    /// <param name="restoreContinueOp">Phase 10: the validated <c>--restore-continue</c> operation, if any.</param>
    public static WorkHubServices Create(WorkHubPaths paths, string? restoreContinueOp = null) => new(paths, restoreContinueOp);

    private readonly RestoreCoordinator _restore;

    /// <summary>Releases this process's startup locks (Presence). The OS does the same when the process exits.</summary>
    public void Dispose() => _restore.Dispose();

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

    /// <summary>Read-only discovery and checking of database backups (Settings → Backups). It cannot change a database.</summary>
    public IBackupCatalog Backups { get; }

    /// <summary>Phase 10: requests a restore (reservation, staging, confirmation) from a running window.</summary>
    public IRestoreRequester RestoreRequester { get; }

    /// <summary>Phase 10: this process's owner-window actions for an unfinished restore (and the startup protocol).</summary>
    public IRestoreOwner RestoreOwner { get; }

    /// <summary>The one application-wide manual link checker (bounded; never polls).</summary>
    public ILinkHealthChecker LinkChecker { get; }
}
