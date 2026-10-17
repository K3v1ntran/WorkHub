using WorkHub.Core.Backup;
using WorkHub.Core.Startup;

namespace WorkHub.Core.Restore;

/// <summary>What the user did on a waiting screen since the last poll.</summary>
public enum RestoreWaitAction
{
    None,
    Exit,

    /// <summary>Only offered on <see cref="RestoreWaitKind.Confirmed"/>.</summary>
    CancelRestore,
}

public enum RestoreAskKind
{
    /// <summary><c>Confirmed</c> without token: Restore now · Cancel restore · Not now.</summary>
    Restore,

    /// <summary><c>Executing</c> interrupted before anything was changed: Restore now · Cancel restore · Not now.</summary>
    Interrupted,

    /// <summary><c>UndoRequested</c> without token: Put back now · Not now (no Cancel, R25).</summary>
    PutBack,
}

public enum RestoreAskAnswer
{
    Now,

    /// <summary>Only for <see cref="RestoreAskKind.Restore"/> and <see cref="RestoreAskKind.Interrupted"/>.</summary>
    Cancel,

    NotNow,
}

/// <summary>What the UI shows about the operation (from the journal it was read from; never acted on directly).</summary>
public sealed record RestoreOpSummary(
    string Op,
    RestoreState State,
    int Revision,
    string SourceFileName,
    DateTimeOffset? SourceTaken,
    UndoOrigin? Origin)
{
    public static RestoreOpSummary From(RestoreJournal journal) =>
        new(journal.Op, journal.State, journal.Revision, journal.Source.FileName, journal.Source.Taken, journal.Undo?.Origin);
}

/// <summary>
/// The startup screen's side of the protocol (§5.3, §13.3). Called from the startup worker thread; an implementation
/// marshals to its UI. <see cref="Ask"/> blocks the worker (which holds G) until the user answers.
/// </summary>
public interface IRestoreStartupUi
{
    /// <summary>Another start or a restore holds the startup Gate. Exit stays available.</summary>
    void ShowGateWait();

    void ShowWaiting(RestoreWaitKind kind, RestoreOpSummary op);

    /// <summary>Returns and clears the user's last waiting-screen action.</summary>
    RestoreWaitAction TakeWaitAction();

    void ShowNotice(string message);

    RestoreAskAnswer Ask(RestoreAskKind kind, RestoreOpSummary op);

    /// <param name="closeBlocked">True during X5–X10 and U2–U6: the window must not close.</param>
    void ShowProgress(string text, bool closeBlocked);
}

public enum RestoreEntryKind
{
    /// <summary>The database may be initialized now; <see cref="RestoreEntry.Session"/> holds the startup Gate.</summary>
    Proceed,

    /// <summary>The user chose Exit or Not now. Nothing was changed.</summary>
    Exit,

    /// <summary>Fail closed: no initializer, no seeding, no moves. The recovery screen is shown.</summary>
    RecoveryRequired,

    /// <summary>The executor stopped in a non-terminal state and now owns it (see <see cref="IRestoreOwner"/>).</summary>
    OwnerStopped,
}

/// <summary>Held from the restore startup protocol through <c>DatabaseInitializer</c> (R6) — then completion (X12).</summary>
public interface IRestoreStartupSession
{
    /// <summary>Called after a successful initialization. Returns a notice for the user, if any. Releases the Gate.</summary>
    string? InitializationSucceeded(DatabaseInitializationResult result);

    /// <summary>Called when initialization threw. Releases the Gate; a restore start becomes the owner of <c>Published</c>.</summary>
    void InitializationFailed(Exception exception);
}

public sealed record RestoreEntry(RestoreEntryKind Kind, string? Message = null, IRestoreStartupSession? Session = null);

/// <summary>Runs before <c>DatabaseInitializer</c> on every start: Gate, cleanup, reconciliation, waiting and acting (§5.3, §10.2).</summary>
public interface IRestoreStartup
{
    RestoreEntry Enter(IRestoreStartupUi ui);
}

/// <summary>Startup stopped for a restore reason (Exit, fail closed, owner stop). Nothing was initialized.</summary>
public sealed class RestoreStartupException(RestoreEntryKind kind, string message) : Exception(message)
{
    public RestoreEntryKind Kind { get; } = kind;
}

