using System.ComponentModel;
using System.Diagnostics;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Launching;

namespace WorkHub.Infrastructure.Restore;

/// <summary>
/// The Phase 10 startup protocol and owner-window actions for one process (§5, §8.4, §10). One instance per process; its
/// locks live as long as the process (shared Presence is the "I am running" registration).
/// <para>
/// Every start: G (blocking poll, holding nothing) → R try for cleanup only (released) → scan. No unfinished op: shared P
/// under G → database initialization → release G (R6). An unfinished op: wait holding G ONLY (never R) until exclusive P
/// is free, then try R, re-read journal and file facts under all three, reconcile and act on the fresh state only.
/// </para>
/// </summary>
public sealed class RestoreCoordinator : IRestoreStartup, IRestoreOwner, IDisposable
{
    private readonly WorkHubPaths _paths;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;
    private readonly SchemaReference _references;
    private readonly IVolumeProbe _volumes;
    private readonly IProcessStarter _starter;
    private readonly Action<RestoreStage>? _hook;
    private readonly TimeSpan _poll;
    private readonly InstanceLocks _locks;
    private readonly IRestoreFileOps _files;
    private readonly RestoreJournalStore _store;
    private readonly Lock _ownerGate = new();
    private string? _token;
    private RestoreOwnerStatus? _owner;
    private CompletionRecord? _unrecordedCompletion;

    public RestoreCoordinator(WorkHubPaths paths, IAppLog log, string? restoreContinueOp)
        : this(paths, log, restoreContinueOp, TimeProvider.System, SchemaReference.Frozen, new VolumeProbe(), new ProcessStarter(), null, null, TimeSpan.FromSeconds(1))
    {
    }

    internal RestoreCoordinator(
        WorkHubPaths paths,
        IAppLog log,
        string? restoreContinueOp,
        TimeProvider time,
        SchemaReference references,
        IVolumeProbe volumes,
        IProcessStarter starter,
        Func<InstanceLocks, IRestoreFileOps>? files,
        Action<RestoreStage>? hook,
        TimeSpan poll)
    {
        _paths = paths;
        _log = log;
        _token = RestoreOpId.IsValid(restoreContinueOp) ? restoreContinueOp : null;
        _time = time;
        _references = references;
        _volumes = volumes;
        _starter = starter;
        _hook = hook;
        _poll = poll;
        _locks = new InstanceLocks(paths);
        _files = files?.Invoke(_locks) ?? new RestoreFileOps(paths, () => _locks.HoldsRestoreLock);
        _store = new RestoreJournalStore(paths, _files);
    }

    /// <summary>Tests only: this actor's locks.</summary>
    internal InstanceLocks Locks => _locks;

    public RestoreOwnerStatus? Status
    {
        get
        {
            lock (_ownerGate)
                return _owner;
        }
    }

    // ---- startup -----------------------------------------------------------------------------------------------------

    public RestoreEntry Enter(IRestoreStartupUi ui)
    {
        if (!AcquireGate(ui))
            return new RestoreEntry(RestoreEntryKind.Exit);
        _hook?.Invoke(RestoreStage.GateAcquired);

        try
        {
            string? notice = null;
            while (true)
            {
                var scanner = new RestoreScanner(_paths, _files, _store, _log, _time);
                RestoreScan scan;
                if (_locks.TryRestoreLock())
                {
                    try
                    {
                        scan = scanner.Scan(cleanup: true);
                    }
                    finally
                    {
                        _locks.ReleaseRestoreLock();
                    }
                }
                else
                {
                    scan = scanner.Scan(cleanup: false); // R busy: Staged and journal-less ops are live
                }

                if (scan.Problem is { } problem)
                    return Recovery(problem);
                if (scan.NonTerminal is not { } op)
                {
                    _locks.TakePresenceShared();
                    return new RestoreEntry(RestoreEntryKind.Proceed, Session: new StartupSession(this, null, notice));
                }

                var outcome = WaitAndAct(ui, op.Op);
                switch (outcome.Kind)
                {
                    case LoopKind.Continue:
                        notice = outcome.Message ?? notice;
                        continue;
                    case LoopKind.Exit:
                        ReleaseAll();
                        return new RestoreEntry(RestoreEntryKind.Exit);
                    case LoopKind.Recovery:
                        return Recovery(outcome.Message!);
                    case LoopKind.Proceed:
                        return new RestoreEntry(RestoreEntryKind.Proceed, Session: new StartupSession(this, outcome.Journal, notice));
                    case LoopKind.Stopped:
                        BecomeOwner(new RestoreOwnerStatus(outcome.Situation!.Value, RestoreOpSummary.From(outcome.Journal!), outcome.Message!));
                        _locks.DowngradePresence();
                        _locks.ReleaseRestoreLock();
                        _locks.ReleaseGate();
                        return new RestoreEntry(RestoreEntryKind.OwnerStopped, outcome.Message);
                }
            }
        }
        catch
        {
            ReleaseAll();
            throw;
        }
    }

