using Microsoft.Data.Sqlite;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// Creates a new database or validates and migrates an existing one.
/// Safety rules:
/// <list type="bullet">
/// <item>An existing file is opened with <see cref="SqliteOpenMode.ReadWrite"/> and is never deleted, replaced or reset.</item>
/// <item>Unreadable, corrupt, foreign or newer-schema databases stop startup with <see cref="DatabaseStartupException"/>.</item>
/// <item>Before migrating an existing schema, a validated copy is written to <c>Backups\Migrations</c>.</item>
/// <item>Each migration and its version bump commit atomically.</item>
/// </list>
/// </summary>
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private readonly WorkHubPaths _paths;
    private readonly SqliteConnectionFactory _connections;
    private readonly IReadOnlyList<ISchemaMigration> _migrations;
    private readonly TimeProvider _time;
    private readonly IAppLog _log;

    public DatabaseInitializer(WorkHubPaths paths, IAppLog log)
        : this(paths, SchemaMigrations.All, TimeProvider.System, log)
    {
    }

    internal DatabaseInitializer(WorkHubPaths paths, IReadOnlyList<ISchemaMigration> migrations, TimeProvider time, IAppLog log)
    {
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
                throw new ArgumentException("Migrations must be ordered and numbered 1..N without gaps.", nameof(migrations));
        }

        _paths = paths;
        _connections = new SqliteConnectionFactory(paths.DatabaseFile);
        _migrations = migrations;
        _time = time;
        _log = log;
    }

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    public DatabaseInitializationResult Initialize()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        var existed = File.Exists(_paths.DatabaseFile);

        try
        {
            using var connection = _connections.Open(existed ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate);
            return existed ? InitializeExisting(connection) : InitializeNew(connection);
        }
        catch (SqliteException ex)
        {
            throw new DatabaseStartupException(
                DatabaseStartupFailure.Unreadable,
                $"The WorkHub database could not be opened ({ex.SqliteErrorCode}). It was left unchanged.",
                ex);
        }
    }

    private DatabaseInitializationResult InitializeNew(SqliteConnection connection)
    {
        ApplyMigrations(connection, fromVersion: 0);
        return new DatabaseInitializationResult(DatabaseInitializationOutcome.Created, 0, LatestVersion);
    }

    private DatabaseInitializationResult InitializeExisting(SqliteConnection connection)
    {
        var problem = connection.QuickCheck();
        if (problem is not null)
        {
            throw new DatabaseStartupException(
                DatabaseStartupFailure.IntegrityCheckFailed,
                $"The WorkHub database failed its integrity check ({problem}). It was left unchanged.");
        }

        var version = connection.GetUserVersion();

        if (version > LatestVersion)
        {
            throw new DatabaseStartupException(
                DatabaseStartupFailure.NewerSchema,
                $"The WorkHub database uses schema v{version}, newer than this build supports (v{LatestVersion}). Update WorkHub.");
        }

        if (version == 0)
        {
            // A zero-version file with no objects is an empty/aborted creation: safe to initialize.
            // Anything else at version 0 was not created by WorkHub and must not be touched.
            if (connection.HasUserObjects())
            {
                throw new DatabaseStartupException(
                    DatabaseStartupFailure.UnrecognizedDatabase,
                    "The file at the WorkHub database location is not a WorkHub database. It was left unchanged.");
            }

            _log.Warn("Existing database file had no schema; initializing it.");
            return InitializeNew(connection);
        }

        if (version == LatestVersion)
            return new DatabaseInitializationResult(DatabaseInitializationOutcome.Opened, version, version);

        var backupFile = CreateMigrationBackup(connection, version);
        ApplyMigrations(connection, version);
        return new DatabaseInitializationResult(DatabaseInitializationOutcome.Migrated, version, LatestVersion, backupFile);
    }

    private void ApplyMigrations(SqliteConnection connection, int fromVersion)
    {
        foreach (var migration in _migrations.Where(m => m.Version > fromVersion))
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                migration.Apply(connection, transaction);
                connection.SetUserVersion(migration.Version, transaction);
                transaction.Commit();
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                throw new DatabaseStartupException(
                    DatabaseStartupFailure.MigrationFailed,
                    $"Schema migration v{migration.Version} ({migration.Description}) failed and was rolled back.",
                    ex);
            }

            _log.Info($"Applied schema migration v{migration.Version}: {migration.Description}.");
        }
    }

    private string CreateMigrationBackup(SqliteConnection source, int fromVersion)
    {
        Directory.CreateDirectory(_paths.MigrationBackupsDirectory);
        var stamp = _time.GetUtcNow().ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var file = Path.Combine(_paths.MigrationBackupsDirectory, $"workhub_v{fromVersion}_before_v{LatestVersion}_{stamp}.db");

        try
        {
            var backupConnections = new SqliteConnectionFactory(file);
            using (var destination = backupConnections.Open(SqliteOpenMode.ReadWriteCreate, pooling: false))
                source.BackupDatabase(destination);

            using var check = backupConnections.Open(SqliteOpenMode.ReadOnly, pooling: false);
            var problem = check.QuickCheck();
            var copiedVersion = check.GetUserVersion();
            if (problem is not null || copiedVersion != fromVersion)
                throw new InvalidOperationException($"Backup validation failed (check: {problem ?? "ok"}, version: {copiedVersion}).");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new DatabaseStartupException(
                DatabaseStartupFailure.MigrationBackupFailed,
                "The safety backup required before a schema upgrade could not be created. The database was not migrated.",
                ex);
        }

        _log.Info($"Created pre-migration backup {Path.GetFileName(file)}.");
        return file;
    }
}
