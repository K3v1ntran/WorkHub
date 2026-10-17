using WorkHub.Core.Backup;
using WorkHub.Core.Restore;

namespace WorkHub.Tests.Restore;

/// <summary>
/// Pure reconciliation (Phase 10 §8.2, §9.4, §9.5; tests E, M3/M4, P7/P8, U2). Every state is evaluated against every
/// configuration of a small universe of file facts; exactly the configurations the proposal permits are accepted (with the
/// expected action), and everything else fails closed.
/// </summary>
public sealed class RestoreReconcilerTests
{
    private const string Db = "workhub.db";
    private static readonly FileFact P1 = new(Db, 100, "P1");
    private static readonly FileFact P2 = new(Db + "-journal", 10, "P2");
    private static readonly FileFact C1 = new(Db, 200, "C1");
    private static readonly FileFact C2 = new(Db + "-wal", 20, "C2");
    private static readonly FileFact X9 = new(Db, 300, "X9");
    private static readonly FileFact Dup = new("copy.db", 100, "P1");
    private static readonly FileFact CandidateC1 = new(RestoreNames.Candidate, 200, "C1");
    private static readonly FileFact CandidateX9 = new(RestoreNames.Candidate, 200, "X9");

    private static readonly InventoryFile[] PreviousInventory = [Inv(P1), Inv(P2)];
    private static readonly InventoryFile[] RestoredInventory = [Inv(C1), Inv(C2)];

    private static readonly (string Name, FileFact[] Files)[] DataOptions =
    [
        ("D0", []), ("D1", [P1, P2]), ("D2", [P1]), ("D3", [P2]), ("D4", [C1]), ("D5", [C1, C2]), ("D6", [X9]), ("D7", [C1, P2]), ("D8", [C2]),
    ];

    private static readonly (string Name, FileFact[]? Files)[] PreviousOptions =
    [
        ("Pnull", null), ("P0", []), ("Pfull", [P1, P2]), ("P1", [P1]), ("P2", [P2]), ("Pextra", [P1, P2, new FileFact("other.db", 1, "X8")]), ("Pdup", [P1, P2, Dup]),
    ];

    private static readonly (string Name, FileFact[]? Files)[] RestoredOptions =
    [
        ("Rnull", null), ("R0", []), ("Rfull", [C1, C2]), ("R1", [C1]), ("R2", [C2]), ("Rx", [X9]),
    ];

    private static readonly (string Name, FileFact? File)[] CandidateOptions = [("Cnone", null), ("Cic", CandidateC1), ("Cx", CandidateX9)];

    private static InventoryFile Inv(FileFact f) => new(f.Name, f.Size, f.FileId);

    internal static RestoreJournal Journal(RestoreState state, UndoOrigin? origin = null, RestoreState? cancelledFrom = null, bool withInventory = true) => new()
    {
        Op = "20261016-000000-abcdef01",
        State = state,
        Revision = 7,
        CreatedUtc = DateTimeOffset.UnixEpoch,
        UpdatedUtc = DateTimeOffset.UnixEpoch,
        Build = new RestoreBuildInfo("test", 7),
        DataDir = new DirectoryIdentity("V", "D"),
        Fs = new FileSystemFacts("NTFS", "V"),
        Source = new RestoreSource(BackupFolder.Daily, "workhub_daily.db", BackupKind.Daily, 7, null, 200, "H"),
        Candidate = new CandidateFacts(200, "H", "C1", 7, BackupCompatibility.Current),
        PreviousInventory = withInventory ? PreviousInventory : null,
        CancelledFrom = cancelledFrom,
        Undo = origin is { } o ? new UndoRecord(DateTimeOffset.UnixEpoch, o, o == UndoOrigin.AfterPublication && state != RestoreState.UndoRequested ? RestoredInventory : null) : null,
    };

    private static IEnumerable<(string Key, RestoreFileFacts Facts)> Universe()
    {
        foreach (var d in DataOptions)
        foreach (var p in PreviousOptions)
        foreach (var r in RestoredOptions)
        foreach (var c in CandidateOptions)
            yield return ($"{d.Name}/{p.Name}/{r.Name}/{c.Name}", new RestoreFileFacts(d.Files, p.Files, r.Files, c.File));
    }

