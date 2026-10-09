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
/// <item>A NEW database (no file, or an empty SQLite file with no objects) is built completely in a staging file
/// (<c>workhub.db.new</c>): schema, then the optional initial data in one transaction. Only a finished database is
/// moved into place. If anything fails, the staging file is deleted and no database exists, so the next start is
/// again a new environment and retries; a staging file left by an interrupted start is discarded. An existing
/// database is never seeded, even if its tables are empty.</item>
/// </list>
/// </summary>
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    /// <summary>Loads initial data into a new database inside the given transaction; returns a log summary.</summary>
    public delegate string NewDatabaseSeed(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset utcNow);

    private readonly WorkHubPaths _paths;
    private readonly SqliteConnectionFactory _connections;
    private readonly IReadOnlyList<ISchemaMigration> _migrations;
    private readonly TimeProvider _time;
    private readonly IAppLog _log;
    private readonly NewDatabaseSeed? _seed;

    /// <param name="seed">Initial data for a new database (the application passes <see cref="InitialSystemsSeed.Apply"/>).</param>
    public DatabaseInitializer(WorkHubPaths paths, IAppLog log, NewDatabaseSeed? seed = null)
        : this(paths, SchemaMigrations.All, TimeProvider.System, log, seed)
    {
    }

    internal DatabaseInitializer(WorkHubPaths paths, IReadOnlyList<ISchemaMigration> migrations, TimeProvider time, IAppLog log, NewDatabaseSeed? seed = null)
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
        _seed = seed;
    }

    public int LatestVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    /// <summary>Where a new database is assembled before it is moved to <see cref="WorkHubPaths.DatabaseFile"/>.</summary>
    internal string StagingFile => _paths.DatabaseFile + ".new";

    public DatabaseInitializationResult Initialize()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        if (!File.Exists(_paths.DatabaseFile))
            return CreateNew(replaceEmptyFile: false);

        DatabaseInitializationResult? result;
        try
        {
            using var connection = _connections.Open(SqliteOpenMode.ReadWrite);
            result = InitializeExisting(connection);
        }
        catch (SqliteException ex)
        {
            throw new DatabaseStartupException(
                DatabaseStartupFailure.Unreadable,
                $"The WorkHub database could not be opened ({ex.SqliteErrorCode}). It was left unchanged.",
                ex);
        }

        if (result is not null)
            return result;

        // An empty SQLite file (no objects at version 0) is an aborted creation: build a new database and replace it.
        _connections.ClearPools();
        return CreateNew(replaceEmptyFile: true);
    }

    private DatabaseInitializationResult CreateNew(bool replaceEmptyFile)
    {
        DeleteStaging(); // left by an interrupted earlier start
        try
        {
            var staging = new SqliteConnectionFactory(StagingFile);
            using (var connection = staging.Open(SqliteOpenMode.ReadWriteCreate, pooling: false))
            {
                ApplyMigrations(connection, fromVersion: 0);
                if (_seed is not null)
                {
                    using var transaction = connection.BeginTransaction();
                    var summary = _seed(connection, transaction, _time.GetUtcNow());
                    transaction.Commit();
                    _log.Info(summary);
                }
            }

            File.Move(StagingFile, _paths.DatabaseFile, overwrite: replaceEmptyFile);
        }
        catch (Exception ex)
        {
            DeleteStaging();
            _log.Error("Creating the new database failed; nothing was kept.", ex);
            throw new DatabaseStartupException(DatabaseStartupFailure.NewDatabaseFailed, NewDatabaseFailedMessage, ex);
        }

        return new DatabaseInitializationResult(DatabaseInitializationOutcome.Created, 0, LatestVersion);
    }

    private const string NewDatabaseFailedMessage =
        "WorkHub could not create its database and initial data. Nothing was kept; WorkHub will try again the next time it starts.";

    private void DeleteStaging()
    {
        foreach (var file in new[] { StagingFile, StagingFile + "-journal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not delete the incomplete database file {Path.GetFileName(file)} ({ex.GetType().Name}).");
            }
        }
    }

    /// <returns>Null when the file is an empty SQLite file that should be (re)created as a new database.</returns>
    private DatabaseInitializationResult? InitializeExisting(SqliteConnection connection)
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

            _log.Warn("Existing database file had no schema; creating a new database in its place.");
            return null;
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
