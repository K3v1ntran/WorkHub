using System.Globalization;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Startup;

namespace WorkHub.Infrastructure.Backup;

/// <summary>
/// Startup daily database backup (Phase 9 §4–§7). One run, under the per-data-root lock:
/// orphan cleanup (D8) → done-today (quick level) → snapshot S1–S8 → candidate-level validation → publish by rename
/// (never overwriting) → verify → retention (evidence rule, full level). Backup problems are results, never exceptions.
/// The live database is only read.
/// </summary>
public sealed class DailyBackupService : IDailyBackup
{
    private static readonly TimeSpan SlowWarning = TimeSpan.FromSeconds(10);

    private readonly WorkHubPaths _paths;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;
    private readonly JstCalendar _calendar;
    private readonly SchemaReference _references;
    private readonly IBackupFileOps _files;
    private readonly Action<BackupStage>? _hook;
    private readonly BackupValidator _validator;
    private readonly SqliteSnapshotter _snapshotter;

    public DailyBackupService(WorkHubPaths paths, IAppLog log)
        : this(paths, log, TimeProvider.System, JstCalendar.Resolve(), SchemaReference.Frozen, new BackupFileOps())
    {
    }

    internal DailyBackupService(
        WorkHubPaths paths,
        IAppLog log,
        TimeProvider time,
        JstCalendar calendar,
        SchemaReference references,
        IBackupFileOps files,
        Action<BackupStage>? hook = null,
        int lockWaitSeconds = SqliteSnapshotter.DefaultLockWaitSeconds)
    {
        _paths = paths;
        _log = log;
        _time = time;
        _calendar = calendar;
        _references = references;
        _files = files;
        _hook = hook;
        _validator = new BackupValidator(references, files);
        _snapshotter = new SqliteSnapshotter(references, files, lockWaitSeconds, hook);
    }

