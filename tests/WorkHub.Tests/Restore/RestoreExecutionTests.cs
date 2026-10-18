using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Infrastructure.Restore;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Restore;

/// <summary>
/// Restart-time execution X0–X12 on real files (Phase 10 §7, §8; tests B current states, C safety failures, G migration /
/// completion, I data preservation across all ten tables, J backup separation, K current-data policy, M5/M6 inventories).
/// </summary>
public sealed class RestoreExecutionTests : RestoreTestBase
{
    private string Db => Root.Paths.DatabaseFile;

    private static string Data(TempWorkHubRoot root, string name) => Path.Combine(root.Paths.DataDirectory, name);

    private (InitializationResult? Result, RestoreStartupException? Stop, Exception? Error) StartWithToken(string op, ScriptedRestoreUi? ui = null, Func<InstanceLocks, IRestoreFileOps>? files = null, Action<RestoreStage>? hook = null) =>
        RestoreKit.Start(Root, Coordinator(token: op, files: files, hook: hook), ui ?? new ScriptedRestoreUi());

    // ---- I / G: complete restores ------------------------------------------------------------------------------------

    [Fact]
    public async Task I_CurrentBackup_EveryTableAndBothTrashLevels_AreRestoredExactly_AndTheCurrentDataIsKeptTwice()
    {
        var ids = await PopulatedV7.CreateAsync(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var backupDump = BackupKit.Dump(BackupKit.Daily(Root, entry.FileName));
        // Change the current data after the backup (rows in several tables).
        using (var connection = BackupKit.Open(Db))
        {
            BackupKit.Exec(connection, $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('AFTERBKP', 'x', '{Db_.Now}', '{Db_.Now}');");
            BackupKit.Exec(connection, $"UPDATE ProjectNotes SET Content = 'changed after backup', Revision = Revision + 1 WHERE Id = {ids.EditedNote};");
            BackupKit.Exec(connection, "UPDATE OwnerGroups SET Name = 'renamed after backup' WHERE Id = (SELECT min(Id) FROM OwnerGroups);");
        }

        var currentDump = BackupKit.Dump(Db);
        Assert.NotEqual(backupDump, currentDump);
        foreach (var table in new[] { "Systems", "SystemRepositories", "OwnerGroups", "OwnerItems", "WorkspaceNodes", "Projects", "ProjectFolders", "ProjectLinks", "ProjectNotes", "ProjectTrashEntries" })
            Assert.Contains($"## {table}\n", backupDump);

        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        var (result, stop, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.Null(stop);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome);
        Assert.Contains(entry.FileName, result.RestoreNotice);
        RestoreKit.ClearPools(Root);
        Assert.Equal(backupDump, BackupKit.Dump(Db));
        Assert.Equal(currentDump, BackupKit.Dump(Path.Combine(RestoreKit.Previous(Root, op), "workhub.db")));

        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(RestoreState.Completed, journal.State);
        Assert.Equal(new DataClassification(CurrentDataClass.Healthy, Version: SchemaMigrations.LatestVersion), journal.ExecutionClass);
        Assert.Equal(SafetyKind.Validated, journal.Safety!.Kind);
        var safety = Path.Combine(Root.Paths.PreRestoreBackupsDirectory, journal.Safety.FileName!);
        Assert.StartsWith($"workhub_prerestore_v{SchemaMigrations.LatestVersion}_", journal.Safety.FileName);
        Assert.Equal(currentDump, BackupKit.Dump(safety));
        Assert.True(BackupKit.Validator().Check(safety, BackupCheckLevel.Full, SchemaMigrations.LatestVersion).IsFullyValidated);
        Assert.False(File.Exists(RestoreKit.Candidate(Root, op))); // renamed into Data\ at X9
        Assert.Equal(journal.Candidate!.FileId, RestoreKit.FileId(Db));
        Assert.Equal("Opened", journal.Completed!.Outcome);
        Assert.Equal(journal.Revision, journal.Revision); // sanity
        Assert.True(journal.Revision >= 8);
    }

    [Fact]
    public async Task G_OlderCandidate_IsRestoredThenMigratedByTheNormalInitializer_WithItsOwnPreMigrationBackup()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceOlderDaily(Root, version: 6);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);

        var (result, _, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Migrated, result!.Database.Outcome);
        Assert.Equal(6, result.Database.PreviousSchemaVersion);
        Assert.Contains($"upgraded from schema v6 to v{SchemaMigrations.LatestVersion}", result.RestoreNotice);
        Assert.NotNull(result.Database.MigrationBackupFile);
        Assert.StartsWith($"workhub_v6_before_v{SchemaMigrations.LatestVersion}_", Path.GetFileName(result.Database.MigrationBackupFile));
        RestoreKit.ClearPools(Root);
        using (var connection = BackupKit.Open(Db, SqliteOpenMode.ReadOnly))
        {
            Assert.Equal((long)SchemaMigrations.LatestVersion, BackupKit.Scalar(connection, "PRAGMA user_version;"));
            Assert.Equal(1L, BackupKit.Scalar(connection, "SELECT count(*) FROM Systems WHERE SystemCode = 'OLDV0001';"));
        }

        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(new CompletionRecord("Migrated", 6, SchemaMigrations.LatestVersion, Path.GetFileName(result.Database.MigrationBackupFile)), journal.Completed);
        Assert.Equal(BackupCompatibility.Older, journal.Candidate!.Compatibility);
    }