public enum RestoreOwnerSituation
{
    /// <summary><c>Published</c> and the initializer failed: Put back (origin After) · Try starting again · Exit.</summary>
    PublishedInitializationFailed,

    /// <summary><c>Published</c>, initialization succeeded but <c>Completed</c> could not be written: banner Try again.</summary>
    CompletionUnrecorded,

    /// <summary><c>MovedAside</c> (publication failed): Retry · Put back (origin Before) · Exit.</summary>
    MovedAside,

    /// <summary><c>MovingAside</c> after a failed same-run roll back: Retry · Exit.</summary>
    MovingAside,

    /// <summary>Put back stopped on a move failure: Retry · Exit (no cancel).</summary>
    PutBackStopped,
}

public sealed record RestoreOwnerStatus(RestoreOwnerSituation Situation, RestoreOpSummary Op, string Message);

/// <param name="RelaunchOp">When set, the caller relaunches with <c>--restore-continue</c> for this op and exits.</param>
public sealed record OwnerActionResult(bool Succeeded, string? Message = null, string? RelaunchOp = null);

/// <summary>
/// The owner window's actions (§8.4, §9.2, §10.1). Every write: R try (≤ 2 s) → re-read journal and facts → write only
/// if the state and revision are still the ones shown.
/// </summary>
public interface IRestoreOwner
{
    RestoreOwnerStatus? Status { get; }

    /// <summary>Completed-not-recorded: Try again.</summary>
    OwnerActionResult RecordCompletion();

    /// <summary>Put back previous data: publishes <c>UndoRequested</c> with the origin chosen from fresh facts.</summary>
    OwnerActionResult RequestPutBack();

    /// <summary>True when a restore operation is non-terminal (the Failed shell then hides "Restore from a backup…").</summary>
    bool HasUnfinishedOperation();

    /// <summary>The <c>Data\Restore</c> folder, for the recovery screen's "Open the Restore folder".</summary>
    string? OpenRestoreFolder();
}

public sealed record RestoreContentCounts(long? Systems, long? OwnerItems, long? Projects, long? ProjectsInTrash, long? Notes, long? ProjectTrashItems);

/// <summary>What the confirmation shows (§13.2); produced while the Restore lock is held.</summary>
public sealed record RestoreConfirmationFacts(
    string Op,
    BackupEntry Source,
    int SchemaVersion,
    BackupCompatibility Compatibility,
    int LatestSchema,
    long Size,
    RestoreContentCounts Counts,
    DataClassification RequestClass);

/// <summary>
/// A reservation (Q2–Q4) that holds the Restore lock until it is confirmed or cancelled. Disposing an undecided
/// request cancels it.
/// </summary>
public interface IRestoreRequest : IDisposable
{
    RestoreConfirmationFacts Facts { get; }

    /// <summary>Q5b: publishes <c>Confirmed</c> with the acknowledgement and releases the Restore lock. Returns the op.</summary>
    Task<string> ConfirmAsync();

    /// <summary>Q5a: deletes the candidate, publishes <c>Cancelled</c> and releases the Restore lock.</summary>
    Task CancelAsync();
}

/// <param name="Refusal">A user-facing reason when no request could be made.</param>
public sealed record RestoreRequestStart(IRestoreRequest? Request, string? Refusal);

public interface IRestoreRequester
{
    /// <summary>Q0–Q3: reserve under R, precheck, filesystem check, stage and validate, classify current data (ReadOnly).</summary>
    Task<RestoreRequestStart> BeginAsync(BackupEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// For starts without a screen (tests, tools): never presses a waiting-screen button and answers every question with
/// Not now, so an unfinished restore is left exactly as it is (the start then stops with <see cref="RestoreEntryKind.Exit"/>).
/// </summary>
public sealed class NonInteractiveRestoreUi : IRestoreStartupUi
{
    public static NonInteractiveRestoreUi Instance { get; } = new();

    public void ShowGateWait() { }

    public void ShowWaiting(RestoreWaitKind kind, RestoreOpSummary op) { }

    public RestoreWaitAction TakeWaitAction() => RestoreWaitAction.None;

    public void ShowNotice(string message) { }

    public RestoreAskAnswer Ask(RestoreAskKind kind, RestoreOpSummary op) => RestoreAskAnswer.NotNow;

    public void ShowProgress(string text, bool closeBlocked) { }
}
