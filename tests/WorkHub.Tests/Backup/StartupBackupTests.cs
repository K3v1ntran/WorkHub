using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.FileSystem;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Infrastructure.Settings;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Backup;

/// <summary>Daily backup inside startup (Phase 9 §5.1–§5.3; tests F48–F51).</summary>
public sealed class StartupBackupTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SettableClock _clock = new(SettableClock.Tokyo(2026, 10, 13));

    public void Dispose() => _root.Dispose();

    private AppInitializer Initializer(IDatabaseInitializer database, IDailyBackup? backup, IAppLog? log = null) =>
        new(_root.Paths, new LocalStorage(_root.Paths), new JsonSettingsStore(_root.Paths.SettingsFile), database, log ?? NullAppLog.Instance, backup);

    [Theory]
    [InlineData(DatabaseStartupFailure.Unreadable)]
    [InlineData(DatabaseStartupFailure.IntegrityCheckFailed)]
    [InlineData(DatabaseStartupFailure.NewerSchema)]
    [InlineData(DatabaseStartupFailure.UnrecognizedDatabase)]
    [InlineData(DatabaseStartupFailure.MigrationBackupFailed)]
    [InlineData(DatabaseStartupFailure.MigrationFailed)]
    [InlineData(DatabaseStartupFailure.NewDatabaseFailed)]
    public void RefusedDatabase_TheDailyBackupNeverRuns(DatabaseStartupFailure failure)
    {
        var backup = new CountingBackup();

        Assert.Throws<DatabaseStartupException>(() => Initializer(new ThrowingDatabase(failure), backup).Initialize());

        Assert.Equal(0, backup.Runs);
        Assert.False(Directory.Exists(_root.Paths.DailyBackupsDirectory));
    }

    [Fact]
    public void CreatedProfile_IsBackedUpOnItsFirstStart()
    {
        var database = new DatabaseInitializer(_root.Paths, NullAppLog.Instance, InitialSystemsSeed.Apply);

        var result = Initializer(database, BackupKit.Service(_root, _clock)).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Database.Outcome);
        Assert.Equal(DailyBackupOutcome.Created, result.DailyBackup!.Outcome);
        Assert.Equal(BackupKit.Dump(_root.Paths.DatabaseFile), BackupKit.Dump(BackupKit.Daily(_root, result.DailyBackup.FileName!)));
    }

    [Fact]
    public void UnexpectedExceptionInTheBackup_DoesNotFailStartup()
    {
        var database = new DatabaseInitializer(_root.Paths, NullAppLog.Instance);
        var log = new RecordingLog();

        var result = Initializer(database, new ThrowingBackup(), log).Initialize();

        Assert.Equal(DailyBackupOutcome.Failed, result.DailyBackup!.Outcome);
        Assert.Equal(DailyBackupFailure.Unexpected, result.DailyBackup.Failure);
        Assert.Contains("startup continues", log.All);
    }

    [Fact]
    public void WithoutABackupConfigured_NothingChanges()
    {
        var result = _root.CreateInitializer().Initialize();

        Assert.Null(result.DailyBackup);
        Assert.Empty(Directory.GetFileSystemEntries(_root.Paths.BackupsDirectory));
    }

    [Fact]
    public void MigratedDatabase_HasAMigrationBackupAndAV7DailyBackup_MigrationBackupNeverPruned()
    {
        Directory.CreateDirectory(_root.Paths.DataDirectory);
        BackupKit.BuildSchema(_root.Paths.DatabaseFile, 6);
        var database = new DatabaseInitializer(_root.Paths, NullAppLog.Instance);

        var result = Initializer(database, BackupKit.Service(_root, _clock)).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Database.Outcome);
        var migrationBackup = Assert.Single(Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
        var hash = BackupKit.Hash(migrationBackup);
        Assert.StartsWith("workhub_daily_20261013_v7_", result.DailyBackup!.FileName);

        for (var day = 1; day <= 10; day++)
        {
            _clock.Now = SettableClock.Tokyo(2026, 10, 13).AddDays(day);
            Assert.Equal(DailyBackupOutcome.Created, BackupKit.Service(_root, _clock).Run().Outcome);
        }

        Assert.Equal(hash, BackupKit.Hash(migrationBackup));
        Assert.Equal(3, BackupKit.PublishedBackups(_root).Length);
    }

    private sealed class CountingBackup : IDailyBackup
    {
        public int Runs { get; private set; }

        public DailyBackupResult Run()
        {
            Runs++;
            return new DailyBackupResult(DailyBackupOutcome.Created);
        }
    }

    private sealed class ThrowingBackup : IDailyBackup
    {
        public DailyBackupResult Run() => throw new InvalidOperationException("boom");
    }

    private sealed class ThrowingDatabase(DatabaseStartupFailure failure) : IDatabaseInitializer
    {
        public DatabaseInitializationResult Initialize() => throw new DatabaseStartupException(failure, "refused");
    }
}