    public DailyBackupResult Run()
    {
        // The day key is captured once: a run that crosses midnight still backs up the day it started on.
        var started = _time.GetUtcNow();
        var tokyoNow = _calendar.ToTokyo(started);
        var today = DateOnly.FromDateTime(tokyoNow.DateTime);
        if (_calendar.UsesFixedOffset)
            _log.Warn("Daily backup: the Asia/Tokyo time zone is not available; using a fixed +09:00 offset.");

        try
        {
            _files.CreateDirectory(_paths.DailyBackupsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(DailyBackupFailure.FolderUnavailable, $"the Daily backups folder could not be created ({ex.GetType().Name})");
        }

        IDisposable dailyLock;
        try
        {
            dailyLock = _files.OpenExclusive(_paths.DailyBackupLockFile);
        }
        catch (IOException ex) when (IoErrors.IsSharingViolation(ex))
        {
            _log.Info("Daily backup skipped: another WorkHub instance is creating today's backup.");
            return new DailyBackupResult(DailyBackupOutcome.SkippedBusy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(DailyBackupFailure.FolderUnavailable, $"the daily backup lock could not be opened ({ex.GetType().Name})");
        }

        using (dailyLock)
        {
            _hook?.Invoke(BackupStage.LockAcquired);
            return RunLocked(today, tokyoNow, started);
        }
    }

    private DailyBackupResult RunLocked(DateOnly today, DateTimeOffset tokyoNow, DateTimeOffset started)
    {
        var timings = new List<string>();
        var stageStart = started;
        void Stage(string name)
        {
            var now = _time.GetUtcNow();
            timings.Add($"{name} {(now - stageStart).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
            stageStart = now;
        }

        CleanOwnOrphans();
        _hook?.Invoke(BackupStage.OrphansCleaned);

        IReadOnlyList<string> names;
        try
        {
            names = _files.EnumerateFileNames(_paths.DailyBackupsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(DailyBackupFailure.FolderUnavailable, $"the Daily backups folder could not be read ({ex.GetType().Name})");
        }

        var future = DailyBackupPolicy.FutureDated(names, today);
        if (future.Count > 0)
            _log.Warn($"Daily backup: {future.Count} backup(s) are dated after today ({today:yyyy-MM-dd} JST); they are kept and not counted (clock change?).");

        // Re-checked under the lock, so two instances never publish two backups for one day.
        var eligible = DailyBackupPolicy.Eligible(names, today);
        var doneBy = eligible.Where(n => n.Day == today).FirstOrDefault(n => Check(n, BackupCheckLevel.Quick).PassedQuick);
        Stage("discovery");
        if (doneBy is not null)
        {
            _log.Info($"Daily backup: today's backup already exists ({doneBy.FileName}).");
            return new DailyBackupResult(DailyBackupOutcome.AlreadyDone, FileName: doneBy.FileName);
        }

        var latest = _references.LatestVersion;
        var finalName = DailyBackupPolicy.FileName(today, latest, tokyoNow);
        var finalPath = Path.Combine(_paths.DailyBackupsDirectory, finalName);
        var candidatePath = Path.Combine(_paths.DailyBackupsDirectory, DailyBackupPolicy.CandidateName(finalName, Guid.NewGuid()));

        try
        {
            var facts = _snapshotter.Copy(_paths.DatabaseFile, candidatePath, new SnapshotRules(latest, ValidationRules.Daily));
            Stage("snapshot");

            var check = _validator.CheckCandidate(candidatePath, facts, ValidationRules.Daily);
            Stage("validation");
            if (!check.IsFullyValidated)
            {
                DeleteCandidate(candidatePath);
                var failure = check.Status == BackupStatus.NotStandalone ? DailyBackupFailure.NotStandalone : DailyBackupFailure.CandidateInvalid;
                return Failed(failure, $"the copy failed its check ({check.Status}: {check.Detail}) and was discarded");
            }

            _hook?.Invoke(BackupStage.CandidateValidated);
            var length = _files.GetLength(candidatePath);

            try
            {
                _files.Move(candidatePath, finalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeleteCandidate(candidatePath);
                var failure = ex is IOException io && IoErrors.IsDiskFull(io) ? DailyBackupFailure.DiskFull : DailyBackupFailure.PublishFailed;
                return Failed(failure, $"the checked copy could not be put in place ({ex.GetType().Name})");
            }

            _hook?.Invoke(BackupStage.Published);

            if (!PublishedIntact(finalPath, length))
            {
                // Kept as it is (never deleted): a later quick check decides whether today is done.
                return Failed(DailyBackupFailure.PublishFailed, $"{finalName} changed, disappeared or could not be read right after it was published");
            }

            _hook?.Invoke(BackupStage.Verified);

            var retention = Prune(today, finalName);
            Stage("retention");

            var elapsed = _time.GetUtcNow() - started;
            _log.Info($"Daily backup created: {finalName} ({length} bytes). Retention: {retention.Deleted} removed, " +
                      $"{retention.DeleteFailures} could not be removed, {retention.Preserved} kept because they did not pass full validation" +
                      $"{(retention.InsufficientEvidence ? ", nothing removed (fewer than 3 fully validated backups)" : string.Empty)}. " +
                      $"Timings: {string.Join(", ", timings)}.");
            if (elapsed > SlowWarning)
                _log.Warn($"Daily backup took {elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s (startup waited for it).");

            return new DailyBackupResult(DailyBackupOutcome.Created, FileName: finalName, Retention: retention);
        }
        catch (BackupStepException ex)
        {
            DeleteCandidate(candidatePath);
            return Failed(ex.Failure, ex.Message);
        }
        catch (Exception ex) when (ex is not SimulatedCrashException)
        {
            DeleteCandidate(candidatePath);
            var failure = ex switch
            {
                IOException io when IoErrors.IsDiskFull(io) => DailyBackupFailure.DiskFull,
                IOException or UnauthorizedAccessException => DailyBackupFailure.FolderUnavailable,
                _ => DailyBackupFailure.Unexpected,
            };
            _log.Error("Daily backup failed unexpectedly.", ex);
            return Failed(failure, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Retention (§7). Runs only after this run published a candidate that passed candidate-level validation. Deletes a file
    /// only after three newer fully validated backups were established in this run, and only if the file itself is fully
    /// validated now. Everything else is kept. Deletes run oldest first; a failed delete is logged and skipped.
    /// </summary>
    private RetentionSummary Prune(DateOnly today, string publishedName)
    {
        IReadOnlyList<string> names;
        try
        {
            names = _files.EnumerateFileNames(_paths.DailyBackupsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Daily backup retention skipped: the folder could not be read ({ex.GetType().Name}).");
            return new RetentionSummary(0, 0, 0, InsufficientEvidence: true);
        }

        var plan = DailyBackupPolicy.PlanRetention(
            DailyBackupPolicy.Eligible(names, today),
            publishedName,
            n => Check(n, BackupCheckLevel.Full).IsFullyValidated);

        foreach (var kept in plan.Preserved)
            _log.Warn($"Daily backup retention: {kept.FileName} did not pass full validation and is kept.");
        if (plan.InsufficientEvidence)
        {
            _log.Warn($"Daily backup retention: only {plan.Evidence.Count} fully validated backup(s); nothing removed.");
            return new RetentionSummary(0, 0, plan.Preserved.Count, InsufficientEvidence: true);
        }

        int deleted = 0, failed = 0;
        foreach (var old in plan.Delete)
        {
            try
            {
                _files.Delete(Path.Combine(_paths.DailyBackupsDirectory, old.FileName));
                deleted++;
                _hook?.Invoke(BackupStage.Pruned);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                _log.Warn($"Daily backup retention: {old.FileName} could not be removed ({ex.GetType().Name}); it is kept and retried later.");
            }
        }

        return new RetentionSummary(deleted, failed, plan.Preserved.Count, InsufficientEvidence: false);
    }

    private bool PublishedIntact(string finalPath, long length)
    {
        try
        {
            return _files.FileExists(finalPath) && _files.GetLength(finalPath) == length && StandaloneGate.Sidecars(_files, finalPath).Count == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>D8: only this feature's own leftover candidates (and their sidecars) in Daily, under the lock.</summary>
    private void CleanOwnOrphans()
    {
        IReadOnlyList<string> names;
        try
        {
            names = _files.EnumerateFileNames(_paths.DailyBackupsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // reported by the enumeration that follows
        }

        var removed = 0;
        foreach (var name in names.Where(DailyBackupPolicy.IsOwnCandidate))
        {
            try
            {
                _files.Delete(Path.Combine(_paths.DailyBackupsDirectory, name));
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Daily backup: leftover {name} could not be removed ({ex.GetType().Name}).");
            }
        }

        if (removed > 0)
            _log.Info($"Daily backup: removed {removed} leftover file(s) of an interrupted earlier backup.");
    }

    private BackupCheckResult Check(DailyBackupName name, BackupCheckLevel level) =>
        _validator.Check(Path.Combine(_paths.DailyBackupsDirectory, name.FileName), level, name.Version, ValidationRules.Daily);

    private void DeleteCandidate(string candidatePath)
    {
        foreach (var file in StandaloneGate.SidecarSuffixes.Select(s => candidatePath + s).Prepend(candidatePath))
        {
            try
            {
                if (_files.FileExists(file))
                    _files.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Daily backup: {Path.GetFileName(file)} could not be removed ({ex.GetType().Name}); it is removed on a later start.");
            }
        }
    }

    private DailyBackupResult Failed(DailyBackupFailure failure, string detail)
    {
        if (failure == DailyBackupFailure.SourceCheck)
            _log.Error($"Daily backup not made: the database failed a check while being backed up ({detail}). No older backup was removed.");
        else
            _log.Warn($"Daily backup not made ({failure}): {detail}. No older backup was removed; WorkHub tries again next start.");
        return DailyBackupResult.Fail(failure, detail);
    }
}
