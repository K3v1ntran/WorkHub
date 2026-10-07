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

    public string DatabaseFile => Path.Combine(DataDirectory, "workhub.db");

    public string SettingsFile => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>Not created during first run. Created only when Password Manager setup completes (later phase).</summary>
    public string SecurityDirectory => Path.Combine(Root, "Security");

    /// <summary>Not created during first run. See <see cref="SecurityDirectory"/>.</summary>
    public string VaultFile => Path.Combine(SecurityDirectory, "credentials.whvault");

    /// <summary>Folders that first-run initialization creates.</summary>
    public IReadOnlyList<string> RequiredDirectories => [DataDirectory, ConfigDirectory, LogsDirectory, BackupsDirectory];
}
