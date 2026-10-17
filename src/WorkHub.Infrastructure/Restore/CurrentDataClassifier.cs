using Microsoft.Data.Sqlite;
using WorkHub.Core.Restore;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Persistence;

namespace WorkHub.Infrastructure.Restore;

/// <summary>A request is refused before anything is reserved or changed; <see cref="Exception.Message"/> is user text.</summary>
internal sealed class RestoreRefusedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>X-abort: the restore stops before anything is moved and publishes <c>Aborted</c>.</summary>
internal sealed class RestoreAbortException(string reason, string userMessage, Exception? inner = null) : Exception(userMessage, inner)
{
    public string Reason { get; } = reason;
}

/// <summary>The current database is held by another connection (possibly an older WorkHub).</summary>
internal sealed class CurrentDataBusyException(Exception inner) : Exception("The database is in use by another program.", inner);

/// <summary>
/// Classification of the CURRENT data (§7.2, §7.3). The live database is never put through the standalone backup gate.
/// <list type="bullet">
/// <item>Request time: unpooled <c>Mode=ReadOnly</c>. It never modifies anything: a hot journal or a WAL that needs
/// recovery makes the read fail (<c>SQLITE_READONLY_*</c>), which is reported as <see cref="CurrentDataClass.NeedsRecovery"/>.</item>
/// <item>Execution time (after the as-found copy): unpooled <c>Mode=ReadWrite</c>, so SQLite rolls back a hot journal or
/// recovers a WAL; the checks run inside a pinned deferred read transaction.</item>
/// </list>
/// Checks, in order: <c>integrity_check</c> → <c>user_version</c> → fingerprint identity (1…Latest only) →
/// <c>foreign_key_check</c>.
/// </summary>
internal sealed class CurrentDataClassifier(SchemaReference references, int busyTimeoutSeconds = 10)
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    public DataClassification ClassifyReadOnly(string databaseFile) => Classify(databaseFile, SqliteOpenMode.ReadOnly);

    public DataClassification ClassifyForExecution(string databaseFile) => Classify(databaseFile, SqliteOpenMode.ReadWrite);

    private DataClassification Classify(string databaseFile, SqliteOpenMode mode)
    {
        if (!File.Exists(databaseFile))
            return DataClassification.Missing;
        if (!LooksLikeSqlite(databaseFile))
            return DataClassification.Unreadable;

        try
        {
            using var connection = new SqliteConnectionFactory(databaseFile, busyTimeoutSeconds).Open(mode, pooling: false);
            using var pin = connection.BeginTransaction(deferred: true);
            connection.Scalar<long>("SELECT count(*) FROM sqlite_schema;", pin); // pins the snapshot (hot journal / WAL recovery here)

            if (BackupValidator.IntegrityCheck(connection, pin) is not null)
                return DataClassification.Unreadable;

            var version = connection.GetUserVersion(pin);
            if (version <= 0)
                return new DataClassification(CurrentDataClass.Degraded, DegradedKind.Foreign, version);
            if (version > references.LatestVersion)
                return new DataClassification(CurrentDataClass.Degraded, DegradedKind.Newer, version);
            if (SchemaFingerprint.Read(connection, pin).DifferencesFrom(references.For(version)).Count > 0)
                return new DataClassification(CurrentDataClass.Degraded, DegradedKind.StructureDiffers, version);
            if (BackupValidator.ForeignKeyViolations(connection, pin) > 0)
                return new DataClassification(CurrentDataClass.Degraded, DegradedKind.ForeignKeys, version);
            return new DataClassification(CurrentDataClass.Healthy, Version: version);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCodes.Busy or SqliteCodes.Locked)
        {
            throw new CurrentDataBusyException(ex);
        }
        catch (SqliteException ex) when (mode == SqliteOpenMode.ReadOnly && NeedsRecovery(ex, databaseFile))
        {
            return new DataClassification(CurrentDataClass.NeedsRecovery);
        }
        catch (SqliteException)
        {
            return DataClassification.Unreadable;
        }
    }

    /// <summary>SQLITE_READONLY_ROLLBACK (776) / _RECOVERY (264) / _CANTINIT, or a read-only open that fails while sidecars exist.</summary>
    private static bool NeedsRecovery(SqliteException ex, string databaseFile) =>
        ex.SqliteErrorCode == ReadOnlyCode
        || (ex.SqliteErrorCode is not (SqliteCodes.Corrupt or SqliteCodes.NotADatabase)
            && (File.Exists(databaseFile + "-journal") || File.Exists(databaseFile + "-wal")));

    private const int ReadOnlyCode = 8;

    private static bool LooksLikeSqlite(string databaseFile)
    {
        try
        {
            using var stream = new FileStream(databaseFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length == 0)
                return false;
            var header = new byte[Magic.Length];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header.AsSpan().SequenceEqual(Magic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
