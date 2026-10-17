using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;

namespace WorkHub.Infrastructure.Restore;

/// <summary>Boundaries a test (or the crash probe) can observe, pause at or be killed at.</summary>
internal enum RestoreStage
{
    GateAcquired,
    WaitRead,
    ActingLocksAcquired,
    ExecutingPublished,
    AsFoundCopied,
    Preserved,
    MovingAsidePublished,
    PreviousFileMoved,
    MovedAsidePublished,
    CandidateMoved,
    PublishedPublished,
    CompletedPublished,
    UndoingPublished,
    RestoredFileMoved,
    RestoredAsidePublished,
    PreviousFileReturned,
    UndonePublished,
    BeforeUndoRequestedWrite,
    AfterUndoRequestedWrite,
    BeforeCancelledWrite,
    AfterCancelledWrite,
    BeforeCompletedWrite,
}

internal enum ExecutionKind
{
    /// <summary>The operation reached a terminal state (Aborted, Cancelled, Undone); a normal start follows.</summary>
    Terminal,

    /// <summary><c>Published</c>: the normal initializer runs next (X11).</summary>
    Proceed,

    /// <summary>Stopped in a non-terminal state; this process becomes its owner window.</summary>
    Stopped,

    /// <summary>The files contradict the journal: fail closed.</summary>
    Recovery,
}

internal sealed record ExecutionResult(ExecutionKind Kind, RestoreJournal Journal, string? Message = null, RestoreOwnerSituation? Situation = null);

