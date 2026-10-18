using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Infrastructure.Persistence;

namespace WorkHub.Infrastructure.Backup;

/// <summary>What a backup must satisfy beyond integrity (daily backups: everything; migration backups: D14/D17).</summary>
/// <param name="RequireReferenceIdentity">Fingerprint must equal the frozen-migration reference for its version.</param>
/// <param name="RequireForeignKeys"><c>foreign_key_check</c> must be empty.</param>
internal sealed record ValidationRules(bool RequireReferenceIdentity, bool RequireForeignKeys)
{
    public static ValidationRules Daily { get; } = new(true, true);

    /// <summary>D14/D17: a migration backup is a faithful copy; identity and FK problems of the source are logged, not fatal.</summary>
    public static ValidationRules Migration { get; } = new(false, false);
}

/// <summary>
/// Validation levels (Phase 9 §4.6). Every file passes the pre-open gates first and is then opened with an unpooled
/// <c>Mode=ReadOnly</c> connection that is closed before returning; afterwards the validator confirms no sidecar appeared.
/// </summary>
internal sealed class BackupValidator(SchemaReference references, IBackupFileOps files)
{
    private const int MaxDetailLength = 200;

    /// <summary>Quick or full level for an existing file.</summary>
    /// <param name="nameVersion">Version from the file name, if any; a different stored version is <see cref="BackupStatus.NameVersionMismatch"/>.</param>
    public BackupCheckResult Check(string path, BackupCheckLevel level, int? nameVersion = null, ValidationRules? rules = null) =>
        Inspect(path, level, rules ?? ValidationRules.Daily, nameVersion, extra: null);

    /// <summary>
    /// Candidate level: full level plus fidelity — schema version, fingerprint and every table's row count equal the
    /// source facts, and the file length equals page_count × page_size.
    /// </summary>
    public BackupCheckResult CheckCandidate(string path, SourceFacts source, ValidationRules rules) =>
        Inspect(path, BackupCheckLevel.Full, rules, nameVersion: null, extra: connection => Fidelity(connection, path, source));

    /// <summary>
    /// Phase 10 preservation check for a faithful copy of DEGRADED current data (newer, foreign, structurally different or
    /// with foreign-key rows). Standalone gate, unpooled ReadOnly open, full <c>integrity_check</c>, and fidelity to the
    /// pinned source facts (version, fingerprint hash, every table's row count, length = page_count × page_size). There is
    /// deliberately NO version-range, schema-identity or foreign-key classification: the result is <see cref="BackupStatus.Preserved"/>,
    /// never <see cref="BackupStatus.FullyValidated"/>. <see cref="Check"/> and <see cref="CheckCandidate"/> are unchanged, so
    /// restore candidates are never accepted through this path.
    /// </summary>
    public BackupCheckResult CheckPreservation(string path, SourceFacts source)
    {
        try
        {
            if (StandaloneGate.Check(files, path) is { } refused)
                return refused;
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return Unavailable(ex);
        }

        BackupCheckResult result;
        try
        {
            using var connection = new SqliteConnectionFactory(path).Open(SqliteOpenMode.ReadOnly, pooling: false);
            var version = connection.GetUserVersion();
            result = IntegrityCheck(connection) is { } problem
                ? new BackupCheckResult(BackupStatus.IntegrityFailed, version, Short(problem))
                : Fidelity(connection, path, source) ?? new BackupCheckResult(BackupStatus.Preserved, version);
        }
        catch (SqliteException ex)
        {
            result = new BackupCheckResult(ex.SqliteErrorCode == SqliteCodes.Corrupt ? BackupStatus.IntegrityFailed : BackupStatus.Unreadable, Detail: $"SQLite error {ex.SqliteErrorCode}");
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            result = Unavailable(ex);
        }

        IReadOnlyList<string> leftovers;
        try
        {
            leftovers = StandaloneGate.Sidecars(files, path);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return Unavailable(ex);
        }

        return leftovers.Count == 0
            ? result
            : new BackupCheckResult(BackupStatus.ValidationSideEffect, result.SchemaVersion, $"checking left {string.Join(", ", leftovers)} behind");
    }

    /// <summary>
    /// Every expected per-file file-system failure (locked by another process, removed, access denied) — in the pre-open
    /// gate, while inspecting, or in the sidecar re-check afterwards — is a non-passing <see cref="BackupStatus.Unreadable"/>
    /// result for this file, never an exception: one unavailable backup must not fail a run or be treated as validated.
    /// Programming errors and simulated crashes are not caught.
    /// </summary>
    private BackupCheckResult Inspect(string path, BackupCheckLevel level, ValidationRules rules, int? nameVersion, Func<SqliteConnection, BackupCheckResult?>? extra)
    {
        try
        {
            if (StandaloneGate.Check(files, path) is { } refused)
                return refused; // never opened
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return Unavailable(ex);
        }

        BackupCheckResult result;
        try
        {
            using var connection = new SqliteConnectionFactory(path).Open(SqliteOpenMode.ReadOnly, pooling: false);
            result = InspectOpen(connection, level, rules, nameVersion, extra);
        }
        catch (SqliteException ex)
        {
            result = ex.SqliteErrorCode == SqliteCodes.Corrupt
                ? new BackupCheckResult(BackupStatus.IntegrityFailed, Detail: $"SQLite error {ex.SqliteErrorCode}")
                : new BackupCheckResult(BackupStatus.Unreadable, Detail: $"SQLite error {ex.SqliteErrorCode}");
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            result = Unavailable(ex);
        }

        IReadOnlyList<string> leftovers;
        try
        {
            leftovers = StandaloneGate.Sidecars(files, path);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return Unavailable(ex); // cannot confirm the check had no side effect: not a passing result
        }

        return leftovers.Count == 0
            ? result
            : new BackupCheckResult(BackupStatus.ValidationSideEffect, result.SchemaVersion, $"checking left {string.Join(", ", leftovers)} behind");
    }

