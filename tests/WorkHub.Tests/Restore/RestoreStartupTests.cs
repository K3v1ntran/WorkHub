using System.Collections.Concurrent;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Restore;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Restore;

/// <summary>
/// The startup protocol (Phase 10 §5.3, §8.4, §9.5, §10): waiting holds G only, acting re-reads under G + exclusive P + R,
/// owner actions, Cancel restore versus no-cancel Put back, the terminal guard, ambiguity and races (tests O, C2–C4, P1,
/// P2, P5–P7, E, Q5–Q7).
/// </summary>
public sealed class RestoreStartupTests : RestoreTestBase
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private string Db => Root.Paths.DatabaseFile;

    /// <summary>Another running window: shared Presence registered under G, G released.</summary>
    private InstanceLocks OtherWindow()
    {
        var locks = Own(new InstanceLocks(Root.Paths));
        Assert.True(locks.TryAcquireGate());
        locks.TakePresenceShared();
        locks.ReleaseGate();
        return locks;
    }

    private Task<(InitializationResult? Result, RestoreStartupException? Stop, Exception? Error)> StartInBackground(RestoreCoordinator coordinator, ScriptedRestoreUi ui) =>
        Task.Run(() => RestoreKit.Start(Root, coordinator, ui));

    private static async Task<T> Within<T>(Task<T> task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Timeout));
        Assert.True(finished == task, "timed out (deadlock?)");
        return await task;
    }

    private static async Task WaitFor(SemaphoreSlim signal) => Assert.True(await signal.WaitAsync(Timeout), "the waiting screen was not shown");

    private async Task<string> ConfirmedAsync()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        RestoreKit.InsertSystem(Db, "CURRENT1");
        return await RestoreKit.RequestAndConfirmAsync(Root, entry);
    }

    /// <summary>A window that owns a <c>Published</c> op whose initializer failed (migration backup blocked).</summary>
    private async Task<(RestoreCoordinator Owner, string Op, string CurrentDump)> PublishedInitFailedOwnerAsync()
    {
        BackupKit.CreateDatabase(Root);
        RestoreKit.InsertSystem(Db, "CURRENT1");
        var currentDump = BackupKit.Dump(Db);
        var entry = RestoreKit.PlaceOlderDaily(Root, version: 6);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        File.WriteAllText(Root.Paths.MigrationBackupsDirectory, "blocks the folder");
        var owner = Coordinator(token: op);
        var (_, _, error) = RestoreKit.Start(Root, owner, new ScriptedRestoreUi());
        Assert.IsType<DatabaseStartupException>(error);
        Assert.Equal(RestoreOwnerSituation.PublishedInitializationFailed, owner.Status!.Situation);
        File.Delete(Root.Paths.MigrationBackupsDirectory);
        return (owner, op, currentDump);
    }

    /// <summary>A window that owns a <c>MovedAside</c> op whose publication (X9) failed.</summary>
    private async Task<(RestoreCoordinator Owner, string Op, string CurrentDump)> MovedAsideOwnerAsync()
    {
        var op = await ConfirmedAsync();
        var currentDump = BackupKit.Dump(Db);
        var owner = Coordinator(token: op, files: RestoreKit.Faulty(Root, ops => ops.OnMove = (from, _) =>
        {
            if (from.EndsWith(RestoreNames.Candidate, StringComparison.Ordinal))
                throw new IOException("denied", unchecked((int)0x80070005));
        }));
        var (_, stop, _) = RestoreKit.Start(Root, owner, new ScriptedRestoreUi());
        Assert.Equal(RestoreEntryKind.OwnerStopped, stop!.Kind);
        return (owner, op, currentDump);
    }

    // ---- O / C: waiting for a live owner holds G only ----------------------------------------------------------------

    [Fact]
    public async Task O1_C2_C3_CompletionUnrecorded_WaiterHoldsGateOnly_OwnerTryAgain_WaiterStartsWhileTheOwnerRuns()
    {
        var op = await ConfirmedAsync();
        var failCompleted = false;
        var owner = Coordinator(token: op, hook: s => failCompleted |= s == RestoreStage.BeforeCompletedWrite,
            files: RestoreKit.Faulty(Root, ops => ops.OnWrite = _ =>
            {
                if (failCompleted)
                    throw new IOException("disk", unchecked((int)0x80070070));
            }));
        Assert.NotNull(RestoreKit.Start(Root, owner, new ScriptedRestoreUi()).Result);
        Assert.Equal(RestoreOwnerSituation.CompletionUnrecorded, owner.Status!.Situation);
        failCompleted = false;

        var waiter = Coordinator();
        var observed = new ConcurrentQueue<string>();
        var ui = new ScriptedRestoreUi
        {
            OnWaiting = (kind, _) =>
            {
                using var probe = new InstanceLocks(Root.Paths);
                observed.Enqueue($"{kind} gate:{waiter.Locks.HoldsGate} presence:{waiter.Locks.HoldsPresence} ownR:{waiter.Locks.HoldsRestoreLock} R-free:{probe.TryRestoreLock()}");
            },
        };
        var start = StartInBackground(waiter, ui);
        await WaitFor(ui.WaitingShown);

        var tried = owner.RecordCompletion(); // the owner keeps running
        var (result, stop, error) = await Within(start);

        Assert.True(tried.Succeeded, tried.Message);
        Assert.Null(error);
        Assert.Null(stop);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome);
        Assert.Equal("Owned gate:True presence:False ownR:False R-free:True", observed.First());
        Assert.Empty(ui.Asks);
        Assert.Equal(RestoreState.Completed, RestoreKit.Journal(Root, op).State);
        Assert.True(owner.Locks.HoldsPresence); // the owner never had to close
    }

    [Fact]
    public async Task C4_OwnerExitsWithoutRecording_TheWaiterReinitializesAndRecordsCompleted()
    {
        var op = await ConfirmedAsync();
        var failCompleted = false;
        var owner = Coordinator(token: op, hook: s => failCompleted |= s == RestoreStage.BeforeCompletedWrite,
            files: RestoreKit.Faulty(Root, ops => ops.OnWrite = _ =>
            {
                if (failCompleted)
                    throw new IOException("disk", unchecked((int)0x80070070));
            }));
        RestoreKit.Start(Root, owner, new ScriptedRestoreUi());
        var ui = new ScriptedRestoreUi();
        var start = StartInBackground(Coordinator(), ui);
        await WaitFor(ui.WaitingShown);

        owner.Dispose();
        RestoreKit.ClearPools(Root);
        var (result, _, error) = await Within(start);

        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome);
        Assert.Equal(RestoreState.Completed, RestoreKit.Journal(Root, op).State);
    }

    [Fact]
    public async Task O2_PublishedInitFailure_OwnerPutBack_ExactlyOneProcessExecutesUndo_TheTokenStartThenStartsNormally()
    {
        var (owner, op, currentDump) = await PublishedInitFailedOwnerAsync();
        var waiterUi = new ScriptedRestoreUi { OnAsk = (_, _) => RestoreAskAnswer.Now };
        var waiting = StartInBackground(Coordinator(), waiterUi);
        await WaitFor(waiterUi.WaitingShown);

        var putBack = owner.RequestPutBack();
        Assert.True(putBack.Succeeded, putBack.Message);
        Assert.Equal(UndoOrigin.AfterPublication, RestoreKit.Journal(Root, op).Undo!.Origin);
        var tokenUi = new ScriptedRestoreUi();
        var tokenStart = StartInBackground(Coordinator(token: putBack.RelaunchOp), tokenUi); // the relaunched child
        owner.Dispose(); // the owner exits after relaunching
        RestoreKit.ClearPools(Root);

        var first = await Within(waiting);
        var second = await Within(tokenStart);

        Assert.Null(first.Error);
        Assert.Null(second.Error);
        Assert.Equal(RestoreState.Undone, RestoreKit.Journal(Root, op).State);
        var undoers = new[] { waiterUi, tokenUi }.Count(u => u.Progress.Any(p => p.Text.StartsWith("Putting back", StringComparison.Ordinal)));
        Assert.Equal(1, undoers);
        RestoreKit.ClearPools(Root);
        Assert.Equal(currentDump, BackupKit.Dump(Db));
        Assert.True(File.Exists(Path.Combine(RestoreKit.Restored(Root, op), "workhub.db")));
    }

    [Fact]
    public async Task O3_StateChangesWhileWaiting_TheWaiterActsOnlyOnTheReRead_PutBack()
    {
        var (owner, op, _) = await PublishedInitFailedOwnerAsync();
        var changed = false;
        var ui = new ScriptedRestoreUi();
        var waiter = Coordinator(hook: stage =>
        {
            if (stage != RestoreStage.WaitRead || changed)
                return;
            changed = true; // the waiter has read (and cached) Published
            Assert.True(owner.RequestPutBack().Succeeded);
            owner.Dispose();
            RestoreKit.ClearPools(Root);
        });

        var (_, stop, error) = await Within(StartInBackground(waiter, ui)); // ask answered Not now

        Assert.Null(error);
        Assert.Equal(RestoreEntryKind.Exit, stop!.Kind);
        var (kind, seen) = Assert.Single(ui.Asks);
        Assert.Equal(RestoreAskKind.PutBack, kind);
        Assert.Equal(RestoreState.UndoRequested, seen.State); // never acted on the cached Published
        Assert.Equal(RestoreState.UndoRequested, RestoreKit.Journal(Root, op).State);
    }

    [Fact]
    public async Task O3_StateChangesWhileWaiting_OwnerTryAgain_TheWaiterStartsNormally_NoPublishedAction()
    {
        var op = await ConfirmedAsync();
        var failCompleted = false;
        var owner = Coordinator(token: op, hook: s => failCompleted |= s == RestoreStage.BeforeCompletedWrite,
            files: RestoreKit.Faulty(Root, ops => ops.OnWrite = _ =>
            {
                if (failCompleted)
                    throw new IOException("disk", unchecked((int)0x80070070));
            }));
        RestoreKit.Start(Root, owner, new ScriptedRestoreUi());
        failCompleted = false;
        var log = new RecordingLog();
        var ui = new ScriptedRestoreUi();
        var waiter = Coordinator(log: log, hook: stage =>
        {
            if (stage == RestoreStage.WaitRead && owner.Status is not null)
                Assert.True(owner.RecordCompletion().Succeeded);
        });

        var (result, _, error) = await Within(StartInBackground(waiter, ui));

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.DoesNotContain("acting on Published", log.All);
        Assert.Empty(ui.Asks);
    }

    [Fact]
    public async Task O4_DuringAnExclusiveExecution_LaterStartsNeverPassTheGate()
    {
        var op = await ConfirmedAsync();
        var laterUi = new ScriptedRestoreUi();
        Task<(InitializationResult? Result, RestoreStartupException? Stop, Exception? Error)>? later = null;
        RestoreCoordinator? laterCoordinator = null;
        var duringExecution = new List<string>();
        var executor = Coordinator(token: op, hook: stage =>
        {
            if (stage == RestoreStage.MovingAsidePublished)
            {
                laterCoordinator = Coordinator();
                later = StartInBackground(laterCoordinator, laterUi);
                Thread.Sleep(800);
                using var probe = new InstanceLocks(Root.Paths);
                duringExecution.Add($"gate free:{probe.TryAcquireGate()} later holds gate:{laterCoordinator.Locks.HoldsGate} presence:{laterCoordinator.Locks.HoldsPresence}");
            }
        });

        var (result, _, error) = RestoreKit.Start(Root, executor, new ScriptedRestoreUi());
        var laterResult = await Within(later!);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(["gate free:False later holds gate:False presence:False"], duringExecution);
        Assert.True(laterUi.GateWaitShown);
        Assert.Null(laterResult.Error);
        Assert.NotNull(laterResult.Result); // then a normal start
        Assert.Empty(laterUi.Asks);
    }

    // ---- P: cancellation policy (R25) -------------------------------------------------------------------------------

    [Theory]
    [InlineData(UndoOrigin.BeforePublication)]
    [InlineData(UndoOrigin.AfterPublication)]
    public async Task P1_P2_UndoRequested_CannotBeCancelled_ExitAndNotNowKeepTheRecoveryBarrier_TheTokenResumes(UndoOrigin origin)
    {
        var (owner, op, currentDump) = origin == UndoOrigin.BeforePublication ? await MovedAsideOwnerAsync() : await PublishedInitFailedOwnerAsync();
        var putBack = owner.RequestPutBack();
        Assert.True(putBack.Succeeded, putBack.Message);
        owner.Dispose();
        RestoreKit.ClearPools(Root);
        var requested = RestoreKit.Journal(Root, op);
        Assert.Equal(origin, requested.Undo!.Origin);
        var dataBefore = RestoreKit.DataSet(Root);
        var dataIdBefore = File.Exists(Db) ? RestoreKit.FileId(Db) : null;
        var previousIdBefore = RestoreKit.FileId(Path.Combine(RestoreKit.Previous(Root, op), "workhub.db"));

        void AssertBarrierKept()
        {
            var journal = RestoreKit.Journal(Root, op);
            Assert.Equal(RestoreState.UndoRequested, journal.State);
            Assert.Equal(requested.Revision, journal.Revision);
            Assert.Equal(dataBefore, RestoreKit.DataSet(Root)); // BeforePublication: still empty — never seeded
            Assert.Equal(dataIdBefore, File.Exists(Db) ? RestoreKit.FileId(Db) : null);
            Assert.Equal(previousIdBefore, RestoreKit.FileId(Path.Combine(RestoreKit.Previous(Root, op), "workhub.db")));
            Assert.False(Directory.Exists(RestoreKit.Restored(Root, op)));
            Assert.Equal(origin == UndoOrigin.BeforePublication, File.Exists(RestoreKit.Candidate(Root, op)));
        }

        // Another window keeps Presence: W-PutBack. Cancel is not offered (a stray Cancel is ignored); Exit changes nothing.
        var window = OtherWindow();
        var ui = new ScriptedRestoreUi();
        var waiting = StartInBackground(Coordinator(), ui);
        await WaitFor(ui.WaitingShown);
        Assert.Equal(RestoreWaitKind.PutBack, ui.Waits.Single().Kind);
        ui.Actions.Enqueue(RestoreWaitAction.CancelRestore);
        await Task.Delay(200);
        AssertBarrierKept();
        ui.Actions.Enqueue(RestoreWaitAction.Exit);
        var exited = await Within(waiting);
        Assert.Equal(RestoreEntryKind.Exit, exited.Stop!.Kind);
        Assert.Null(exited.Result);
        AssertBarrierKept();

        // The window closes; a start without token asks Put back now · Not now — Not now changes nothing.
        window.Dispose();
        var askUi = new ScriptedRestoreUi { OnAsk = (_, _) => RestoreAskAnswer.Cancel }; // even a Cancel answer is Not now
        var asked = RestoreKit.Start(Root, Coordinator(), askUi);
        Assert.Equal(RestoreEntryKind.Exit, asked.Stop!.Kind);
        Assert.Equal(RestoreAskKind.PutBack, askUi.Asks.Single().Kind);
        AssertBarrierKept();

        // The token start resumes it; the initializer runs once, on the previous data, after Undone.
        var (result, _, error) = RestoreKit.Start(Root, Coordinator(token: op), new ScriptedRestoreUi());
        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome);
        Assert.Equal(RestoreState.Undone, RestoreKit.Journal(Root, op).State);
        RestoreKit.ClearPools(Root);
        Assert.Equal(currentDump, BackupKit.Dump(Db));
        Assert.Equal(previousIdBefore, RestoreKit.FileId(Db));
    }

    [Fact]
    public async Task P5_CancelRestoreFromTheWaitingScreen_BeforeAnyMove_CurrentDataUntouched()
    {
        var op = await ConfirmedAsync();
        var dump = BackupKit.Dump(Db);
        OtherWindow();
        var ui = new ScriptedRestoreUi();
        var start = StartInBackground(Coordinator(token: op), ui);
        await WaitFor(ui.WaitingShown);
        Assert.Equal(RestoreWaitKind.Confirmed, ui.Waits.Single().Kind);

        ui.Actions.Enqueue(RestoreWaitAction.CancelRestore);
        var (result, _, error) = await Within(start);

        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome); // normal start on the current data
        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(RestoreState.Cancelled, journal.State);
        Assert.Equal(RestoreState.Confirmed, journal.CancelledFrom);
        Assert.False(File.Exists(RestoreKit.Candidate(Root, op)));
        RestoreKit.ClearPools(Root);
        Assert.Equal(dump, BackupKit.Dump(Db));
    }

    [Theory]
    [InlineData(RestoreState.Confirmed)]
    [InlineData(RestoreState.Executing)]
    public async Task P5_CancelFromTheAsk_ConfirmedOrInterruptedExecuting(RestoreState state)
    {
        var op = await ConfirmedAsync();
        if (state == RestoreState.Executing)
            RestoreKit.WriteJournal(Root, RestoreKit.Journal(Root, op).Next(RestoreState.Executing, DateTimeOffset.UtcNow));
        var ui = new ScriptedRestoreUi { OnAsk = (_, _) => RestoreAskAnswer.Cancel };

        var (result, _, error) = RestoreKit.Start(Root, Coordinator(), ui);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(state == RestoreState.Confirmed ? RestoreAskKind.Restore : RestoreAskKind.Interrupted, ui.Asks.Single().Kind);
        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(RestoreState.Cancelled, journal.State);
        Assert.Equal(state, journal.CancelledFrom);
    }

    [Fact]
    public async Task P5_AcknowledgedMissingCurrentData_Cancelled_ThenTheExistingSeededStart()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        File.Delete(Db);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);

        var (result, _, error) = RestoreKit.Start(Root, Coordinator(), new ScriptedRestoreUi { OnAsk = (_, _) => RestoreAskAnswer.Cancel });

        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Created, result!.Database.Outcome); // nothing was ever moved
        Assert.Equal(RestoreState.Cancelled, RestoreKit.Journal(Root, op).State);
    }

    public static TheoryData<string> StaleChanges() => ["executing", "revision", "undo"];

    [Theory]
    [MemberData(nameof(StaleChanges))]
    public async Task P6_StateChangesBeforeCancelGetsTheRestoreLock_TheReReadRefuses_NothingIsWritten(string change)
    {
        var op = await ConfirmedAsync();
        OtherWindow();
        var ui = new ScriptedRestoreUi();
        RestoreJournal? changed = null;
        var waiter = Coordinator(hook: stage =>
        {
            if (stage != RestoreStage.WaitRead || changed is not null || ui.Waits.IsEmpty)
                return;
            var current = RestoreKit.Journal(Root, op); // after the waiter's read of what it shows
            changed = change switch
            {
                "executing" => current.Next(RestoreState.Executing, DateTimeOffset.UtcNow),
                "revision" => current.Next(RestoreState.Confirmed, DateTimeOffset.UtcNow),
                _ => current.Next(RestoreState.UndoRequested, DateTimeOffset.UtcNow) with { Undo = new UndoRecord(DateTimeOffset.UtcNow, UndoOrigin.BeforePublication) },
            };
            RestoreKit.WriteJournal(Root, changed);
            ui.Actions.Enqueue(RestoreWaitAction.CancelRestore); // pressed on the stale screen
        });
        var start = StartInBackground(waiter, ui);
        await WaitFor(ui.WaitingShown);
        var deadline = DateTime.UtcNow + Timeout;
        while (ui.Notices.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        ui.Actions.Enqueue(RestoreWaitAction.Exit);
        await Within(start);

        Assert.Contains("can no longer be cancelled here", Assert.Single(ui.Notices));
        Assert.Equal(changed, RestoreKit.Journal(Root, op));
        Assert.True(File.Exists(RestoreKit.Candidate(Root, op)));
    }

    [Fact]
    public async Task P6_TheRestoreLockIsBusyLongerThanTheRetry_CancelIsRefused_NothingIsWritten()
    {
        var op = await ConfirmedAsync();
        OtherWindow();
        var ui = new ScriptedRestoreUi();
        var start = StartInBackground(Coordinator(), ui);
        await WaitFor(ui.WaitingShown);
        var before = RestoreJournalText.Of(Root, op);

        using (var r = new InstanceLocks(Root.Paths))
        {
            Assert.True(r.TryRestoreLock());
            ui.Actions.Enqueue(RestoreWaitAction.CancelRestore);
            var deadline = DateTime.UtcNow + Timeout;
            while (ui.Notices.IsEmpty && DateTime.UtcNow < deadline)
                await Task.Delay(20);
        }

        ui.Actions.Enqueue(RestoreWaitAction.Exit);
        await Within(start);
        Assert.Contains("changing the restore right now", Assert.Single(ui.Notices));
        Assert.Equal(before, RestoreJournalText.Of(Root, op));
    }

    [Theory]
    [InlineData(RestoreState.Cancelled, RestoreState.UndoRequested)]
    [InlineData(RestoreState.Cancelled, RestoreState.Confirmed)]
    [InlineData(RestoreState.Aborted, null)]
    [InlineData(RestoreState.Abandoned, null)]
    public async Task P7_TerminalJournalWithDataSetAside_FailsClosed_NoInitializer_NoSeeding_NoCleanup(RestoreState state, RestoreState? cancelledFrom)
    {
        var (owner, op, _) = await MovedAsideOwnerAsync(); // previous\ holds the data, Data\ is empty, candidate staged
        owner.Dispose();
        var moved = RestoreKit.Journal(Root, op);
        RestoreKit.WriteJournal(Root, moved.Next(state, DateTimeOffset.UtcNow) with { CancelledFrom = cancelledFrom });
        var candidateHash = BackupKit.Hash(RestoreKit.Candidate(Root, op));

        var (result, stop, error) = RestoreKit.Start(Root, Coordinator(), new ScriptedRestoreUi());

        Assert.Null(error);
        Assert.Null(result);
        Assert.Equal(RestoreEntryKind.RecoveryRequired, stop!.Kind);
        Assert.Contains("inconsistent", stop.Message);
        Assert.Empty(RestoreKit.DataSet(Root)); // no seeded database
        Assert.Equal(candidateHash, BackupKit.Hash(RestoreKit.Candidate(Root, op))); // no cleanup of that op
        Assert.True(File.Exists(Path.Combine(RestoreKit.Previous(Root, op), "workhub.db")));
    }

    // ---- E: ambiguity fails closed ----------------------------------------------------------------------------------

    public static TheoryData<string> Ambiguities() => ["garbage-journal", "two-unfinished", "unknown-folder", "journal-less-with-db", "copied-folder", "unknown-version"];

    [Theory]
    [MemberData(nameof(Ambiguities))]
    public async Task E_AmbiguousRestoreMetadata_FailsClosed(string ambiguity)
    {
        var op = await ConfirmedAsync();
        var journalPath = Path.Combine(RestoreKit.OpDir(Root, op), RestoreNames.Journal);
        switch (ambiguity)
        {
            case "garbage-journal":
                File.WriteAllText(journalPath, "{ not json");
                break;
            case "two-unfinished":
                var second = "20261001-000000-00000002";
                Directory.CreateDirectory(RestoreKit.OpDir(Root, second));
                RestoreKit.WriteJournal(Root, RestoreKit.Journal(Root, op) with { Op = second });
                break;
            case "unknown-folder":
                Directory.CreateDirectory(Path.Combine(Root.Paths.RestoreDirectory, "something-else"));
                break;
            case "journal-less-with-db":
                var orphan = Path.Combine(Root.Paths.RestoreDirectory, "20261001-000000-00000003");
                Directory.CreateDirectory(orphan);
                File.WriteAllBytes(Path.Combine(orphan, "workhub.db"), [1]);
                break;
            case "copied-folder":
                RestoreKit.WriteJournal(Root, RestoreKit.Journal(Root, op) with { DataDir = new DirectoryIdentity("00000000", "00000000-0000000000000001") });
                break;
            case "unknown-version":
                File.WriteAllText(journalPath, File.ReadAllText(journalPath).Replace("\"Version\": 2", "\"Version\": 3", StringComparison.Ordinal));
                break;
        }

        var dump = BackupKit.Dump(Db);
        var (result, stop, error) = RestoreKit.Start(Root, Coordinator(token: op), new ScriptedRestoreUi());

        Assert.Null(error);
        Assert.Null(result);
        Assert.Equal(RestoreEntryKind.RecoveryRequired, stop!.Kind);
        Assert.Equal(dump, BackupKit.Dump(Db));
        using var locks = new InstanceLocks(Root.Paths);
        Assert.True(locks.TryAcquireGate()); // nothing kept held
    }

    // ---- Q5–Q7 / R9: confirmed requests at start ----------------------------------------------------------------------

    [Fact]
    public async Task Q5_R9_ConfirmedWithoutToken_IsAsked_NotNowChangesNothing_ATokenForAnotherOpStillAsks()
    {
        var op = await ConfirmedAsync();
        var before = RestoreJournalText.Of(Root, op);

        var ui = new ScriptedRestoreUi();
        var (_, stop, _) = RestoreKit.Start(Root, Coordinator(token: "20261001-000000-0000aaaa"), ui);

        Assert.Equal(RestoreEntryKind.Exit, stop!.Kind);
        Assert.Equal(RestoreAskKind.Restore, ui.Asks.Single().Kind);
        Assert.Equal(before, RestoreJournalText.Of(Root, op));

        var now = RestoreKit.Start(Root, Coordinator(), new ScriptedRestoreUi { OnAsk = (_, _) => RestoreAskAnswer.Now });
        Assert.Null(now.Error);
        Assert.Equal(RestoreState.Completed, RestoreKit.Journal(Root, op).State);
    }

    [Fact]
    public async Task Q6_Q7_RequesterTokenChildAndPlainStartsRace_ExactlyOneExecution_NoDeadlock()
    {
        var op = await ConfirmedAsync();
        var uis = Enumerable.Range(0, 5).Select(_ => new ScriptedRestoreUi { OnAsk = (_, _) => RestoreAskAnswer.Now }).ToList();
        var starts = uis.Select((ui, i) => StartInBackground(Coordinator(token: i == 0 ? op : null), ui)).ToList();
        var second = RestoreKit.Requester(Root).BeginAsync(RestoreKit.DailyEntry(Root, Directory.GetFiles(Root.Paths.DailyBackupsDirectory).Select(Path.GetFileName).First()!));

        var results = await Within(Task.WhenAll(starts));
        var refused = await Within(second);

        Assert.All(results, r => Assert.Null(r.Error));
        Assert.Equal(1, uis.Count(u => u.Progress.Any(p => p.Text.StartsWith("Restoring", StringComparison.Ordinal))));
        Assert.Equal(RestoreState.Completed, RestoreKit.Journal(Root, op).State);
        refused.Request?.Dispose(); // refused while unfinished, or a fresh reservation after completion — never a second execution
        Assert.Single(RestoreKit.Ops(Root), o => RestoreKit.Journal(Root, o).State == RestoreState.Completed);
    }
}
