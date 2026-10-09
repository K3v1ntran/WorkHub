namespace WorkHub.Core.Startup;

public enum DatabaseStartupFailure
{
    /// <summary>The file could not be opened or is not a SQLite database.</summary>
    Unreadable,

    /// <summary>The SQLite integrity check failed.</summary>
    IntegrityCheckFailed,

    /// <summary>The database was written by a newer WorkHub version.</summary>
    NewerSchema,

    /// <summary>The file is a SQLite database but not one WorkHub created.</summary>
    UnrecognizedDatabase,

    /// <summary>The pre-migration safety backup could not be created or validated.</summary>
    MigrationBackupFailed,

    /// <summary>A schema migration failed and was rolled back.</summary>
    MigrationFailed,

    /// <summary>
    /// Creating a NEW database (schema plus initial data) failed. Nothing was kept, so the next start creates it again.
    /// </summary>
    NewDatabaseFailed,
}

/// <summary>
/// Raised when startup refuses to use the database. The database file is not replaced or reset in these cases.
/// </summary>
public sealed class DatabaseStartupException(DatabaseStartupFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public DatabaseStartupFailure Failure { get; } = failure;
}