    /// <summary>The permitted configurations of each case, written out from the proposal's tables.</summary>
    private static Dictionary<string, RestoreActionKind> Expected(RestoreState state, UndoOrigin? origin)
    {
        var expected = new Dictionary<string, RestoreActionKind>();
        void Add(RestoreActionKind kind, string[] d, string[] p, string[] r, string[] c)
        {
            foreach (var a in d)
            foreach (var b in p)
            foreach (var x in r)
            foreach (var y in c)
                expected[$"{a}/{b}/{x}/{y}"] = kind;
        }

        string[] allD = DataOptions.Select(o => o.Name).ToArray(), allC = CandidateOptions.Select(o => o.Name).ToArray();
        string[] emptyP = ["Pnull", "P0"], emptyR = ["Rnull", "R0"];

        // Split of the previous set between Data\ and previous\ (each file in exactly one place, nothing else).
        void PreviousSplit(RestoreActionKind kind, string[] r, string[] c)
        {
            Add(kind, ["D1"], emptyP, r, c);
            Add(kind, ["D2"], ["P2"], r, c);
            Add(kind, ["D3"], ["P1"], r, c);
            Add(kind, ["D0"], ["Pfull"], r, c);
        }

        switch (state, origin)
        {
            case (RestoreState.Confirmed, null):
                Add(RestoreActionKind.AskRestore, allD, emptyP, emptyR, allC);
                break;
            case (RestoreState.Executing, null):
                Add(RestoreActionKind.AskInterrupted, allD, emptyP, emptyR, allC);
                break;
            case (RestoreState.MovingAside, null):
                PreviousSplit(RestoreActionKind.RollForwardMoveAside, emptyR, ["Cic"]);
                break;
            case (RestoreState.MovedAside, null):
                Add(RestoreActionKind.ContinuePublication, ["D0"], ["Pfull"], emptyR, ["Cic"]);
                Add(RestoreActionKind.ContinuePublication, ["D4"], ["Pfull"], emptyR, ["Cnone"]);
                break;
            case (RestoreState.Published, null):
                Add(RestoreActionKind.ReinitializePublished, ["D4", "D5"], ["Pfull"], emptyR, ["Cnone"]);
                break;
            case (RestoreState.UndoRequested, UndoOrigin.AfterPublication):
                Add(RestoreActionKind.AskPutBack, ["D4", "D5"], ["Pfull"], emptyR, ["Cnone"]);
                break;
            case (RestoreState.UndoRequested, UndoOrigin.BeforePublication):
                Add(RestoreActionKind.AskPutBack, ["D0"], ["Pfull"], ["Rnull"], ["Cic"]);
                break;
            case (RestoreState.Undoing, UndoOrigin.AfterPublication):
                Add(RestoreActionKind.RollForwardUndo, ["D5"], ["Pfull"], emptyR, ["Cnone"]);
                Add(RestoreActionKind.RollForwardUndo, ["D4"], ["Pfull"], ["R2"], ["Cnone"]);
                Add(RestoreActionKind.RollForwardUndo, ["D8"], ["Pfull"], ["R1"], ["Cnone"]);
                Add(RestoreActionKind.RollForwardUndo, ["D0"], ["Pfull"], ["Rfull"], ["Cnone"]);
                break;
            case (RestoreState.Undoing, UndoOrigin.BeforePublication):
                Add(RestoreActionKind.RollForwardUndo, ["D0"], ["Pfull"], ["Rnull"], ["Cic"]);
                break;
            case (RestoreState.RestoredAside, UndoOrigin.AfterPublication):
                PreviousSplit(RestoreActionKind.RollForwardUndo, ["Rfull"], ["Cnone"]);
                break;
            case (RestoreState.RestoredAside, UndoOrigin.BeforePublication):
                PreviousSplit(RestoreActionKind.RollForwardUndo, ["Rnull"], ["Cic"]);
                break;
        }

        return expected;
    }

    public static TheoryData<RestoreState, UndoOrigin?> NonTerminalCases() => new()
    {
        { RestoreState.Confirmed, null },
        { RestoreState.Executing, null },
        { RestoreState.MovingAside, null },
        { RestoreState.MovedAside, null },
        { RestoreState.Published, null },
        { RestoreState.UndoRequested, UndoOrigin.AfterPublication },
        { RestoreState.UndoRequested, UndoOrigin.BeforePublication },
        { RestoreState.Undoing, UndoOrigin.AfterPublication },
        { RestoreState.Undoing, UndoOrigin.BeforePublication },
        { RestoreState.RestoredAside, UndoOrigin.AfterPublication },
        { RestoreState.RestoredAside, UndoOrigin.BeforePublication },
    };

