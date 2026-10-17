using System.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Restore;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Restore;

/// <summary>
/// Filesystem support boundary (Phase 10 §11; tests V1–V7) and the remaining Put-back cases (U2 inconsistent files,
/// U3 move failure and retry, U5 unreadable previous data).
/// </summary>
public sealed class RestoreBoundaryTests : RestoreTestBase
{
    private string Db => Root.Paths.DatabaseFile;

    // ---- V: filesystem -------------------------------------------------------------------------------------------------

    [Fact]
    public void V1_TheLocalNtfsDataFolder_IsSupported_ByHandle()
    {
        BackupKit.CreateDatabase(Root);
        Directory.CreateDirectory(Root.Paths.RestoreDirectory);

        var result = new VolumeCheck(Root.Paths, new VolumeProbe()).Check(null);

        Assert.True(result.IsSupported, result.Message);
        Assert.Equal("NTFS", result.Facts!.Name);
    }

    [Theory]
    [InlineData("FAT32")]
    [InlineData("exFAT")]
    [InlineData("ReFS")]
    [InlineData("")]
    public void V2_OtherFileSystems_AreRefused(string fileSystem)
    {
        BackupKit.CreateDatabase(Root);
        var result = new VolumeCheck(Root.Paths, new FakeVolumes(_ => new VolumeFacts(fileSystem, "1", "1", 3, false))).Check(null);

        Assert.False(result.IsSupported);
        Assert.StartsWith("Restore needs the WorkHub data folder on a local NTFS drive. This folder is on ", result.Message);
        Assert.DoesNotContain(Root.Paths.Root, result.Message); // no absolute paths in the UI text
    }

    [Fact]
    public void V3_DifferentVolumeSerials_AreRefused()
    {
        BackupKit.CreateDatabase(Root);
        var differentPerPath = new FakeVolumes(path => path.EndsWith("workhub.db", StringComparison.Ordinal)
            ? new VolumeFacts("NTFS", "2", "2", 3, false)
            : new VolumeFacts("NTFS", "1", "1", 3, false));
        var differentInHandle = new FakeVolumes(_ => new VolumeFacts("NTFS", "1", "9", 3, false));

        Assert.Contains("different volumes", new VolumeCheck(Root.Paths, differentPerPath).Check(null).Message);
        Assert.Contains("different volumes", new VolumeCheck(Root.Paths, differentInHandle).Check(null).Message);
    }