    private bool AcquireGate(IRestoreStartupUi ui)
    {
        var watch = Stopwatch.StartNew();
        var shown = false;
        while (!_locks.TryAcquireGate())
        {
            if (!shown && watch.Elapsed > TimeSpan.FromMilliseconds(500))
            {
                ui.ShowGateWait();
                shown = true;
            }

            if (ui.TakeWaitAction() == RestoreWaitAction.Exit)
                return false;
            Thread.Sleep(Math.Min(250, (int)_poll.TotalMilliseconds));
        }

        return true;
    }

    private enum LoopKind
    {
        Continue,
        Exit,
        Recovery,
        Proceed,
        Stopped,
    }

    private sealed record LoopOutcome(LoopKind Kind, string? Message = null, RestoreJournal? Journal = null, RestoreOwnerSituation? Situation = null);

    /// <summary>§5.3: wait holding G only; act only on the state re-read under G + exclusive P + R.</summary>
    private LoopOutcome WaitAndAct(IRestoreStartupUi ui, string op)
    {
        RestoreOpSummary? shown = null;
        var waitingShown = false;
        while (true)
        {
            var read = _store.Read(op); // display only — never acted on
            if (read.Journal is not { } cached)
                return new LoopOutcome(LoopKind.Recovery, $"The restore journal of {op} cannot be trusted: {read.Problem}.");
            if (RestoreStates.IsTerminal(cached.State) || cached.State == RestoreState.Staged)
                return new LoopOutcome(LoopKind.Continue);

            var summary = RestoreOpSummary.From(cached);
            if (waitingShown && (shown is null || shown.State != summary.State || shown.Revision != summary.Revision))
                ui.ShowWaiting(RestoreReconciler.WaitKindFor(cached.State), summary);
            shown = summary;
            _hook?.Invoke(RestoreStage.WaitRead);

            switch (ui.TakeWaitAction())
            {
                case RestoreWaitAction.Exit:
                    return new LoopOutcome(LoopKind.Exit);
                case RestoreWaitAction.CancelRestore when RestoreReconciler.WaitKindFor(summary.State) == RestoreWaitKind.Confirmed:
                    if (CancelFromWaitingScreen(summary) is { } refusal)
                        ui.ShowNotice(refusal);
                    continue;
            }

            if (!_locks.TryPresenceExclusive())
            {
                if (!waitingShown)
                {
                    ui.ShowWaiting(RestoreReconciler.WaitKindFor(summary.State), summary);
                    waitingShown = true;
                }

                if (SleepWatching(ui, summary) == RestoreWaitAction.Exit)
                    return new LoopOutcome(LoopKind.Exit);
                continue;
            }

            if (!_locks.TryRestoreLock())
            {
                _locks.ReleasePresence(); // never hold P while R is busy
                if (SleepWatching(ui, summary) == RestoreWaitAction.Exit)
                    return new LoopOutcome(LoopKind.Exit);
                continue;
            }

            _hook?.Invoke(RestoreStage.ActingLocksAcquired);
            var outcome = Act(ui, op);
            switch (outcome.Kind)
            {
                case LoopKind.Proceed:
                    return outcome; // G, exclusive P and R stay held through X11–X12
                case LoopKind.Stopped:
                    return outcome; // the caller downgrades P and releases R and G
                default:
                    _locks.ReleaseRestoreLock();
                    _locks.ReleasePresence();
                    return outcome;
            }
        }
    }

