using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Startup;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Backup;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// Phase 14: permanent deletion from a Project's content Trash (proposal Revision 2 with the two approved corrections).
/// <list type="number">
/// <item><b>Preview</b>: one deferred read transaction → <see cref="ProjectTrashPurgePlanner"/> → a Ready preview with its token,
/// or a typed refusal.</item>
/// <item><b>PreDelete copy</b> (under <c>Backups\PreDelete\predelete.lock</c>): the snapshot protocol pins a read snapshot, the
/// plan and token are recomputed IN that snapshot (stale: no file is written), the candidate is copied and fully validated, the
/// token is recomputed FROM the validated copy, and only then is it published as <c>workhub_predelete_v9_….db</c>.</item>
/// <item><b>Deletion</b>: ONE <c>BEGIN IMMEDIATE</c> transaction recomputes the plan and token (Project and every Workspace
/// ancestor included), then deletes notes, links, folders deepest first and the entries last, asserting each statement's row
/// count. Any difference or exception rolls everything back.</item>
/// </list>
/// <b>Cancellation boundary.</b> Cancellation is honoured until <see cref="PurgeStage.Deleting"/>: that stage is reported, then
/// the token is checked one last time, and from that check on the deletion runs to its committed or rolled-back end and that
/// result is reported (a late cancellation never turns a commit into "nothing deleted"). A cancellation before publication
/// deletes the unpublished candidate; after publication the PreDelete copy is kept and named in the result. A cancellation
/// requested before that boundary also wins over a handled backup failure that follows it (for example the copy's lock-wait
/// timeout): the result is Cancelled and the failure is logged; stale and refused results keep their own outcomes.
/// No write transaction is held while the user confirms; nothing a link points to is touched.
/// </summary>
public sealed class ProjectTrashPurge : IProjectTrashPurge
{
    private readonly WorkHubPaths _paths;
    private readonly SqliteConnectionFactory _connections;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;
    private readonly SchemaReference _references;
    private readonly IBackupFileOps _files;
    private readonly BackupValidator _validator;
    private readonly SqliteSnapshotter _snapshotter;
    private readonly ProjectTrashPurgePlanner _planner;

    public ProjectTrashPurge(WorkHubPaths paths, SqliteConnectionFactory connections, IAppLog log)
        : this(paths, connections, log, TimeProvider.System, SchemaReference.Frozen, new BackupFileOps(), SqliteProjectContentStore.MaxDepth)
    {
    }

    /// <param name="maxDepth">Ancestry walk bound (tests lower it to prove the walks fail closed).</param>
    internal ProjectTrashPurge(
        WorkHubPaths paths,
        SqliteConnectionFactory connections,
        IAppLog log,
        TimeProvider time,
        SchemaReference references,
        IBackupFileOps files,
        int maxDepth,
        Action<BackupStage>? backupHook = null,
        int lockWaitSeconds = SqliteSnapshotter.DefaultLockWaitSeconds)
    {
        _paths = paths;
        _connections = connections;
        _log = log;
        _time = time;
        _references = references;
        _files = files;
        _validator = new BackupValidator(references, files);
        _snapshotter = new SqliteSnapshotter(references, files, lockWaitSeconds, backupHook);
        _planner = new ProjectTrashPurgePlanner(maxDepth);
    }

    /// <summary>Tests only: called with "purge:notes", "purge:links", "purge:folders:{depth}" and "purge:entries" after each delete
    /// statement inside the transaction; throwing from it proves the whole deletion rolls back.</summary>
    internal Action<string>? OnWriteStep { get; set; }

    /// <summary>Tests only: deterministic gates at "backup-start", "in-snapshot", "candidate-validated", "published", "before-delete" and "committed".</summary>
    internal Action<string>? OnCheckpoint { get; set; }