    /// <summary>
    /// Phase 12 §10.6: a v7 backup holding an archived Project and an archived-plus-trashed one (states an earlier build or
    /// raw SQL can leave) is staged as Older, restored, and migrated to v8 with its own pre-migration backup; both keep
    /// their status and get serial 0.
    /// </summary>
    [Fact]
    public async Task G_V7CandidateWithArchivedProjects_IsRestoredThenMigratedToV8_StatusKept_SerialZero()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceOlderDaily(Root, version: 7);
        using (var connection = BackupKit.Open(BackupKit.Daily(Root, entry.FileName)))
        {
            var archived = TestSupport.Db.InsertNode(connection, null, "Project", "Archived one");
            TestSupport.Db.InsertProjectDetails(connection, archived, "0000000000000000000000000000000a");
            var trashed = TestSupport.Db.InsertNode(connection, null, "Project", "Archived and trashed");
            TestSupport.Db.InsertProjectDetails(connection, trashed, "0000000000000000000000000000000b");
            BackupKit.Exec(connection, "UPDATE Projects SET Status = 'Archived';");
            BackupKit.Exec(connection, $"UPDATE WorkspaceNodes SET TrashedAt = '2026-02-01T00:00:00.000Z' WHERE Id = {trashed};");
        }

        var start = await RestoreKit.Requester(Root).BeginAsync(entry);
        Assert.Equal(BackupCompatibility.Older, start.Request!.Facts.Compatibility);
        var op = await start.Request.ConfirmAsync();
        start.Request.Dispose();

