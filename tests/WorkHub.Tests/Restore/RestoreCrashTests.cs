using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Restore;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Restore;

/// <summary>
/// Real process kills (Phase 10 §18; tests D interruption points, U1, M2, P3, P4, Q3): the CrashProbe child runs the real
/// coordinator / requester and is KILLED at a boundary; then a fresh start in this process reconciles from journal and
/// file facts. Process kills only — nothing here claims power-loss safety.
/// </summary>
public sealed class RestoreCrashTests : RestoreTestBase
{
    private string Db => Root.Paths.DatabaseFile;

    private sealed record Arranged(string Op, string BackupDump, string CurrentDump);

    private async Task<Arranged> ConfirmedAsync(bool hotJournal = false)
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var backupDump = BackupKit.Dump(BackupKit.Daily(Root, entry.FileName));
        RestoreKit.InsertSystem(Db, "CURRENT1");
        var currentDump = BackupKit.Dump(Db);
        if (hotJournal)
        {
            using (var child = RestoreKit.StartProbe("hot-journal", Db))
                child.Kill();
            Assert.True(File.Exists(Db + "-journal"));
        }

        return new Arranged(await RestoreKit.RequestAndConfirmAsync(Root, entry), backupDump, currentDump);
    }

    private void Kill(params string[] args)
    {
        using var child = RestoreKit.StartProbe(args);
        child.Kill();
    }

    private void AssertRestoredAfterRecovery(Arranged arranged)
    {
        var (result, stop, error) = RestoreKit.Start(Root, Coordinator(token: arranged.Op), new ScriptedRestoreUi());
        Assert.Null(error);
        Assert.Null(stop);
        Assert.NotEqual(DatabaseInitializationOutcome.Created, result!.Database.Outcome); // never seeded
        Assert.Equal(RestoreState.Completed, RestoreKit.Journal(Root, arranged.Op).State);
        RestoreKit.ClearPools(Root);
        Assert.Equal(arranged.BackupDump, BackupKit.Dump(Db));
        Assert.Equal(arranged.CurrentDump, BackupKit.Dump(Path.Combine(RestoreKit.Previous(Root, arranged.Op), "workhub.db")));
    }

    // ---- D: every restore boundary -----------------------------------------------------------------------------------

    public static TheoryData<string, RestoreState, bool> RestoreBoundaries() => new()
    {
        { nameof(RestoreStage.ExecutingPublished), RestoreState.Executing, false },
        { nameof(RestoreStage.AsFoundCopied), RestoreState.Executing, true },
        { nameof(RestoreStage.Preserved), RestoreState.Executing, true },
        { nameof(RestoreStage.MovingAsidePublished), RestoreState.MovingAside, false },
        { nameof(RestoreStage.PreviousFileMoved), RestoreState.MovingAside, true },
        { nameof(RestoreStage.MovedAsidePublished), RestoreState.MovedAside, false },
        { nameof(RestoreStage.CandidateMoved), RestoreState.MovedAside, false },
        { nameof(RestoreStage.PublishedPublished), RestoreState.Published, false },
    };

    [Theory]
    [MemberData(nameof(RestoreBoundaries))]
    public async Task D_KilledAtARestoreBoundary_TheNextStartReconcilesAndFinishes(string stageName, RestoreState expected, bool hotJournal)
    {
        var stage = Enum.Parse<RestoreStage>(stageName);
        var arranged = await ConfirmedAsync(hotJournal);

        Kill("restore", Root.Paths.Root, arranged.Op, stage.ToString());

        var journal = RestoreKit.Journal(Root, arranged.Op);
        Assert.Equal(expected, journal.State);
        if (expected is RestoreState.MovingAside)
            Assert.True(RestoreReconciler.SplitBetween(journal.PreviousInventory!, ReadFacts(arranged.Op).Data, ReadFacts(arranged.Op).Previous ?? []));
        if (stage == RestoreStage.CandidateMoved)
            Assert.Equal(journal.Candidate!.FileId, RestoreKit.FileId(Db)); // published by ID, journal behind
        if (expected is RestoreState.MovedAside or RestoreState.MovingAside && stage != RestoreStage.CandidateMoved)
            Assert.False(File.Exists(Db) && RestoreKit.FileId(Db) == journal.Candidate!.FileId);

        AssertRestoredAfterRecovery(arranged);
    }

    private RestoreFileFacts ReadFacts(string op)
    {
        using var locks = new InstanceLocks(Root.Paths);
        return RestoreFacts.Read(Root.Paths, new RestoreFileOps(Root.Paths, () => locks.HoldsRestoreLock), op);
    }

    // ---- U / M: every Put-back boundary, both origins -----------------------------------------------------------------

    private async Task<(string Op, string CurrentDump, string? CandidateHash)> UndoRequestedAsync(UndoOrigin origin)
    {
        BackupKit.CreateDatabase(Root);
        RestoreKit.InsertSystem(Db, "CURRENT1");
        var currentDump = BackupKit.Dump(Db);
        RestoreCoordinator owner;
        string op;
        if (origin == UndoOrigin.AfterPublication)
        {
            var entry = RestoreKit.PlaceOlderDaily(Root, version: 6);
            op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
            File.WriteAllText(Root.Paths.MigrationBackupsDirectory, "blocks the folder");
            owner = Coordinator(token: op);
            Assert.IsType<DatabaseStartupException>(RestoreKit.Start(Root, owner, new ScriptedRestoreUi()).Error);
            File.Delete(Root.Paths.MigrationBackupsDirectory);
        }
        else
        {
            var entry = RestoreKit.PlaceDaily(Root);
            op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
            owner = Coordinator(token: op, files: RestoreKit.Faulty(Root, ops => ops.OnMove = (from, _) =>
            {
                if (from.EndsWith(RestoreNames.Candidate, StringComparison.Ordinal))
                    throw new IOException("denied", unchecked((int)0x80070005));
            }));
            Assert.Equal(RestoreEntryKind.OwnerStopped, RestoreKit.Start(Root, owner, new ScriptedRestoreUi()).Stop!.Kind);
        }

        Assert.True(owner.RequestPutBack().Succeeded);
        owner.Dispose();
        RestoreKit.ClearPools(Root);
        var candidateHash = origin == UndoOrigin.BeforePublication ? BackupKit.Hash(RestoreKit.Candidate(Root, op)) : null;
        return (op, currentDump, candidateHash);
    }

    public static TheoryData<UndoOrigin, string, RestoreState> UndoBoundaries() => new()
    {
        { UndoOrigin.AfterPublication, nameof(RestoreStage.UndoingPublished), RestoreState.Undoing },
        { UndoOrigin.AfterPublication, nameof(RestoreStage.RestoredFileMoved), RestoreState.Undoing },
        { UndoOrigin.AfterPublication, nameof(RestoreStage.RestoredAsidePublished), RestoreState.RestoredAside },
        { UndoOrigin.AfterPublication, nameof(RestoreStage.PreviousFileReturned), RestoreState.RestoredAside },
        { UndoOrigin.AfterPublication, nameof(RestoreStage.UndonePublished), RestoreState.Undone },
        { UndoOrigin.BeforePublication, nameof(RestoreStage.UndoingPublished), RestoreState.Undoing },
        { UndoOrigin.BeforePublication, nameof(RestoreStage.RestoredAsidePublished), RestoreState.RestoredAside },
        { UndoOrigin.BeforePublication, nameof(RestoreStage.PreviousFileReturned), RestoreState.RestoredAside },
        { UndoOrigin.BeforePublication, nameof(RestoreStage.UndonePublished), RestoreState.Undone },
    };

    [Theory]
    [MemberData(nameof(UndoBoundaries))]
    public async Task U1_M2_KilledAtAPutBackBoundary_RollsForwardToUndone_InitializerOnlyAfterUndone(UndoOrigin origin, string stage, RestoreState expected)
    {
        var (op, currentDump, candidateHash) = await UndoRequestedAsync(origin);

        Kill("restore", Root.Paths.Root, op, stage);

        Assert.Equal(expected, RestoreKit.Journal(Root, op).State);
        if (origin == UndoOrigin.BeforePublication)
        {
            Assert.Equal(candidateHash, BackupKit.Hash(RestoreKit.Candidate(Root, op)));
            Assert.False(Directory.Exists(RestoreKit.Restored(Root, op)));
        }

        var log = new RecordingLog();
        var (result, stop, error) = RestoreKit.Start(Root, Coordinator(token: expected == RestoreState.Undone ? null : op), new ScriptedRestoreUi(), log);

        Assert.Null(error);
        Assert.Null(stop);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome);
        Assert.Equal(RestoreState.Undone, RestoreKit.Journal(Root, op).State);
        RestoreKit.ClearPools(Root);
        Assert.Equal(currentDump, BackupKit.Dump(Db));
        if (origin == UndoOrigin.BeforePublication)
        {
            Assert.Equal(candidateHash, BackupKit.Hash(RestoreKit.Candidate(Root, op)));
            Assert.False(Directory.Exists(RestoreKit.Restored(Root, op)));
        }
        else
        {
            Assert.True(File.Exists(Path.Combine(RestoreKit.Restored(Root, op), "workhub.db")));
        }
    }

    // ---- P3: kills around the Put-back intent write ---------------------------------------------------------------------

    [Theory]
    [InlineData(UndoOrigin.AfterPublication, nameof(RestoreStage.BeforeUndoRequestedWrite))]
    [InlineData(UndoOrigin.AfterPublication, nameof(RestoreStage.AfterUndoRequestedWrite))]
    [InlineData(UndoOrigin.BeforePublication, nameof(RestoreStage.BeforeUndoRequestedWrite))]
    [InlineData(UndoOrigin.BeforePublication, nameof(RestoreStage.AfterUndoRequestedWrite))]
    public async Task P3_OwnerKilledImmediatelyBeforeOrAfterTheUndoRequestedWrite_TheNextStartReconcilesSafely(UndoOrigin origin, string stage)
    {
        var arranged = await ConfirmedAsync();
        var args = new List<string> { "putback", Root.Paths.Root, arranged.Op, stage };
        if (origin == UndoOrigin.BeforePublication)
            args.Add("fail-publish");

        Kill([.. args]);

        var journal = RestoreKit.Journal(Root, arranged.Op);
        if (stage == nameof(RestoreStage.BeforeUndoRequestedWrite))
        {
            // Put back was never requested: the original Restore continues (owner gone).
            Assert.Equal(origin == UndoOrigin.AfterPublication ? RestoreState.Published : RestoreState.MovedAside, journal.State);
            Assert.Null(journal.Undo);
            AssertRestoredAfterRecovery(arranged);
            return;
        }

        Assert.Equal(RestoreState.UndoRequested, journal.State);
        Assert.Equal(origin, journal.Undo!.Origin);
        var journalText = RestoreJournalText.Of(Root, arranged.Op);
        var dataBefore = RestoreKit.DataSet(Root);

        // A start without token: Put back now · Not now; even a Cancel answer changes nothing (R25).
        var ui = new ScriptedRestoreUi { OnAsk = (_, _) => RestoreAskAnswer.Cancel };
        var asked = RestoreKit.Start(Root, Coordinator(), ui);
        Assert.Equal(RestoreEntryKind.Exit, asked.Stop!.Kind);
        Assert.Equal(journalText, RestoreJournalText.Of(Root, arranged.Op));
        Assert.Equal(dataBefore, RestoreKit.DataSet(Root)); // BeforePublication: still empty, never seeded

        var (result, _, error) = RestoreKit.Start(Root, Coordinator(token: arranged.Op), new ScriptedRestoreUi());
        Assert.Null(error);
        Assert.Equal(RestoreState.Undone, RestoreKit.Journal(Root, arranged.Op).State);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome);
        RestoreKit.ClearPools(Root);
        Assert.Equal(arranged.CurrentDump, BackupKit.Dump(Db));
    }

    // ---- P4: kills around the Cancelled write ---------------------------------------------------------------------------

    [Fact]
    public async Task P4_KilledImmediatelyBeforeTheCancelledWrite_ConfirmedIsKept_AndAskedAgain()
    {
        var arranged = await ConfirmedAsync();
        var before = RestoreJournalText.Of(Root, arranged.Op);

        Kill("cancel", Root.Paths.Root, arranged.Op, nameof(RestoreStage.BeforeCancelledWrite));

        Assert.Equal(before, RestoreJournalText.Of(Root, arranged.Op));
        Assert.True(File.Exists(RestoreKit.Candidate(Root, arranged.Op)));
        var ui = new ScriptedRestoreUi();
        Assert.Equal(RestoreEntryKind.Exit, RestoreKit.Start(Root, Coordinator(), ui).Stop!.Kind);
        Assert.Equal(RestoreAskKind.Restore, ui.Asks.Single().Kind);
        RestoreKit.ClearPools(Root);
        Assert.Equal(arranged.CurrentDump, BackupKit.Dump(Db));
    }

    [Fact]
    public async Task P4_KilledImmediatelyAfterTheCancelledWrite_TheGuardPasses_TheLeftoverCandidateIsRemoved_NormalStart()
    {
        var arranged = await ConfirmedAsync();

        Kill("cancel", Root.Paths.Root, arranged.Op, nameof(RestoreStage.AfterCancelledWrite));

        Assert.Equal(RestoreState.Cancelled, RestoreKit.Journal(Root, arranged.Op).State);
        Assert.True(File.Exists(RestoreKit.Candidate(Root, arranged.Op)));
        var (result, _, error) = RestoreKit.Start(Root, Coordinator(), new ScriptedRestoreUi());
        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome);
        Assert.False(File.Exists(RestoreKit.Candidate(Root, arranged.Op)));
        RestoreKit.ClearPools(Root);
        Assert.Equal(arranged.CurrentDump, BackupKit.Dump(Db));
    }

    // ---- Q3: requester killed ------------------------------------------------------------------------------------------

    [Fact]
    public void Q3_RequesterKilledInStaged_TheNextStartAbandonsIt_AndRemovesTheCandidate()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);

        Kill("request", Root.Paths.Root, entry.FileName, "staged");

        var op = RestoreKit.Ops(Root).Single();
        Assert.Equal(RestoreState.Staged, RestoreKit.Journal(Root, op).State);
        var (result, _, error) = RestoreKit.Start(Root, Coordinator(), new ScriptedRestoreUi());
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(RestoreState.Abandoned, RestoreKit.Journal(Root, op).State);
        Assert.False(File.Exists(RestoreKit.Candidate(Root, op)));
    }

    [Fact]
    public void Q3_RequesterKilledAfterConfirmed_TheNextStartAsks()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);

        Kill("request", Root.Paths.Root, entry.FileName, "confirmed");

        var op = RestoreKit.Ops(Root).Single();
        Assert.Equal(RestoreState.Confirmed, RestoreKit.Journal(Root, op).State);
        var ui = new ScriptedRestoreUi();
        Assert.Equal(RestoreEntryKind.Exit, RestoreKit.Start(Root, Coordinator(), ui).Stop!.Kind);
        Assert.Equal(RestoreAskKind.Restore, ui.Asks.Single().Kind);
    }

    // ---- a start killed while waiting (G only) ---------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KilledWhileWaiting_ReleasesTheGate_AndChangesNothing(bool putBack)
    {
        string op;
        if (putBack)
            op = (await UndoRequestedAsync(UndoOrigin.BeforePublication)).Op;
        else
            op = (await ConfirmedAsync()).Op;
        var before = RestoreJournalText.Of(Root, op);
        var dataBefore = RestoreKit.DataSet(Root);
        using var window = new InstanceLocks(Root.Paths);
        Assert.True(window.TryAcquireGate());
        window.TakePresenceShared();
        window.ReleaseGate();

        Kill("restore", Root.Paths.Root, op, nameof(RestoreStage.WaitRead));

        Assert.Equal(before, RestoreJournalText.Of(Root, op));
        Assert.Equal(dataBefore, RestoreKit.DataSet(Root));
        using var probe = new InstanceLocks(Root.Paths);
        Assert.True(probe.TryAcquireGate());   // released by the OS
        Assert.True(probe.TryRestoreLock());   // never held while waiting
    }
}