    [Theory]
    [MemberData(nameof(NonTerminalCases))]
    public void EveryConfiguration_IsAcceptedExactlyWhenTheProposalPermitsIt(RestoreState state, UndoOrigin? origin)
    {
        var expected = Expected(state, origin);
        Assert.NotEmpty(expected);
        var accepted = 0;
        foreach (var (key, facts) in Universe())
        {
            var decision = RestoreReconciler.Decide(Journal(state, origin), facts);
            if (expected.TryGetValue(key, out var kind))
            {
                Assert.True(decision.Kind == kind, $"{state}/{origin} {key}: expected {kind}, got {decision.Kind} ({decision.Reason})");
                Assert.Equal(origin, decision.Origin);
                accepted++;
            }
            else
            {
                Assert.True(decision.Kind == RestoreActionKind.FailClosed, $"{state}/{origin} {key}: expected FailClosed, got {decision.Kind}");
                Assert.False(string.IsNullOrEmpty(decision.Reason));
            }
        }

        Assert.Equal(expected.Count, accepted);
    }

    [Theory]
    [MemberData(nameof(NonTerminalCases))]
    public void NoNonTerminalState_EverLeadsToANormalStart(RestoreState state, UndoOrigin? origin)
    {
        foreach (var (_, facts) in Universe())
            Assert.NotEqual(RestoreActionKind.NormalStart, RestoreReconciler.Decide(Journal(state, origin), facts).Kind);
    }

    [Theory]
    [InlineData(RestoreState.UndoRequested)]
    [InlineData(RestoreState.Undoing)]
    [InlineData(RestoreState.RestoredAside)]
    [InlineData(RestoreState.MovingAside)]
    [InlineData(RestoreState.MovedAside)]
    [InlineData(RestoreState.Published)]
    public void StatesWithoutTheirRequiredRecords_FailClosed(RestoreState state)
    {
        // Put-back states without an origin; restore states without the previous inventory.
        var journal = state is RestoreState.UndoRequested or RestoreState.Undoing or RestoreState.RestoredAside
            ? Journal(state)
            : Journal(state, withInventory: false);
        foreach (var (_, facts) in Universe())
            Assert.Equal(RestoreActionKind.FailClosed, RestoreReconciler.Decide(journal, facts).Kind);
    }

    [Theory]
    [InlineData(RestoreState.Confirmed)]
    [InlineData(RestoreState.Executing)]
    [InlineData(RestoreState.MovedAside)]
    [InlineData(RestoreState.Published)]
    public void AnUndoRecordOnARestoreState_FailsClosed(RestoreState state)
    {
        foreach (var (_, facts) in Universe())
            Assert.Equal(RestoreActionKind.FailClosed, RestoreReconciler.Decide(Journal(state, UndoOrigin.BeforePublication), facts).Kind);
    }

    // ---- terminal-state guard (§9.5, tests P7) -----------------------------------------------------------------------

    public static TheoryData<RestoreState, RestoreState?, bool> TerminalCases() => new()
    {
        // state, cancelledFrom, consistent when previous\ is empty
        { RestoreState.Cancelled, RestoreState.Staged, true },
        { RestoreState.Cancelled, RestoreState.Confirmed, true },
        { RestoreState.Cancelled, RestoreState.Executing, true },
        { RestoreState.Cancelled, RestoreState.UndoRequested, false },
        { RestoreState.Cancelled, RestoreState.MovedAside, false },
        { RestoreState.Cancelled, RestoreState.MovingAside, false },
        { RestoreState.Cancelled, RestoreState.Published, false },
        { RestoreState.Cancelled, null, false },
        { RestoreState.Abandoned, null, true },
        { RestoreState.Aborted, null, true },
        { RestoreState.Aborted, RestoreState.Confirmed, false },
        { RestoreState.Completed, null, true },
        { RestoreState.Undone, null, true },
    };

    [Theory]
    [MemberData(nameof(TerminalCases))]
    public void TerminalGuard_AcceptsOnlyConsistentTerminalJournals(RestoreState state, RestoreState? cancelledFrom, bool consistent)
    {
        foreach (var (key, facts) in Universe())
        {
            var decision = RestoreReconciler.Decide(Journal(state, cancelledFrom: cancelledFrom), facts);
            var previousHoldsData = !facts.PreviousEmpty;
            // Completed and Undone keep previous\ (Completed) or never had a reason to: they are not guarded on it.
            var guardedOnPrevious = state is RestoreState.Cancelled or RestoreState.Abandoned or RestoreState.Aborted;
            var ok = consistent && !(guardedOnPrevious && previousHoldsData);
            Assert.True(ok ? decision.Kind == RestoreActionKind.NormalStart : decision.Kind == RestoreActionKind.FailClosed,
                $"{state} from {cancelledFrom} {key}: {decision.Kind} ({decision.Reason})");
        }
    }

