using System.Reflection;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;

namespace WorkHub.Infrastructure.Restore;

/// <summary>
/// Request: reservation, staging, confirmation, publication (Phase 10 §6.1). Runs in a running window (which holds
/// shared Presence). It tries the Restore lock once and never waits for G or P. R is held from the precheck through the
/// confirmation dialog until <c>Confirmed</c> or <c>Cancelled</c> is published.
/// </summary>
public sealed class RestoreRequester : IRestoreRequester
{
    private readonly WorkHubPaths _paths;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;
    private readonly SchemaReference _references;
    private readonly IVolumeProbe _volumes;
    private readonly Func<InstanceLocks, IRestoreFileOps> _files;

    public RestoreRequester(WorkHubPaths paths, IAppLog log)
        : this(paths, log, TimeProvider.System, SchemaReference.Frozen, new VolumeProbe(), null)
    {
    }

    internal RestoreRequester(WorkHubPaths paths, IAppLog log, TimeProvider time, SchemaReference references, IVolumeProbe volumes, Func<InstanceLocks, IRestoreFileOps>? files)
    {
        _paths = paths;
        _log = log;
        _time = time;
        _references = references;
        _volumes = volumes;
        _files = files ?? (locks => new RestoreFileOps(paths, () => locks.HoldsRestoreLock));
    }

    /// <summary>Tests only: runs while R is held, after staging and before the request is returned.</summary>
    internal Action? AfterStaging { get; init; }

    public Task<RestoreRequestStart> BeginAsync(BackupEntry entry, CancellationToken cancellationToken = default) =>
        Task.Run(() => Begin(entry), cancellationToken);

