using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Infrastructure.Persistence;

namespace WorkHub.Infrastructure.Backup;

/// <summary>Facts read from the pinned source snapshot (S3); the candidate must reproduce them exactly.</summary>
/// <param name="Warnings">Log-only findings (D17: migration backups record identity/FK problems instead of failing).</param>
internal sealed record SourceFacts(
    int Version,
    SchemaFingerprint Fingerprint,
    IReadOnlyDictionary<string, long> RowCounts,
    IReadOnlyList<string> Warnings);

/// <param name="ExpectedVersion">LatestVersion for daily backups; fromVersion for migration backups.</param>
internal sealed record SnapshotRules(int ExpectedVersion, ValidationRules Validation);

/// <summary>Stages a test hook can observe (and, by throwing <see cref="SimulatedCrashException"/>, crash at).</summary>
internal enum BackupStage
{
    LockAcquired,
    OrphansCleaned,
    SourcePinned,
    Copied,
    Normalized,
    CandidateValidated,
    Published,
    Verified,
    Pruned,
}

/// <summary>Tests only: thrown by a stage hook to stop a run as a crash would — no cleanup, no result.</summary>
internal sealed class SimulatedCrashException(BackupStage stage) : Exception($"Simulated crash at {stage}.")
{
    public BackupStage Stage { get; } = stage;
}

/// <summary>A backup step that failed for a known reason (mapped to a result or, for migrations, a startup failure).</summary>
internal sealed class BackupStepException(DailyBackupFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public DailyBackupFailure Failure { get; } = failure;
}

/// <summary>
/// Snapshot protocol S1–S8 (Phase 9 §4.2), for DELETE and WAL sources. All connections are unpooled.
/// <list type="bullet">
/// <item>The source is opened ReadWrite (never created) so SQLite itself rolls back a hot journal or recovers a WAL before
/// reading. WorkHub never changes the source's journal mode, never checkpoints it and never touches its sidecar files.</item>
/// <item>A deferred read transaction pins the snapshot first: its first read honours the lock-wait timeout (a bare
/// <c>BackupDatabase</c> does not). DELETE mode: other connections cannot commit until the pin is released; WAL mode: they
/// can, but their commits are not in the snapshot.</item>
/// <item>The candidate is normalized on its own connection (<c>journal_mode=DELETE</c>) before it is closed, so a copy of a
/// WAL source never exists on disk as a closed WAL-mode file, and it must pass the standalone gate afterwards.</item>
/// </list>
/// The lock-wait timeout bounds only waiting for another connection's lock; copying and checking take as long as the
/// database size requires.
/// </summary>
internal sealed class SqliteSnapshotter(SchemaReference references, IBackupFileOps files, int lockWaitSeconds, Action<BackupStage>? hook = null)
{
    public const int DefaultLockWaitSeconds = 10;

