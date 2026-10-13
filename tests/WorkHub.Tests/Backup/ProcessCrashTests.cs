using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Backup;

/// <summary>
/// Real process crashes (Phase 9 §10.1; tests B20, D36, D37): a child process (WorkHub.CrashProbe) runs the real backup
/// code and is KILLED at a chosen point; then a fresh service in this process runs on the same data root.
/// </summary>
public sealed class ProcessCrashTests : IDisposable
{
    private static readonly string ProbePath = typeof(ProcessCrashTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CrashProbePath").Value!;

    private readonly TempWorkHubRoot _root = new();
    private readonly SettableClock _clock = new(SettableClock.Tokyo(2026, 10, 13));

    public ProcessCrashTests() => BackupKit.CreateDatabase(_root);

    public void Dispose() => _root.Dispose();

    [Fact]
    public void ProbeExists()
    {
        Assert.True(File.Exists(ProbePath), ProbePath);
    }

    [Fact]
    public void KilledWhileHoldingTheDailyLock_TheOsReleasesIt()
    {
        using (var child = Start("stage", _root.Paths.Root, Instant(), nameof(BackupStage.LockAcquired)))
        {
            Assert.Equal(DailyBackupOutcome.SkippedBusy, BackupKit.Service(_root, _clock).Run().Outcome); // really held
            child.Kill();
        }

        Assert.Equal(DailyBackupOutcome.Created, BackupKit.Service(_root, _clock).Run().Outcome);
    }

    [Fact]
    public void KilledWhilePinned_DeleteSource_WritersResumeAndTheSourceIsIntact()
    {
        var before = BackupKit.Dump(_root.Paths.DatabaseFile);
        using (var child = Start("stage", _root.Paths.Root, Instant(), nameof(BackupStage.SourcePinned)))
        {
            using var blocked = BackupKit.Open(_root.Paths.DatabaseFile, timeout: 1);
            Assert.Equal(5, Assert.Throws<SqliteException>(() => BackupKit.Exec(blocked, Insert("BLOCKED1"))).SqliteErrorCode);
            child.Kill();
        }

        Assert.Equal(before, BackupKit.Dump(_root.Paths.DatabaseFile));
        using (var writer = BackupKit.Open(_root.Paths.DatabaseFile, timeout: 5))
            BackupKit.Exec(writer, Insert("AFTER001"));
        Assert.Empty(BackupKit.Sidecars(_root.Paths.DatabaseFile));
        Assert.Equal(DailyBackupOutcome.Created, BackupKit.Service(_root, _clock).Run().Outcome);
    }

    [Fact]
    public void KilledAfterTheCopy_WalSource_OrphanWithWalAndShm_IsCleanedByTheNextRun()
    {
        using var holder = BackupKit.Open(_root.Paths.DatabaseFile);
        Assert.Equal("wal", BackupKit.Scalar(holder, "PRAGMA journal_mode = WAL;"));
        BackupKit.Scalar(holder, "SELECT count(*) FROM Systems;");

        using (var child = Start("stage", _root.Paths.Root, Instant(), nameof(BackupStage.Copied)))
            child.Kill();

        var orphan = Assert.Single(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial"));
        // Killed before normalization: a WAL-mode file. It has no -wal/-shm yet (nothing read it in WAL mode); the state
        // with sidecars is covered by KilledWhileAWalHeaderCandidateHasAnOpenWrite.
        Assert.Equal(((byte)2, (byte)2), BackupKit.Header(orphan));
        Assert.Empty(BackupKit.PublishedBackups(_root));
        Assert.Equal("ok", BackupKit.Scalar(holder, "PRAGMA quick_check;"));
        Assert.Equal("wal", BackupKit.Scalar(holder, "PRAGMA journal_mode;"));

        var result = BackupKit.Service(_root, _clock).Run();

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.Empty(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial*"));
        var backup = BackupKit.Daily(_root, result.FileName!);
        Assert.Equal(((byte)1, (byte)1), BackupKit.Header(backup));
        Assert.Empty(BackupKit.Sidecars(backup));
    }

    [Fact]
    public void KilledWhileAWalHeaderCandidateHasAnOpenWrite_OrphanAndSidecarsAreCleaned()
    {
        // The state a kill during normalization can leave: a WAL-mode candidate with frames in its -wal.
        Directory.CreateDirectory(_root.Paths.DailyBackupsDirectory);
        var orphan = BackupKit.Daily(_root, DailyBackupPolicy.CandidateName(BackupKit.NameFor(new DateOnly(2026, 10, 13)), Guid.NewGuid()));
        using (var source = BackupKit.Open(_root.Paths.DatabaseFile))
        using (var target = BackupKit.Open(orphan, SqliteOpenMode.ReadWriteCreate))
        {
            source.BackupDatabase(target);
            Assert.Equal("wal", BackupKit.Scalar(target, "PRAGMA journal_mode = WAL;"));
        }

        using (var child = Start("wal-write", orphan))
            child.Kill();

        Assert.NotEmpty(BackupKit.Sidecars(orphan));

        var result = BackupKit.Service(_root, _clock).Run();

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.Empty(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial*"));
    }

    [Fact]
    public void HotJournalFromAKilledWriter_IsRolledBackBySqlite_TheBackupHoldsTheCommittedState()
    {
        var committed = BackupKit.Dump(_root.Paths.DatabaseFile);
        using (var child = Start("hot-journal", _root.Paths.DatabaseFile))
            child.Kill();
        Assert.True(File.Exists(_root.Paths.DatabaseFile + "-journal"), "the killed writer should leave a hot journal");

        var result = BackupKit.Service(_root, _clock).Run();

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.Equal(committed, BackupKit.Dump(BackupKit.Daily(_root, result.FileName!)));
        Assert.Equal(committed, BackupKit.Dump(_root.Paths.DatabaseFile));
    }

    private string Instant() => _clock.Now.ToString("O", CultureInfo.InvariantCulture);

    private static string Insert(string code) =>
        $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('{code}', 'x', '{Db.Now}', '{Db.Now}');";

    /// <summary>Starts the probe and returns once it printed READY (it then waits to be killed).</summary>
    private static Child Start(params string[] args)
    {
        var info = new ProcessStartInfo(ProbePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        var ready = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (line == "READY")
                    return true;
            }

            return false;
        });
        if (!ready.Wait(TimeSpan.FromSeconds(60)) || !ready.Result)
        {
            var error = process.HasExited ? process.StandardError.ReadToEnd() : "(still running)";
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"The crash probe did not reach its point: {error}");
        }

        return new Child(process);
    }

    private sealed class Child(Process process) : IDisposable
    {
        public void Kill()
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        public void Dispose()
        {
            Kill();
            process.Dispose();
        }
    }
}