    private RestoreRequestStart Begin(BackupEntry entry)
    {
        var locks = new InstanceLocks(_paths);
        try
        {
            // Q0: reserve first, then check for another active operation.
            if (!locks.TryRestoreLock())
            {
                locks.Dispose();
                _log.Info("Restore request refused: the Restore lock is busy.");
                return new RestoreRequestStart(null, "Another WorkHub window is preparing or performing a restore.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            locks.Dispose();
            return new RestoreRequestStart(null, $"The restore folder could not be prepared ({ex.GetType().Name}).");
        }

        var files = _files(locks);
        var store = new RestoreJournalStore(_paths, files);
        RestoreJournal? journal = null;
        try
        {
            // Q1: precheck under R (cleanup of what is provably dead; any unfinished or inconsistent op refuses).
            var scan = new RestoreScanner(_paths, files, store, _log, _time).Scan(cleanup: true);
            if (scan.Problem is { } problem)
                throw new RestoreRefusedException($"An earlier restore needs attention: {problem} See \"Unfinished restore\" in the README.");
            if (scan.NonTerminal is not null)
                throw new RestoreRefusedException("A restore is pending or unfinished. Finish it first (restart WorkHub).");

            var (sourcePath, nameVersion) = SourceOf(entry);
            if (entry.Size >= 0 && files.FileExists(sourcePath) && files.GetLength(sourcePath) != entry.Size)
                throw new RestoreRefusedException("The selected backup changed since the list was loaded. Refresh the list and select it again.");

            // Q2: filesystem support, then the reservation journal.
            var volume = new VolumeCheck(_paths, _volumes).Check(null);
            if (!volume.IsSupported)
            {
                _log.Warn($"Restore refused by the filesystem check: {volume.LogDetail}.");
                throw new RestoreRefusedException(volume.Message!);
            }

            var now = _time.GetUtcNow();
            var op = RestoreOpId.New(now);
            files.CreateDirectory(_paths.RestoreOperationDirectory(op));
            journal = new RestoreJournal
            {
                Op = op,
                State = RestoreState.Staged,
                Revision = 1,
                CreatedUtc = now,
                UpdatedUtc = now,
                Build = new RestoreBuildInfo(AppVersion, _references.LatestVersion),
                DataDir = RestoreFacts.DataDirectoryIdentity(_paths),
                Fs = volume.Facts!,
                Source = new RestoreSource(entry.Folder, entry.FileName, entry.Kind, nameVersion, entry.Taken, entry.Size, string.Empty),
            };
            store.Write(journal);

            var opVolume = new VolumeCheck(_paths, _volumes).Check(op);
            if (!opVolume.IsSupported)
                throw new RestoreRefusedException(opVolume.Message!);

            // Q3: stage and validate the candidate; classify the current data ReadOnly (never modifies anything).
            var candidatePath = Path.Combine(_paths.RestoreOperationDirectory(op), RestoreNames.Candidate);
            var stager = new CandidateStager(files, new BackupValidator(_references, new BackupFileOps()), _references.LatestVersion);
            var (candidate, sourceSize, sourceSha) = stager.Stage(sourcePath, nameVersion, candidatePath);
            DataClassification requestClass;
            try
            {
                requestClass = new CurrentDataClassifier(_references).ClassifyReadOnly(_paths.DatabaseFile);
            }
            catch (CurrentDataBusyException)
            {
                throw new RestoreRefusedException("Your current database is busy (another WorkHub window is writing). Try again in a moment.");
            }

            var counts = RestoreContents.Read(candidatePath);
            journal = journal.Next(RestoreState.Staged, _time.GetUtcNow()) with
            {
                Source = journal.Source with { Size = sourceSize, Sha256 = sourceSha },
                Candidate = candidate,
                RequestClass = requestClass,
            };
            store.Write(journal);
            AfterStaging?.Invoke();
            _log.Info($"Restore staged {op}: {entry.Folder}/{entry.FileName}, schema v{candidate.SchemaVersion} ({candidate.Compatibility}); current data {requestClass}.");

            var facts = new RestoreConfirmationFacts(op, entry, candidate.SchemaVersion, candidate.Compatibility, _references.LatestVersion, candidate.Size, counts, requestClass);
            return new RestoreRequestStart(new RestoreRequest(_paths, locks, files, store, journal, facts, _time, _log), null);
        }
        catch (Exception ex) when (ex is RestoreRefusedException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Q5a for any error after the reservation: delete the candidate, publish Cancelled, release R.
            if (journal is not null)
                RestoreRequest.CancelStaged(files, store, journal, _paths, _time, _log);
            locks.Dispose();
            var message = ex is RestoreRefusedException ? ex.Message : $"The restore could not be prepared ({ex.GetType().Name}). Nothing was changed.";
            _log.Warn($"Restore request refused: {message}");
            return new RestoreRequestStart(null, message);
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    private (string Path, int? NameVersion) SourceOf(BackupEntry entry)
    {
        if (entry.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || entry.FileName.Contains("..", StringComparison.Ordinal))
            throw new RestoreRefusedException("The selected file is not a WorkHub backup.");

        switch (entry.Folder, entry.Kind)
        {
            case (BackupFolder.Daily, BackupKind.Daily) when DailyBackupPolicy.TryParse(entry.FileName, out var daily):
                return (Path.Combine(_paths.DailyBackupsDirectory, entry.FileName), daily.Version);
            case (BackupFolder.Migrations, BackupKind.PreMigration) when DailyBackupPolicy.TryParseMigration(entry.FileName, out var from, out _, out _):
                return (Path.Combine(_paths.MigrationBackupsDirectory, entry.FileName), from);
            case (BackupFolder.PreRestore, BackupKind.PreRestore or BackupKind.Preserved) when PreRestoreNames.TryParse(entry.FileName, out _, out var version, out _):
                return (Path.Combine(_paths.PreRestoreBackupsDirectory, entry.FileName), version);
            default:
                throw new RestoreRefusedException("Only recognized backups in Daily, Migrations or PreRestore can be restored.");
        }
    }

    internal static string AppVersion =>
        typeof(RestoreRequester).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}

/// <summary>A reservation holding R (Q4) until Confirm (Q5b) or Cancel (Q5a).</summary>
internal sealed class RestoreRequest(
    WorkHubPaths paths,
    InstanceLocks locks,
    IRestoreFileOps files,
    RestoreJournalStore store,
    RestoreJournal journal,
    RestoreConfirmationFacts facts,
    TimeProvider time,
    IAppLog log) : IRestoreRequest
{
    private readonly Lock _gate = new();
    private bool _decided;

    public RestoreConfirmationFacts Facts { get; } = facts;

    public Task<string> ConfirmAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            if (_decided)
                throw new InvalidOperationException("The request was already decided.");
            _decided = true;
            try
            {
                var now = time.GetUtcNow();
                store.Write(journal.Next(RestoreState.Confirmed, now) with { ConfirmedUtc = now, Acknowledgement = journal.RequestClass });
                log.Info($"Restore {journal.Op} confirmed (acknowledged current data {journal.RequestClass}).");
                return journal.Op;
            }
            catch
            {
                CancelStaged(files, store, journal, paths, time, log);
                throw;
            }
            finally
            {
                locks.Dispose();
            }
        }
    });

    /// <summary>Tests only: the requester "dies" with the dialog open — R is released by the OS, nothing is decided.</summary>
    internal void ReleaseLockWithoutDeciding()
    {
        lock (_gate)
        {
            _decided = true;
            locks.Dispose();
        }
    }

    public Task CancelAsync() => Task.Run(Cancel);

    public void Dispose() => Cancel();

    private void Cancel()
    {
        lock (_gate)
        {
            if (_decided)
                return;
            _decided = true;
            try
            {
                CancelStaged(files, store, journal, paths, time, log);
            }
            finally
            {
                locks.Dispose();
            }
        }
    }

    /// <summary>Q5a: delete the candidate, then publish <c>Cancelled{cancelledFrom: Staged}</c>. A failure leaves a dead
    /// <c>Staged</c> journal that the next R holder abandons.</summary>
    internal static void CancelStaged(IRestoreFileOps files, RestoreJournalStore store, RestoreJournal journal, WorkHubPaths paths, TimeProvider time, IAppLog log)
    {
        try
        {
            var candidate = Path.Combine(paths.RestoreOperationDirectory(journal.Op), RestoreNames.Candidate);
            if (files.FileExists(candidate))
                files.Delete(candidate);
            store.Write(journal.Next(RestoreState.Cancelled, time.GetUtcNow()) with { CancelledFrom = RestoreState.Staged });
            log.Info($"Restore {journal.Op} cancelled before confirmation.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Restore {journal.Op}: cancelling left it Staged ({ex.GetType().Name}); the next start abandons it.");
        }
    }
}
