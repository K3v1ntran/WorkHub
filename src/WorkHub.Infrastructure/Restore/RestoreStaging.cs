using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Persistence;

namespace WorkHub.Infrastructure.Restore;

/// <summary>
/// Candidate eligibility (§7.1, never loosened): a private byte copy of the selected backup must be identical to the
/// source before and after copying (H1 = H2 = Hc) and pass Phase 9 <c>Check(Full, ValidationRules.Daily)</c> — standalone
/// gate, integrity, foreign keys, schema identity, name/version match — with a Current or Older schema.
/// </summary>
internal sealed class CandidateStager(IRestoreFileOps files, BackupValidator validator, int latestVersion)
{
    public (CandidateFacts Candidate, long SourceSize, string SourceSha) Stage(string sourcePath, int? nameVersion, string candidatePath)
    {
        if (!files.FileExists(sourcePath))
            throw new RestoreRefusedException("The selected backup no longer exists. Refresh the list.");
        if ((files.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
            throw new RestoreRefusedException("The selected backup is a link, not a backup file.");
        if (StandaloneGate.Sidecars(new BackupFileOps(), sourcePath).Count > 0)
            throw new RestoreRefusedException("The selected backup has journal or WAL files next to it (not standalone) and cannot be restored.");

        var size = files.GetLength(sourcePath);
        var h1 = files.Sha256(sourcePath);
        files.Copy(sourcePath, candidatePath);
        var h2 = files.Sha256(sourcePath);
        var hc = files.Sha256(candidatePath);
        if (h1 != h2 || h1 != hc || files.GetLength(candidatePath) != size)
            throw new RestoreRefusedException("The selected backup changed while it was being copied. Nothing was changed; try again.");

        var result = validator.Check(candidatePath, BackupCheckLevel.Full, nameVersion, ValidationRules.Daily);
        var compatibility = result.CompatibilityWith(latestVersion);
        if (!result.IsFullyValidated || compatibility is not (BackupCompatibility.Current or BackupCompatibility.Older))
            throw new RestoreRefusedException(RefusalText(result));

        return (new CandidateFacts(size, hc, files.FileId(candidatePath), result.SchemaVersion!.Value, compatibility), size, h1);
    }

    /// <summary>X1 / X8: the staged copy must still be exactly what was confirmed (ID, size, SHA-256; X1 adds the full check).</summary>
    public string? Revalidate(string candidatePath, CandidateFacts expected, bool fullCheck)
    {
        if (!files.FileExists(candidatePath))
            return "the staged copy is missing";
        if (files.FileId(candidatePath) != expected.FileId)
            return "the staged copy was replaced";
        if (files.GetLength(candidatePath) != expected.Size || files.Sha256(candidatePath) != expected.Sha256)
            return "the staged copy changed";
        if (!fullCheck)
            return null;

        var result = validator.Check(candidatePath, BackupCheckLevel.Full, expected.SchemaVersion, ValidationRules.Daily);
        return result.IsFullyValidated && result.CompatibilityWith(latestVersion) is BackupCompatibility.Current or BackupCompatibility.Older
            ? null
            : $"the staged copy failed its check ({result.Status})";
    }

    public static string RefusalText(BackupCheckResult result) => result.Status switch
    {
        BackupStatus.NewerSchema => "This backup was made by a newer WorkHub version and cannot be restored by this version.",
        BackupStatus.Foreign => "This file is not a WorkHub database and cannot be restored.",
        BackupStatus.SchemaMismatch => "This backup's structure differs from what WorkHub creates and cannot be restored.",
        BackupStatus.NotStandalone => "This backup is not standalone (journal or WAL files) and cannot be restored.",
        BackupStatus.IntegrityFailed => "This backup is damaged and cannot be restored.",
        BackupStatus.ForeignKeyViolations => "This backup has foreign-key problems and cannot be restored.",
        BackupStatus.NameVersionMismatch => "This backup's schema version does not match its name and it cannot be restored.",
        BackupStatus.ValidationSideEffect => "Checking this backup left files behind; it cannot be restored.",
        BackupStatus.Unreadable => "This backup cannot be read and cannot be restored.",
        _ => $"This backup did not pass full validation ({result.Status}) and cannot be restored.",
    };
}

/// <summary>The counts shown in the confirmation (§13.2), read from the staged copy with an unpooled ReadOnly connection.</summary>
internal static class RestoreContents
{
    public static RestoreContentCounts Read(string candidatePath)
    {
        using var connection = new SqliteConnectionFactory(candidatePath).Open(SqliteOpenMode.ReadOnly, pooling: false);
        var hasTrashColumn = Count(connection, "SELECT count(*) FROM pragma_table_info('WorkspaceNodes') WHERE name = 'TrashedAt';") > 0;
        return new RestoreContentCounts(
            Count(connection, "SELECT count(*) FROM Systems;"),
            Count(connection, "SELECT count(*) FROM OwnerItems;"),
            hasTrashColumn
                ? Count(connection, "SELECT count(*) FROM WorkspaceNodes WHERE NodeType = 'Project' AND TrashedAt IS NULL;")
                : Count(connection, "SELECT count(*) FROM WorkspaceNodes WHERE NodeType = 'Project';"),
            hasTrashColumn ? Count(connection, "SELECT count(*) FROM WorkspaceNodes WHERE NodeType = 'Project' AND TrashedAt IS NOT NULL;") : null,
            Count(connection, "SELECT count(*) FROM ProjectNotes;"),
            Count(connection, "SELECT count(*) FROM ProjectTrashEntries;"));
    }

    private static long? Count(SqliteConnection connection, string sql)
    {
        try
        {
            return connection.Scalar<long>(sql);
        }
        catch (SqliteException)
        {
            return null; // the table does not exist at this schema version
        }
    }
}

/// <summary>
/// X4 preservation of the CURRENT data according to its execution-time class (§7.2):
/// Healthy → Phase 9 snapshot with Daily rules + candidate-level validation (<c>workhub_prerestore_…</c>);
/// Degraded → Phase 9 snapshot with Migration rules + the preservation check (<c>workhub_preserved_…</c>);
/// Unreadable / Missing → nothing (the raw set is moved aside). Every connection is unpooled and closed before returning.
/// A failure aborts the restore before anything is moved.
/// </summary>
internal sealed class CurrentDataPreserver(WorkHubPaths paths, SchemaReference references, IBackupFileOps files, IAppLog log, Action? beforePublish = null)
{
    public SafetyRecord Preserve(DataClassification execution, DateTimeOffset utcNow)
    {
        if (execution.Class is not (CurrentDataClass.Healthy or CurrentDataClass.Degraded) || execution.Version is not { } version)
            return new SafetyRecord(SafetyKind.None, null, null);

        var healthy = execution.Class == CurrentDataClass.Healthy;
        var finalName = healthy ? PreRestoreNames.Validated(version, utcNow) : PreRestoreNames.Preserved(version, utcNow);
        var finalPath = Path.Combine(paths.PreRestoreBackupsDirectory, finalName);
        var candidate = Path.Combine(paths.PreRestoreBackupsDirectory, PreRestoreNames.CandidateName(finalName, Guid.NewGuid()));
        var validator = new BackupValidator(references, files);

        try
        {
            files.CreateDirectory(paths.PreRestoreBackupsDirectory);
            var snapshotter = new SqliteSnapshotter(references, files, SqliteSnapshotter.DefaultLockWaitSeconds);
            var facts = snapshotter.Copy(paths.DatabaseFile, candidate, new SnapshotRules(version, healthy ? ValidationRules.Daily : ValidationRules.Migration));
            foreach (var warning in facts.Warnings)
                log.Warn($"Restore: current data finding (kept in the preserved copy): {warning}.");

            var check = healthy ? validator.CheckCandidate(candidate, facts, ValidationRules.Daily) : validator.CheckPreservation(candidate, facts);
            var expected = healthy ? BackupStatus.FullyValidated : BackupStatus.Preserved;
            if (check.Status != expected)
                throw new RestoreAbortException($"safety copy check {check.Status}: {check.Detail}", SafetyFailedMessage);

            beforePublish?.Invoke();
            files.Move(candidate, finalPath);
            using var stream = new FileStream(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
            log.Info($"Restore: {(healthy ? "validated safety backup" : "preserved copy")} {finalName} created.");
            return new SafetyRecord(healthy ? SafetyKind.Validated : SafetyKind.Preserved, finalName, sha);
        }
        catch (BackupStepException ex) when (ex.Failure is DailyBackupFailure.SourceBusy)
        {
            DeleteCandidate(candidate);
            throw new RestoreAbortException("current data busy", InUseMessage, ex);
        }
        catch (Exception ex) when (ex is BackupStepException or SqliteException or IOException or UnauthorizedAccessException)
        {
            DeleteCandidate(candidate);
            throw new RestoreAbortException($"safety copy failed ({ex.GetType().Name}: {ex.Message})", SafetyFailedMessage, ex);
        }
        catch (RestoreAbortException)
        {
            DeleteCandidate(candidate);
            throw;
        }
    }

    public const string SafetyFailedMessage = "A safety copy of your current data could not be made, so the restore stopped. Nothing was changed.";

    public const string InUseMessage = "The database is in use by another program — possibly an older WorkHub. Nothing was changed.";

    /// <summary>Own leftover <c>.partial</c> files in PreRestore (cleaned under exclusive Presence only).</summary>
    public void CleanOwnOrphans()
    {
        if (!files.DirectoryExists(paths.PreRestoreBackupsDirectory))
            return;
        foreach (var name in files.EnumerateFileNames(paths.PreRestoreBackupsDirectory).Where(PreRestoreNames.IsOwnCandidate))
        {
            try
            {
                files.Delete(Path.Combine(paths.PreRestoreBackupsDirectory, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Restore: leftover {name} could not be removed ({ex.GetType().Name}).");
            }
        }
    }

    private void DeleteCandidate(string candidate)
    {
        foreach (var file in StandaloneGate.SidecarSuffixes.Select(s => candidate + s).Prepend(candidate))
        {
            try
            {
                if (files.FileExists(file))
                    files.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Restore: {Path.GetFileName(file)} could not be removed ({ex.GetType().Name}); it is removed later.");
            }
        }
    }
}
