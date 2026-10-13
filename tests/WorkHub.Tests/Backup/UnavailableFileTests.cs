using WorkHub.Core.Backup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Backup;

/// <summary>
/// Per-file file-system failures while checking a backup (Codex review finding): a file that is locked, vanishes or is
/// denied is a non-passing result for THAT file — never an exception that fails the whole run, never "validated", never
/// deleted. Programming errors and simulated crashes still propagate.
/// </summary>
public sealed class UnavailableFileTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SettableClock _clock = new(SettableClock.Tokyo(2026, 10, 13));
    private static readonly DateOnly Today = new(2026, 10, 13);

    public UnavailableFileTests() => BackupKit.CreateDatabase(_root);

    public void Dispose() => _root.Dispose();

    private List<string> PlaceOld(int count) =>
        Enumerable.Range(1, count).Select(i => BackupKit.PlaceValidBackup(_root, BackupKit.NameFor(Today.AddDays(-i)))).ToList();

    private static FileStream Hold(string file) => new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    [Theory]
    [InlineData(BackupCheckLevel.Quick)]
    [InlineData(BackupCheckLevel.Full)]
    public void FileHeldWithFileShareNone_IsANonPassingResult_NotAnException(BackupCheckLevel level)
    {
        var file = PlaceOld(1)[0];
        using var held = Hold(file);

        var result = BackupKit.Validator().Check(file, level);

        Assert.Equal(BackupStatus.Unreadable, result.Status);
        Assert.False(result.PassedQuick);
        Assert.Contains("could not be read", result.Detail);
    }

    public static TheoryData<string, string> PerFileErrors => new()
    {
        { "GetAttributes", "missing" }, { "GetAttributes", "denied" },
        { "GetLength", "missing" }, { "GetLength", "denied" },
        { "ReadHeader", "missing" }, { "ReadHeader", "denied" }, { "ReadHeader", "locked" },
        { "FileExists", "denied" },
    };

    [Theory]
    [MemberData(nameof(PerFileErrors))]
    public void ExpectedFileSystemErrors_AtEveryValidatorStep_AreClassifiedPerFile(string operation, string error)
    {
        var file = PlaceOld(1)[0];
        var files = new FaultyBackupFileOps();
        Exception Make() => error switch
        {
            "missing" => new FileNotFoundException("gone", file),
            "denied" => new UnauthorizedAccessException("denied"),
            _ => FaultyBackupFileOps.SharingViolation(),
        };
        Action<string> fault = p =>
        {
            if (string.Equals(p, file, StringComparison.OrdinalIgnoreCase) || p.StartsWith(file + "-", StringComparison.OrdinalIgnoreCase))
                throw Make();
        };
        switch (operation)
        {
            case "GetAttributes": files.OnGetAttributes = fault; break;
            case "GetLength": files.OnGetLength = fault; break;
            case "ReadHeader": files.OnReadHeader = fault; break;
            case "FileExists": files.OnFileExists = fault; break;
        }

        var result = new BackupValidator(SchemaReference.Frozen, files).Check(file, BackupCheckLevel.Full);

        Assert.Equal(BackupStatus.Unreadable, result.Status);
        Assert.False(result.IsFullyValidated);
    }

    [Fact]
    public void AFailureOfTheSidecarCheckAfterValidation_IsNotReportedAsValidated()
    {
        var file = PlaceOld(1)[0];
        var files = new FaultyBackupFileOps();
        var calls = 0;
        files.OnFileExists = p =>
        {
            // Gate checks pass; the post-validation sidecar re-check (second round of -journal lookups) fails.
            if (p.EndsWith("-journal", StringComparison.Ordinal) && ++calls == 2)
                throw new UnauthorizedAccessException("denied");
        };

        var result = new BackupValidator(SchemaReference.Frozen, files).Check(file, BackupCheckLevel.Full);

        Assert.Equal(BackupStatus.Unreadable, result.Status);
        Assert.False(result.IsFullyValidated);
    }

    [Fact]
    public void ProgrammingErrorsAndSimulatedCrashes_StillPropagate()
    {
        var file = PlaceOld(1)[0];
        var files = new FaultyBackupFileOps { OnReadHeader = _ => throw new InvalidOperationException("bug") };
        Assert.Throws<InvalidOperationException>(() => new BackupValidator(SchemaReference.Frozen, files).Check(file, BackupCheckLevel.Full));

        files.OnReadHeader = _ => throw new SimulatedCrashException(BackupStage.Pruned);
        Assert.Throws<SimulatedCrashException>(() => new BackupValidator(SchemaReference.Frozen, files).Check(file, BackupCheckLevel.Full));
    }

    [Fact]
    public void LockedNewestOlderBackup_IsPreservedAndNotEvidence_RunStaysCreated()
    {
        var olds = PlaceOld(4); // olds[0] newest
        DailyBackupResult result;
        using (Hold(olds[0]))
            result = BackupKit.Service(_root, _clock).Run();

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.True(File.Exists(BackupKit.Daily(_root, result.FileName!)));
        // Evidence: new + olds[1] + olds[2] (three newer fully validated). olds[3] is deleted; the locked olds[0] is kept.
        Assert.Equal(new RetentionSummary(1, 0, 1, false), result.Retention);
        Assert.True(File.Exists(olds[0]));
        Assert.True(File.Exists(olds[1]));
        Assert.True(File.Exists(olds[2]));
        Assert.False(File.Exists(olds[3]));
    }

    [Fact]
    public void LockedDeletionCandidate_IsPreserved_OthersStillPruned()
    {
        var olds = PlaceOld(4);
        DailyBackupResult result;
        using (Hold(olds[3]))
            result = BackupKit.Service(_root, _clock).Run();

        // Evidence: new + olds[0] + olds[1]. Candidates: olds[2] (deleted), olds[3] (locked → not validated → kept).
        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.Equal(new RetentionSummary(1, 0, 1, false), result.Retention);
        Assert.False(File.Exists(olds[2]));
        Assert.True(File.Exists(olds[3]));
    }

    [Fact]
    public void OnlyLockedOlderBackups_InsufficientEvidence_NothingDeleted_RunStaysCreated()
    {
        var olds = PlaceOld(3);
        DailyBackupResult result;
        using (Hold(olds[0]))
        using (Hold(olds[1]))
            result = BackupKit.Service(_root, _clock).Run();

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.True(result.Retention!.InsufficientEvidence);
        Assert.All(olds, f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public void LockedFileOfToday_DoesNotCountAsDone_AndDoesNotFailTheRun()
    {
        var todays = BackupKit.PlaceValidBackup(_root, BackupKit.NameFor(Today));
        _clock.Now = SettableClock.Tokyo(2026, 10, 13, 10); // a later stamp than the 09:00 file
        DailyBackupResult result;
        using (Hold(todays))
            result = BackupKit.Service(_root, _clock).Run();

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        Assert.NotEqual(Path.GetFileName(todays), result.FileName);
        Assert.True(File.Exists(todays));
    }

    [Fact]
    public void AnOlderBackupDisappearingDuringRetention_IsSkipped_RunStaysCreated()
    {
        var olds = PlaceOld(4);
        var files = new FaultyBackupFileOps
        {
            OnReadHeader = p =>
            {
                if (string.Equals(p, olds[3], StringComparison.OrdinalIgnoreCase))
                    throw new FileNotFoundException("gone", p);
            },
        };

        var result = BackupKit.Service(_root, _clock, files).Run();

        Assert.Equal(DailyBackupOutcome.Created, result.Outcome);
        // Evidence: new + olds[0] + olds[1]. olds[2] is deleted; the vanished olds[3] is not validated, so it is kept.
        Assert.Equal(new RetentionSummary(1, 0, 1, false), result.Retention);
        Assert.True(File.Exists(olds[3]));
    }

    [Fact]
    public void AnUnreadableCandidate_IsStillAFailedRun_CandidateInvalid()
    {
        var files = new FaultyBackupFileOps
        {
            OnReadHeader = p =>
            {
                if (p.EndsWith(".partial", StringComparison.Ordinal))
                    throw FaultyBackupFileOps.SharingViolation();
            },
        };

        var result = BackupKit.Service(_root, _clock, files).Run();

        Assert.Equal(DailyBackupOutcome.Failed, result.Outcome);
        Assert.Equal(DailyBackupFailure.CandidateInvalid, result.Failure);
        Assert.Empty(BackupKit.PublishedBackups(_root));
        Assert.Empty(Directory.GetFiles(_root.Paths.DailyBackupsDirectory, "*.partial*"));
    }

    [Fact]
    public void PublishedFileUnreadableRightAfterPublication_IsPublishFailed_FileKept()
    {
        string? published = null;
        var files = new FaultyBackupFileOps();
        files.OnMove = (_, destination) => published = destination;
        files.OnGetLength = p =>
        {
            if (published is not null && string.Equals(p, published, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("denied");
        };

        var result = BackupKit.Service(_root, _clock, files).Run();

        Assert.Equal(DailyBackupFailure.PublishFailed, result.Failure);
        Assert.True(File.Exists(published));
    }

    [Fact]
    public void SimulatedCrashFromAFileOperationDuringRetention_Propagates()
    {
        var olds = PlaceOld(4);
        var files = new FaultyBackupFileOps
        {
            OnReadHeader = p =>
            {
                if (string.Equals(p, olds[3], StringComparison.OrdinalIgnoreCase))
                    throw new SimulatedCrashException(BackupStage.Pruned);
            },
        };

        Assert.Throws<SimulatedCrashException>(() => BackupKit.Service(_root, _clock, files).Run());
        Assert.All(olds, f => Assert.True(File.Exists(f)));
    }
}