        var (result, _, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Migrated, result!.Database.Outcome);
        Assert.Equal((7, 8), (result.Database.PreviousSchemaVersion, result.Database.SchemaVersion));
        Assert.StartsWith("workhub_v7_before_v8_", Path.GetFileName(result.Database.MigrationBackupFile));
        RestoreKit.ClearPools(Root);
        using var restored = BackupKit.Open(Db, SqliteOpenMode.ReadOnly);
        Assert.Equal(8L, BackupKit.Scalar(restored, "PRAGMA user_version;"));
        Assert.Equal(2L, BackupKit.Scalar(restored, "SELECT count(*) FROM Projects WHERE Status = 'Archived' AND ArchiveSerial = 0;"));
        Assert.Equal(1L, BackupKit.Scalar(restored, "SELECT count(*) FROM WorkspaceNodes WHERE TrashedAt IS NOT NULL;"));
        Assert.Empty(SchemaReference.Frozen.DifferencesFrom(8, SchemaFingerprint.Read(restored)));
    }

    [Fact]
    public async Task G_MigrationBackupFailsAfterPublication_TheOpStaysPublishedAndOwned_RetryAfterwardsCompletesIt()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceOlderDaily(Root, version: 6);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        File.WriteAllText(Root.Paths.MigrationBackupsDirectory, "blocks the folder"); // CreateDirectory fails → MigrationBackupFailed

        var coordinator = Coordinator(token: op);
        var (_, stop, error) = RestoreKit.Start(Root, coordinator, new ScriptedRestoreUi());

        Assert.Null(stop);
        var refused = Assert.IsType<DatabaseStartupException>(error);
        Assert.Equal(DatabaseStartupFailure.MigrationBackupFailed, refused.Failure);
        Assert.Equal(RestoreState.Published, RestoreKit.Journal(Root, op).State);
        Assert.Equal(RestoreOwnerSituation.PublishedInitializationFailed, coordinator.Status!.Situation);
        Assert.True(coordinator.Locks.HoldsPresence && !coordinator.Locks.HoldsPresenceExclusive);
        Assert.False(coordinator.Locks.HoldsGate);
        Assert.False(coordinator.Locks.HoldsRestoreLock);

        // F13 / U4 (R20): the process that ran the failed initializer still holds a pooled connection; moving fails.
        var moveInProcess = Assert.Throws<IOException>(() => File.Move(Db, Db + ".moved"));
        Assert.Equal(32, moveInProcess.HResult & 0xFFFF);

        coordinator.Dispose(); // the owner window exits (Try starting again)
        RestoreKit.ClearPools(Root);
        File.Delete(Root.Paths.MigrationBackupsDirectory);
        var ui = new ScriptedRestoreUi();
        var (result, _, retryError) = RestoreKit.Start(Root, Coordinator(), ui); // no token: Published is deterministic

        Assert.Null(retryError);
        Assert.Equal(DatabaseInitializationOutcome.Migrated, result!.Database.Outcome);
        Assert.Empty(ui.Asks);
        Assert.Equal(RestoreState.Completed, RestoreKit.Journal(Root, op).State);
    }

    [Fact]
    public async Task G_CurrentRestore_RestoredDataCanBeEditedAndADailyBackupFollows_PreRestoreIsOutsideRetention()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        var clock = new SettableClock(SettableClock.Tokyo(2026, 10, 20));
        var daily = BackupKit.Service(Root, clock);

        var result = RestoreKit.Initializer(Root, Coordinator(token: op), daily: daily).Initialize(new ScriptedRestoreUi());

        Assert.Equal(DailyBackupOutcome.Created, result.DailyBackup!.Outcome);
        var preRestore = Directory.GetFiles(Root.Paths.PreRestoreBackupsDirectory);
        Assert.Single(preRestore);
        // J: retention never sees PreRestore or Data\Restore.
        for (var day = 21; day <= 26; day++)
        {
            clock.Now = SettableClock.Tokyo(2026, 10, day);
            BackupKit.Service(Root, clock).Run();
        }

        Assert.Equal(preRestore, Directory.GetFiles(Root.Paths.PreRestoreBackupsDirectory));
        Assert.True(File.Exists(Path.Combine(RestoreKit.Previous(Root, op), "workhub.db")));
        Assert.Equal(RestoreState.Completed, RestoreKit.Journal(Root, op).State);
        Assert.DoesNotContain(BackupKit.PublishedBackups(Root), n => n.Contains("_20261020_", StringComparison.Ordinal)); // pruned as usual
    }

    // ---- K: current-data classes -------------------------------------------------------------------------------------

    [Fact]
    public async Task K1_HealthyLiveWal_SafetyBackupIsStandaloneAndHoldsTheCommittedWalRows()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        using (var child = RestoreKit.StartProbe("wal-commit", Db))
            child.Kill();
        Assert.Equal(["workhub.db", "workhub.db-shm", "workhub.db-wal"], RestoreKit.DataSet(Root));

        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        var (_, _, error) = StartWithToken(op);

        Assert.Null(error);
        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(SafetyKind.Validated, journal.Safety!.Kind);
        var safety = Path.Combine(Root.Paths.PreRestoreBackupsDirectory, journal.Safety.FileName!);
        Assert.Equal(((byte)1, (byte)1), BackupKit.Header(safety));
        Assert.Empty(BackupKit.Sidecars(safety));
        using (var connection = BackupKit.Open(safety, SqliteOpenMode.ReadOnly))
            Assert.Equal(30L, BackupKit.Scalar(connection, "SELECT count(*) FROM Systems WHERE SystemCode LIKE 'WALC%';"));
        // As-found evidence kept the three files; the refreshed inventory is what was moved (M5).
        Assert.True(journal.AsFound);
        Assert.Equal(["workhub.db", "workhub.db-shm", "workhub.db-wal"], journal.AsFoundInventory!.Select(f => f.Name).Order(StringComparer.Ordinal));
        Assert.Equal(journal.AsFoundInventory!.Single(f => f.Name == "workhub.db").FileId, journal.PreviousInventory!.Single(f => f.Name == "workhub.db").FileId);
        Assert.Equal(journal.PreviousInventory!.Select(f => f.Name).Order(StringComparer.Ordinal), Directory.GetFiles(RestoreKit.Previous(Root, op)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(RestoreKit.OpDir(Root, op), "asfound")).Length);
    }

    [Fact]
    public async Task K2_K7_HotJournal_RequestIsReadOnly_AsFoundIsByteExact_SafetyEqualsTheRecoveredState()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var committed = BackupKit.Dump(Db);
        using (var child = RestoreKit.StartProbe("hot-journal", Db))
            child.Kill();
        Assert.True(File.Exists(Db + "-journal"));
        var dbHash = BackupKit.Hash(Db);
        var journalHash = BackupKit.Hash(Db + "-journal");

        var start = await RestoreKit.Requester(Root).BeginAsync(entry);
        using (var request = start.Request!)
        {
            // K7: classification at request time never changes bytes.
            Assert.Equal(CurrentDataClass.NeedsRecovery, request.Facts.RequestClass.Class);
            Assert.Equal(dbHash, BackupKit.Hash(Db));
            Assert.Equal(journalHash, BackupKit.Hash(Db + "-journal"));
            await request.ConfirmAsync();
        }

        var op = RestoreKit.Ops(Root).Single();
        var (_, _, error) = StartWithToken(op);

        Assert.Null(error);
        var asFound = Path.Combine(RestoreKit.OpDir(Root, op), "asfound");
        Assert.Equal(dbHash, BackupKit.Hash(Path.Combine(asFound, "workhub.db")));
        Assert.Equal(journalHash, BackupKit.Hash(Path.Combine(asFound, "workhub.db-journal")));
        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(CurrentDataClass.Healthy, journal.ExecutionClass!.Class); // NeedsRecovery acknowledged → any readable class
        Assert.Equal(committed, BackupKit.Dump(Path.Combine(Root.Paths.PreRestoreBackupsDirectory, journal.Safety!.FileName!)));
        // M5: SQLite consumed the journal in X4; previousInventory (refreshed) holds only the main file, same ID.
        Assert.Contains(journal.AsFoundInventory!, f => f.Name == "workhub.db-journal");
        Assert.Equal(["workhub.db"], journal.PreviousInventory!.Select(f => f.Name));
        Assert.Equal(committed, BackupKit.Dump(Path.Combine(RestoreKit.Previous(Root, op), "workhub.db")));
    }

    public static TheoryData<string, DegradedKind, int> DegradedCases() => new()
    {
        { $"PRAGMA user_version = {SchemaMigrations.LatestVersion + 1};", DegradedKind.Newer, SchemaMigrations.LatestVersion + 1 },
        { "PRAGMA user_version = 0;", DegradedKind.Foreign, 0 },
        { "CREATE TABLE Extra (Id INTEGER PRIMARY KEY, Name TEXT);", DegradedKind.StructureDiffers, SchemaMigrations.LatestVersion },
        { "PRAGMA foreign_keys = OFF; INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, SortOrder, CreatedAt, UpdatedAt) VALUES (9999, 'orphan', 'Web', 'https://x.test', 0, 'x', 'x');", DegradedKind.ForeignKeys, SchemaMigrations.LatestVersion },
    };

    [Theory]
    [MemberData(nameof(DegradedCases))]
    public async Task K3_K5_DegradedCurrentData_IsPreservedFaithfully_NotValidated_AndTheCandidatePolicyIsUnchanged(string damage, DegradedKind kind, int version)
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        using (var connection = BackupKit.Open(Db))
            BackupKit.Exec(connection, damage);
        var damagedDump = BackupKit.Dump(Db);

        var start = await RestoreKit.Requester(Root).BeginAsync(entry);
        Assert.Equal(new DataClassification(CurrentDataClass.Degraded, kind, version), start.Request!.Facts.RequestClass);
        await start.Request.ConfirmAsync();
        var op = RestoreKit.Ops(Root).Single();
        var (result, _, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome);
        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(SafetyKind.Preserved, journal.Safety!.Kind);
        Assert.StartsWith($"workhub_preserved_v{version}_", journal.Safety.FileName);
        var preserved = Path.Combine(Root.Paths.PreRestoreBackupsDirectory, journal.Safety.FileName!);
        Assert.Equal(damagedDump, BackupKit.Dump(preserved));
        Assert.Empty(BackupKit.Sidecars(preserved));

        // The Phase 9 candidate rules still refuse it (K9): it is shown with its real status and cannot be restored.
        var check = BackupKit.Validator().Check(preserved, BackupCheckLevel.Full, version);
        Assert.False(check.IsFullyValidated);
        var catalogEntry = (await new BackupCatalog(Root.Paths).ListAsync()).Single(e => e.FileName == journal.Safety.FileName);
        Assert.Equal(BackupKind.Preserved, catalogEntry.Kind);
        Assert.Equal(BackupFolder.PreRestore, catalogEntry.Folder);
        var refused = await RestoreKit.Requester(Root).BeginAsync(catalogEntry);
        Assert.Null(refused.Request);
        Assert.Contains("cannot be restored", refused.Refusal);
    }

    [Fact]
    public async Task K6_UnreadableCurrentData_NoCopyIsMade_TheRawSetIsMovedAsideByteIdentical()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        BackupKit.CorruptPage(Db);
        var hash = BackupKit.Hash(Db);

        var start = await RestoreKit.Requester(Root).BeginAsync(entry);
        Assert.Equal(CurrentDataClass.Unreadable, start.Request!.Facts.RequestClass.Class);
        await start.Request.ConfirmAsync();
        var op = RestoreKit.Ops(Root).Single();
        var (result, _, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.NotNull(result);
        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(SafetyKind.None, journal.Safety!.Kind);
        Assert.False(Directory.Exists(Root.Paths.PreRestoreBackupsDirectory) && Directory.GetFiles(Root.Paths.PreRestoreBackupsDirectory).Length > 0);
        Assert.Equal(hash, BackupKit.Hash(Path.Combine(RestoreKit.Previous(Root, op), "workhub.db")));
    }

    [Fact]
    public async Task B_MissingCurrentData_IsAcknowledged_NothingToPreserve_TheBackupIsRestored_NoSeeding()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var backupDump = BackupKit.Dump(BackupKit.Daily(Root, entry.FileName));
        File.Delete(Db);

        var start = await RestoreKit.Requester(Root).BeginAsync(entry);
        Assert.Equal(CurrentDataClass.Missing, start.Request!.Facts.RequestClass.Class);
        await start.Request.ConfirmAsync();
        var op = RestoreKit.Ops(Root).Single();
        var (result, _, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome); // never Created (seeded)
        RestoreKit.ClearPools(Root);
        Assert.Equal(backupDump, BackupKit.Dump(Db));
        Assert.Empty(RestoreKit.Journal(Root, op).PreviousInventory!);
    }

    // ---- X-abort: nothing is moved ------------------------------------------------------------------------------------

    [Fact]
    public async Task K5_ClassChangedAfterConfirmation_AbortsBeforeAnyMove()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        using (var connection = BackupKit.Open(Db))
            BackupKit.Exec(connection, "CREATE TABLE Extra (Id INTEGER PRIMARY KEY, Name TEXT);");
        var dump = BackupKit.Dump(Db);
        var id = RestoreKit.FileId(Db);

        var (result, _, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.Equal(RestoreExecutor.ClassChangedMessage, result!.RestoreNotice);
        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(RestoreState.Aborted, journal.State);
        Assert.Contains("class changed", journal.AbortReason);
        RestoreKit.ClearPools(Root);
        Assert.Equal(id, RestoreKit.FileId(Db));
        Assert.Equal(dump, BackupKit.Dump(Db));
        Assert.False(Directory.Exists(RestoreKit.Previous(Root, op)));
    }

    [Fact]
    public async Task A_StagedCopyChangedAfterConfirmation_AbortsAtX1()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        RestoreKit.InsertSystem(RestoreKit.Candidate(Root, op), "TAMPERED");
        var dump = BackupKit.Dump(Db);

        var (result, _, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.Equal(RestoreExecutor.CandidateChangedMessage, result!.RestoreNotice);
        Assert.Equal(RestoreState.Aborted, RestoreKit.Journal(Root, op).State);
        RestoreKit.ClearPools(Root);
        Assert.Equal(dump, BackupKit.Dump(Db));
    }

    [Fact]
    public async Task M6_MainFileReplacedBetweenX2AndX5_Aborts()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);

        var (result, _, error) = StartWithToken(op, hook: stage =>
        {
            if (stage != RestoreStage.Preserved)
                return;
            File.Copy(Db, Db + ".copy");
            File.Delete(Db);
            File.Move(Db + ".copy", Db); // same bytes, different file ID
        });

        Assert.Null(error);
        Assert.Equal(RestoreState.Aborted, RestoreKit.Journal(Root, op).State);
        Assert.Contains("replaced", RestoreKit.Journal(Root, op).AbortReason);
        Assert.NotNull(result);
        Assert.True(File.Exists(Db));
    }

    [Fact]
    public async Task C_SafetyCopyCannotBeMade_AbortsBeforeAnyMove()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        File.WriteAllText(Root.Paths.PreRestoreBackupsDirectory, "blocks the folder");
        var id = RestoreKit.FileId(Db);

        var (result, _, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.Equal(CurrentDataPreserver.SafetyFailedMessage, result!.RestoreNotice);
        Assert.Equal(RestoreState.Aborted, RestoreKit.Journal(Root, op).State);
        Assert.Equal(id, RestoreKit.FileId(Db));
    }

    [Fact]
    public async Task X6_DatabaseHeldOpenByAnotherProgram_MoveAsideFails_EveryMovedFileComesBack_Aborted()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        File.WriteAllBytes(Db + "-wal", []); // a sidecar that moves first; the held main file then fails
        RestoreKit.ProbeChild? holder = null;
        try
        {
            var (result, _, error) = StartWithToken(op, hook: stage =>
            {
                if (stage == RestoreStage.MovingAsidePublished)
                    holder = RestoreKit.StartProbe("hold", Db); // an older WorkHub that ignores the locks
            });

            Assert.Null(error);
            Assert.Equal(CurrentDataPreserver.InUseMessage, result is null ? null : result.RestoreNotice);
        }
        finally
        {
            holder?.Dispose();
        }

        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(RestoreState.Aborted, journal.State);
        Assert.Empty(Directory.GetFiles(RestoreKit.Previous(Root, op)));
        Assert.Equal(journal.PreviousInventory!.Select(f => f.Name).Order(StringComparer.Ordinal), RestoreKit.DataSet(Root));
    }

    [Fact]
    public async Task X6_MovingBackAlsoFails_StaysMovingAside_AndThisWindowOwnsIt()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        File.WriteAllBytes(Db + "-wal", []);
        var coordinator = Coordinator(token: op, files: RestoreKit.Faulty(Root, ops => ops.OnMove = (from, to) =>
        {
            if (from.EndsWith("workhub.db", StringComparison.Ordinal) || to.EndsWith(@"Data\workhub.db-wal", StringComparison.Ordinal))
                throw new IOException("denied", unchecked((int)0x80070005));
        }));

        var (_, stop, _) = RestoreKit.Start(Root, coordinator, new ScriptedRestoreUi());

        Assert.Equal(RestoreEntryKind.OwnerStopped, stop!.Kind);
        Assert.Equal(RestoreState.MovingAside, RestoreKit.Journal(Root, op).State);
        Assert.Equal(RestoreOwnerSituation.MovingAside, coordinator.Status!.Situation);
        Assert.False(coordinator.Locks.HoldsGate || coordinator.Locks.HoldsRestoreLock);
        Assert.True(File.Exists(Db)); // nothing deleted; one sidecar is aside, the main file is in place
        Assert.True(File.Exists(Path.Combine(RestoreKit.Previous(Root, op), "workhub.db-wal")));

        // Retry (a new start after this window exits) rolls forward from the consistent MovingAside (R10).
        coordinator.Dispose();
        var (result, _, error) = RestoreKit.Start(Root, Coordinator(), new ScriptedRestoreUi());
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(RestoreState.Completed, RestoreKit.Journal(Root, op).State);
    }

    [Fact]
    public async Task X9_PublicationFails_StaysMovedAside_OwnerCanPutBackBeforePublication()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        RestoreKit.InsertSystem(Db, "CURRENT1");
        var currentDump = BackupKit.Dump(Db);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        var coordinator = Coordinator(token: op, files: RestoreKit.Faulty(Root, ops => ops.OnMove = (from, to) =>
        {
            if (from.EndsWith(RestoreNames.Candidate, StringComparison.Ordinal))
                throw new IOException("denied", unchecked((int)0x80070005));
        }));

        var (_, stop, _) = RestoreKit.Start(Root, coordinator, new ScriptedRestoreUi());

        Assert.Equal(RestoreEntryKind.OwnerStopped, stop!.Kind);
        Assert.Equal(RestoreState.MovedAside, RestoreKit.Journal(Root, op).State);
        Assert.Equal(RestoreOwnerSituation.MovedAside, coordinator.Status!.Situation);
        Assert.Empty(RestoreKit.DataSet(Root));
        var candidateHash = BackupKit.Hash(RestoreKit.Candidate(Root, op));

        var putBack = coordinator.RequestPutBack();
        Assert.True(putBack.Succeeded, putBack.Message);
        Assert.Equal(op, putBack.RelaunchOp);
        var requested = RestoreKit.Journal(Root, op);
        Assert.Equal(RestoreState.UndoRequested, requested.State);
        Assert.Equal(UndoOrigin.BeforePublication, requested.Undo!.Origin);
        coordinator.Dispose();

        var (result, _, error) = RestoreKit.Start(Root, Coordinator(token: putBack.RelaunchOp), new ScriptedRestoreUi());

        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome);
        Assert.Equal("Your previous data was put back.", result.RestoreNotice);
        Assert.Equal(RestoreState.Undone, RestoreKit.Journal(Root, op).State);
        RestoreKit.ClearPools(Root);
        Assert.Equal(currentDump, BackupKit.Dump(Db));
        Assert.Equal(candidateHash, BackupKit.Hash(RestoreKit.Candidate(Root, op))); // staged copy untouched
        Assert.False(Directory.Exists(RestoreKit.Restored(Root, op)));                // never created for BeforePublication
    }

    // ---- C: Completed cannot be written (§8.4) ------------------------------------------------------------------------

    [Fact]
    public async Task C1_CompletedWriteFails_AppRuns_OnlySharedPresenceHeld_TryAgainRecordsIt()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        var failCompleted = false;
        var coordinator = Coordinator(
            token: op,
            hook: stage => failCompleted |= stage == RestoreStage.BeforeCompletedWrite,
            files: RestoreKit.Faulty(Root, ops => ops.OnWrite = _ =>
            {
                if (failCompleted)
                    throw new IOException("disk", unchecked((int)0x80070070));
            }));

        var (result, _, error) = RestoreKit.Start(Root, coordinator, new ScriptedRestoreUi());

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Null(result!.RestoreNotice);
        Assert.Equal(RestoreOwnerSituation.CompletionUnrecorded, coordinator.Status!.Situation);
        Assert.Equal(RestoreState.Published, RestoreKit.Journal(Root, op).State);
        Assert.True(coordinator.Locks.HoldsPresence && !coordinator.Locks.HoldsPresenceExclusive);
        using (var other = new InstanceLocks(Root.Paths))
        {
            Assert.True(other.TryAcquireGate());   // G free
            Assert.True(other.TryRestoreLock());   // R free
        }

        failCompleted = false;
        var tried = coordinator.RecordCompletion();

        Assert.True(tried.Succeeded, tried.Message);
        Assert.Null(coordinator.Status);
        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(RestoreState.Completed, journal.State);
        Assert.Equal("Opened", journal.Completed!.Outcome);
    }

    // ---- R6 / F: normal starts --------------------------------------------------------------------------------------

    [Fact]
    public void R6_NormalStart_RegistersPresenceUnderTheGate_AndHoldsTheGateThroughInitialization()
    {
        BackupKit.CreateDatabase(Root);
        var observed = new List<string>();
        var coordinator = Coordinator();
        var initializer = new AppInitializer(Root.Paths, new WorkHub.Infrastructure.FileSystem.LocalStorage(Root.Paths), new WorkHub.Infrastructure.Settings.JsonSettingsStore(Root.Paths.SettingsFile),
            new ObservingInitializer(() =>
            {
                using var other = new InstanceLocks(Root.Paths);
                observed.Add($"gate free: {other.TryAcquireGate()}");
                observed.Add($"presence held by us: {coordinator.Locks.HoldsPresence}");
            }), WorkHub.Core.Diagnostics.NullAppLog.Instance, null, coordinator);

        initializer.Initialize(new ScriptedRestoreUi());

        Assert.Equal(["gate free: False", "presence held by us: True"], observed);
        Assert.False(coordinator.Locks.HoldsGate);
        Assert.True(coordinator.Locks.HoldsPresence);
    }

    private sealed class ObservingInitializer(Action during) : IDatabaseInitializer
    {
        public DatabaseInitializationResult Initialize()
        {
            during();
            return new DatabaseInitializationResult(DatabaseInitializationOutcome.Opened, 7, 7);
        }
    }

    private static class Db_
    {
        public const string Now = TestSupport.Db.Now;
    }
}
