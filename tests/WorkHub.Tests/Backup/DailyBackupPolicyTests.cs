using WorkHub.Core.Backup;

namespace WorkHub.Tests.Backup;

/// <summary>Pure JST calendar, naming, done-today and retention-evidence rules (Phase 9 §4.1, §6, §7; tests A1–A5).</summary>
public sealed class DailyBackupPolicyTests
{
    private static readonly JstCalendar Tokyo = JstCalendar.Resolve();

    [Fact]
    public void TokyoDay_ChangesAt15UtcExactly()
    {
        Assert.Equal(new DateOnly(2026, 10, 13), Tokyo.DayOf(new DateTimeOffset(2026, 10, 13, 14, 59, 59, 999, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 14), Tokyo.DayOf(new DateTimeOffset(2026, 10, 13, 15, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void TokyoDay_IgnoresTheOffsetTheInstantIsExpressedIn()
    {
        // The same instant written with a European, a US and a UTC offset: one Tokyo day (machine zone is irrelevant).
        var utc = new DateTimeOffset(2026, 10, 13, 16, 30, 0, TimeSpan.Zero);
        foreach (var offset in new[] { TimeSpan.Zero, TimeSpan.FromHours(2), TimeSpan.FromHours(-7) })
            Assert.Equal(new DateOnly(2026, 10, 14), Tokyo.DayOf(utc.ToOffset(offset)));
    }

    [Fact]
    public void Resolve_PrefersAsiaTokyoThenWindowsIdThenFixedOffset()
    {
        var tried = new List<string>();
        var calendar = JstCalendar.Resolve(id =>
        {
            tried.Add(id);
            throw new TimeZoneNotFoundException();
        });

        Assert.Equal(["Asia/Tokyo", "Tokyo Standard Time"], tried);
        Assert.True(calendar.UsesFixedOffset);
        Assert.Equal(new DateOnly(2026, 10, 14), calendar.DayOf(new DateTimeOffset(2026, 10, 13, 15, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 10, 13), calendar.DayOf(new DateTimeOffset(2026, 10, 13, 14, 59, 59, TimeSpan.Zero)));

        var windowsOnly = JstCalendar.Resolve(id => id == "Tokyo Standard Time" ? TimeZoneInfo.FindSystemTimeZoneById(id) : throw new TimeZoneNotFoundException());
        Assert.False(windowsOnly.UsesFixedOffset);
    }

    [Fact]
    public void FileName_RoundTripsAndCandidatesNeverMatchThePublishedPattern()
    {
        var name = DailyBackupPolicy.FileName(new DateOnly(2026, 10, 13), 7, new DateTimeOffset(2026, 10, 13, 8, 30, 15, 123, TimeSpan.FromHours(9)));
        Assert.Equal("workhub_daily_20261013_v7_20261013T083015123.db", name);
        Assert.True(DailyBackupPolicy.TryParse(name, out var parsed));
        Assert.Equal(new DateOnly(2026, 10, 13), parsed.Day);
        Assert.Equal(7, parsed.Version);

        var candidate = DailyBackupPolicy.CandidateName(name, Guid.NewGuid());
        Assert.False(DailyBackupPolicy.TryParse(candidate, out _));
        Assert.True(DailyBackupPolicy.IsOwnCandidate(candidate));
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" })
            Assert.True(DailyBackupPolicy.IsOwnCandidate(candidate + suffix));
    }

    [Theory]
    [InlineData("workhub_daily_20261013_v7_20261013T083015123.db.bak")]
    [InlineData("workhub_daily_20261399_v7_20261013T083015123.db")]
    [InlineData("workhub_daily_foo.db")]
    [InlineData("notes.txt")]
    [InlineData("daily-backup.lock")]
    [InlineData("workhub_v6_before_v7_20261012-133915006.db")]
    [InlineData("workhub_daily_20261013_v7_20261013T083015123.db.partial")]
    [InlineData("workhub_daily_20261013_v7_20261013T083015123.db.0123456789abcdef0123456789abcdeg.partial")]
    public void UnrelatedNames_AreNeitherBackupsNorOwnCandidates(string name)
    {
        Assert.False(DailyBackupPolicy.TryParse(name, out _));
        Assert.False(DailyBackupPolicy.IsOwnCandidate(name));
    }

    [Fact]
    public void Eligible_ExcludesFutureDatedAndOrdersNewestFirst()
    {
        var today = new DateOnly(2026, 10, 13);
        string[] names =
        [
            Name(2026, 10, 11), Name(2026, 10, 13, ms: 5), Name(2026, 10, 13, ms: 1), Name(2027, 1, 1), "notes.txt", Name(2026, 10, 12),
        ];

        var eligible = DailyBackupPolicy.Eligible(names, today);

        Assert.Equal([Name(2026, 10, 13, ms: 5), Name(2026, 10, 13, ms: 1), Name(2026, 10, 12), Name(2026, 10, 11)], eligible.Select(n => n.FileName));
        Assert.Equal([Name(2027, 1, 1)], DailyBackupPolicy.FutureDated(names, today).Select(n => n.FileName));
    }

    [Fact]
    public void DoneToday_RequiresTodaysExactKeyThatPassesTheQuickCheck()
    {
        var today = new DateOnly(2026, 10, 13);
        var eligible = DailyBackupPolicy.Eligible([Name(2026, 10, 12), Name(2026, 10, 13)], today);

        Assert.True(DailyBackupPolicy.IsDoneToday(eligible, today, _ => true));
        Assert.False(DailyBackupPolicy.IsDoneToday(eligible, today, n => n.Day != today));
        Assert.False(DailyBackupPolicy.IsDoneToday(DailyBackupPolicy.Eligible([Name(2026, 10, 12)], today), today, _ => true));
    }

    [Fact]
    public void Retention_KeepsThreeNewestAndDeletesOlderFullyValidatedOldestFirst()
    {
        var names = Days(5);
        var asked = new List<string>();

        var plan = DailyBackupPolicy.PlanRetention(names, names[0].FileName, n => { asked.Add(n.FileName); return true; });

        Assert.False(plan.InsufficientEvidence);
        Assert.Equal(names.Take(3), plan.Evidence);
        Assert.Equal([names[4], names[3]], plan.Delete);
        Assert.DoesNotContain(names[0].FileName, asked); // the just-published file is not checked twice
        Assert.Equal(names.Skip(1).Select(n => n.FileName), asked); // every deleted file was checked
    }

    [Fact]
    public void Retention_WithTooFewFullyValidatedBackups_DeletesNothing()
    {
        var names = Days(5);
        // The two newest older backups pass quick (not modelled here) but not full validation.
        var plan = DailyBackupPolicy.PlanRetention(names, names[0].FileName, n => n != names[1] && n != names[2] && n != names[3]);

        Assert.True(plan.InsufficientEvidence);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void Retention_DamagedNewerBackupsAreSkippedAsEvidenceAndPreserved()
    {
        var names = Days(6);
        var plan = DailyBackupPolicy.PlanRetention(names, names[0].FileName, n => n != names[1]);

        Assert.Equal([names[0], names[2], names[3]], plan.Evidence);
        Assert.Equal([names[5], names[4]], plan.Delete);
        Assert.Equal([names[1]], plan.Preserved);
    }

    [Fact]
    public void Retention_ADeletionCandidateThatFailsFullValidationIsPreserved()
    {
        var names = Days(6);
        var plan = DailyBackupPolicy.PlanRetention(names, names[0].FileName, n => n != names[4]);

        Assert.Equal([names[5], names[3]], plan.Delete);
        Assert.Equal([names[4]], plan.Preserved);
    }

    private static string Name(int y, int m, int d, int ms = 0) =>
        DailyBackupPolicy.FileName(new DateOnly(y, m, d), 7, new DateTimeOffset(y, m, d, 9, 0, 0, ms, TimeSpan.FromHours(9)));

    private static List<DailyBackupName> Days(int count) =>
        DailyBackupPolicy.Eligible(Enumerable.Range(0, count).Select(i => Name(2026, 10, 20 - i)), new DateOnly(2026, 10, 20)).ToList();
}