    public SourceFacts Copy(string sourceFile, string candidateFile, SnapshotRules rules)
    {
        if (!files.FileExists(sourceFile))
            throw new BackupStepException(DailyBackupFailure.SourceMissing, "The database file does not exist.");

        SqliteConnection source;
        try
        {
            // S1: existing file only; ReadWrite so hot-journal rollback / WAL recovery can happen inside SQLite.
            source = new SqliteConnectionFactory(sourceFile, lockWaitSeconds).Open(SqliteOpenMode.ReadWrite, pooling: false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteCodes.CantOpen)
        {
            throw new BackupStepException(DailyBackupFailure.SourceMissing, "The database file could not be opened.", ex);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCodes.Busy or SqliteCodes.Locked)
        {
            throw new BackupStepException(DailyBackupFailure.SourceBusy, "The database stayed locked by another connection.", ex);
        }

        using (source)
        {
            SqliteTransaction pin;
            try
            {
                // S2: pin. The SELECT acquires the read lock / WAL read mark, waiting up to the lock-wait timeout.
                pin = source.BeginTransaction(deferred: true);
                source.Scalar<long>("SELECT count(*) FROM sqlite_schema;", pin);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCodes.Busy or SqliteCodes.Locked)
            {
                throw new BackupStepException(DailyBackupFailure.SourceBusy, $"The database stayed locked by another connection for more than {lockWaitSeconds} s.", ex);
            }

            using (pin)
            {
                hook?.Invoke(BackupStage.SourcePinned);
                var facts = CheckSource(source, pin, rules);   // S3
                CopyAndNormalize(source, candidateFile);      // S4–S6
                pin.Commit();                                  // S7 (read-only transaction: releases the pin)
                return VerifyStandalone(candidateFile, facts); // S8
            }
        }
    }

    private SourceFacts CheckSource(SqliteConnection source, SqliteTransaction pin, SnapshotRules rules)
    {
        string? quickProblem;
        try
        {
            var result = source.Scalar<string>("PRAGMA quick_check(1);", pin);
            quickProblem = string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? null : result ?? "no result";
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCodes.Corrupt or SqliteCodes.NotADatabase)
        {
            quickProblem = $"SQLite error {ex.SqliteErrorCode}";
        }

        if (quickProblem is not null)
            throw new BackupStepException(DailyBackupFailure.SourceCheck, $"integrity check failed ({BackupValidator.Short(quickProblem)})");

        var version = source.GetUserVersion(pin);
        if (version != rules.ExpectedVersion)
            throw new BackupStepException(DailyBackupFailure.SourceCheck, $"schema v{version}, expected v{rules.ExpectedVersion}");

        var warnings = new List<string>();
        var fingerprint = SchemaFingerprint.Read(source, pin);
        if (version >= 1 && version <= references.LatestVersion)
        {
            var differences = fingerprint.DifferencesFrom(references.For(version));
            if (differences.Count > 0)
            {
                var message = $"schema structure differs from WorkHub v{version}: {string.Join("; ", differences)}";
                if (rules.Validation.RequireReferenceIdentity)
                    throw new BackupStepException(DailyBackupFailure.SourceCheck, message);
                warnings.Add(message);
            }
        }

        if (BackupValidator.ForeignKeyViolations(source, pin) > 0)
        {
            const string message = "foreign_key_check found rows";
            if (rules.Validation.RequireForeignKeys)
                throw new BackupStepException(DailyBackupFailure.SourceCheck, message);
            warnings.Add(message);
        }

        return new SourceFacts(version, fingerprint, RowCounts(source, pin), warnings);
    }

    private void CopyAndNormalize(SqliteConnection source, string candidateFile)
    {
        try
        {
            // S4: a new, uniquely named file. ReadWriteCreate is used for the candidate only.
            using var candidate = new SqliteConnectionFactory(candidateFile).Open(SqliteOpenMode.ReadWriteCreate, pooling: false);

            // S5: copy the pinned snapshot (page copy; a WAL source yields a WAL-header candidate at this point).
            source.BackupDatabase(candidate);
            hook?.Invoke(BackupStage.Copied);

            // S6: normalize the CANDIDATE only, before it is closed.
            var mode = candidate.Scalar<string>("PRAGMA journal_mode=DELETE;");
            if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
                throw new BackupStepException(DailyBackupFailure.NotStandalone, $"the copy could not be made standalone (journal mode {mode ?? "unknown"})");
            hook?.Invoke(BackupStage.Normalized);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteCodes.Full)
        {
            throw new BackupStepException(DailyBackupFailure.DiskFull, "not enough disk space for the backup", ex);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteCodes.CantOpen)
        {
            throw new BackupStepException(DailyBackupFailure.FolderUnavailable, "the backup file could not be created", ex);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCodes.Busy or SqliteCodes.Locked)
        {
            throw new BackupStepException(DailyBackupFailure.SourceBusy, "the database was locked during the copy", ex);
        }
        catch (SqliteException ex)
        {
            throw new BackupStepException(DailyBackupFailure.CandidateInvalid, $"the copy failed (SQLite error {ex.SqliteErrorCode})", ex);
        }
    }

    private SourceFacts VerifyStandalone(string candidateFile, SourceFacts facts)
    {
        // S8: after close — header 1/1, no -journal/-wal/-shm. Checked again in full validation; failing here is explicit.
        BackupCheckResult? problem;
        try
        {
            problem = StandaloneGate.Check(files, candidateFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BackupStepException(DailyBackupFailure.CandidateInvalid, $"the copy could not be read back ({ex.GetType().Name})", ex);
        }

        if (problem is { Status: BackupStatus.NotStandalone })
            throw new BackupStepException(DailyBackupFailure.NotStandalone, $"the copy is not standalone ({problem.Detail})");
        return facts;
    }

    /// <summary>Row count of every user table (names are quoted; they come from sqlite_schema).</summary>
    internal static IReadOnlyDictionary<string, long> RowCounts(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
            counts[table] = connection.Scalar<long>($"SELECT count(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\";", transaction);
        return counts;
    }
}
