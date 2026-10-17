using WorkHub.Core.Backup;
using WorkHub.Core.Restore;
using WorkHub.Infrastructure.Restore;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Restore;

/// <summary>
/// Request: reservation under R, staging, confirmation, publication (Phase 10 §6, §7.1; tests A candidate refusals and
/// changes, Q1/Q2/Q4/Q8 reservation).
/// </summary>
public sealed class RestoreRequestTests : RestoreTestBase
{
    private string Db => Root.Paths.DatabaseFile;

    // ---- A: candidate eligibility (never loosened) --------------------------------------------------------------------

    public static TheoryData<string, string, string> RefusedCandidates() => new()
    {
        // arrangement, file version in the name, expected refusal fragment
        { "PRAGMA user_version = 8;", "8", "newer WorkHub version" },
        { "PRAGMA user_version = 0;", "7", "not a WorkHub database" },
        { "CREATE TABLE Extra (Id INTEGER PRIMARY KEY, Name TEXT);", "7", "structure differs" },
        { "PRAGMA foreign_keys = OFF; INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, SortOrder, CreatedAt, UpdatedAt) VALUES (9999, 'orphan', 'Web', 'https://x.test', 0, 'x', 'x');", "7", "foreign-key problems" },
        { "PRAGMA user_version = 6;", "7", "structure differs" },
        { "v6content", "7", "does not match its name" },
        { "corrupt", "7", "damaged" },
        { "sidecar", "7", "not standalone" },
    };

    [Theory]
    [MemberData(nameof(RefusedCandidates))]
    public async Task A_CandidatesThatAreNotFullyValidatedCurrentOrOlder_AreRefused_NothingIsLeftReserved(string arrangement, string nameVersion, string refusal)
    {
        BackupKit.CreateDatabase(Root);
        Directory.CreateDirectory(Root.Paths.DailyBackupsDirectory);
        var name = BackupKit.NameFor(new DateOnly(2026, 10, 1), version: int.Parse(nameVersion, System.Globalization.CultureInfo.InvariantCulture));
        var file = BackupKit.Daily(Root, name);
        BackupKit.SnapshotTo(Db, file);
        switch (arrangement)
        {
            case "corrupt":
                BackupKit.CorruptPage(file);
                break;
            case "v6content":
                File.Delete(file);
                BackupKit.BuildSchema(file, 6);
                break;
            case "sidecar":
                File.WriteAllBytes(file + "-journal", [1, 2, 3]);
                break;
            default:
                using (var connection = BackupKit.Open(file))
                    BackupKit.Exec(connection, arrangement);
                break;
        }

        var start = await RestoreKit.Requester(Root).BeginAsync(RestoreKit.DailyEntry(Root, name));

        Assert.Null(start.Request);
        Assert.Contains(refusal, start.Refusal, StringComparison.OrdinalIgnoreCase);
        AssertNoReservation();
    }

    [Fact]
    public async Task A_UnrecognizedEntries_AreRefused_WithoutOpeningAnything()
    {
        BackupKit.CreateDatabase(Root);
        var entry = new BackupEntry("notes.txt", BackupFolder.Daily, BackupKind.Unrecognized, 10, null, null);

        var start = await RestoreKit.Requester(Root).BeginAsync(entry);

        Assert.Null(start.Request);
        Assert.Contains("recognized backups", start.Refusal);
    }

    [Fact]
    public async Task A_SourceChangedAfterSelection_IsRefused()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        using (var connection = BackupKit.Open(BackupKit.Daily(Root, entry.FileName)))
            BackupKit.Exec(connection, $"INSERT INTO Systems (SystemCode, SystemName, Description, CreatedAt, UpdatedAt) VALUES ('GROW0001', 'x', '{new string('g', 9000)}', 'x', 'x');");

        var start = await RestoreKit.Requester(Root).BeginAsync(entry); // the entry still carries the listed size

