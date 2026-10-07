namespace WorkHub.Core.Startup;

public interface ILocalStorage
{
    /// <summary>Creates any missing required folders. Never deletes or moves existing content.</summary>
    void EnsureRequiredDirectories();
}

public enum DatabaseInitializationOutcome
{
    /// <summary>No database existed (or it was an empty SQLite file); one was created at the latest schema.</summary>
    Created,

    /// <summary>The existing database was already at the latest schema.</summary>
    Opened,

    /// <summary>The existing database was migrated to the latest schema after a safety backup.</summary>
    Migrated,
}

public sealed record DatabaseInitializationResult(
    DatabaseInitializationOutcome Outcome,
    int PreviousSchemaVersion,
    int SchemaVersion,
    string? MigrationBackupFile = null);

public interface IDatabaseInitializer
{
    /// <summary>
    /// Creates or opens the database and brings it to the latest schema.
    /// Throws <see cref="DatabaseStartupException"/> rather than replacing a database it cannot trust.
    /// </summary>
    DatabaseInitializationResult Initialize();
}