    [Theory]
    [InlineData(RestoreState.Cancelled)]
    [InlineData(RestoreState.Abandoned)]
    [InlineData(RestoreState.Aborted)]
    public void ATerminalJournalWithAPutBackRecord_FailsClosed(RestoreState state)
    {
        var journal = Journal(state, UndoOrigin.BeforePublication, state == RestoreState.Cancelled ? RestoreState.Confirmed : null);
        Assert.NotNull(RestoreReconciler.TerminalViolation(journal, new RestoreFileFacts([], null, null, null)));
        Assert.Equal(RestoreActionKind.FailClosed, RestoreReconciler.Decide(journal, new RestoreFileFacts([], null, null, null)).Kind);
    }

    [Fact]
    public void TheFindingConfiguration_CancelledFromUndoRequestedBeforePublication_FailsClosed_NotANormalStart()
    {
        // Revision-2 §5.3 "Cancel put back": previous\ holds the data, Data\ is empty, the candidate is staged.
        var journal = Journal(RestoreState.Cancelled, UndoOrigin.BeforePublication, RestoreState.UndoRequested);
        var facts = new RestoreFileFacts([], [P1, P2], null, CandidateC1);

        var decision = RestoreReconciler.Decide(journal, facts);

        Assert.Equal(RestoreActionKind.FailClosed, decision.Kind);
    }

    [Theory]
    [InlineData(RestoreState.MovingAside)]
    [InlineData(RestoreState.Published)]
    [InlineData(RestoreState.UndoRequested)]
    [InlineData(RestoreState.Completed)]
    public void ACancellationRecordOutsideCancelled_FailsClosed(RestoreState state)
    {
        var journal = Journal(state, state == RestoreState.UndoRequested ? UndoOrigin.BeforePublication : null, RestoreState.Confirmed);
        Assert.Equal(RestoreActionKind.FailClosed, RestoreReconciler.Decide(journal, new RestoreFileFacts([], [P1, P2], null, CandidateC1)).Kind);
    }

    // ---- cancellation policy (R25, tests P8) -------------------------------------------------------------------------

    [Fact]
    public void OnlyStatesBeforeMovingAside_AreCancellable()
    {
        var cancellable = Enum.GetValues<RestoreState>().Where(RestoreStates.IsCancellable).ToArray();
        Assert.Equal([RestoreState.Staged, RestoreState.Confirmed, RestoreState.Executing], cancellable);
        Assert.False(RestoreStates.IsCancellable(RestoreState.UndoRequested));
    }

    [Fact]
    public void WaitKinds_PutBackHasItsOwnKind_ConfirmedAndInterruptedShareOne()
    {
        Assert.Equal(RestoreWaitKind.Confirmed, RestoreReconciler.WaitKindFor(RestoreState.Confirmed));
        Assert.Equal(RestoreWaitKind.Confirmed, RestoreReconciler.WaitKindFor(RestoreState.Executing));
        Assert.Equal(RestoreWaitKind.PutBack, RestoreReconciler.WaitKindFor(RestoreState.UndoRequested));
        foreach (var state in new[] { RestoreState.MovingAside, RestoreState.MovedAside, RestoreState.Published, RestoreState.Undoing, RestoreState.RestoredAside })
            Assert.Equal(RestoreWaitKind.Owned, RestoreReconciler.WaitKindFor(state));
    }

    // ---- origin from fresh facts (§9.3, tests M4) --------------------------------------------------------------------