    /// <summary>
    /// Sleeps one poll interval while watching the waiting screen. Exit ends the wait; Cancel restore (W-Confirmed only) is
    /// carried out at once against the state and revision that were SHOWN (R try → re-read → compare).
    /// </summary>
    private RestoreWaitAction SleepWatching(IRestoreStartupUi ui, RestoreOpSummary shown)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < _poll)
        {
            Thread.Sleep(Math.Max(10, Math.Min(100, (int)_poll.TotalMilliseconds)));
            switch (ui.TakeWaitAction())
            {
                case RestoreWaitAction.Exit:
                    return RestoreWaitAction.Exit;
                case RestoreWaitAction.CancelRestore when RestoreReconciler.WaitKindFor(shown.State) == RestoreWaitKind.Confirmed:
                    if (CancelFromWaitingScreen(shown) is { } refusal)
                        ui.ShowNotice(refusal);
                    return RestoreWaitAction.CancelRestore;
            }
        }

        return RestoreWaitAction.None;
    }

    private LoopOutcome Act(IRestoreStartupUi ui, string op)
    {
        // Re-read under G + exclusive P + R. The cached value seen while waiting is never used.
        var read = _store.Read(op);
        if (read.Journal is not { } journal)
            return new LoopOutcome(LoopKind.Recovery, $"The restore journal of {op} cannot be trusted: {read.Problem}.");
        if (journal.DataDir != RestoreFacts.DataDirectoryIdentity(_paths))
            return new LoopOutcome(LoopKind.Recovery, $"The restore journal of {op} belongs to another data folder (the folder was copied).");

        var facts = RestoreFacts.Read(_paths, _files, op);
        var decision = RestoreReconciler.Decide(journal, facts);
        _log.Info($"Restore {op}: acting on {journal.State} (revision {journal.Revision}): {decision.Kind}{(decision.Reason is null ? "" : $" ({decision.Reason})")}.");
        var executor = new RestoreExecutor(_paths, _files, _store, _references, _volumes, _log, _time, _hook, ui);
        var token = _token == op;

        switch (decision.Kind)
        {
            case RestoreActionKind.NormalStart:
                return new LoopOutcome(LoopKind.Continue);

            case RestoreActionKind.FailClosed:
                return new LoopOutcome(LoopKind.Recovery, $"The unfinished restore {op} is inconsistent with its files: {decision.Reason}.");

            case RestoreActionKind.AskRestore:
            case RestoreActionKind.AskInterrupted:
            {
                var answer = token
                    ? RestoreAskAnswer.Now
                    : ui.Ask(decision.Kind == RestoreActionKind.AskRestore ? RestoreAskKind.Restore : RestoreAskKind.Interrupted, RestoreOpSummary.From(journal));

                if (answer == RestoreAskAnswer.NotNow)
                    return new LoopOutcome(LoopKind.Exit);
                if (answer == RestoreAskAnswer.Cancel)
                {
                    CancelUnderLocks(journal);
                    return new LoopOutcome(LoopKind.Continue, "The restore was cancelled. Nothing was changed.");
                }

                _token = null;
                return FromExecution(executor.Execute(journal));
            }

            case RestoreActionKind.RollForwardMoveAside:
                return ResumedFilesystemProblem(op) is { } moveProblem
                    ? new LoopOutcome(LoopKind.Recovery, moveProblem)
                    : FromExecution(executor.RollForwardMoveAside(journal));

            case RestoreActionKind.ContinuePublication:
                return ResumedFilesystemProblem(op) is { } publishProblem
                    ? new LoopOutcome(LoopKind.Recovery, publishProblem)
                    : FromExecution(executor.Publication(journal));

            case RestoreActionKind.ReinitializePublished:
                return ResumedFilesystemProblem(op) is { } initializeProblem
                    ? new LoopOutcome(LoopKind.Recovery, initializeProblem)
                    : new LoopOutcome(LoopKind.Proceed, Journal: journal);

            case RestoreActionKind.AskPutBack:
            {
                // R25: no Cancel. Not now / Exit leave everything unchanged; the next start resumes.
                var answer = token ? RestoreAskAnswer.Now : ui.Ask(RestoreAskKind.PutBack, RestoreOpSummary.From(journal));
                if (answer != RestoreAskAnswer.Now)
                    return new LoopOutcome(LoopKind.Exit);
                _token = null;
                return FromExecution(executor.Undo(journal));
            }

            case RestoreActionKind.RollForwardUndo:
                return FromExecution(executor.Undo(journal));

            default:
                return new LoopOutcome(LoopKind.Recovery, $"Unknown restore action {decision.Kind}.");
        }
    }

    /// <summary>
    /// §11 for a RESUMED Restore (MovingAside, MovedAside in either layout, Published): the current filesystem facts —
    /// never the journal's or the request's — are checked under G + exclusive P + R before any move, publication or
    /// initialization (X0 covers a fresh execution, U0 a Put back). Unsupported or unverifiable facts fail closed: nothing
    /// is written, moved, deleted or initialized, so the journal and the recovery files stay for a later retry.
    /// </summary>
    private string? ResumedFilesystemProblem(string op)
    {
        VolumeCheckResult check;
        try
        {
            check = new VolumeCheck(_paths, _volumes).Check(op);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Error($"Restore {op}: the filesystem could not be inspected before resuming ({ex.GetType().Name}); nothing was changed.");
            return $"The unfinished restore {op} cannot be resumed: the data folder's filesystem could not be checked. Nothing was changed.";
        }

        if (check.IsSupported)
            return null;
        _log.Error($"Restore {op}: resuming refused by the filesystem check ({check.LogDetail}); nothing was changed.");
        return $"The unfinished restore {op} cannot be resumed here. {check.Message} Nothing was changed.";
    }

    private static LoopOutcome FromExecution(ExecutionResult result) => result.Kind switch
    {
        ExecutionKind.Terminal => new LoopOutcome(LoopKind.Continue, result.Message, result.Journal),
        ExecutionKind.Proceed => new LoopOutcome(LoopKind.Proceed, Journal: result.Journal),
        ExecutionKind.Stopped => new LoopOutcome(LoopKind.Stopped, result.Message, result.Journal, result.Situation),
        _ => new LoopOutcome(LoopKind.Recovery, result.Message, result.Journal),
    };

    private RestoreEntry Recovery(string message)
    {
        _log.Error($"Restore: fail closed — {message}");
        ReleaseAll();
        return new RestoreEntry(RestoreEntryKind.RecoveryRequired, message);
    }

    private void ReleaseAll()
    {
        _locks.ReleaseRestoreLock();
        _locks.ReleasePresence();
        _locks.ReleaseGate();
    }

    // ---- cancellation (R25) ------------------------------------------------------------------------------------------

    /// <summary>
    /// Waiting-screen Cancel restore (holding G only): R try (≤ 2 s) → re-read → write <c>Cancelled{cancelledFrom}</c> only
    /// if the state and revision are the ones shown and the state is before <c>MovingAside</c>; then delete the candidate.
    /// Returns a refusal text, or null when cancelled.
    /// </summary>
    private string? CancelFromWaitingScreen(RestoreOpSummary shown)
    {
        if (!_locks.TryRestoreLock(TimeSpan.FromSeconds(2)))
            return "Another WorkHub window is changing the restore right now. Try again.";
        try
        {
            var read = _store.Read(shown.Op);
            if (read.Journal is not { } journal || journal.State != shown.State || journal.Revision != shown.Revision || !RestoreStates.IsCancellable(journal.State))
            {
                _log.Info($"Restore {shown.Op}: Cancel restore refused; the state changed since it was shown.");
                return "The restore has moved on and can no longer be cancelled here. Showing its current state.";
            }

            if (!RestoreFacts.Read(_paths, _files, shown.Op).PreviousEmpty)
                return "The restore has moved on and can no longer be cancelled here. Showing its current state.";
            CancelUnderLocks(journal);
            return null;
        }
        finally
        {
            _locks.ReleaseRestoreLock();
        }
    }

    /// <summary>Publish <c>Cancelled{cancelledFrom}</c> first, then delete the candidate (a kill in between is cleaned up
    /// by the next R holder after the terminal guard passes).</summary>
    private void CancelUnderLocks(RestoreJournal journal)
    {
        if (!RestoreStates.IsCancellable(journal.State))
            throw new InvalidOperationException($"{journal.State} cannot be cancelled.");
        _hook?.Invoke(RestoreStage.BeforeCancelledWrite);
        _store.Write(journal.Next(RestoreState.Cancelled, _time.GetUtcNow()) with { CancelledFrom = journal.State });
        _hook?.Invoke(RestoreStage.AfterCancelledWrite);
        var candidate = Path.Combine(_paths.RestoreOperationDirectory(journal.Op), RestoreNames.Candidate);
        if (_files.FileExists(candidate))
            _files.Delete(candidate);
        _log.Info($"Restore {journal.Op} cancelled from {journal.State}. Nothing was changed.");
    }

    // ---- sessions (R6, X11–X12) -------------------------------------------------------------------------------------

    private sealed class StartupSession(RestoreCoordinator owner, RestoreJournal? published, string? notice) : IRestoreStartupSession
    {
        public string? InitializationSucceeded(DatabaseInitializationResult result)
        {
            if (published is null)
            {
                owner._locks.ReleaseGate();
                return notice;
            }

            return owner.Complete(published, result);
        }

        public void InitializationFailed(Exception exception)
        {
            if (published is null)
            {
                owner._locks.ReleaseGate();
                return;
            }

            owner._log.Error($"Restore {published.Op}: the restored database could not be opened; this window owns the restore.", exception);
            owner.BecomeOwner(new RestoreOwnerStatus(
                RestoreOwnerSituation.PublishedInitializationFailed,
                RestoreOpSummary.From(published),
                "The restored backup could not be opened. Put back your previous data, or try starting again."));
            owner.ReleaseToOwner();
        }
    }

    /// <summary>X12: publish <c>Completed</c> (3 × 200 ms). On failure the outcome stays in memory (§8.4).</summary>
    private string? Complete(RestoreJournal published, DatabaseInitializationResult result)
    {
        var completion = new CompletionRecord(result.Outcome.ToString(), result.PreviousSchemaVersion, result.SchemaVersion, result.MigrationBackupFile is null ? null : Path.GetFileName(result.MigrationBackupFile));
        string? notice = $"The backup {published.Source.FileName} was restored." +
                         (result.Outcome == DatabaseInitializationOutcome.Migrated ? $" It was upgraded from schema v{result.PreviousSchemaVersion} to v{result.SchemaVersion}." : string.Empty);
        try
        {
            _hook?.Invoke(RestoreStage.BeforeCompletedWrite);
            RestoreIo.WithRetries(() => _store.Write(published.Next(RestoreState.Completed, _time.GetUtcNow()) with { Completed = completion }), attempts: 3);
            _hook?.Invoke(RestoreStage.CompletedPublished);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"Restore {published.Op}: Completed could not be recorded; other windows wait until Try again succeeds.", ex);
            lock (_ownerGate)
                _unrecordedCompletion = completion;
            BecomeOwner(new RestoreOwnerStatus(
                RestoreOwnerSituation.CompletionUnrecorded,
                RestoreOpSummary.From(published),
                "The restore worked, but WorkHub could not record that it finished. Other WorkHub windows on this data folder cannot start until this is recorded."));
            notice = null;
        }

        ReleaseToOwner();
        return notice;
    }

    /// <summary>Downgrade P to shared (still under G), release R, release G.</summary>
    private void ReleaseToOwner()
    {
        if (_locks.HoldsPresenceExclusive)
            _locks.DowngradePresence();
        _locks.ReleaseRestoreLock();
        _locks.ReleaseGate();
    }

    private void BecomeOwner(RestoreOwnerStatus status)
    {
        lock (_ownerGate)
            _owner = status;
    }

    // ---- owner window (§8.4, §9.2, §10.1) ----------------------------------------------------------------------------

    public OwnerActionResult RecordCompletion()
    {
        RestoreOwnerStatus? status;
        CompletionRecord? completion;
        lock (_ownerGate)
        {
            status = _owner;
            completion = _unrecordedCompletion;
        }

        if (status is not { Situation: RestoreOwnerSituation.CompletionUnrecorded } || completion is null)
            return new OwnerActionResult(false, "There is nothing to record.");

        if (!_locks.TryRestoreLock(TimeSpan.FromSeconds(2)))
            return new OwnerActionResult(false, "Another WorkHub window is changing the restore right now. Try again.");
        try
        {
            var read = _store.Read(status.Op.Op);
            if (read.Journal is not { } journal)
                return new OwnerActionResult(false, $"The restore journal cannot be read ({read.Problem}).");
            var main = _paths.DatabaseFile;
            if (journal.State != RestoreState.Published || journal.Revision != status.Op.Revision
                || !_files.FileExists(main) || _files.FileId(main) != journal.Candidate?.FileId)
            {
                if (RestoreStates.IsTerminal(journal.State))
                    ClearOwner();
                return new OwnerActionResult(false, "The restore state changed; nothing was written.");
            }

            _store.Write(journal.Next(RestoreState.Completed, _time.GetUtcNow()) with { Completed = completion });
            _log.Info($"Restore {journal.Op}: Completed recorded by Try again.");
            ClearOwner();
            return new OwnerActionResult(true, "The restore is recorded as finished.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Restore: Try again could not record Completed ({ex.GetType().Name}).");
            return new OwnerActionResult(false, "WorkHub still could not record that the restore finished. Try again.");
        }
        finally
        {
            _locks.ReleaseRestoreLock();
        }
    }

    public OwnerActionResult RequestPutBack()
    {
        var status = Status;
        if (status is not { Situation: RestoreOwnerSituation.PublishedInitializationFailed or RestoreOwnerSituation.MovedAside })
            return new OwnerActionResult(false, "Put back is not available now.");
        if (!_locks.TryRestoreLock(TimeSpan.FromSeconds(2)))
            return new OwnerActionResult(false, "Another WorkHub window is changing the restore right now. Try again.");
        try
        {
            // Re-read the journal AND the file facts; write only if state and revision are the ones shown.
            var read = _store.Read(status.Op.Op);
            if (read.Journal is not { } journal || journal.State != status.Op.State || journal.Revision != status.Op.Revision)
                return new OwnerActionResult(false, "The restore state changed; nothing was written. Restart WorkHub to see its current state.");
            var origin = RestoreReconciler.OriginFromFacts(journal, RestoreFacts.Read(_paths, _files, journal.Op));
            if (origin is not { } chosen)
                return new OwnerActionResult(false, "Put back is not possible: the restore files are not as expected. See \"Unfinished restore\" in the README.");

            _hook?.Invoke(RestoreStage.BeforeUndoRequestedWrite);
            _store.Write(journal.Next(RestoreState.UndoRequested, _time.GetUtcNow()) with { Undo = new UndoRecord(_time.GetUtcNow(), chosen) });
            _hook?.Invoke(RestoreStage.AfterUndoRequestedWrite);
            _log.Info($"Restore {journal.Op}: Put back requested ({chosen}).");
            ClearOwner();
            return new OwnerActionResult(true, RelaunchOp: journal.Op);
        }
        finally
        {
            _locks.ReleaseRestoreLock();
        }
    }

    public bool HasUnfinishedOperation()
    {
        try
        {
            var scan = new RestoreScanner(_paths, _files, _store, _log, _time).Scan(cleanup: false);
            return scan.Problem is not null || scan.NonTerminal is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // unknown: do not offer a new restore
        }
    }

    public string? OpenRestoreFolder()
    {
        if (!Directory.Exists(_paths.RestoreDirectory))
            return "The Restore folder does not exist.";
        try
        {
            _starter.Start(new ProcessStartInfo(_paths.RestoreDirectory) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"The Restore folder could not be opened ({ex.Message}).";
        }
    }

    /// <summary>Releases every lock of this process (the OS does the same when the process exits).</summary>
    public void Dispose() => _locks.Dispose();

    private void ClearOwner()
    {
        lock (_ownerGate)
        {
            _owner = null;
            _unrecordedCompletion = null;
        }
    }
}
