using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.FileSystem;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Restore;
using WorkHub.Infrastructure.Settings;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Restore;

/// <summary>
/// Codex review finding: a restart that RESUMES an interrupted Restore (MovingAside, MovedAside with the candidate staged
/// or already renamed, Published) must check the CURRENT filesystem boundary (§11) under G + exclusive P + R before it
/// moves, publishes or initializes. Unsupported or unverifiable facts fail closed and keep the recovery barrier.
/// </summary>
public sealed class RestoreResumeVolumeTests : RestoreTestBase
{
    /// <summary>Counts every volume inspection; describes the facts the case needs (or the real ones).</summary>
    private sealed class CountingVolumes(Func<string, VolumeFacts> describe) : IVolumeProbe
    {
        private int _calls;

        public int Calls => _calls;

        public VolumeFacts Describe(string path)
        {
            Interlocked.Increment(ref _calls);
            return describe(path);
        }
    }

    private sealed class CountingInitializer(IDatabaseInitializer inner) : IDatabaseInitializer
    {
        public int Calls { get; private set; }

        public DatabaseInitializationResult Initialize()
        {
            Calls++;
            return inner.Initialize();
        }
    }

    private sealed class SimulatedKill : Exception;

    public static TheoryData<string> InterruptedStates() => ["MovingAside", "MovedAsideStaged", "MovedAsideRenamed", "Published"];

    /// <summary>Confirms a restore with supported facts, then interrupts the executor at the boundary for the case.</summary>
    private async Task<string> InterruptedAsync(string state)
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        RestoreKit.InsertSystem(Root.Paths.DatabaseFile, "CURRENT1");
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        var stop = state switch
        {
            "MovingAside" => RestoreStage.MovingAsidePublished,
            "MovedAsideStaged" => RestoreStage.MovedAsidePublished,
            "MovedAsideRenamed" => RestoreStage.CandidateMoved,
            _ => RestoreStage.PublishedPublished,
        };
        var interrupted = Coordinator(token: op, hook: s =>
        {
            if (s == stop)
                throw new SimulatedKill();
        });
        Assert.IsType<SimulatedKill>(RestoreKit.Start(Root, interrupted, new ScriptedRestoreUi()).Error);
        interrupted.Dispose();
        RestoreKit.ClearPools(Root);

        var journal = RestoreKit.Journal(Root, op);
        Assert.Equal(state switch { "MovingAside" => RestoreState.MovingAside, "Published" => RestoreState.Published, _ => RestoreState.MovedAside }, journal.State);
        Assert.Equal(state is "MovingAside" or "MovedAsideStaged", File.Exists(RestoreKit.Candidate(Root, op)));
        return op;
    }

    private (InitializationResult? Result, RestoreStartupException? Stop, Exception? Error, int InitializerCalls) Restart(IVolumeProbe volumes)
    {
        var coordinator = Coordinator(volumes: volumes);
        var initializer = new CountingInitializer(new DatabaseInitializer(Root.Paths, NullAppLog.Instance, InitialSystemsSeed.Apply));
        var app = new AppInitializer(Root.Paths, new LocalStorage(Root.Paths), new JsonSettingsStore(Root.Paths.SettingsFile), initializer, NullAppLog.Instance, null, coordinator);
        try
        {
            return (app.Initialize(new ScriptedRestoreUi()), null, null, initializer.Calls);
        }
        catch (RestoreStartupException ex)
        {
            return (null, ex, null, initializer.Calls);
        }
        catch (Exception ex)
        {
            return (null, null, ex, initializer.Calls);
        }
    }

    /// <summary>Every file under Data\ (names, sizes and IDs) — the recovery evidence and the database set.</summary>
    private string Snapshot() => string.Join("\n", Directory.GetFiles(Root.Paths.DataDirectory, "*", SearchOption.AllDirectories)
        .Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal)
        .Select(f => $"{Path.GetRelativePath(Root.Paths.DataDirectory, f)}|{new FileInfo(f).Length}|{RestoreKit.FileId(f)}"));

    [Theory]
    [MemberData(nameof(InterruptedStates))]
    public async Task SupportedFacts_TheResumedRestoreIsValidated_AndCompletes(string state)
    {
        var op = await InterruptedAsync(state);
        var volumes = new CountingVolumes(new VolumeProbe().Describe);

        var (result, stop, error, initializerCalls) = Restart(volumes);

        Assert.Null(error);
        Assert.Null(stop);
        Assert.NotEqual(DatabaseInitializationOutcome.Created, result!.Database.Outcome);
        Assert.Equal(1, initializerCalls);
        Assert.True(volumes.Calls > 0, "the filesystem boundary was not checked on the resumed path");
        Assert.Equal(RestoreState.Completed, RestoreKit.Journal(Root, op).State);
    }

    public static TheoryData<string, string> UnsupportedCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var state in new[] { "MovingAside", "MovedAsideStaged", "MovedAsideRenamed", "Published" })
        {
            foreach (var facts in new[] { "fat32", "removable", "volume-mismatch", "reparse", "inspection-fails" })
                data.Add(state, facts);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(UnsupportedCases))]
    public async Task UnsupportedOrUnverifiableFacts_FailClosed_KeepingTheRecoveryBarrierAndEvidence(string state, string facts)
    {
        var op = await InterruptedAsync(state);
        var journalBefore = RestoreJournalText.Of(Root, op);
        var filesBefore = Snapshot();
        var volumes = new CountingVolumes(path => facts switch
        {
            "fat32" => new VolumeFacts("FAT32", "1", "1", 3, false),
            "removable" => new VolumeFacts("NTFS", "1", "1", 2, false),
            "volume-mismatch" => Path.GetFileName(path) == op ? new VolumeFacts("NTFS", "2", "2", 3, false) : new VolumeFacts("NTFS", "1", "1", 3, false),
            "reparse" => new VolumeFacts("NTFS", "1", "1", 3, Path.GetFileName(path) == op), // the operation folder exists in every interrupted state
            _ => throw new IOException("the volume could not be inspected"),
        });

        var (result, stop, error, initializerCalls) = Restart(volumes);

        Assert.Null(error);
        Assert.Null(result);
        Assert.Equal(RestoreEntryKind.RecoveryRequired, stop!.Kind);
        Assert.True(volumes.Calls > 0, "the filesystem boundary was not checked on the resumed path");
        Assert.Equal(0, initializerCalls);                              // never initialized (or seeded)
        Assert.Equal(journalBefore, RestoreJournalText.Of(Root, op));   // state and revision kept: no Aborted/Cancelled/Completed
        Assert.Equal(filesBefore, Snapshot());                           // nothing moved, published, deleted or cleaned
        using (var locks = new InstanceLocks(Root.Paths))
        {
            Assert.True(locks.TryAcquireGate());                         // nothing kept held
            Assert.True(locks.TryRestoreLock());
        }

        // A later start on a supported volume still finishes the same operation from the preserved journal.
        var (retried, _, retryError, _) = Restart(new VolumeProbe());
        Assert.Null(retryError);
        Assert.NotNull(retried);
        Assert.Equal(RestoreState.Completed, RestoreKit.Journal(Root, op).State);
    }
}
