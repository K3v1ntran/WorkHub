using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Backup;

/// <summary>
/// Daily policy, publication, retention, simulated crashes and cross-instance locking through the real service on a
/// temporary data root (Phase 9 §5–§7, §10.1; tests A2–A10, D32–D44, E45–E46).
/// </summary>
public sealed class DailyBackupServiceTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SettableClock _clock = new(SettableClock.Tokyo(2026, 10, 13));
    private static readonly DateOnly Today = new(2026, 10, 13);

    public DailyBackupServiceTests() => BackupKit.CreateDatabase(_root);

    public void Dispose() => _root.Dispose();

    private DailyBackupResult Run(IBackupFileOps? files = null, Action<BackupStage>? hook = null, RecordingLog? log = null) =>
        BackupKit.Service(_root, _clock, files, hook, log).Run();

    private List<string> PlaceOld(int count, DateOnly newest)
    {
        Directory.CreateDirectory(_root.Paths.DailyBackupsDirectory);
        return Enumerable.Range(0, count).Select(i => BackupKit.PlaceValidBackup(_root, BackupKit.NameFor(newest.AddDays(-i)))).ToList();
    }

    private Dictionary<string, string> Snapshot() =>
        Directory.GetFiles(_root.Paths.DailyBackupsDirectory).ToDictionary(f => Path.GetFileName(f)!, BackupKit.Hash);

    // ---------------- A: daily policy ----------------

    [Fact]
    public void FirstStartupOfTheDay_Creates_RepeatedStartups_ChangeNothing()
    {
        var first = Run();
        Assert.Equal(DailyBackupOutcome.Created, first.Outcome);
        Assert.True(first.BackedUpToday);
        Assert.StartsWith($"workhub_daily_20261013_v{SchemaMigrations.LatestVersion}_20261013T090000000", first.FileName);
        var after = Snapshot();

        _clock.Now = SettableClock.Tokyo(2026, 10, 13, 23, 59, 59, 999);
        var second = Run();
        var third = Run();

        Assert.Equal(DailyBackupOutcome.AlreadyDone, second.Outcome);
        Assert.Equal(DailyBackupOutcome.AlreadyDone, third.Outcome);
        Assert.Equal(first.FileName, second.FileName);
        Assert.Equal(after, Snapshot()); // byte-identical folder
    }

    [Fact]
    public void JustAfterTokyoMidnight_IsANewDay()
    {
        _clock.Now = SettableClock.Tokyo(2026, 10, 13, 23, 59, 59, 999);
        Assert.Equal(DailyBackupOutcome.Created, Run().Outcome);
        _clock.Now = SettableClock.Tokyo(2026, 10, 14, 0, 0, 0);
        var next = Run();

        Assert.Equal(DailyBackupOutcome.Created, next.Outcome);
        Assert.StartsWith("workhub_daily_20261014_", next.FileName);
    }

    [Fact]
    public void RunSpanningMidnight_KeepsTheDayItStartedOn()
    {
        _clock.Now = SettableClock.Tokyo(2026, 10, 13, 23, 59, 59, 900);
        var result = Run(hook: stage =>
        {
            if (stage == BackupStage.SourcePinned)
                _clock.Now = SettableClock.Tokyo(2026, 10, 14, 0, 0, 1);
        });

        Assert.StartsWith("workhub_daily_20261013_", result.FileName);
        Assert.Equal(DailyBackupOutcome.Created, Run().Outcome); // the 14th is not done yet
        Assert.Equal(2, BackupKit.PublishedBackups(_root).Length);
    }

    [Fact]
    public void ClockSetBack_CreatesTheEarlierDay_FutureDatedFilesAreNeverCountedOrPruned()
    {
        var future = PlaceOld(1, new DateOnly(2026, 10, 13)).Single();
        PlaceOld(4, new DateOnly(2026, 9, 30));
        _clock.Now = SettableClock.Tokyo(2026, 10, 1);
        var log = new RecordingLog();

        var result = Run(log: log);

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.StartsWith("workhub_daily_20261001_", result.FileName);
        Assert.True(File.Exists(future));
        Assert.Contains("dated after today", log.All);
        // Keep 3 (the new one + 2 newest past) plus the untouched future file.
        Assert.Equal(4, BackupKit.PublishedBackups(_root).Length);
        Assert.Equal(2, result.Retention!.Deleted);
    }

    [Fact]
    public void ClockForwardThenCorrected_DailyBackupsContinue_TheFutureFileIsKept()
    {
        _clock.Now = SettableClock.Tokyo(2027, 1, 1);
        var futureName = Run().FileName!;
        foreach (var day in Enumerable.Range(14, 6))
        {
            _clock.Now = SettableClock.Tokyo(2026, 10, day);
            Assert.Equal(DailyBackupOutcome.Created, Run().Outcome);
        }

        var names = BackupKit.PublishedBackups(_root);
        Assert.Contains(futureName, names);
        Assert.Equal(4, names.Length); // 3 newest past days + the future-dated file
    }

    [Fact]
    public void FailedAttempt_DoesNotConsumeTheDay_NextStartupRetries()
    {
        var files = new FaultyBackupFileOps { OnMove = (_, _) => throw FaultyBackupFileOps.DiskFull() };
        var failed = Run(files);

        Assert.Equal(DailyBackupFailure.DiskFull, failed.Failure);
        Assert.Empty(BackupKit.PublishedBackups(_root));
        Assert.Empty(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial*"));

        Assert.Equal(DailyBackupOutcome.Created, Run().Outcome);
    }

    [Fact]
    public void TodaysFileCorruptedLater_NextStartupPublishesANewOne_TheDamagedFileIsUntouched()
    {
        var first = Run().FileName!;
        var damaged = BackupKit.Daily(_root, first);
        BackupKit.CorruptPage(damaged, 3);
        var hash = BackupKit.Hash(damaged);
        _clock.Now = _clock.Now.AddHours(1);

        var second = Run();

        Assert.Equal(DailyBackupOutcome.Created, second.Outcome);
        Assert.NotEqual(first, second.FileName);
        Assert.Equal(hash, BackupKit.Hash(damaged));
    }

    [Fact]
    public void TodaysFilePassingQuickButNotFull_CountsAsDone_ButNeverAsRetentionEvidence()
    {
        Directory.CreateDirectory(_root.Paths.DailyBackupsDirectory);
        var todays = BackupValidatorTests.WithDamagedIndex(_root.Paths.DailyBackupsDirectory, BackupKit.NameFor(Today));
        PlaceOld(2, Today.AddDays(-1));

        Assert.Equal(DailyBackupOutcome.AlreadyDone, Run().Outcome);

        _clock.Now = SettableClock.Tokyo(2026, 10, 14);
        var next = Run();
        // Evidence: the new one + the 2 valid older ones; the damaged file is preserved, not deleted.
        Assert.Equal(DailyBackupOutcome.Created, next.Outcome);
        Assert.True(File.Exists(todays));
        Assert.Equal(1, next.Retention!.Preserved);
        Assert.Equal(0, next.Retention.Deleted);
    }

    // ---------------- D: publication, retention ----------------

    [Fact]
    public void Retention_KeepsTheNewestThree_DeletesOldestFirst_EachDeletedFileFullyValidated()
    {
        var olds = PlaceOld(5, Today.AddDays(-1));
        var files = new FaultyBackupFileOps();

        var result = Run(files);

        Assert.Equal(new RetentionSummary(3, 0, 0, false), result.Retention);
        Assert.Equal([result.FileName!, Path.GetFileName(olds[0]), Path.GetFileName(olds[1])], BackupKit.PublishedBackups(_root).OrderDescending(StringComparer.Ordinal));
        Assert.Equal([Path.GetFileName(olds[4]), Path.GetFileName(olds[3]), Path.GetFileName(olds[2])], files.Deleted.Where(n => n.EndsWith(".db", StringComparison.Ordinal)));
    }

    [Fact]
    public void Retention_WithInsufficientFullEvidence_DeletesNothing()
    {
        Directory.CreateDirectory(_root.Paths.DailyBackupsDirectory);
        // Two newest older backups pass the quick check but fail full validation; one older valid one.
        BackupValidatorTests.WithDamagedIndex(_root.Paths.DailyBackupsDirectory, BackupKit.NameFor(Today.AddDays(-1)));
        BackupValidatorTests.WithDamagedIndex(_root.Paths.DailyBackupsDirectory, BackupKit.NameFor(Today.AddDays(-2)));
        PlaceOld(1, Today.AddDays(-3));
        var before = Snapshot();
        var log = new RecordingLog();

        var result = Run(log: log);

        Assert.True(result.Retention!.InsufficientEvidence);
        Assert.Equal(0, result.Retention.Deleted);
        Assert.All(before, kv => Assert.Equal(kv.Value, BackupKit.Hash(BackupKit.Daily(_root, kv.Key))));
        Assert.Contains("nothing removed", log.All);
    }

    [Fact]
    public void Retention_DamagedNewerBackupIsSkippedAsEvidence_AndADamagedDeletionCandidateIsPreserved()
    {
        var olds = PlaceOld(5, Today.AddDays(-1));
        BackupKit.CorruptPage(olds[0], 3); // newest older: damaged
        BackupKit.CorruptPage(olds[3], 3); // an old one beyond the evidence: damaged

        var result = Run();

        // Evidence: new + olds[1] + olds[2]. Deletion candidates: olds[3] (damaged → preserved), olds[4] (deleted).
        Assert.Equal(new RetentionSummary(1, 0, 2, false), result.Retention);
        Assert.True(File.Exists(olds[0]));
        Assert.True(File.Exists(olds[3]));
        Assert.False(File.Exists(olds[4]));
    }

    public static TheoryData<string> FailureKinds => ["SourceBusy", "DiskFull", "CandidateInvalid", "FolderUnavailable", "PublishCollision", "NotEqualToSource"];

    [Theory]
    [MemberData(nameof(FailureKinds))]
    public void AFailedNewAttempt_NeverPrunes(string kind)
    {
        PlaceOld(5, Today.AddDays(-1));
        var before = Snapshot();
        SqliteConnection? exclusive = null;
        var files = new FaultyBackupFileOps();
        Action<BackupStage>? hook = null;
        var lockWait = 10;
        switch (kind)
        {
            case "SourceBusy":
                exclusive = BackupKit.Open(_root.Paths.DatabaseFile);
                BackupKit.Exec(exclusive, "BEGIN EXCLUSIVE;");
                lockWait = 1;
                break;
            case "DiskFull":
                files.OnMove = (_, _) => throw FaultyBackupFileOps.DiskFull();
                break;
            case "CandidateInvalid":
                hook = stage =>
                {
                    if (stage == BackupStage.Normalized)
                        BackupKit.CorruptPage(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial").Single(), 3);
                };
                break;
            case "FolderUnavailable":
                files.OnOpenExclusive = _ => throw new UnauthorizedAccessException();
                break;
            case "PublishCollision":
                files.OnMove = (_, destination) => File.WriteAllText(destination, "someone else's file");
                break;
            case "NotEqualToSource":
                hook = stage =>
                {
                    if (stage != BackupStage.Normalized)
                        return;
                    using var c = BackupKit.Open(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial").Single());
                    BackupKit.Exec(c, "DELETE FROM SystemRepositories WHERE (SystemId, RepositoryType) IN (SELECT SystemId, RepositoryType FROM SystemRepositories LIMIT 1);");
                };
                break;
        }

        var result = BackupKit.Service(_root, _clock, files, hook, lockWaitSeconds: lockWait).Run();
        if (exclusive is not null)
        {
            BackupKit.Exec(exclusive, "ROLLBACK;");
            exclusive.Dispose();
        }

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        foreach (var (name, hash) in before)
            Assert.Equal(hash, BackupKit.Hash(BackupKit.Daily(_root, name)));
        Assert.Empty(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial*"));
        if (kind == "NotEqualToSource")
            Assert.Contains("NotEqualToSource", result.Detail);
    }

    [Fact]
    public void PublishCollision_LeavesTheExistingFileUnchanged_AndRemovesTheCandidate()
    {
        string? collided = null;
        var files = new FaultyBackupFileOps
        {
            OnMove = (_, destination) =>
            {
                collided = destination;
                File.WriteAllText(destination, "someone else's file");
            },
        };

        var result = Run(files);

        Assert.Equal(DailyBackupFailure.PublishFailed, result.Failure);
        Assert.Equal("someone else's file", File.ReadAllText(collided!));
        Assert.Empty(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial*"));
    }

    [Fact]
    public void DeleteFailure_IsLoggedAndSkipped_ResultStaysCreated()
    {
        var olds = PlaceOld(5, Today.AddDays(-1));
        using var held = BackupKit.Open(olds[4], SqliteOpenMode.ReadOnly); // real open handle: delete fails
        BackupKit.Scalar(held, "SELECT count(*) FROM sqlite_schema;");
        var log = new RecordingLog();

        var result = Run(log: log);

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.Equal(new RetentionSummary(2, 1, 0, false), result.Retention);
        Assert.True(File.Exists(olds[4]));
        Assert.Contains("could not be removed", log.All);
    }

    [Fact]
    public void Untouchables_SurviveTenDaysOfBackupsAndRetention()
    {
        Directory.CreateDirectory(_root.Paths.DailyBackupsDirectory);
        Directory.CreateDirectory(_root.Paths.MigrationBackupsDirectory);
        var migration = Path.Combine(_root.Paths.MigrationBackupsDirectory, "workhub_v6_before_v7_20261001-000000000.db");
        BackupKit.SnapshotTo(_root.Paths.DatabaseFile, migration);
        var rootFile = Path.Combine(_root.Paths.BackupsDirectory, "readme.txt");
        File.WriteAllText(rootFile, "user file");
        var notes = BackupKit.Daily(_root, "notes.txt");
        File.WriteAllText(notes, "notes");
        var foo = BackupKit.Daily(_root, "workhub_daily_foo.db");
        BackupKit.SnapshotTo(_root.Paths.DatabaseFile, foo);
        var sub = Path.Combine(_root.Paths.DailyBackupsDirectory, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, BackupKit.NameFor(Today.AddDays(-30))), "x");
        var reparse = BackupKit.PlaceValidBackup(_root, BackupKit.NameFor(Today.AddDays(-20)));
        var future = BackupKit.PlaceValidBackup(_root, BackupKit.NameFor(new DateOnly(2027, 1, 1)));
        var notStandalone = BackupKit.PlaceValidBackup(_root, BackupKit.NameFor(Today.AddDays(-19)));
        File.WriteAllBytes(notStandalone + "-wal", [0]);
        var quickOnly = BackupValidatorTests.WithDamagedIndex(_root.Paths.DailyBackupsDirectory, BackupKit.NameFor(Today.AddDays(-18)));
        var untouchables = new[] { migration, rootFile, notes, foo, reparse, future, notStandalone, notStandalone + "-wal", quickOnly };
        var hashes = untouchables.ToDictionary(f => f, BackupKit.Hash);
        var files = new FaultyBackupFileOps();
        files.FakeReparsePoints.Add(reparse);

        for (var day = 0; day < 10; day++)
        {
            _clock.Now = SettableClock.Tokyo(2026, 10, 13).AddDays(day);
            Assert.Equal(DailyBackupOutcome.Created, Run(files).Outcome);
        }

        Assert.All(untouchables, f => Assert.Equal(hashes[f], BackupKit.Hash(f)));
        Assert.True(Directory.Exists(sub));
        var published = BackupKit.PublishedBackups(_root);
        Assert.Equal(3 + 4, published.Length); // 3 newest + reparse, future, not-standalone, quick-only (sub is a folder)
        Assert.DoesNotContain(files.Touched, p => p.StartsWith(_root.Paths.MigrationBackupsDirectory, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OwnOrphanCandidatesAndTheirSidecars_AreCleanedUnderTheLock_ForeignPartialsAreNot()
    {
        Directory.CreateDirectory(_root.Paths.DailyBackupsDirectory);
        var orphan = BackupKit.Daily(_root, DailyBackupPolicy.CandidateName(BackupKit.NameFor(Today.AddDays(-1)), Guid.NewGuid()));
        foreach (var file in new[] { orphan, orphan + "-wal", orphan + "-shm", orphan + "-journal" })
            File.WriteAllBytes(file, [0]);
        var foreign = BackupKit.Daily(_root, "something.partial");
        File.WriteAllText(foreign, "x");
        var log = new RecordingLog();

        Assert.Equal(DailyBackupOutcome.Created, Run(log: log).Outcome);

        Assert.Empty(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "workhub_daily_*.partial*"));
        Assert.True(File.Exists(foreign));
        Assert.Contains("removed 4 leftover", log.All);
    }

    // ---------------- §10.1: simulated crash at every stage, then a fresh service ----------------

    public static TheoryData<string> Stages =>
    [
        nameof(BackupStage.LockAcquired), nameof(BackupStage.OrphansCleaned), nameof(BackupStage.SourcePinned), nameof(BackupStage.Copied), nameof(BackupStage.Normalized),
        nameof(BackupStage.CandidateValidated), nameof(BackupStage.Published), nameof(BackupStage.Verified), nameof(BackupStage.Pruned),
    ];

    [Theory]
    [MemberData(nameof(Stages))]
    public void CrashAtAnyStage_ThenAFreshService_BehavesAsSpecified(string stageName)
    {
        var stage = Enum.Parse<BackupStage>(stageName);
        var olds = PlaceOld(5, Today.AddDays(-1));
        var crashed = Assert.Throws<SimulatedCrashException>(() => Run(hook: s =>
        {
            if (s == stage)
                throw new SimulatedCrashException(s);
        }));
        Assert.Equal(stage, crashed.Stage);

        // Never fewer than the newest three fully validated backups, whatever was interrupted.
        var validator = BackupKit.Validator();
        var valid = BackupKit.PublishedBackups(_root).Count(n => validator.Check(BackupKit.Daily(_root, n), BackupCheckLevel.Full).IsFullyValidated);
        Assert.True(valid >= 3, $"{valid} fully validated after a crash at {stage}");

        var fresh = Run();
        var published = stage >= BackupStage.Published;
        Assert.Equal(published ? DailyBackupOutcome.AlreadyDone : DailyBackupOutcome.Created, fresh.Outcome);
        Assert.Empty(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial*"));
        Assert.Single(BackupKit.PublishedBackups(_root), n => n.StartsWith("workhub_daily_20261013_", StringComparison.Ordinal));
        Assert.True(File.Exists(olds[0]));
    }

    // ---------------- E: cross-instance ----------------

    [Fact]
    public async Task TwoInstancesOnOneRoot_PublishExactlyOneBackup()
    {
        using var firstPinned = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => BackupKit.Service(_root, _clock, hook: s =>
        {
            if (s != BackupStage.SourcePinned)
                return;
            firstPinned.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        }).Run());
        Assert.True(firstPinned.Wait(TimeSpan.FromSeconds(30)));

        var second = BackupKit.Service(_root, _clock).Run(); // a separate service = another instance
        release.Set();
        var firstResult = await first;
        var third = BackupKit.Service(_root, _clock).Run();

        Assert.Equal(DailyBackupOutcome.SkippedBusy, second.Outcome);
        Assert.Equal(DailyBackupOutcome.Created, firstResult.Outcome);
        Assert.Equal(DailyBackupOutcome.AlreadyDone, third.Outcome);
        Assert.Single(BackupKit.PublishedBackups(_root));
    }

    [Fact]
    public void LockHeldExternally_IsSkippedBusy_AndNothingIsWritten()
    {
        Directory.CreateDirectory(_root.Paths.DailyBackupsDirectory);
        using (new FileStream(_root.Paths.DailyBackupLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var result = Run();
            Assert.Equal(DailyBackupOutcome.SkippedBusy, result.Outcome);
            Assert.False(result.BackedUpToday);
        }

        Assert.Equal(["daily-backup.lock"], BackupKit.DailyFiles(_root));
    }

    [Fact]
    public void EveryPathTouched_IsInsideTheDataRoot()
    {
        PlaceOld(4, Today.AddDays(-1));
        var files = new FaultyBackupFileOps();

        Run(files);

        Assert.NotEmpty(files.Touched);
        Assert.All(files.Touched, p => Assert.StartsWith(_root.Paths.Root + Path.DirectorySeparatorChar, p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StageDurationsAreLogged_ASlowRunAddsAWarning()
    {
        var log = new RecordingLog();
        Run(hook: s =>
        {
            if (s == BackupStage.Normalized)
                _clock.Now = _clock.Now.AddSeconds(11);
        }, log: log);

        Assert.Contains("Timings: discovery", log.All);
        Assert.Contains("Daily backup took 11", log.All);
        Assert.DoesNotContain(_root.Paths.Root, log.All, StringComparison.OrdinalIgnoreCase); // names only, no paths
    }
}
