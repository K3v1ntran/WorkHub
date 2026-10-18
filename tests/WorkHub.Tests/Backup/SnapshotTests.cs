using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Startup;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Backup;

/// <summary>
/// Snapshot S1–S8 through the real daily backup service, with DELETE and WAL sources and real concurrent writers
/// (Phase 9 §4.2; tests B11–B21).
/// </summary>
public sealed class SnapshotTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SettableClock _clock = new(SettableClock.Tokyo(2026, 10, 13));

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task PopulatedV7_EveryRowOfEveryTableIsPreserved()
    {
        await PopulatedV7.CreateAsync(_root);
        var before = BackupKit.Dump(_root.Paths.DatabaseFile);

        var result = BackupKit.Service(_root, _clock).Run();

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        var backup = BackupKit.Daily(_root, result.FileName!);
        Assert.Equal(before, BackupKit.Dump(backup));
        Assert.Equal(before, BackupKit.Dump(_root.Paths.DatabaseFile)); // source unchanged
        using var connection = BackupKit.Open(backup, SqliteOpenMode.ReadOnly);
        Assert.Empty(SchemaReference.Frozen.DifferencesFrom(7, SchemaFingerprint.Read(connection)));
        Assert.True((long)BackupKit.Scalar(connection, "SELECT count(*) FROM sqlite_sequence;")! > 0);
    }

    [Fact]
    public async Task PublishedBackup_OpensAsAWorkHubDatabaseWithAllContent()
    {
        var ids = await PopulatedV7.CreateAsync(_root);
        var result = BackupKit.Service(_root, _clock).Run();

        using var other = new TempWorkHubRoot();
        Directory.CreateDirectory(other.Paths.DataDirectory);
        File.Copy(BackupKit.Daily(_root, result.FileName!), other.Paths.DatabaseFile);
        Assert.Equal(DatabaseInitializationOutcome.Opened, other.CreateDatabaseInitializer([.. WorkHub.Infrastructure.Persistence.Migrations.SchemaMigrations.All]).Initialize().Outcome);

        var connections = new SqliteConnectionFactory(other.Paths.DatabaseFile);
        var workspace = new WorkspaceService(new SqliteWorkspaceStore(connections));
        var content = new ProjectContentService(new SqliteProjectContentStore(connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));

        Assert.Contains(await workspace.ListTrashAsync(), e => e.NodeId == ids.TrashedProject);
        var trash = await content.ListTrashAsync(ids.ActiveProject);
        Assert.Contains(trash, e => e.Id == ids.NestedTrashEntry && e.ItemCount == 3);
        Assert.Contains(trash, e => e.Id == ids.IndependentTrashEntry);
        var note = await content.GetNoteAsync(ids.EditedNote);
        Assert.Equal("v3 edited again", note!.Content);
        Assert.True(note.Revision >= 3);
        connections.ClearPools();
    }

    [Fact]
    public async Task DeleteSource_ContinuousCommittingWriter_GivesAConsistentBackup()
    {
        BackupKit.CreateDatabase(_root);
        using var stop = new CancellationTokenSource();
        var writer = PairWriter(stop.Token);

        var results = Enumerable.Range(0, 3).Select(i =>
        {
            _clock.Now = SettableClock.Tokyo(2026, 10, 13 + i);
            return BackupKit.Service(_root, _clock).Run();
        }).ToList();
        stop.Cancel();
        var commits = await writer;

        Assert.All(results, r => Assert.Equal(DailyBackupOutcome.Created, r.Outcome));
        Assert.True(commits > 0);
        foreach (var r in results)
            AssertPairsComplete(BackupKit.Daily(_root, r.FileName!));
    }

    [Fact]
    public void DeleteSource_WriterHoldingReserved_IsExcludedAndCommitsAfterwards()
    {
        BackupKit.CreateDatabase(_root);
        using var writer = BackupKit.Open(_root.Paths.DatabaseFile, timeout: 30);
        var transaction = writer.BeginTransaction(deferred: false);
        BackupKit.Exec(writer, InsertSystem("UNCOMMIT1"), transaction);

        var result = BackupKit.Service(_root, _clock).Run();
        transaction.Commit(); // the pin is released: the commit succeeds now

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.Equal(0L, CountSystems(BackupKit.Daily(_root, result.FileName!), "UNCOMMIT1"));
        Assert.Equal(1L, CountSystems(_root.Paths.DatabaseFile, "UNCOMMIT1"));
    }

    [Fact]
    public void DeleteSource_CommitAttemptWhilePinned_WaitsAndIsNotInTheBackup()
    {
        BackupKit.CreateDatabase(_root);
        string? writerOutcome = null;
        var service = BackupKit.Service(_root, _clock, hook: stage =>
        {
            if (stage != BackupStage.SourcePinned)
                return;
            try
            {
                using var w = BackupKit.Open(_root.Paths.DatabaseFile, timeout: 1);
                BackupKit.Exec(w, InsertSystem("LATE0001"));
                writerOutcome = "committed";
            }
            catch (SqliteException ex)
            {
                writerOutcome = $"busy {ex.SqliteErrorCode}";
            }
        });

        var result = service.Run();

        Assert.Equal("busy 5", writerOutcome); // DELETE mode: the pin blocks commits
        Assert.Equal(0L, CountSystems(BackupKit.Daily(_root, result.FileName!), "LATE0001"));
    }

    [Fact]
    public void DeleteSource_ExclusiveLockLongerThanTheLockWait_FailsSourceBusy_WithNoFilesLeft()
    {
        BackupKit.CreateDatabase(_root);
        using var holder = BackupKit.Open(_root.Paths.DatabaseFile);
        BackupKit.Exec(holder, "BEGIN EXCLUSIVE;");

        var result = BackupKit.Service(_root, _clock, lockWaitSeconds: 1).Run();
        BackupKit.Exec(holder, "ROLLBACK;");

        Assert.Equal(DailyBackupFailure.SourceBusy, result.Failure);
        Assert.Equal(["daily-backup.lock"], BackupKit.DailyFiles(_root));
    }

    [Fact]
    public void WalSource_CommitAfterThePin_SucceedsAndIsNotInTheBackup()
    {
        BackupKit.CreateDatabase(_root);
        using var holder = UseWal();
        string? writerOutcome = null;
        var service = BackupKit.Service(_root, _clock, hook: stage =>
        {
            if (stage != BackupStage.SourcePinned)
                return;
            using var w = BackupKit.Open(_root.Paths.DatabaseFile, timeout: 1);
            BackupKit.Exec(w, InsertSystem("LATE0001"));
            writerOutcome = "committed";
        });

        var result = service.Run();

        Assert.Equal("committed", writerOutcome); // WAL mode: commits continue during the snapshot
        Assert.Equal(0L, CountSystems(BackupKit.Daily(_root, result.FileName!), "LATE0001"));
        Assert.Equal(1L, CountSystems(_root.Paths.DatabaseFile, "LATE0001"));
    }

    [Fact]
    public void WalSource_UncommittedWriterDuringTheCopy_IsNotInTheBackup()
    {
        BackupKit.CreateDatabase(_root);
        using var holder = UseWal();
        SqliteConnection? writer = null;
        SqliteTransaction? transaction = null;
        var service = BackupKit.Service(_root, _clock, hook: stage =>
        {
            if (stage != BackupStage.SourcePinned)
                return;
            writer = BackupKit.Open(_root.Paths.DatabaseFile, timeout: 1);
            transaction = writer.BeginTransaction(deferred: false);
            BackupKit.Exec(writer, InsertSystem("UNCOMMIT1"), transaction);
        });

        var result = service.Run();
        transaction!.Commit();
        writer!.Dispose();

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.Equal(0L, CountSystems(BackupKit.Daily(_root, result.FileName!), "UNCOMMIT1"));
    }

    [Fact]
    public async Task WalSource_ContinuousWriter_ConsistentStandaloneBackup_SourceModeAndSidecarsUntouched()
    {
        BackupKit.CreateDatabase(_root);
        using var holder = UseWal();
        using var stop = new CancellationTokenSource();
        var writer = PairWriter(stop.Token);
        var files = new FaultyBackupFileOps();

        var result = BackupKit.Service(_root, _clock, files).Run();
        stop.Cancel();
        await writer;

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        var backup = BackupKit.Daily(_root, result.FileName!);
        AssertPairsComplete(backup);
        Assert.Equal(((byte)1, (byte)1), BackupKit.Header(backup));
        Assert.Empty(BackupKit.Sidecars(backup));
        Assert.Equal("wal", BackupKit.Scalar(holder, "PRAGMA journal_mode;"));
        Assert.True(File.Exists(_root.Paths.DatabaseFile + "-wal"), "source sidecars: " + string.Join(",", BackupKit.Sidecars(_root.Paths.DatabaseFile)) + " in " + string.Join(",", Directory.GetFiles(_root.Paths.DataDirectory).Select(Path.GetFileName)));
        // WorkHub's own file operations never touched the source or its sidecars.
        Assert.DoesNotContain(files.Touched, p => p.StartsWith(_root.Paths.DatabaseFile + "-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files.Deleted, n => n.StartsWith("workhub.db", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Candidate_IsNormalizedBeforeClose_AndPublishedStandalone(bool wal)
    {
        BackupKit.CreateDatabase(_root);
        using var holder = wal ? UseWal() : null;
        (byte, byte)? headerAfterCopy = null;
        string? modeAfterNormalize = null;
        var service = BackupKit.Service(_root, _clock, hook: stage =>
        {
            var candidate = Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial").SingleOrDefault();
            if (stage == BackupStage.Copied)
                headerAfterCopy = BackupKit.Header(candidate!);
            if (stage == BackupStage.Normalized)
                modeAfterNormalize = BackupKit.Header(candidate!) == (1, 1) ? "delete" : "not normalized";
        });

        var result = service.Run();

        Assert.Equal(wal ? ((byte)2, (byte)2) : ((byte)1, (byte)1), headerAfterCopy);
        Assert.Equal("delete", modeAfterNormalize);
        var backup = BackupKit.Daily(_root, result.FileName!);
        Assert.Equal(((byte)1, (byte)1), BackupKit.Header(backup));
        Assert.Empty(BackupKit.Sidecars(backup));
        Assert.Empty(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial*"));
        using var check = BackupKit.Open(backup, SqliteOpenMode.ReadOnly);
        Assert.Equal("delete", BackupKit.Scalar(check, "PRAGMA journal_mode;"));
        Assert.Equal(2L, BackupKit.Scalar(check, "PRAGMA synchronous;")); // FULL: SQLite syncs the candidate's commits
    }

    [Fact]
    public void MissingSource_FailsAndCreatesNoDatabase()
    {
        Directory.CreateDirectory(_root.Paths.DataDirectory);

        var result = BackupKit.Service(_root, _clock).Run();

        Assert.Equal(DailyBackupFailure.SourceMissing, result.Failure);
        Assert.False(File.Exists(_root.Paths.DatabaseFile));
        Assert.Empty(BackupKit.PublishedBackups(_root));
    }

    [Fact]
    public void SourceWithATamperedGuard_FailsSourceCheck_AndNothingIsPublishedOrPruned()
    {
        BackupKit.CreateDatabase(_root);
        var olds = Enumerable.Range(1, 5).Select(d => BackupKit.PlaceValidBackup(_root, BackupKit.NameFor(new DateOnly(2026, 10, 12 - d)))).ToList();
        using (var c = BackupKit.Open(_root.Paths.DatabaseFile))
            BackupKit.Exec(c, "DROP TRIGGER TR_Projects_NoDelete; CREATE TRIGGER TR_Projects_NoDelete BEFORE DELETE ON Projects BEGIN SELECT 1; END;");

        var result = BackupKit.Service(_root, _clock).Run();

        Assert.Equal(DailyBackupFailure.SourceCheck, result.Failure);
        Assert.Contains("TR_Projects_NoDelete", result.Detail);
        Assert.All(olds, f => Assert.True(File.Exists(f)));
        Assert.Equal(5, BackupKit.PublishedBackups(_root).Length);
    }

    private SqliteConnection UseWal()
    {
        var holder = BackupKit.Open(_root.Paths.DatabaseFile);
        Assert.Equal("wal", BackupKit.Scalar(holder, "PRAGMA journal_mode = WAL;"));
        // A read attaches this connection to the WAL index, as a running WorkHub instance would be; otherwise SQLite
        // itself checkpoints and removes -wal/-shm when the last attached connection closes.
        BackupKit.Scalar(holder, "SELECT count(*) FROM Systems;");
        return holder;
    }

    /// <summary>Commits a System plus its membership in one transaction, repeatedly, until cancelled.</summary>
    private Task<int> PairWriter(CancellationToken token) => Task.Run(() =>
    {
        using var w = BackupKit.Open(_root.Paths.DatabaseFile, timeout: 30);
        var n = 0;
        while (!token.IsCancellationRequested)
        {
            using var t = w.BeginTransaction(deferred: false);
            BackupKit.Exec(w, InsertSystem($"PAIR{n:D5}"), t);
            BackupKit.Exec(w, "INSERT INTO SystemRepositories (SystemId, RepositoryType) VALUES (last_insert_rowid(), 'SVN_SYSTEM');", t);
            t.Commit();
            n++;
        }

        return n;
    });

    private static void AssertPairsComplete(string file)
    {
        using var c = BackupKit.Open(file, SqliteOpenMode.ReadOnly);
        Assert.Equal(0L, BackupKit.Scalar(c, "SELECT count(*) FROM Systems s WHERE s.SystemCode LIKE 'PAIR%' AND NOT EXISTS (SELECT 1 FROM SystemRepositories r WHERE r.SystemId = s.Id);"));
        Assert.Equal(0L, BackupKit.Scalar(c, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal("ok", BackupKit.Scalar(c, "PRAGMA integrity_check;"));
    }

    private static string InsertSystem(string code) =>
        $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('{code}', 'Writer', '{Db.Now}', '{Db.Now}');";

    private static long CountSystems(string file, string code)
    {
        using var c = BackupKit.Open(file, SqliteOpenMode.ReadOnly);
        return (long)BackupKit.Scalar(c, $"SELECT count(*) FROM Systems WHERE SystemCode = '{code}';")!;
    }
}