    [Theory]
    [InlineData(RestoreState.Published, "D4", "Cnone", UndoOrigin.AfterPublication)]
    [InlineData(RestoreState.Published, "D5", "Cnone", UndoOrigin.AfterPublication)]
    [InlineData(RestoreState.MovedAside, "D4", "Cnone", UndoOrigin.AfterPublication)] // publication done, journal behind
    [InlineData(RestoreState.MovedAside, "D0", "Cic", UndoOrigin.BeforePublication)]
    [InlineData(RestoreState.MovedAside, "D4", "Cic", null)] // contradictory: Ic in Data\ and a staged candidate
    [InlineData(RestoreState.MovedAside, "D0", "Cnone", null)]
    [InlineData(RestoreState.MovedAside, "D6", "Cic", null)] // foreign database in Data\
    [InlineData(RestoreState.MovedAside, "D0", "Cx", null)] // candidate replaced
    [InlineData(RestoreState.Confirmed, "D4", "Cnone", null)]
    public void Origin_IsChosenFromFreshFacts(RestoreState state, string data, string candidate, UndoOrigin? expected)
    {
        var facts = new RestoreFileFacts(
            DataOptions.Single(o => o.Name == data).Files,
            [P1, P2],
            null,
            CandidateOptions.Single(o => o.Name == candidate).File);

        Assert.Equal(expected, RestoreReconciler.OriginFromFacts(Journal(state), facts));
    }

    [Fact]
    public void Origin_RequiresPreviousToMatchTheInventory()
    {
        var facts = new RestoreFileFacts([C1], [P1], null, null);
        Assert.Null(RestoreReconciler.OriginFromFacts(Journal(RestoreState.Published), facts));
    }

    // ---- acknowledgement rule (§7.3, tests K8) -----------------------------------------------------------------------

    public static TheoryData<DataClassification, DataClassification, bool> AcknowledgementCases()
    {
        var healthy7 = new DataClassification(CurrentDataClass.Healthy, Version: 7);
        var healthy6 = new DataClassification(CurrentDataClass.Healthy, Version: 6);
        var newer8 = new DataClassification(CurrentDataClass.Degraded, DegradedKind.Newer, 8);
        var structure7 = new DataClassification(CurrentDataClass.Degraded, DegradedKind.StructureDiffers, 7);
        var unreadable = DataClassification.Unreadable;
        var missing = DataClassification.Missing;
        var recovery = new DataClassification(CurrentDataClass.NeedsRecovery);
        return new TheoryData<DataClassification, DataClassification, bool>
        {
            { healthy7, healthy7, true },
            { healthy7, healthy6, false },
            { healthy7, unreadable, false },
            { healthy7, newer8, false },
            { healthy7, structure7, false },
            { healthy7, missing, false },
            { newer8, newer8, true },
            { newer8, new DataClassification(CurrentDataClass.Degraded, DegradedKind.Newer, 9), false },
            { newer8, structure7, false },
            { structure7, structure7, true },
            { unreadable, unreadable, true },
            { unreadable, healthy7, false },
            { missing, missing, true },
            { missing, healthy7, false },
            { recovery, healthy7, true },
            { recovery, newer8, true },
            { recovery, unreadable, true },
            { recovery, missing, false },
        };
    }

    [Theory]
    [MemberData(nameof(AcknowledgementCases))]
    public void AcknowledgementMatrix(DataClassification acknowledged, DataClassification execution, bool allowed)
    {
        Assert.Equal(allowed, AcknowledgementRule.Allows(acknowledged, execution));
    }

    [Fact]
    public void ExplicitAcknowledgement_IsNeededForDegradedUnreadableAndNeedsRecovery()
    {
        Assert.False(AcknowledgementRule.NeedsExplicitAcknowledgement(new DataClassification(CurrentDataClass.Healthy, Version: 7)));
        Assert.False(AcknowledgementRule.NeedsExplicitAcknowledgement(DataClassification.Missing));
        Assert.True(AcknowledgementRule.NeedsExplicitAcknowledgement(new DataClassification(CurrentDataClass.Degraded, DegradedKind.Foreign, 0)));
        Assert.True(AcknowledgementRule.NeedsExplicitAcknowledgement(DataClassification.Unreadable));
        Assert.True(AcknowledgementRule.NeedsExplicitAcknowledgement(new DataClassification(CurrentDataClass.NeedsRecovery)));
    }

    // ---- identifiers --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("20261016-123456-0123abcd", true)]
    [InlineData("20261016-123456-0123ABCD", false)]
    [InlineData("..\\..\\x", false)]
    [InlineData("20261016-123456-0123abcd\\..", false)]
    [InlineData("", false)]
    public void OperationIdentifiers_AreStrict(string op, bool valid)
    {
        Assert.Equal(valid, RestoreOpId.IsValid(op));
    }

    [Fact]
    public void NewOperationIdentifiers_AreValid()
    {
        Assert.True(RestoreOpId.IsValid(RestoreOpId.New(DateTimeOffset.UtcNow)));
    }
}