    public Task<PurgePreviewResult> PreparePurgeAsync(PurgeRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var connection = _connections.OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: true);
            var plan = _planner.Plan(connection, transaction, request);
            transaction.Commit();
            return plan.Plan is { } ready ? PurgePreviewResult.Ready(ready.Preview) : PurgePreviewResult.Refused(plan.Status);
        }, cancellationToken);

    public Task<PurgeResult> PurgeAsync(PurgeRequest request, string confirmedToken, IProgress<PurgeStage>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var result = Purge(request, confirmedToken, progress, cancellationToken);
            _log.Info($"Project Trash purge ({request.Mode}): {result.Outcome}" +
                      (result.Refusal is { } refusal ? $" ({refusal})" : string.Empty) +
                      (result.Deleted is { } counts ? $", {counts.Entries} entries / {counts.Items} items deleted" : ", nothing deleted") +
                      (result.BackupFileName is { } file ? $"; PreDelete copy {file}" : string.Empty) + ".");
            return result;
        }, CancellationToken.None);

    private PurgeResult Purge(PurgeRequest request, string confirmedToken, IProgress<PurgeStage>? progress, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return new PurgeResult(PurgeOutcome.Cancelled, false);

        progress?.Report(PurgeStage.MakingBackup);
        OnCheckpoint?.Invoke("backup-start");
        var backup = MakeBackup(request, confirmedToken, cancellationToken);
        if (backup.Failure is { } failure)
        {
            // Outcome precedence: a cancellation requested before the non-cancellable stage wins over a handled backup failure
            // (such as the lock-wait timeout) that ends the wait it was requested in. The real cause stays in the log (operation
            // and failure kind only). Stale and refused results keep their own types; MakeBackup already removed unpublished staging.
            if (failure.Outcome == PurgeOutcome.BackupFailed && cancellationToken.IsCancellationRequested)
            {
                _log.Warn($"Project Trash purge: cancellation was requested and the PreDelete copy then failed ({failure.Detail}); reported as Cancelled, nothing deleted.");
                return new PurgeResult(PurgeOutcome.Cancelled, false);
            }

            return failure;
        }
        var backupFile = backup.FileName!;
        progress?.Report(PurgeStage.BackupPublished);

        if (cancellationToken.IsCancellationRequested)
            return new PurgeResult(PurgeOutcome.Cancelled, false, BackupFileName: backupFile);

        // The transition into the non-cancellable stage: reported first, then the LAST cancellation check.
        progress?.Report(PurgeStage.Deleting);
        OnCheckpoint?.Invoke("before-delete");
        if (cancellationToken.IsCancellationRequested)
            return new PurgeResult(PurgeOutcome.Cancelled, false, BackupFileName: backupFile);

        return Delete(request, confirmedToken, backupFile);
    }

    // ---- Deletion: one BEGIN IMMEDIATE transaction ---------------------------------------------------------------------

    private PurgeResult Delete(PurgeRequest request, string confirmedToken, string backupFile)
    {
        try
        {
            using var connection = _connections.OpenExisting();
            SqliteTransaction transaction;
            try
            {
                transaction = connection.BeginTransaction(deferred: false);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCodes.Busy or SqliteCodes.Locked)
            {
                return new PurgeResult(PurgeOutcome.DatabaseBusy, false, BackupFileName: backupFile);
            }

            using (transaction)
            {
                var plan = _planner.Plan(connection, transaction, request);
                if (plan.Plan is not { } ready)
                    return new PurgeResult(PurgeOutcome.Refused, false, plan.Status, BackupFileName: backupFile);
                if (!string.Equals(ready.Preview.Token, confirmedToken, StringComparison.Ordinal))
                    return new PurgeResult(PurgeOutcome.ConfirmationStale, false, BackupFileName: backupFile);

                var closure = Ids(ready.Closure);
                var project = request.ProjectNodeId;
                var notes = Execute(connection, transaction, "DELETE FROM ProjectNotes WHERE ProjectNodeId = $p AND TrashEntryId IN (SELECT value FROM json_each($ids));", project, closure);
                Expect("notes", notes, ready.Preview.Counts.Notes);
                OnWriteStep?.Invoke("purge:notes");
                var links = Execute(connection, transaction, "DELETE FROM ProjectLinks WHERE ProjectNodeId = $p AND TrashEntryId IN (SELECT value FROM json_each($ids));", project, closure);
                Expect("links", links, ready.Preview.Counts.Links);
                OnWriteStep?.Invoke("purge:links");

                // RESTRICT is checked per row as each parent is deleted: one statement per depth, deepest first.
                var folders = 0;
                foreach (var level in ready.Rows.Where(r => r.Kind == ContentKind.Folder).GroupBy(r => r.Depth).OrderByDescending(g => g.Key))
                {
                    var deleted = Execute(connection, transaction,
                        "DELETE FROM ProjectFolders WHERE ProjectNodeId = $p AND TrashEntryId IN (SELECT value FROM json_each($ids)) AND Id IN (SELECT value FROM json_each($folders));",
                        project, closure, ("$folders", Ids(level.Select(r => r.Id))));
                    Expect($"folders at depth {level.Key}", deleted, level.Count());
                    folders += deleted;
                    OnWriteStep?.Invoke($"purge:folders:{level.Key.ToString(CultureInfo.InvariantCulture)}");
                }

                var entries = Execute(connection, transaction, "DELETE FROM ProjectTrashEntries WHERE ProjectNodeId = $p AND Id IN (SELECT value FROM json_each($ids));", project, closure);
                Expect("entries", entries, ready.Closure.Count);
                OnWriteStep?.Invoke("purge:entries");

                transaction.Commit();
                OnCheckpoint?.Invoke("committed");
                return new PurgeResult(PurgeOutcome.Purged, true, Deleted: new PurgeCounts(entries, folders, links, notes), BackupFileName: backupFile);
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCodes.Busy or SqliteCodes.Locked)
        {
            return new PurgeResult(PurgeOutcome.DatabaseBusy, false, BackupFileName: backupFile);
        }
        catch (Exception ex)
        {
            // Dispose rolled the transaction back: nothing was deleted.
            _log.Error("Project Trash purge failed; nothing was deleted.", ex);
            return new PurgeResult(PurgeOutcome.Failed, false, BackupFileName: backupFile, Detail: ex.GetType().Name);
        }
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, long project, string ids, params (string Name, object Value)[] extra)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$p", project);
        command.Parameters.AddWithValue("$ids", ids);
        foreach (var (name, value) in extra)
            command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    private static void Expect(string step, int actual, int planned)
    {
        if (actual != planned)
            throw new InvalidOperationException($"Purge {step}: deleted {actual} rows, planned {planned}; nothing was deleted.");
    }

    private static string Ids(IEnumerable<long> ids) => "[" + string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture))) + "]";

    // ---- PreDelete copy -------------------------------------------------------------------------------------------------

    private sealed class StaleException(PurgeResult result) : Exception("The confirmation is stale.")
    {
        public PurgeResult Result { get; } = result;
    }

    private readonly record struct BackupStep(string? FileName, PurgeResult? Failure);

    private BackupStep MakeBackup(PurgeRequest request, string confirmedToken, CancellationToken cancellationToken)
    {
        var directory = _paths.PreDeleteBackupsDirectory;
        try
        {
            _files.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed($"the PreDelete folder could not be created ({ex.GetType().Name})");
        }

        IDisposable gate;
        try
        {
            gate = _files.OpenExclusive(_paths.PreDeleteLockFile);
        }
        catch (IOException ex) when (IoErrors.IsSharingViolation(ex))
        {
            return Failed("another WorkHub window is making a PreDelete copy");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed($"the PreDelete lock could not be opened ({ex.GetType().Name})");
        }

        using (gate)
        {
            CleanOwnCandidates(directory);

            // Never overwrite: under the lock, a name taken by an earlier copy in the same millisecond moves to the next one.
            var stamp = _time.GetUtcNow();
            string finalName, finalPath;
            while (_files.FileExists(finalPath = Path.Combine(directory, finalName = PreDeleteNames.Validated(_references.LatestVersion, stamp))))
                stamp = stamp.AddMilliseconds(1);
            var candidate = Path.Combine(directory, PreDeleteNames.CandidateName(finalName, Guid.NewGuid()));
            try
            {
                // The plan and token are checked IN the pinned snapshot that is copied; a stale confirmation writes no file.
                var facts = _snapshotter.Copy(_paths.DatabaseFile, candidate, new SnapshotRules(_references.LatestVersion, ValidationRules.Daily), (source, pin) =>
                {
                    OnCheckpoint?.Invoke("in-snapshot");
                    if (Stale(_planner.Plan(source, pin, request), confirmedToken) is { } stale)
                        throw new StaleException(stale);
                });

                if (cancellationToken.IsCancellationRequested)
                {
                    DeleteCandidate(candidate);
                    return new BackupStep(null, new PurgeResult(PurgeOutcome.Cancelled, false));
                }

                var check = _validator.CheckCandidate(candidate, facts, ValidationRules.Daily);
                if (!check.IsFullyValidated)
                {
                    DeleteCandidate(candidate);
                    return Failed($"the copy failed its check ({check.Status}: {check.Detail}) and was discarded");
                }

                // The guarantee: the validated copy itself holds exactly the confirmed rows.
                if (Stale(PlanFromCopy(candidate, request), confirmedToken) is { } copyStale)
                {
                    DeleteCandidate(candidate);
                    return new BackupStep(null, copyStale);
                }

                if (StandaloneGate.Sidecars(_files, candidate).Count > 0)
                {
                    DeleteCandidate(candidate);
                    return Failed("checking the copy left a journal file behind; it was discarded");
                }

                OnCheckpoint?.Invoke("candidate-validated");
                if (cancellationToken.IsCancellationRequested)
                {
                    DeleteCandidate(candidate); // unpublished staging only
                    return new BackupStep(null, new PurgeResult(PurgeOutcome.Cancelled, false));
                }

                var length = _files.GetLength(candidate);
                try
                {
                    _files.Move(candidate, finalPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DeleteCandidate(candidate);
                    return Failed($"the checked copy could not be put in place ({ex.GetType().Name})");
                }

                if (!PublishedIntact(finalPath, length))
                    return Failed($"{finalName} changed, disappeared or could not be read right after it was published (kept as it is)");

                OnCheckpoint?.Invoke("published");
                return new BackupStep(finalName, null);
            }
            catch (StaleException ex)
            {
                DeleteCandidate(candidate);
                return new BackupStep(null, ex.Result);
            }
            catch (BackupStepException ex)
            {
                DeleteCandidate(candidate);
                return Failed(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                DeleteCandidate(candidate);
                _log.Error("The PreDelete copy failed.", ex);
                return Failed(ex.GetType().Name);
            }
        }

        static BackupStep Failed(string detail) => new(null, new PurgeResult(PurgeOutcome.BackupFailed, false, Detail: detail));
    }

    private static PurgeResult? Stale(PurgePlanResult plan, string confirmedToken) =>
        plan.Plan is not { } ready ? new PurgeResult(PurgeOutcome.Refused, false, plan.Status)
        : !string.Equals(ready.Preview.Token, confirmedToken, StringComparison.Ordinal) ? new PurgeResult(PurgeOutcome.ConfirmationStale, false)
        : null;

    private PurgePlanResult PlanFromCopy(string candidate, PurgeRequest request)
    {
        using var connection = new SqliteConnectionFactory(candidate).Open(SqliteOpenMode.ReadOnly, pooling: false);
        using var transaction = connection.BeginTransaction(deferred: true);
        return _planner.Plan(connection, transaction, request);
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

    /// <summary>This feature's own leftover candidates (and sidecars) of an interrupted copy, under the PreDelete lock.</summary>
    private void CleanOwnCandidates(string directory)
    {
        IReadOnlyList<string> names;
        try
        {
            names = _files.EnumerateFileNames(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var name in names.Where(PreDeleteNames.IsOwnCandidate))
        {
            try
            {
                _files.Delete(Path.Combine(directory, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"PreDelete copy: leftover {name} could not be removed ({ex.GetType().Name}).");
            }
        }
    }

    private void DeleteCandidate(string candidate)
    {
        foreach (var file in StandaloneGate.SidecarSuffixes.Select(s => candidate + s).Prepend(candidate))
        {
            try
            {
                if (_files.FileExists(file))
                    _files.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"PreDelete copy: {Path.GetFileName(file)} could not be removed ({ex.GetType().Name}); it is removed by the next purge.");
            }
        }
    }
}