/// <summary>Hardened pre-migration backups (Phase 9 §8.5, D14/D17; tests G54–G57).</summary>
public sealed class MigrationBackupTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public MigrationBackupTests()
    {
        Directory.CreateDirectory(_root.Paths.DataDirectory);
    }

    public void Dispose() => _root.Dispose();

    private DatabaseInitializer Initializer(RecordingLog? log = null, TimeProvider? time = null) =>
        new(_root.Paths, SchemaMigrations.All, time ?? TimeProvider.System, log ?? (IAppLog)NullAppLog.Instance);

    private void V6WithData()
    {
        BackupKit.BuildSchema(_root.Paths.DatabaseFile, 6);
        using var c = BackupKit.Open(_root.Paths.DatabaseFile);
        BackupKit.Exec(c, $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('MIG001', 'Migrate', '{Db.Now}', '{Db.Now}');");
        BackupKit.Exec(c, "INSERT INTO SystemRepositories (SystemId, RepositoryType) VALUES (1, 'SVN_SYSTEM');");
    }

    private int LiveVersion()
    {
        using var c = BackupKit.Open(_root.Paths.DatabaseFile, Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly);
        return (int)(long)BackupKit.Scalar(c, "PRAGMA user_version;")!;
    }

    [Fact]
    public void Success_PublishesAStandaloneFullyValidatedCopyAtFromVersion_UnderTheUsualName()
    {
        V6WithData();
        var before = BackupKit.Dump(_root.Paths.DatabaseFile);

        var result = Initializer().Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        var file = Assert.Single(Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
        Assert.Equal(file, result.MigrationBackupFile);
        Assert.Matches(@"^workhub_v6_before_v7_\d{8}-\d{9}\.db$", Path.GetFileName(file));
        Assert.True(DailyBackupPolicy.TryParseMigration(Path.GetFileName(file), out var from, out var to, out _) && from == 6 && to == 7);
        Assert.Equal(((byte)1, (byte)1), BackupKit.Header(file));
        Assert.Empty(BackupKit.Sidecars(file));
        Assert.Equal(before, BackupKit.Dump(file));
        var check = BackupKit.Validator().Check(file, BackupCheckLevel.Full, nameVersion: 6);
        Assert.Equal(BackupStatus.FullyValidated, check.Status);
        Assert.Equal(6, check.SchemaVersion);
    }

    [Fact]
    public void BackupFolderUnavailable_FailsClosed_DatabaseNotMigrated()
    {
        V6WithData();
        Directory.CreateDirectory(_root.Paths.BackupsDirectory);
        File.WriteAllText(_root.Paths.MigrationBackupsDirectory, "a file where the folder should be");

        var ex = Assert.Throws<DatabaseStartupException>(() => Initializer().Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationBackupFailed, ex.Failure);
        Assert.Equal(6, LiveVersion());
    }

    [Fact]
    public void CandidateFailingFullValidation_FailsClosed_NothingPublished_DatabaseNotMigrated()
    {
        // Index content damage passes the initializer's quick_check and the snapshot's quick_check, but not the
        // candidate's integrity_check: the safety backup is not valid, so the migration must not run.
        BackupKit.BuildSchema(_root.Paths.DatabaseFile, 6);
        DamageIndex(_root.Paths.DatabaseFile);

        var ex = Assert.Throws<DatabaseStartupException>(() => Initializer().Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationBackupFailed, ex.Failure);
        Assert.Contains("IntegrityFailed", ex.InnerException!.Message);
        Assert.Equal(6, LiveVersion());
        Assert.Empty(Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
    }

    /// <summary>
    /// D14 lock-wait timeout at the safety-backup stage, with the PRODUCTION wait (10 s): after the initializer's own
    /// checks passed, another connection holds the database exclusively; the snapshot cannot pin it, the backup fails,
    /// and the migration must not run. Deterministic: the lock is taken synchronously by the stage hook.
    /// </summary>
    [Fact]
    public void LockContentionAtTheSafetyBackupStage_TimesOutAfterTheProductionWait_FailsClosed_ThenRetrySucceeds()
    {
        V6WithData();
        var before = BackupKit.Dump(_root.Paths.DatabaseFile);
        Microsoft.Data.Sqlite.SqliteConnection? holder = null;
        var initializer = new DatabaseInitializer(_root.Paths, SchemaMigrations.All, TimeProvider.System, NullAppLog.Instance)
        {
            BeforeMigrationBackup = () =>
            {
                holder = BackupKit.Open(_root.Paths.DatabaseFile);
                BackupKit.Exec(holder, "BEGIN EXCLUSIVE;");
            },
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<DatabaseStartupException>(() => initializer.Initialize());
        stopwatch.Stop();
        Assert.NotNull(holder); // the contention really was arranged at this stage
        BackupKit.Exec(holder!, "ROLLBACK;");
        holder!.Dispose();

        Assert.Equal(DatabaseStartupFailure.MigrationBackupFailed, ex.Failure);
        Assert.Equal(DailyBackupFailure.SourceBusy, Assert.IsType<BackupStepException>(ex.InnerException).Failure);
        Assert.Equal(SqliteSnapshotter.DefaultLockWaitSeconds, 10);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(9.5), TimeSpan.FromSeconds(60));
        Assert.Equal(6, LiveVersion());
        Assert.Equal(before, BackupKit.Dump(_root.Paths.DatabaseFile));
        Assert.Empty(Directory.GetFiles(_root.Paths.MigrationBackupsDirectory)); // nothing published, no candidate left

        // The next start, without contention, backs up and migrates normally.
        var retry = Initializer().Initialize();
        Assert.Equal(DatabaseInitializationOutcome.Migrated, retry.Outcome);
        Assert.Single(Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
    }

    [Fact]
    public void NameCollision_FailsClosed_ExistingFileUntouched_CandidateRemoved()
    {
        V6WithData();
        var time = new SettableClock(new DateTimeOffset(2026, 10, 13, 1, 2, 3, 456, TimeSpan.Zero));
        Directory.CreateDirectory(_root.Paths.MigrationBackupsDirectory);
        var existing = Path.Combine(_root.Paths.MigrationBackupsDirectory, "workhub_v6_before_v7_20261013-010203456.db");
        File.WriteAllText(existing, "existing");

        var ex = Assert.Throws<DatabaseStartupException>(() => Initializer(time: time).Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationBackupFailed, ex.Failure);
        Assert.Equal("existing", File.ReadAllText(existing));
        Assert.Equal([existing], Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
        Assert.Equal(6, LiveVersion());
    }

    [Fact]
    public void ReferenceMismatchAndForeignKeyRowsAtFromVersion_AreLoggedOnly_MigrationProceeds()
    {
        V6WithData();
        using (var c = BackupKit.Open(_root.Paths.DatabaseFile))
        {
            BackupKit.Exec(c, "CREATE INDEX IX_Extra ON Systems (SystemName);");
            BackupKit.Exec(c, "PRAGMA foreign_keys = OFF;");
            BackupKit.Exec(c, "INSERT INTO SystemRepositories (SystemId, RepositoryType) VALUES (999, 'RELEASE');");
        }

        var log = new RecordingLog();

        var result = Initializer(log).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal(7, LiveVersion());
        Assert.Contains("logged only", log.All);
        Assert.Contains("extra index IX_Extra", log.All);
        Assert.Contains("foreign_key_check found rows", log.All);
        Assert.Single(Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
    }

    [Fact]
    public void WalModeOlderDatabase_GetsAStandaloneMigrationBackup_LiveJournalModeUnchanged()
    {
        V6WithData();
        using (var c = BackupKit.Open(_root.Paths.DatabaseFile))
            Assert.Equal("wal", BackupKit.Scalar(c, "PRAGMA journal_mode = WAL;"));

        Initializer().Initialize();
        new SqliteConnectionFactory(_root.Paths.DatabaseFile).ClearPools();

        var file = Assert.Single(Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
        Assert.Equal(((byte)1, (byte)1), BackupKit.Header(file));
        Assert.Empty(BackupKit.Sidecars(file));
        Assert.Equal(((byte)2, (byte)2), BackupKit.Header(_root.Paths.DatabaseFile));
        Assert.Equal(7, LiveVersion());
    }

    /// <summary>Changes the text inside IX_SystemRepositories_RepositoryType's root page so it no longer matches its row.</summary>
    private static void DamageIndex(string file)
    {
        long rootPage;
        using (var c = BackupKit.Open(file))
        {
            BackupKit.Exec(c, $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('IDX001', 'Index', '{Db.Now}', '{Db.Now}');");
            BackupKit.Exec(c, "INSERT INTO SystemRepositories (SystemId, RepositoryType) VALUES (1, 'GIT_ORACLE');");
            rootPage = (long)BackupKit.Scalar(c, "SELECT rootpage FROM sqlite_schema WHERE name = 'IX_SystemRepositories_RepositoryType';")!;
        }

        BackupKit.ClearPools(file);
        var bytes = File.ReadAllBytes(file);
        var pageSize = (bytes[16] << 8) | bytes[17];
        var start = (int)(rootPage - 1) * pageSize;
        var needle = "GIT_ORACLE"u8.ToArray();
        var at = bytes.AsSpan(start, pageSize).IndexOf(needle);
        Assert.True(at >= 0);
        bytes[start + at + needle.Length - 1] = (byte)'F';
        File.WriteAllBytes(file, bytes);
    }
}
