namespace WorkHub.Core.Backup;

public enum BackupKind
{
    /// <summary>A published daily backup in <c>Backups\Daily</c>.</summary>
    Daily,

    /// <summary>A safety copy taken before a schema migration, in <c>Backups\Migrations</c>.</summary>
    PreMigration,

    /// <summary>Any other file in those folders. Listed by name and size only; never opened.</summary>
    Unrecognized,
}

/// <summary>Result of checking one backup file (Phase 9 §4.6). Only <see cref="FullyValidated"/> counts as verified.</summary>
public enum BackupStatus
{
    /// <summary>Not a recognized backup name; the file was not opened.</summary>
    Unrecognized,

    /// <summary>A <c>-journal</c>, <c>-wal</c> or <c>-shm</c> file exists next to it, or its header says WAL. Not opened.</summary>
    NotStandalone,

    /// <summary>Not a SQLite database, unreadable, or not a complete file.</summary>
    Unreadable,

    /// <summary><c>quick_check</c> or <c>integrity_check</c> reported a problem.</summary>
    IntegrityFailed,

    /// <summary>A SQLite database that WorkHub did not create (schema version 0).</summary>
    Foreign,

    /// <summary>Made by a newer WorkHub version.</summary>
    NewerSchema,

    /// <summary>The schema definition differs from what the frozen migrations produce for its version.</summary>
    SchemaMismatch,

    /// <summary><c>foreign_key_check</c> found rows.</summary>
    ForeignKeyViolations,

    /// <summary>The version in the file name differs from the version stored in the file.</summary>
    NameVersionMismatch,

    /// <summary>Checking the file left a <c>-journal</c>, <c>-wal</c> or <c>-shm</c> behind (not expected).</summary>
    ValidationSideEffect,

    /// <summary>A new candidate differs from the source it was copied from.</summary>
    NotEqualToSource,

    /// <summary>Passed the quick level only (no index/UNIQUE consistency or foreign-key check).</summary>
    QuickChecked,

    /// <summary>Passed the full level (<c>integrity_check</c> and <c>foreign_key_check</c> included).</summary>
    FullyValidated,
}

public enum BackupCheckLevel
{
    Quick,
    Full,
}

public enum BackupCompatibility
{
    /// <summary>Not checked, or the check failed before the version was known.</summary>
    Unknown,

    /// <summary>Same schema version as this build.</summary>
    Current,

    /// <summary>An older WorkHub schema; startup would migrate it (after its own migration backup).</summary>
    Older,

    /// <summary>Made by a newer WorkHub; this build cannot use it.</summary>
    Newer,

    /// <summary>Foreign, damaged or structurally different; never usable.</summary>
    NotUsable,
}

/// <param name="Detail">Short, data-free reason (classification, object names, first integrity line).</param>
public sealed record BackupCheckResult(BackupStatus Status, int? SchemaVersion = null, string? Detail = null)
{
    public bool PassedQuick => Status is BackupStatus.QuickChecked or BackupStatus.FullyValidated;

    public bool IsFullyValidated => Status == BackupStatus.FullyValidated;

    /// <summary>How this file relates to the running build's schema.</summary>
    public BackupCompatibility CompatibilityWith(int latestVersion) => Status switch
    {
        BackupStatus.QuickChecked or BackupStatus.FullyValidated when SchemaVersion == latestVersion => BackupCompatibility.Current,
        BackupStatus.QuickChecked or BackupStatus.FullyValidated when SchemaVersion < latestVersion => BackupCompatibility.Older,
        BackupStatus.NewerSchema => BackupCompatibility.Newer,
        BackupStatus.Unrecognized => BackupCompatibility.Unknown,
        _ => BackupCompatibility.NotUsable,
    };
}

public enum BackupFolder
{
    Daily,
    Migrations,
}

/// <summary>One file found in <c>Backups\Daily</c> or <c>Backups\Migrations</c>.</summary>
/// <param name="Taken">When the backup was taken, as Tokyo time (migration names carry UTC and are converted).</param>
/// <param name="NameVersion">Schema version in the name (daily: the backup's; migration: the version before the upgrade).</param>
public sealed record BackupEntry(
    string FileName,
    BackupFolder Folder,
    BackupKind Kind,
    long Size,
    DateTimeOffset? Taken,
    int? NameVersion,
    int? MigratedToVersion = null);

public enum DailyBackupOutcome
{
    /// <summary>A new backup was published (and retention ran).</summary>
    Created,

    /// <summary>Today's backup already exists and passes the quick check.</summary>
    AlreadyDone,

    /// <summary>Another WorkHub instance on this data root holds the daily-backup lock.</summary>
    SkippedBusy,

    /// <summary>No backup was published; nothing was pruned. The next start tries again.</summary>
    Failed,
}

public enum DailyBackupFailure
{
    None,

    /// <summary>The database stayed locked by another connection longer than the lock-wait timeout.</summary>
    SourceBusy,

    /// <summary>The database file was missing when the backup started.</summary>
    SourceMissing,

    /// <summary>The live database failed a check (integrity, structure or foreign keys) while being backed up.</summary>
    SourceCheck,

    DiskFull,

    /// <summary>The Backups folder could not be created, written or locked.</summary>
    FolderUnavailable,

    /// <summary>The copy failed its checks and was discarded.</summary>
    CandidateInvalid,

    /// <summary>The copy could not be made standalone (journal/WAL files) and was discarded.</summary>
    NotStandalone,

    /// <summary>The checked copy could not be put in place, or changed after it was.</summary>
    PublishFailed,

    Unexpected,
}

/// <param name="Deleted">Old backups removed.</param>
/// <param name="DeleteFailures">Old backups that should have been removed but could not be (kept; retried later).</param>
/// <param name="Preserved">Eligible files kept because they did not pass full validation.</param>
/// <param name="InsufficientEvidence">Fewer than three newer fully validated backups; nothing was deleted.</param>
public sealed record RetentionSummary(int Deleted, int DeleteFailures, int Preserved, bool InsufficientEvidence);

public sealed record DailyBackupResult(
    DailyBackupOutcome Outcome,
    DailyBackupFailure Failure = DailyBackupFailure.None,
    string? FileName = null,
    string? Detail = null,
    RetentionSummary? Retention = null)
{
    public static DailyBackupResult Fail(DailyBackupFailure failure, string? detail = null) => new(DailyBackupOutcome.Failed, failure, Detail: detail);

    /// <summary>True when a fully validated backup of today exists after this run.</summary>
    public bool BackedUpToday => Outcome is DailyBackupOutcome.Created or DailyBackupOutcome.AlreadyDone;
}

/// <summary>The startup daily backup (Phase 9 §5). Never throws for backup problems; failures are results.</summary>
public interface IDailyBackup
{
    DailyBackupResult Run();
}

/// <summary>Read-only discovery and checking of backups for the Backups page. It has no operation that changes a database.</summary>
public interface IBackupCatalog
{
    int LatestSchemaVersion { get; }

    /// <summary>Lists both backup folders without opening any file.</summary>
    Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks one file off the calling thread with an unpooled read-only connection that is closed afterwards.</summary>
    Task<BackupCheckResult> CheckAsync(BackupEntry entry, BackupCheckLevel level, CancellationToken cancellationToken = default);

    /// <summary>Opens the Backups folder in Explorer. Returns an error message, or null on success.</summary>
    string? OpenFolder();
}