        Assert.Null(start.Request);
        Assert.Contains("changed since the list was loaded", start.Refusal);
    }

    [Fact]
    public async Task A_SourceChangingWhileItIsCopied_IsRefusedByTheTripleHash()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var source = BackupKit.Daily(Root, entry.FileName);
        var requester = new RestoreRequester(Root.Paths, WorkHub.Core.Diagnostics.NullAppLog.Instance, TimeProvider.System, WorkHub.Infrastructure.Backup.SchemaReference.Frozen, new VolumeProbe(),
            locks =>
            {
                var ops = new FaultyRestoreFileOps(new RestoreFileOps(Root.Paths, () => locks.HoldsRestoreLock));
                return new CopyThenTamper(ops, source);
            });

        var start = await requester.BeginAsync(entry);

        Assert.Null(start.Request);
        Assert.Contains("changed while it was being copied", start.Refusal);
        AssertNoReservation();
    }

    /// <summary>Changes one byte of the source right after the copy (between H1 and H2).</summary>
    private sealed class CopyThenTamper(IRestoreFileOps inner, string source) : IRestoreFileOps
    {
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);
        public long GetLength(string path) => inner.GetLength(path);
        public string FileId(string path) => inner.FileId(path);
        public IReadOnlyList<FileFact> List(string directory) => inner.List(directory);
        public IReadOnlyList<string> DirectoryNames(string directory) => inner.DirectoryNames(directory);
        public string Sha256(string path) => inner.Sha256(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public void Move(string from, string to) => inner.Move(from, to);
        public void Delete(string path) => inner.Delete(path);
        public void DeleteEmptyDirectory(string path) => inner.DeleteEmptyDirectory(path);
        public void WriteThrough(string path, byte[] content) => inner.WriteThrough(path, content);
        public void Replace(string from, string to) => inner.Replace(from, to);
        public byte[] ReadShared(string path) => inner.ReadShared(path);

        public void Copy(string from, string to)
        {
            inner.Copy(from, to);
            using var stream = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            stream.Position = stream.Length - 1;
            var b = stream.ReadByte();
            stream.Position = stream.Length - 1;
            stream.WriteByte((byte)(b ^ 0xFF));
        }
    }

    [Fact]
    public async Task A_MigrationAndPreRestoreBackups_AreAcceptedCandidates()
    {
        BackupKit.CreateDatabase(Root);
        Directory.CreateDirectory(Root.Paths.MigrationBackupsDirectory);
        var migrationName = "workhub_v7_before_v8_20261001-010203004.db";
        BackupKit.SnapshotTo(Db, Path.Combine(Root.Paths.MigrationBackupsDirectory, migrationName));
        Directory.CreateDirectory(Root.Paths.PreRestoreBackupsDirectory);
        var preRestoreName = PreRestoreNames.Validated(7, DateTimeOffset.UtcNow);
        BackupKit.SnapshotTo(Db, Path.Combine(Root.Paths.PreRestoreBackupsDirectory, preRestoreName));

        var entries = await new WorkHub.Infrastructure.Backup.BackupCatalog(Root.Paths).ListAsync();
        foreach (var entry in entries.Where(e => e.Kind is BackupKind.PreMigration or BackupKind.PreRestore))
        {
            var start = await RestoreKit.Requester(Root).BeginAsync(entry);
            Assert.True(start.Request is not null, start.Refusal);
            Assert.Equal(BackupCompatibility.Current, start.Request!.Facts.Compatibility);
            await start.Request.CancelAsync();
        }

        Assert.Equal(2, RestoreKit.Ops(Root).Length);
        Assert.All(RestoreKit.Ops(Root), op => Assert.Equal(RestoreState.Cancelled, RestoreKit.Journal(Root, op).State));
    }

    [Fact]
    public async Task Staging_RecordsFactsAndCounts_ConfirmationRecordsTheExactAcknowledgement()
    {
        await PopulatedV7.CreateAsync(Root);
        var entry = RestoreKit.PlaceDaily(Root);

        var start = await RestoreKit.Requester(Root).BeginAsync(entry);
        using var request = start.Request!;
        var facts = request.Facts;
        Assert.Equal(7, facts.SchemaVersion);
        Assert.Equal(new DataClassification(CurrentDataClass.Healthy, Version: 7), facts.RequestClass);
        Assert.Equal(1L, facts.Counts.ProjectsInTrash); // one Project in the Workspace Trash
        Assert.True(facts.Counts.Systems > 0 && facts.Counts.OwnerItems == 5 && facts.Counts.Notes >= 3 && facts.Counts.ProjectTrashItems == 2);
        var staged = RestoreKit.Journal(Root, facts.Op);
        Assert.Equal(RestoreState.Staged, staged.State);
        Assert.Equal(BackupKit.Hash(BackupKit.Daily(Root, entry.FileName)), staged.Source.Sha256);
        Assert.Equal(staged.Source.Sha256, staged.Candidate!.Sha256);
        Assert.Equal(RestoreKit.FileId(RestoreKit.Candidate(Root, facts.Op)), staged.Candidate.FileId);

        var op = await request.ConfirmAsync();

        var confirmed = RestoreKit.Journal(Root, op);
        Assert.Equal(RestoreState.Confirmed, confirmed.State);
        Assert.Equal(staged.Revision + 1, confirmed.Revision);
        Assert.Equal(confirmed.RequestClass, confirmed.Acknowledgement);
        Assert.NotNull(confirmed.ConfirmedUtc);
        using var locks = new InstanceLocks(Root.Paths);
        Assert.True(locks.TryRestoreLock()); // released at confirmation
    }

    // ---- Q: reservation -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Q1_ConcurrentRequesters_ExactlyOneReserves_TheOthersAreBusy()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        for (var round = 0; round < 5; round++)
        {
            using var release = new ManualResetEventSlim();
            var starts = Enumerable.Range(0, 3).Select(_ => RestoreKit.Requester(Root, afterStaging: () => release.Wait(TimeSpan.FromSeconds(30))).BeginAsync(entry)).ToList();
            await Task.Delay(300);
            release.Set();
            var results = await Task.WhenAll(starts);

            var reserved = results.Where(r => r.Request is not null).ToList();
            Assert.Single(reserved);
            Assert.All(results.Where(r => r.Request is null), r => Assert.Equal("Another WorkHub window is preparing or performing a restore.", r.Refusal));
            await reserved[0].Request!.CancelAsync();
        }

        Assert.Equal(5, RestoreKit.Ops(Root).Length);
    }

    [Fact]
    public async Task Q2_WhileTheConfirmationIsOpen_ASecondWindowIsBusy_AndANewStartDoesNoCleanupAndStartsNormally()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var start = await RestoreKit.Requester(Root).BeginAsync(entry);
        using var open = start.Request!;

        var second = await RestoreKit.Requester(Root).BeginAsync(entry);
        Assert.Equal("Another WorkHub window is preparing or performing a restore.", second.Refusal);

        var (result, stop, error) = RestoreKit.Start(Root, Coordinator(), new ScriptedRestoreUi());
        Assert.Null(error);
        Assert.Null(stop);
        Assert.NotNull(result);
        Assert.Equal(RestoreState.Staged, RestoreKit.Journal(Root, open.Facts.Op).State);
        Assert.True(File.Exists(RestoreKit.Candidate(Root, open.Facts.Op)));
    }

    [Fact]
    public async Task Q4_CancelOrClose_PublishesCancelledFromStaged_AndDeletesTheCandidate()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);

        var first = (await RestoreKit.Requester(Root).BeginAsync(entry)).Request!;
        await first.CancelAsync();
        var second = (await RestoreKit.Requester(Root).BeginAsync(entry)).Request!;
        second.Dispose(); // window closed with the dialog open

        foreach (var op in new[] { first.Facts.Op, second.Facts.Op })
        {
            var journal = RestoreKit.Journal(Root, op);
            Assert.Equal(RestoreState.Cancelled, journal.State);
            Assert.Equal(RestoreState.Staged, journal.CancelledFrom);
            Assert.False(File.Exists(RestoreKit.Candidate(Root, op)));
        }

        using var third = (await RestoreKit.Requester(Root).BeginAsync(entry)).Request;
        Assert.NotNull(third); // R was released
    }

    [Fact]
    public async Task Q1_AnUnfinishedOperation_RefusesANewRequest()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        await RestoreKit.RequestAndConfirmAsync(Root, entry);

        var start = await RestoreKit.Requester(Root).BeginAsync(entry);

        Assert.Null(start.Request);
        Assert.Contains("pending or unfinished", start.Refusal);
    }

    [Fact]
    public async Task Q1_Precheck_RemovesDeadStaging_AbandonsDeadStaged_AndFailsClosedOnUnknownContent()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        // A dead Staged op (its requester is gone; R is free).
        var staged = (await RestoreKit.Requester(Root).BeginAsync(entry)).Request!;
        var stagedOp = staged.Facts.Op;
        ((RestoreRequest)staged).ReleaseLockWithoutDeciding();
        // A journal-less folder that only holds a partial candidate and a temp journal.
        var dead = Path.Combine(Root.Paths.RestoreDirectory, "20261001-000000-0000dead");
        Directory.CreateDirectory(dead);
        File.WriteAllBytes(Path.Combine(dead, RestoreNames.Candidate), [1]);
        File.WriteAllBytes(Path.Combine(dead, RestoreNames.JournalTemp), [1]);

        var start = await RestoreKit.Requester(Root).BeginAsync(entry);

        Assert.NotNull(start.Request);
        await start.Request!.CancelAsync();
        Assert.False(Directory.Exists(dead));
        Assert.Equal(RestoreState.Abandoned, RestoreKit.Journal(Root, stagedOp).State);
        Assert.False(File.Exists(RestoreKit.Candidate(Root, stagedOp)));

        // Unknown content in a journal-less folder: fail closed, nothing deleted.
        var unknown = Path.Combine(Root.Paths.RestoreDirectory, "20261001-000000-0000beef");
        Directory.CreateDirectory(unknown);
        File.WriteAllBytes(Path.Combine(unknown, "workhub.db"), [1]);
        var refused = await RestoreKit.Requester(Root).BeginAsync(entry);
        Assert.Null(refused.Request);
        Assert.Contains("needs attention", refused.Refusal);
        Assert.True(File.Exists(Path.Combine(unknown, "workhub.db")));
    }

    [Fact]
    public void Q8_EveryChangeUnderDataRestore_RequiresTheRestoreLock()
    {
        Directory.CreateDirectory(Root.Paths.RestoreDirectory);
        using var locks = new InstanceLocks(Root.Paths);
        var files = new RestoreFileOps(Root.Paths, () => locks.HoldsRestoreLock);
        var inside = Path.Combine(Root.Paths.RestoreDirectory, "20261001-000000-00000001");
        var outsideFile = Path.Combine(Root.Paths.Root, "outside.txt");
        File.WriteAllText(outsideFile, "x");

        Assert.Throws<RestoreLockNotHeldException>(() => files.CreateDirectory(inside));
        Assert.Throws<RestoreLockNotHeldException>(() => files.WriteThrough(Path.Combine(Root.Paths.RestoreDirectory, "x.tmp"), [1]));
        Assert.Throws<RestoreLockNotHeldException>(() => files.Copy(outsideFile, Path.Combine(Root.Paths.RestoreDirectory, "x")));
        Assert.Throws<RestoreLockNotHeldException>(() => files.Move(outsideFile, Path.Combine(Root.Paths.RestoreDirectory, "x")));
        Assert.Throws<RestoreLockNotHeldException>(() => files.Delete(Path.Combine(Root.Paths.RestoreDirectory, "x")));
        Assert.Throws<RestoreLockNotHeldException>(() => files.Replace(outsideFile, Path.Combine(Root.Paths.RestoreDirectory, "x")));
        Assert.Throws<RestoreLockNotHeldException>(() => files.DeleteEmptyDirectory(inside));
        Assert.Throws<RestoreLockNotHeldException>(() => new RestoreJournalStore(Root.Paths, files).Write(RestoreReconcilerTests.Journal(RestoreState.Confirmed) with { Op = "20261001-000000-00000001" }));

        Assert.True(locks.TryRestoreLock());
        files.CreateDirectory(inside); // allowed now
        Assert.True(Directory.Exists(inside));
        files.CreateDirectory(Path.Combine(Root.Paths.Root, "elsewhere")); // outside Data\Restore: never guarded
    }

    private void AssertNoReservation()
    {
        foreach (var op in RestoreKit.Ops(Root))
        {
            var journal = RestoreKit.Journal(Root, op);
            Assert.Equal(RestoreState.Cancelled, journal.State);
            Assert.False(File.Exists(RestoreKit.Candidate(Root, op)));
        }

        using var locks = new InstanceLocks(Root.Paths);
        Assert.True(locks.TryRestoreLock());
    }
}
