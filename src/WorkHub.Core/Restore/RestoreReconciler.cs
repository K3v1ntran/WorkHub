namespace WorkHub.Core.Restore;

/// <summary>One file (or other entry) as found on disk. Directories carry <see cref="DirectoryId"/> and never match a file.</summary>
public sealed record FileFact(string Name, long Size, string FileId)
{
    public const string DirectoryId = "<directory>";
}

/// <summary>
/// Fresh file facts for one operation, read under G + exclusive P + R (or under R for owner actions).
/// </summary>
/// <param name="Data">Every entry of <c>Data\</c> whose name starts with <c>workhub.db</c>.</param>
/// <param name="Previous">Every entry of <c>previous\</c>; null when the folder does not exist.</param>
/// <param name="Restored">Every entry of <c>restored\</c>; null when the folder does not exist.</param>
/// <param name="Candidate">The staged <c>candidate.db</c>, if present.</param>
public sealed record RestoreFileFacts(
    IReadOnlyList<FileFact> Data,
    IReadOnlyList<FileFact>? Previous,
    IReadOnlyList<FileFact>? Restored,
    FileFact? Candidate)
{
    public bool PreviousEmpty => Previous is null || Previous.Count == 0;

    public bool RestoredEmpty => Restored is null || Restored.Count == 0;

    public FileFact? DataFile(string name) => Data.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

public enum RestoreActionKind
{
    /// <summary>Nothing to do for this op (terminal and consistent, or a live <c>Staged</c>).</summary>
    NormalStart,

    /// <summary>Ambiguous or inconsistent: no initializer, no seeding, no moves, no cleanup of the op.</summary>
    FailClosed,

    /// <summary><c>Confirmed</c>: token → execute; otherwise ask (Restore now · Cancel restore · Not now).</summary>
    AskRestore,

    /// <summary><c>Executing</c> (interrupted in X1–X4, nothing moved): token → execute; otherwise ask, as for Confirmed.</summary>
    AskInterrupted,

    /// <summary><c>MovingAside</c> consistent with <c>previousInventory</c>: finish X6 (R10).</summary>
    RollForwardMoveAside,

    /// <summary><c>MovedAside</c>: continue X8–X10 (or record a publication that already happened).</summary>
    ContinuePublication,

    /// <summary><c>Published</c> without a running owner: re-run X11–X12.</summary>
    ReinitializePublished,

    /// <summary><c>UndoRequested</c>: token → U0; otherwise ask (Put back now · Not now). Never cancelled.</summary>
    AskPutBack,

    /// <summary><c>Undoing</c> / <c>RestoredAside</c> consistent with both inventories: roll forward.</summary>
    RollForwardUndo,
}

public sealed record RestoreDecision(RestoreActionKind Kind, string? Reason = null, UndoOrigin? Origin = null)
{
    public static RestoreDecision Normal { get; } = new(RestoreActionKind.NormalStart);

    public static RestoreDecision Fail(string reason) => new(RestoreActionKind.FailClosed, reason);
}

/// <summary>What a start shows while it holds only G and waits for exclusive Presence (§5.3).</summary>
public enum RestoreWaitKind
{
    /// <summary><c>Confirmed</c> or interrupted <c>Executing</c>: Cancel restore · Exit.</summary>
    Confirmed,

    /// <summary><c>UndoRequested</c>: Exit only — Put back cannot be cancelled.</summary>
    PutBack,

    /// <summary>Another window owns the op: Exit only (the owner decides).</summary>
    Owned,
}

/// <summary>
/// Pure decisions over journal state × file facts (§8.2, §9.4, §9.5, §10.2). No I/O. Everything not explicitly
/// recognized fails closed.
/// </summary>
public static class RestoreReconciler
{
    public static RestoreWaitKind WaitKindFor(RestoreState state) => state switch
    {
        RestoreState.Confirmed or RestoreState.Executing => RestoreWaitKind.Confirmed,
        RestoreState.UndoRequested => RestoreWaitKind.PutBack,
        _ => RestoreWaitKind.Owned,
    };

    /// <summary>
    /// Terminal-state guard (§9.5). A terminal journal is accepted only if it is consistent with the files. Returns the
    /// violation, or null. Runs at every start before any cleanup of the op and before normal initialization.
    /// </summary>
    public static string? TerminalViolation(RestoreJournal journal, RestoreFileFacts facts)
    {
        switch (journal.State)
        {
            case RestoreState.Cancelled:
                if (journal.CancelledFrom is not { } from || !RestoreStates.IsCancellable(from))
                    return $"cancelled from {journal.CancelledFrom?.ToString() ?? "an unrecorded state"}";
                goto case RestoreState.Aborted;
            case RestoreState.Abandoned:
            case RestoreState.Aborted:
                if (journal.Undo is not null)
                    return "a Put back was recorded";
                if (!facts.PreviousEmpty)
                    return "previous data is still set aside";
                if (journal.State != RestoreState.Cancelled && journal.CancelledFrom is not null)
                    return "cancellation recorded outside Cancelled";
                return null;
            case RestoreState.Completed:
            case RestoreState.Undone:
                return journal.CancelledFrom is not null ? "cancellation recorded outside Cancelled" : null;
            default:
                return null;
        }
    }

    public static RestoreDecision Decide(RestoreJournal journal, RestoreFileFacts facts)
    {
        if (journal.State != RestoreState.Cancelled && journal.CancelledFrom is not null)
            return RestoreDecision.Fail("cancellation recorded outside Cancelled");

        if (RestoreStates.IsTerminal(journal.State))
            return TerminalViolation(journal, facts) is { } violation ? RestoreDecision.Fail(violation) : RestoreDecision.Normal;

        switch (journal.State)
        {
            case RestoreState.Staged:
                return RestoreDecision.Normal; // live staging (a dead one was abandoned by cleanup); never executed

            case RestoreState.Confirmed:
            case RestoreState.Executing:
                if (journal.Undo is not null)
                    return RestoreDecision.Fail("a Put back was recorded before the restore moved anything");
                if (!facts.PreviousEmpty || !facts.RestoredEmpty)
                    return RestoreDecision.Fail("files are set aside although nothing should have been moved");
                return new RestoreDecision(journal.State == RestoreState.Confirmed ? RestoreActionKind.AskRestore : RestoreActionKind.AskInterrupted);

            case RestoreState.MovingAside:
                return MovingAside(journal, facts);

            case RestoreState.MovedAside:
                return MovedAside(journal, facts);

            case RestoreState.Published:
                return Published(journal, facts);

            case RestoreState.UndoRequested:
            case RestoreState.Undoing:
            case RestoreState.RestoredAside:
                return Undo(journal, facts);

            default:
                return RestoreDecision.Fail($"unknown state {journal.State}");
        }
    }

    /// <summary>
    /// §9.3: the Put-back origin chosen from fresh facts under R (owner window, state <c>Published</c>/<c>MovedAside</c>).
    /// Null when the facts fit neither origin; then no <c>UndoRequested</c> may be written.
    /// </summary>
    public static UndoOrigin? OriginFromFacts(RestoreJournal journal, RestoreFileFacts facts)
    {
        if (journal.State is not (RestoreState.Published or RestoreState.MovedAside) || journal.Candidate is not { } candidate)
            return null;
        if (journal.PreviousInventory is not { } inventory || !SameSet(facts.Previous, inventory) || !facts.RestoredEmpty)
            return null;

        var main = facts.DataFile(RestoreNames.Database);
        if (main?.FileId == candidate.FileId && facts.Candidate is null && RestoredSetInData(facts.Data, inventory))
            return UndoOrigin.AfterPublication;
        if (facts.Data.Count == 0 && facts.Candidate?.FileId == candidate.FileId)
            return UndoOrigin.BeforePublication;
        return null;
    }

    private static RestoreDecision MovingAside(RestoreJournal journal, RestoreFileFacts facts)
    {
        if (journal.PreviousInventory is not { } inventory || journal.Candidate is not { } candidate)
            return RestoreDecision.Fail("previous inventory or candidate facts missing");
        if (journal.Undo is not null || !facts.RestoredEmpty)
            return RestoreDecision.Fail("unexpected Put-back facts");
        if (facts.Candidate?.FileId != candidate.FileId)
            return RestoreDecision.Fail("the staged candidate is missing or was replaced");
        return SplitBetween(inventory, facts.Data, facts.Previous ?? [])
            ? new RestoreDecision(RestoreActionKind.RollForwardMoveAside)
            : RestoreDecision.Fail("the files set aside do not match the recorded inventory");
    }

    private static RestoreDecision MovedAside(RestoreJournal journal, RestoreFileFacts facts)
    {
        if (journal.PreviousInventory is not { } inventory || journal.Candidate is not { } candidate)
            return RestoreDecision.Fail("previous inventory or candidate facts missing");
        if (journal.Undo is not null || !facts.RestoredEmpty)
            return RestoreDecision.Fail("unexpected Put-back facts");
        if (!SameSet(facts.Previous, inventory))
            return RestoreDecision.Fail("previous\\ does not match the recorded inventory");

        var staged = facts.Data.Count == 0 && facts.Candidate?.FileId == candidate.FileId;
        var published = facts.Candidate is null && facts.Data.Count == 1 && facts.DataFile(RestoreNames.Database)?.FileId == candidate.FileId;
        return staged || published
            ? new RestoreDecision(RestoreActionKind.ContinuePublication)
            : RestoreDecision.Fail("Data\\ holds an unexpected database, or the candidate is missing or replaced");
    }

    private static RestoreDecision Published(RestoreJournal journal, RestoreFileFacts facts)
    {
        if (journal.PreviousInventory is not { } inventory || journal.Candidate is not { } candidate)
            return RestoreDecision.Fail("previous inventory or candidate facts missing");
        if (journal.Undo is not null || !facts.RestoredEmpty)
            return RestoreDecision.Fail("unexpected Put-back facts");
        if (!SameSet(facts.Previous, inventory))
            return RestoreDecision.Fail("previous\\ does not match the recorded inventory");
        if (facts.Candidate is not null)
            return RestoreDecision.Fail("a staged candidate exists although the restore was published");
        return facts.DataFile(RestoreNames.Database)?.FileId == candidate.FileId && RestoredSetInData(facts.Data, inventory)
            ? new RestoreDecision(RestoreActionKind.ReinitializePublished)
            : RestoreDecision.Fail("Data\\workhub.db is not the restored database");
    }

    private static RestoreDecision Undo(RestoreJournal journal, RestoreFileFacts facts)
    {
        if (journal.Undo is not { } undo)
            return RestoreDecision.Fail("Put back without a recorded origin");
        if (journal.PreviousInventory is not { } previous || journal.Candidate is not { } candidate)
            return RestoreDecision.Fail("previous inventory or candidate facts missing");

        return undo.Origin switch
        {
            UndoOrigin.AfterPublication => UndoAfter(journal.State, previous, candidate, undo, facts),
            UndoOrigin.BeforePublication => UndoBefore(journal.State, previous, candidate, undo, facts),
            _ => RestoreDecision.Fail("unknown Put-back origin"),
        };
    }

    private static RestoreDecision UndoAfter(RestoreState state, IReadOnlyList<InventoryFile> previous, CandidateFacts candidate, UndoRecord undo, RestoreFileFacts facts)
    {
        const UndoOrigin origin = UndoOrigin.AfterPublication;
        if (facts.Candidate is not null)
            return RestoreDecision.Fail("a staged candidate exists although the restore was published");

        switch (state)
        {
            case RestoreState.UndoRequested:
                if (!SameSet(facts.Previous, previous) || !facts.RestoredEmpty)
                    return RestoreDecision.Fail("previous\\ or restored\\ do not match");
                return facts.DataFile(RestoreNames.Database)?.FileId == candidate.FileId && RestoredSetInData(facts.Data, previous)
                    ? new RestoreDecision(RestoreActionKind.AskPutBack, Origin: origin)
                    : RestoreDecision.Fail("Data\\workhub.db is not the restored database");

            case RestoreState.Undoing:
                if (undo.RestoredInventory is not { } restored || !SameSet(facts.Previous, previous))
                    return RestoreDecision.Fail("restored inventory missing or previous\\ does not match");
                if (!restored.Any(f => f.Name == RestoreNames.Database && f.FileId == candidate.FileId))
                    return RestoreDecision.Fail("the restored inventory does not hold the restored database");
                return SplitBetween(restored, facts.Data, facts.Restored ?? [])
                    ? new RestoreDecision(RestoreActionKind.RollForwardUndo, Origin: origin)
                    : RestoreDecision.Fail("the restored files do not match their inventory");

            case RestoreState.RestoredAside:
                if (undo.RestoredInventory is not { } aside || !SameSet(facts.Restored, aside))
                    return RestoreDecision.Fail("restored\\ does not match its inventory");
                return SplitBetween(previous, facts.Data, facts.Previous ?? [])
                    ? new RestoreDecision(RestoreActionKind.RollForwardUndo, Origin: origin)
                    : RestoreDecision.Fail("the previous files do not match their inventory");

            default:
                return RestoreDecision.Fail($"unexpected state {state}");
        }
    }

    private static RestoreDecision UndoBefore(RestoreState state, IReadOnlyList<InventoryFile> previous, CandidateFacts candidate, UndoRecord undo, RestoreFileFacts facts)
    {
        const UndoOrigin origin = UndoOrigin.BeforePublication;
        if (facts.Candidate?.FileId != candidate.FileId)
            return RestoreDecision.Fail("the staged candidate is missing or was replaced");
        if (facts.Restored is not null)
            return RestoreDecision.Fail("restored\\ exists although nothing was published");
        if (undo.RestoredInventory is { Count: > 0 })
            return RestoreDecision.Fail("a restored inventory was recorded although nothing was published");

        switch (state)
        {
            case RestoreState.UndoRequested:
            case RestoreState.Undoing:
                if (!SameSet(facts.Previous, previous))
                    return RestoreDecision.Fail("previous\\ does not match the recorded inventory");
                if (facts.Data.Count != 0)
                    return RestoreDecision.Fail("Data\\ holds a database although nothing was published");
                return new RestoreDecision(state == RestoreState.UndoRequested ? RestoreActionKind.AskPutBack : RestoreActionKind.RollForwardUndo, Origin: origin);

            case RestoreState.RestoredAside:
                return SplitBetween(previous, facts.Data, facts.Previous ?? [])
                    ? new RestoreDecision(RestoreActionKind.RollForwardUndo, Origin: origin)
                    : RestoreDecision.Fail("the previous files do not match their inventory");

            default:
                return RestoreDecision.Fail($"unexpected state {state}");
        }
    }

    /// <summary>
    /// Every inventoried file is in exactly one of the two places (same name and file ID), and neither place holds
    /// anything else. The same ID in two places, a missing file or an extra entry fails.
    /// </summary>
    public static bool SplitBetween(IReadOnlyList<InventoryFile> inventory, IReadOnlyList<FileFact> first, IReadOnlyList<FileFact> second)
    {
        if (!Distinct(inventory))
            return false;
        foreach (var file in inventory)
        {
            var inFirst = first.Count(f => Matches(f, file));
            var inSecond = second.Count(f => Matches(f, file));
            if (inFirst + inSecond != 1)
                return false;
        }

        return first.All(f => inventory.Any(i => Matches(f, i))) && second.All(f => inventory.Any(i => Matches(f, i)));
    }

    /// <summary>The folder holds exactly the inventory (names and IDs), nothing more. A missing folder equals an empty inventory.</summary>
    public static bool SameSet(IReadOnlyList<FileFact>? folder, IReadOnlyList<InventoryFile> inventory)
    {
        var files = folder ?? [];
        return Distinct(inventory) && files.Count == inventory.Count && inventory.All(i => files.Count(f => Matches(f, i)) == 1);
    }

    private static bool Distinct(IReadOnlyList<InventoryFile> inventory) =>
        inventory.Select(i => i.Name.ToUpperInvariant()).Distinct().Count() == inventory.Count &&
        inventory.Select(i => i.FileId).Distinct(StringComparer.Ordinal).Count() == inventory.Count;

    private static bool Matches(FileFact fact, InventoryFile file) =>
        string.Equals(fact.Name, file.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(fact.FileId, file.FileId, StringComparison.Ordinal);

    /// <summary>
    /// <c>Data\</c> holds only the restored main file and its own sidecars: regular files with the database-set names, no
    /// ID twice, and no ID of the previous set (the same file in two places, or both sets in <c>Data\</c>).
    /// </summary>
    private static bool RestoredSetInData(IReadOnlyList<FileFact> data, IReadOnlyList<InventoryFile> previous) =>
        data.All(f => f.FileId != FileFact.DirectoryId
                      && (string.Equals(f.Name, RestoreNames.Database, StringComparison.OrdinalIgnoreCase) || RestoreNames.IsSidecar(f.Name.ToLowerInvariant()))
                      && previous.All(p => !string.Equals(p.FileId, f.FileId, StringComparison.Ordinal)))
        && data.Select(f => f.FileId).Distinct(StringComparer.Ordinal).Count() == data.Count;
}