/// <summary>
/// Restore X0–X10 and Put back U0–U6 (Phase 10 §8.1, §9.3). The caller holds G, exclusive P and R and has re-read the
/// journal and file facts under all three. Every intent is published before the moves that depend on it; every move uses
/// <c>overwrite:false</c>; every SQLite connection before X11 is unpooled.
/// </summary>
internal sealed class RestoreExecutor(
    WorkHubPaths paths,
    IRestoreFileOps files,
    RestoreJournalStore store,
    SchemaReference references,
    IVolumeProbe volumes,
    IAppLog log,
    TimeProvider time,
    Action<RestoreStage>? hook,
    IRestoreStartupUi ui)
{
    public const string ClassChangedMessage = "Your current data changed since you confirmed. Nothing was changed.";
    public const string CandidateChangedMessage = "The copy of the backup that you confirmed changed or failed its check, so the restore stopped. Nothing was changed.";
    public const string UnsupportedMessagePrefix = "The restore stopped before changing anything. ";
    public const string PutBackInUseMessage = "Put back could not finish: a file is in use.";
    public const string PublicationFailedMessage = "Your data was set aside, but the restored backup could not be put in place.";

    private string OpDir(RestoreJournal j) => paths.RestoreOperationDirectory(j.Op);

    private string PreviousDir(RestoreJournal j) => Path.Combine(OpDir(j), RestoreNames.Previous);

    private string RestoredDir(RestoreJournal j) => Path.Combine(OpDir(j), RestoreNames.Restored);

    private string CandidatePath(RestoreJournal j) => Path.Combine(OpDir(j), RestoreNames.Candidate);

    private string DataFile(string name) => Path.Combine(paths.DataDirectory, name);

    private RestoreJournal Publish(RestoreJournal next)
    {
        store.Write(next);
        log.Info($"Restore {next.Op}: {next.State} (revision {next.Revision}).");
        return next;
    }

    // ---- restore ---------------------------------------------------------------------------------------------------

    /// <summary>X0–X10 from <c>Confirmed</c> or an interrupted <c>Executing</c>.</summary>
    public ExecutionResult Execute(RestoreJournal journal)
    {
        // X0: filesystem support at execution time.
        var volume = new VolumeCheck(paths, volumes).Check(journal.Op);
        if (!volume.IsSupported)
            return Abort(journal, $"filesystem: {volume.LogDetail}", UnsupportedMessagePrefix + volume.Message);
        if (journal.Candidate is not { } candidate || journal.Acknowledgement is not { } acknowledged)
            return Abort(journal, "candidate or acknowledgement missing", CandidateChangedMessage);

        // X1: Executing, own PreRestore leftovers, candidate revalidation (ID, size, SHA-256, full check, Current/Older).
        ui.ShowProgress("Restoring: checking the backup…", closeBlocked: false);
        journal = Publish(journal.Next(RestoreState.Executing, time.GetUtcNow()));
        hook?.Invoke(RestoreStage.ExecutingPublished);
        var backupFiles = new BackupFileOps();
        new CurrentDataPreserver(paths, references, backupFiles, log).CleanOwnOrphans();
        var stager = new CandidateStager(files, new BackupValidator(references, backupFiles), references.LatestVersion);
        if (stager.Revalidate(CandidatePath(journal), candidate, fullCheck: true) is { } candidateProblem)
            return Abort(journal, candidateProblem, CandidateChangedMessage);

        // X2: as-found inventory by the file system only — evidence, never used to drive moves.
        var unexpected = files.List(paths.DataDirectory)
            .Where(f => f.Name.StartsWith(RestoreNames.Database, StringComparison.OrdinalIgnoreCase)
                        && (f.FileId == FileFact.DirectoryId || !RestoreNames.DatabaseSet.Contains(f.Name.ToLowerInvariant())))
            .Select(f => f.Name)
            .ToList();
        if (unexpected.Count > 0)
            return Abort(journal, $"unexpected files in Data: {string.Join(", ", unexpected)}", "The data folder holds unexpected database files, so the restore stopped. Nothing was changed.");
        var asFound = RestoreFacts.CurrentSet(paths, files);
        journal = Publish(journal.Next(RestoreState.Executing, time.GetUtcNow()) with { AsFoundInventory = asFound });

        // X3: byte copies of the set before any SQLite open, if sidecars exist.
        var hasSidecars = asFound.Any(f => RestoreNames.IsSidecar(f.Name));
        if (hasSidecars)
        {
            ui.ShowProgress("Restoring: keeping your current files exactly as found…", closeBlocked: false);
            if (CopyAsFound(journal, asFound) is { } copyProblem)
                return Abort(journal, copyProblem, "Your current files could not be copied as found, so the restore stopped. Nothing was changed.");
            journal = Publish(journal.Next(RestoreState.Executing, time.GetUtcNow()) with { AsFound = true });
        }

        hook?.Invoke(RestoreStage.AsFoundCopied);

        // X4: execution-time classification (ReadWrite: SQLite may consume sidecars here), acknowledgement, preservation.
        ui.ShowProgress("Restoring: making a safety copy of your current data…", closeBlocked: false);
        DataClassification execution;
        try
        {
            execution = new CurrentDataClassifier(references).ClassifyForExecution(paths.DatabaseFile);
        }
        catch (CurrentDataBusyException ex)
        {
            return Abort(journal, $"current data busy ({ex.InnerException?.GetType().Name})", CurrentDataPreserver.InUseMessage);
        }

        if (!AcknowledgementRule.Allows(acknowledged, execution))
            return Abort(journal with { ExecutionClass = execution }, $"class changed: confirmed {acknowledged}, found {execution}", ClassChangedMessage);

        SafetyRecord safety;
        try
        {
            safety = new CurrentDataPreserver(paths, references, backupFiles, log).Preserve(execution, time.GetUtcNow());
        }
        catch (RestoreAbortException ex)
        {
            return Abort(journal with { ExecutionClass = execution }, ex.Reason, ex.Message);
        }

        journal = Publish(journal.Next(RestoreState.Executing, time.GetUtcNow()) with { ExecutionClass = execution, Safety = safety });
        hook?.Invoke(RestoreStage.Preserved);

        // X5: refresh the inventory after every X4 connection closed; the main file must be the one found at X2.
        var previous = RestoreFacts.CurrentSet(paths, files);
        var asFoundMain = asFound.FirstOrDefault(f => f.Name == RestoreNames.Database);
        var previousMain = previous.FirstOrDefault(f => f.Name == RestoreNames.Database);
        if (asFoundMain?.FileId != previousMain?.FileId)
            return Abort(journal, "the database file was replaced between X2 and X5", "The database file was replaced while the restore was preparing, so it stopped. Nothing was changed.");

        ui.ShowProgress("Restoring: moving your current data aside…", closeBlocked: true);
        journal = Publish(journal.Next(RestoreState.MovingAside, time.GetUtcNow()) with { PreviousInventory = previous });
        hook?.Invoke(RestoreStage.MovingAsidePublished);

        // X6: sidecars first, then the main file; on failure, this run's moves are undone (newest first) → Aborted.
        files.CreateDirectory(PreviousDir(journal));
        var moved = new List<InventoryFile>();
        foreach (var file in MoveOrder(previous))
        {
            try
            {
                RestoreIo.WithRetries(() => files.Move(DataFile(file.Name), Path.Combine(PreviousDir(journal), file.Name)));
                moved.Add(file);
                hook?.Invoke(RestoreStage.PreviousFileMoved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"Restore {journal.Op}: moving {file.Name} aside failed ({ex.GetType().Name}); moving back {moved.Count} file(s).");
                return RollBackMoveAside(journal, moved, ex);
            }
        }

        return FinishMoveAside(journal);
    }

    /// <summary>R10: a consistent <c>MovingAside</c> found at a start is finished forward (no same-run roll back).</summary>
    public ExecutionResult RollForwardMoveAside(RestoreJournal journal)
    {
        ui.ShowProgress("Restoring: finishing moving your current data aside…", closeBlocked: true);
        files.CreateDirectory(PreviousDir(journal));
        foreach (var file in MoveOrder(journal.PreviousInventory!).Where(f => files.FileExists(DataFile(f.Name))))
        {
            try
            {
                RestoreIo.WithRetries(() => files.Move(DataFile(file.Name), Path.Combine(PreviousDir(journal), file.Name)));
                hook?.Invoke(RestoreStage.PreviousFileMoved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Stopped(journal, RestoreOwnerSituation.MovingAside, $"Restore could not finish moving your current data aside: a file is in use ({ex.GetType().Name}).");
            }
        }

        return FinishMoveAside(journal);
    }

    private ExecutionResult RollBackMoveAside(RestoreJournal journal, List<InventoryFile> moved, Exception cause)
    {
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            var file = moved[i];
            try
            {
                RestoreIo.WithRetries(() => files.Move(Path.Combine(PreviousDir(journal), file.Name), DataFile(file.Name)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Error($"Restore {journal.Op}: moving {file.Name} back failed; staying MovingAside.", ex);
                return Stopped(journal, RestoreOwnerSituation.MovingAside, "Restore could not move your data aside and could not move every file back. Use Retry; nothing was deleted.");
            }
        }

        var message = RestoreIo.IsSharingViolation(cause) ? CurrentDataPreserver.InUseMessage : "Your current data could not be moved aside, so the restore stopped. Nothing was changed.";
        return Abort(journal, $"move aside failed: {cause.GetType().Name}", message);
    }

    private ExecutionResult FinishMoveAside(RestoreJournal journal)
    {
        // X7: Data\ must hold no database file now.
        var facts = RestoreFacts.Read(paths, files, journal.Op);
        if (facts.Data.Count != 0 || !RestoreReconciler.SameSet(facts.Previous, journal.PreviousInventory!))
            return new ExecutionResult(ExecutionKind.Recovery, journal, "After moving your data aside, the files are not as expected.");
        journal = Publish(journal.Next(RestoreState.MovedAside, time.GetUtcNow()));
        hook?.Invoke(RestoreStage.MovedAsidePublished);
        return Publication(journal);
    }

    /// <summary>X8–X10 from <c>MovedAside</c> (or only X10 when the rename happened but the journal is behind).</summary>
    public ExecutionResult Publication(RestoreJournal journal)
    {
        ui.ShowProgress("Restoring: putting the backup in place…", closeBlocked: true);
        var candidate = journal.Candidate!;
        var main = DataFile(RestoreNames.Database);
        if (!(files.FileExists(main) && files.FileId(main) == candidate.FileId))
        {
            // X8: the staged copy must still be what was confirmed.
            var stager = new CandidateStager(files, new BackupValidator(references, new BackupFileOps()), references.LatestVersion);
            if (stager.Revalidate(CandidatePath(journal), candidate, fullCheck: false) is { } problem)
            {
                log.Error($"Restore {journal.Op}: X8 failed ({problem}); staying MovedAside.");
                return Stopped(journal, RestoreOwnerSituation.MovedAside, $"{PublicationFailedMessage} ({problem}) Put back your previous data, or Retry.");
            }

            // X9
            try
            {
                RestoreIo.WithRetries(() => files.Move(CandidatePath(journal), main));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Error($"Restore {journal.Op}: X9 publication failed; staying MovedAside.", ex);
                return Stopped(journal, RestoreOwnerSituation.MovedAside, $"{PublicationFailedMessage} Put back your previous data, or Retry.");
            }

            hook?.Invoke(RestoreStage.CandidateMoved);
        }

        // X10
        journal = Publish(journal.Next(RestoreState.Published, time.GetUtcNow()));
        hook?.Invoke(RestoreStage.PublishedPublished);
        ui.ShowProgress("Restoring: opening the restored data…", closeBlocked: false);
        return new ExecutionResult(ExecutionKind.Proceed, journal);
    }

    private ExecutionResult Abort(RestoreJournal journal, string reason, string userMessage)
    {
        log.Warn($"Restore {journal.Op} aborted before anything was moved: {reason}.");
        journal = Publish(journal.Next(RestoreState.Aborted, time.GetUtcNow()) with { AbortReason = reason });
        return new ExecutionResult(ExecutionKind.Terminal, journal, userMessage);
    }

    private string? CopyAsFound(RestoreJournal journal, IReadOnlyList<InventoryFile> set)
    {
        var folder = Path.Combine(OpDir(journal), RestoreNames.AsFound);
        if (files.DirectoryExists(folder) && files.List(folder).Count > 0)
            folder = Path.Combine(OpDir(journal), $"{RestoreNames.AsFound}-{journal.Revision}"); // an earlier attempt's evidence is kept
        files.CreateDirectory(folder);
        foreach (var file in set)
        {
            var source = DataFile(file.Name);
            var target = Path.Combine(folder, file.Name);
            var before = files.Sha256(source);
            files.Copy(source, target);
            if (files.Sha256(target) != before || files.Sha256(source) != before)
                return $"as-found copy of {file.Name} differs from its source";
        }

        return null;
    }

    // ---- put back ---------------------------------------------------------------------------------------------------

    /// <summary>U0–U6 from <c>UndoRequested</c>, <c>Undoing</c> or <c>RestoredAside</c>, for the recorded origin.</summary>
    public ExecutionResult Undo(RestoreJournal journal)
    {
        var undo = journal.Undo!;
        var volume = new VolumeCheck(paths, volumes).Check(journal.Op);
        if (!volume.IsSupported)
            return Stopped(journal, RestoreOwnerSituation.PutBackStopped, "Put back could not start: " + volume.Message);

        ui.ShowProgress("Putting back your previous data…", closeBlocked: true);
        if (journal.State == RestoreState.UndoRequested)
        {
            // U1/U2: the restored set (AfterPublication: Ic + sidecars in Data\; BeforePublication: empty).
            var restored = undo.Origin == UndoOrigin.AfterPublication ? RestoreFacts.CurrentSet(paths, files) : [];
            journal = Publish(journal.Next(RestoreState.Undoing, time.GetUtcNow()) with { Undo = undo with { RestoredInventory = restored } });
            hook?.Invoke(RestoreStage.UndoingPublished);
        }

        if (journal.State == RestoreState.Undoing)
        {
            // U3: AfterPublication only; BeforePublication never creates restored\.
            if (journal.Undo!.Origin == UndoOrigin.AfterPublication)
            {
                files.CreateDirectory(RestoredDir(journal));
                foreach (var file in MoveOrder(journal.Undo.RestoredInventory!).Where(f => files.FileExists(DataFile(f.Name))))
                {
                    try
                    {
                        RestoreIo.WithRetries(() => files.Move(DataFile(file.Name), Path.Combine(RestoredDir(journal), file.Name)));
                        hook?.Invoke(RestoreStage.RestoredFileMoved);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return Stopped(journal, RestoreOwnerSituation.PutBackStopped, PutBackInUseMessage);
                    }
                }
            }

            // U4
            if (RestoreFacts.Read(paths, files, journal.Op).Data.Count != 0)
                return new ExecutionResult(ExecutionKind.Recovery, journal, "Put back found unexpected database files in the data folder.");
            journal = Publish(journal.Next(RestoreState.RestoredAside, time.GetUtcNow()));
            hook?.Invoke(RestoreStage.RestoredAsidePublished);
        }

        // U5: previous sidecars, then the main file, back into Data\.
        foreach (var file in MoveOrder(journal.PreviousInventory!).Where(f => files.FileExists(Path.Combine(PreviousDir(journal), f.Name))))
        {
            try
            {
                RestoreIo.WithRetries(() => files.Move(Path.Combine(PreviousDir(journal), file.Name), DataFile(file.Name)));
                hook?.Invoke(RestoreStage.PreviousFileReturned);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Stopped(journal, RestoreOwnerSituation.PutBackStopped, PutBackInUseMessage);
            }
        }

        // U6: Data\ = previousInventory (names + IDs); BeforePublication: the candidate is still the staged copy.
        var facts = RestoreFacts.Read(paths, files, journal.Op);
        var consistent = RestoreReconciler.SameSet(facts.Data, journal.PreviousInventory!) && facts.PreviousEmpty
                         && (journal.Undo!.Origin != UndoOrigin.BeforePublication || facts.Candidate?.FileId == journal.Candidate!.FileId);
        if (!consistent)
            return new ExecutionResult(ExecutionKind.Recovery, journal, "Put back moved your previous data, but the files are not as expected.");
        journal = Publish(journal.Next(RestoreState.Undone, time.GetUtcNow()));
        hook?.Invoke(RestoreStage.UndonePublished);
        ui.ShowProgress("Opening your previous data…", closeBlocked: false);
        return new ExecutionResult(ExecutionKind.Terminal, journal, "Your previous data was put back.");
    }

    private ExecutionResult Stopped(RestoreJournal journal, RestoreOwnerSituation situation, string message)
    {
        log.Warn($"Restore {journal.Op} stopped in {journal.State}: {message}");
        return new ExecutionResult(ExecutionKind.Stopped, journal, message, situation);
    }

    /// <summary>Sidecars first, the main file last.</summary>
    private static IEnumerable<InventoryFile> MoveOrder(IReadOnlyList<InventoryFile> set) =>
        set.OrderBy(f => f.Name == RestoreNames.Database ? 1 : 0).ThenBy(f => f.Name, StringComparer.Ordinal);
}
