namespace WorkHub.Core.Startup;

/// <summary>
/// Local data layout. Pure path computation; creating folders is the job of <see cref="ILocalStorage"/>.
/// </summary>
public sealed class WorkHubPaths
{
    public const string AppFolderName = "WorkHub";

    public WorkHubPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root path is required.", nameof(root));

        Root = Path.GetFullPath(root);
    }

    /// <summary>%LOCALAPPDATA%\WorkHub</summary>
    public static WorkHubPaths ForCurrentUser() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName));

    public string Root { get; }

    public string DataDirectory => Path.Combine(Root, "Data");

    public string ConfigDirectory => Path.Combine(Root, "Config");

    public string LogsDirectory => Path.Combine(Root, "Logs");

    public string BackupsDirectory => Path.Combine(Root, "Backups");

    /// <summary>Safety copies taken before schema migrations. Kept apart from daily backups.</summary>
    public string MigrationBackupsDirectory => Path.Combine(BackupsDirectory, "Migrations");

    /// <summary>Daily database backups (Phase 9). Owned by the daily backup; created when the first one is taken.</summary>
    public string DailyBackupsDirectory => Path.Combine(BackupsDirectory, "Daily");

    /// <summary>Held exclusively while a daily backup runs, so instances on the same data root never overlap.</summary>
    public string DailyBackupLockFile => Path.Combine(DailyBackupsDirectory, "daily-backup.lock");

    /// <summary>Phase 10: safety backups and preserved copies made before a restore. Outside daily retention; kept forever.</summary>
    public string PreRestoreBackupsDirectory => Path.Combine(BackupsDirectory, "PreRestore");

    public string DatabaseFile => Path.Combine(DataDirectory, "workhub.db");

    /// <summary>Phase 10 startup Gate (G). Held by every start through reconciliation and database initialization.</summary>
    public string StartupGateFile => Path.Combine(DataDirectory, "workhub-startup.lock");

    /// <summary>Phase 10 Presence (P): shared by every running instance; exclusive while a restore or Put back executes.</summary>
    public string PresenceFile => Path.Combine(DataDirectory, "workhub-instance.lock");

    /// <summary>Phase 10 restore metadata: one folder per operation, plus the Restore lock.</summary>
    public string RestoreDirectory => Path.Combine(DataDirectory, "Restore");

    /// <summary>Phase 10 Restore lock (R). Required for every create, write, rename or delete under <see cref="RestoreDirectory"/>.</summary>
    public string RestoreLockFile => Path.Combine(RestoreDirectory, "restore.lock");

    /// <summary>The folder of one restore operation. Only validated operation identifiers become paths.</summary>
    public string RestoreOperationDirectory(string op) =>
        Restore.RestoreOpId.IsValid(op)
            ? Path.Combine(RestoreDirectory, op)
            : throw new ArgumentException("Not a restore operation identifier.", nameof(op));

    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>Not created during first run. Created only when Password Manager setup completes (later phase).</summary>
    public string SecurityDirectory => Path.Combine(Root, "Security");

    /// <summary>Not created during first run. See <see cref="SecurityDirectory"/>.</summary>
    public string VaultFile => Path.Combine(SecurityDirectory, "credentials.whvault");

    /// <summary>Folders that first-run initialization creates.</summary>
    public IReadOnlyList<string> RequiredDirectories => [DataDirectory, ConfigDirectory, LogsDirectory, BackupsDirectory];
}