    [Fact]
    public async Task V4_AJunctionAtDataRestore_IsRefused_AtRequestTime()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var target = Path.Combine(Root.Paths.Root, "elsewhere");
        Directory.CreateDirectory(target);
        Assert.False(Directory.Exists(Root.Paths.RestoreDirectory));
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Root.Paths.RestoreDirectory}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();
        Assert.True((File.GetAttributes(Root.Paths.RestoreDirectory) & FileAttributes.ReparsePoint) != 0);

        var start = await RestoreKit.Requester(Root).BeginAsync(entry);

        Assert.Null(start.Request);
        Assert.Contains(@"Data\Restore is a link to another folder", start.Refusal);
        Assert.Empty(Directory.GetDirectories(target)); // nothing reserved through the link
        Directory.Delete(Root.Paths.RestoreDirectory); // removes the junction only
    }

    [Theory]
    [InlineData(2u, "removable drive")]
    [InlineData(4u, "network drive")]
    [InlineData(0u, "unknown drive type")]
    public void V5_RemovableRemoteOrUnknownDrives_AreRefused(uint driveType, string described)
    {
        BackupKit.CreateDatabase(Root);
        var result = new VolumeCheck(Root.Paths, new FakeVolumes(_ => new VolumeFacts("NTFS", "1", "1", driveType, false))).Check(null);

        Assert.False(result.IsSupported);
        Assert.Contains(described, result.Message);
    }

    [Fact]
    public void V6_ASubstAliasOfTheSameNtfsVolume_IsSupported()
    {
        // By handle a subst drive resolves to the real root: NTFS, fixed, the same serial (probe V3) — modelled here.
        BackupKit.CreateDatabase(Root);
        Assert.True(new VolumeCheck(Root.Paths, FakeVolumes.Ntfs()).Check(null).IsSupported);
    }

    [Fact]
    public async Task V7_TheFileSystemChangesBetweenRequestAndExecution_AbortsBeforeAnyMove()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        var id = RestoreKit.FileId(Db);

        var (result, _, error) = RestoreKit.Start(Root, Coordinator(token: op, volumes: new FakeVolumes(_ => new VolumeFacts("exFAT", "1", "1", 2, false))), new ScriptedRestoreUi());

        Assert.Null(error);
        Assert.StartsWith(RestoreExecutor.UnsupportedMessagePrefix, result!.RestoreNotice);
        Assert.Equal(RestoreState.Aborted, RestoreKit.Journal(Root, op).State);
        Assert.Equal(id, RestoreKit.FileId(Db));
    }

    // ---- U: Put back extras -----------------------------------------------------------------------------------------

    private async Task<(string Op, string CurrentDump)> PublishedInitFailedThenPutBackRequestedAsync()
    {
        BackupKit.CreateDatabase(Root);
        RestoreKit.InsertSystem(Db, "CURRENT1");
        var currentDump = BackupKit.Dump(Db);
        var entry = RestoreKit.PlaceOlderDaily(Root, version: 6);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        File.WriteAllText(Root.Paths.MigrationBackupsDirectory, "blocks the folder");
        var owner = Coordinator(token: op);
        Assert.IsType<DatabaseStartupException>(RestoreKit.Start(Root, owner, new ScriptedRestoreUi()).Error);
        File.Delete(Root.Paths.MigrationBackupsDirectory);
        Assert.True(owner.RequestPutBack().Succeeded);
        owner.Dispose();
        RestoreKit.ClearPools(Root);
        return (op, currentDump);
    }

    [Fact]
    public async Task U3_AMoveFailsBecauseAnotherProgramHoldsTheRestoredFile_StaysInState_RetryRollsForward()
    {
        var (op, currentDump) = await PublishedInitFailedThenPutBackRequestedAsync();
        var coordinator = Coordinator(token: op);
        RestoreStartupException? stop;
        using (RestoreKit.StartProbe("hold", Db)) // e.g. an older WorkHub holding the restored database
            stop = RestoreKit.Start(Root, coordinator, new ScriptedRestoreUi()).Stop;

        Assert.Equal(RestoreEntryKind.OwnerStopped, stop!.Kind);
        Assert.Equal(RestoreExecutor.PutBackInUseMessage, stop.Message);
        Assert.Equal(RestoreOwnerSituation.PutBackStopped, coordinator.Status!.Situation);
        Assert.Equal(RestoreState.Undoing, RestoreKit.Journal(Root, op).State);

        coordinator.Dispose(); // Retry relaunches; this window exits
        var (result, _, error) = RestoreKit.Start(Root, Coordinator(), new ScriptedRestoreUi());

        Assert.Null(error);
        Assert.Equal(RestoreState.Undone, RestoreKit.Journal(Root, op).State);
        Assert.NotNull(result);
        RestoreKit.ClearPools(Root);
        Assert.Equal(currentDump, BackupKit.Dump(Db));
    }

    [Theory]
    [InlineData("stray-wal-in-data")]
    [InlineData("previous-main-missing")]
    [InlineData("restored-folder-has-a-file")]
    public async Task U2_InconsistentFilesAtPutBack_FailClosed_NoInitializer_NoSeeding(string damage)
    {
        var (op, _) = await PublishedInitFailedThenPutBackRequestedAsync();
        switch (damage)
        {
            case "stray-wal-in-data":
                File.WriteAllBytes(Db + "-wal-unknown", [1]);
                break;
            case "previous-main-missing":
                File.Move(Path.Combine(RestoreKit.Previous(Root, op), "workhub.db"), Path.Combine(Root.Paths.Root, "moved-away.db"));
                break;
            case "restored-folder-has-a-file":
                Directory.CreateDirectory(RestoreKit.Restored(Root, op));
                File.WriteAllBytes(Path.Combine(RestoreKit.Restored(Root, op), "x.db"), [1]);
                break;
        }

        var data = RestoreKit.DataSet(Root);
        var (result, stop, error) = RestoreKit.Start(Root, Coordinator(token: op), new ScriptedRestoreUi());

        Assert.Null(error);
        Assert.Null(result);
        Assert.Equal(RestoreEntryKind.RecoveryRequired, stop!.Kind);
        Assert.Equal(RestoreState.UndoRequested, RestoreKit.Journal(Root, op).State);
        Assert.Equal(data, RestoreKit.DataSet(Root));
    }

    [Fact]
    public async Task U5_PreviousDataUnreadable_AfterUndoneTheFailedShellMayOfferRestoreAgain()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceOlderDaily(Root, version: 6);
        BackupKit.CorruptPage(Db);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        File.WriteAllText(Root.Paths.MigrationBackupsDirectory, "blocks the folder");
        var owner = Coordinator(token: op);
        Assert.IsType<DatabaseStartupException>(RestoreKit.Start(Root, owner, new ScriptedRestoreUi()).Error);
        File.Delete(Root.Paths.MigrationBackupsDirectory);
        Assert.True(owner.RequestPutBack().Succeeded);
        owner.Dispose();
        RestoreKit.ClearPools(Root);

        var coordinator = Coordinator(token: op);
        var (_, _, error) = RestoreKit.Start(Root, coordinator, new ScriptedRestoreUi());

        var refused = Assert.IsType<DatabaseStartupException>(error); // the previous data is unreadable again
        Assert.Equal(DatabaseStartupFailure.IntegrityCheckFailed, refused.Failure);
        Assert.Equal(RestoreState.Undone, RestoreKit.Journal(Root, op).State);
        Assert.Null(coordinator.Status);
        Assert.False(coordinator.HasUnfinishedOperation()); // "Restore from a backup…" is offered again
    }
}