    /// <summary>Locked, missing (FileNotFound/DirectoryNotFound are IOExceptions) or access denied.</summary>
    private static bool IsUnavailable(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private static BackupCheckResult Unavailable(Exception ex) =>
        new(BackupStatus.Unreadable, Detail: $"file could not be read ({ex.GetType().Name})");

    private BackupCheckResult InspectOpen(SqliteConnection connection, BackupCheckLevel level, ValidationRules rules, int? nameVersion, Func<SqliteConnection, BackupCheckResult?>? extra)
    {
        if (connection.QuickCheck() is { } quickProblem)
            return new BackupCheckResult(BackupStatus.IntegrityFailed, Detail: Short(quickProblem));

        var version = connection.GetUserVersion();
        if (version <= 0)
            return new BackupCheckResult(BackupStatus.Foreign, version, "not a WorkHub database (schema version 0)");
        if (version > references.LatestVersion)
            return new BackupCheckResult(BackupStatus.NewerSchema, version, $"schema v{version} is newer than this build (v{references.LatestVersion})");

        if (rules.RequireReferenceIdentity)
        {
            var differences = references.DifferencesFrom(version, SchemaFingerprint.Read(connection));
            if (differences.Count > 0)
                return new BackupCheckResult(BackupStatus.SchemaMismatch, version, string.Join("; ", differences));
        }

        if (nameVersion is { } named && named != version)
            return new BackupCheckResult(BackupStatus.NameVersionMismatch, version, $"name says v{named}, file is v{version}");

        if (level == BackupCheckLevel.Quick)
            return new BackupCheckResult(BackupStatus.QuickChecked, version);

        if (IntegrityCheck(connection) is { } integrityProblem)
            return new BackupCheckResult(BackupStatus.IntegrityFailed, version, Short(integrityProblem));

        if (rules.RequireForeignKeys && ForeignKeyViolations(connection) > 0)
            return new BackupCheckResult(BackupStatus.ForeignKeyViolations, version, "foreign_key_check found rows");

        return extra?.Invoke(connection) ?? new BackupCheckResult(BackupStatus.FullyValidated, version);
    }

    private BackupCheckResult? Fidelity(SqliteConnection connection, string path, SourceFacts source)
    {
        var version = connection.GetUserVersion();
        if (version != source.Version)
            return NotEqual(version, $"schema v{version}, source v{source.Version}");

        if (SchemaFingerprint.Read(connection).ExactHash() != source.Fingerprint.ExactHash()) // exact fidelity: never the reference tolerance
            return NotEqual(version, "schema differs from the source");

        var counts = SqliteSnapshotter.RowCounts(connection, null);
        foreach (var (table, count) in source.RowCounts)
        {
            if (!counts.TryGetValue(table, out var copied) || copied != count)
                return NotEqual(version, $"row count of {table} differs");
        }

        if (counts.Count != source.RowCounts.Count)
            return NotEqual(version, "table set differs");

        var pageCount = connection.Scalar<long>("PRAGMA page_count;");
        var pageSize = connection.Scalar<long>("PRAGMA page_size;");
        if (pageCount * pageSize != files.GetLength(path))
            return NotEqual(version, "file length differs from its page count");

        return null;

        static BackupCheckResult NotEqual(int version, string detail) => new(BackupStatus.NotEqualToSource, version, detail);
    }

    /// <summary>Full <c>integrity_check</c>: null when the only row is "ok", otherwise the first problem.</summary>
    internal static string? IntegrityCheck(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return "no result";
        var first = reader.IsDBNull(0) ? "no result" : reader.GetString(0);
        return string.Equals(first, "ok", StringComparison.OrdinalIgnoreCase) && !reader.Read() ? null : first;
    }

    internal static long ForeignKeyViolations(SqliteConnection connection, SqliteTransaction? transaction = null) =>
        connection.Scalar<long>("SELECT count(*) FROM pragma_foreign_key_check;", transaction);

    /// <summary>First meaningful line (skips SQLite's "*** in database main ***" banner), shortened. Page/tree numbers only.</summary>
    internal static string Short(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = lines.FirstOrDefault(l => !l.StartsWith("***", StringComparison.Ordinal)) ?? lines.FirstOrDefault() ?? "no result";
        return line.Length <= MaxDetailLength ? line : line[..MaxDetailLength] + "…";
    }
}

internal static class SqliteCodes
{
    public const int Busy = 5;
    public const int Locked = 6;
    public const int Corrupt = 11;
    public const int Full = 13;
    public const int CantOpen = 14;
    public const int NotADatabase = 26;
}
