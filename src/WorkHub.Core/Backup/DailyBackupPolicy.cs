using System.Globalization;
using System.Text.RegularExpressions;

namespace WorkHub.Core.Backup;

/// <summary>A published daily backup name: <c>workhub_daily_{day:yyyyMMdd}_v{schema}_{tokyo:yyyyMMdd'T'HHmmssfff}.db</c>.</summary>
public sealed record DailyBackupName(string FileName, DateOnly Day, int Version, DateTime Stamp);

/// <param name="Delete">Files to delete, oldest first. Each was reported fully validated by the caller's check.</param>
/// <param name="Preserved">Eligible files kept because they did not pass full validation.</param>
/// <param name="InsufficientEvidence">Fewer than the required newer fully validated backups: nothing may be deleted.</param>
public sealed record RetentionPlan(
    IReadOnlyList<DailyBackupName> Evidence,
    IReadOnlyList<DailyBackupName> Delete,
    IReadOnlyList<DailyBackupName> Preserved,
    bool InsufficientEvidence);

/// <summary>
/// Pure naming, "done today" and retention rules for daily backups (Phase 9 §4.1, §6.2, §7). Validation is supplied by the
/// caller, so the rules can be tested without files.
/// </summary>
public static partial class DailyBackupPolicy
{
    /// <summary>Daily backups kept (newest fully validated).</summary>
    public const int KeepCount = 3;

    private const string DayFormat = "yyyyMMdd";
    private const string StampFormat = "yyyyMMdd'T'HHmmssfff";

    [GeneratedRegex(@"^workhub_daily_(\d{8})_v(\d{1,4})_(\d{8}T\d{9})\.db$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DailyPattern();

    /// <summary>A candidate: the final name plus <c>.{guid:N}.partial</c>, optionally with a SQLite sidecar suffix.</summary>
    [GeneratedRegex(@"^workhub_daily_\d{8}_v\d{1,4}_\d{8}T\d{9}\.db\.[0-9a-f]{32}\.partial(-journal|-wal|-shm)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CandidatePattern();

    [GeneratedRegex(@"^workhub_v(\d{1,4})_before_v(\d{1,4})_(\d{8}-\d{9})\.db$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MigrationPattern();

    public static string FileName(DateOnly day, int version, DateTimeOffset tokyoNow) =>
        $"workhub_daily_{day.ToString(DayFormat, CultureInfo.InvariantCulture)}_v{version.ToString(CultureInfo.InvariantCulture)}_" +
        $"{tokyoNow.ToString(StampFormat, CultureInfo.InvariantCulture)}.db";

    /// <summary>The name a candidate is written under before it is checked and published.</summary>
    public static string CandidateName(string finalName, Guid id) => $"{finalName}.{id:N}.partial";

    public static bool TryParse(string fileName, out DailyBackupName name)
    {
        name = null!;
        var match = DailyPattern().Match(fileName);
        if (!match.Success
            || !DateOnly.TryParseExact(match.Groups[1].Value, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || !DateTime.TryParseExact(match.Groups[3].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            return false;

        name = new DailyBackupName(fileName, day, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), stamp);
        return true;
    }

    /// <summary>True for this feature's own in-progress candidate files and their sidecars (the only files D8 may clean up).</summary>
    public static bool IsOwnCandidate(string fileName) => CandidatePattern().IsMatch(fileName);

    /// <summary>Parses an existing migration backup name (<c>workhub_v{from}_before_v{to}_{utc:yyyyMMdd-HHmmssfff}.db</c>).</summary>
    public static bool TryParseMigration(string fileName, out int fromVersion, out int toVersion, out DateTimeOffset takenUtc)
    {
        fromVersion = toVersion = 0;
        takenUtc = default;
        var match = MigrationPattern().Match(fileName);
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups[3].Value, "yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            return false;

        fromVersion = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        toVersion = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        takenUtc = new DateTimeOffset(stamp, TimeSpan.Zero);
        return true;
    }

    /// <summary>
    /// Daily backups that take part in done-today and retention: recognized names with a day key on or before today, newest
    /// first (day, then stamp, then name). Future-dated files (after a clock error) are excluded: never counted, never pruned.
    /// </summary>
    public static IReadOnlyList<DailyBackupName> Eligible(IEnumerable<string> fileNames, DateOnly today) =>
        fileNames
            .Select(f => TryParse(f, out var n) ? n : null)
            .OfType<DailyBackupName>()
            .Where(n => n.Day <= today)
            .OrderByDescending(n => n.Day).ThenByDescending(n => n.Stamp).ThenByDescending(n => n.FileName, StringComparer.Ordinal)
            .ToList();

    /// <summary>Recognized daily names with a day key after today (kept and logged, never counted or pruned).</summary>
    public static IReadOnlyList<DailyBackupName> FutureDated(IEnumerable<string> fileNames, DateOnly today) =>
        fileNames.Select(f => TryParse(f, out var n) ? n : null).OfType<DailyBackupName>().Where(n => n.Day > today).ToList();

    /// <summary>
    /// Today is done when a published file with today's exact day key passes the quick check (§6.2). Non-destructive, so
    /// the quick level is enough: retention never relies on it.
    /// </summary>
    public static bool IsDoneToday(IReadOnlyList<DailyBackupName> eligible, DateOnly today, Func<DailyBackupName, bool> passesQuickCheck) =>
        eligible.Where(n => n.Day == today).Any(passesQuickCheck);

    /// <summary>
    /// Retention evidence rule (§7). Walks <paramref name="eligible"/> from newest, asking for full validation, until
    /// <paramref name="keep"/> files are fully validated (the just-published file counts without a second check). Only then
    /// are older files considered, and each is deleted only if it is fully validated itself; anything else is preserved.
    /// With too little evidence nothing is deleted. Validation is requested lazily, newest first.
    /// </summary>
    public static RetentionPlan PlanRetention(
        IReadOnlyList<DailyBackupName> eligible,
        string publishedFileName,
        Func<DailyBackupName, bool> isFullyValidated,
        int keep = KeepCount)
    {
        var ordered = eligible
            .OrderByDescending(n => n.Day).ThenByDescending(n => n.Stamp).ThenByDescending(n => n.FileName, StringComparer.Ordinal)
            .ToList();
        var evidence = new List<DailyBackupName>();
        var preserved = new List<DailyBackupName>();
        var delete = new List<DailyBackupName>();

        foreach (var name in ordered)
        {
            var isPublished = string.Equals(name.FileName, publishedFileName, StringComparison.OrdinalIgnoreCase);
            if (evidence.Count < keep)
            {
                if (isPublished || isFullyValidated(name))
                    evidence.Add(name);
                else
                    preserved.Add(name);
                continue;
            }

            if (isPublished)
                continue; // never a deletion candidate (cannot happen for a correctly ordered set)

            if (isFullyValidated(name))
                delete.Add(name);
            else
                preserved.Add(name);
        }

        if (evidence.Count < keep)
            return new RetentionPlan(evidence, [], preserved, InsufficientEvidence: true);

        delete.Reverse(); // oldest first
        return new RetentionPlan(evidence, delete, preserved, InsufficientEvidence: false);
    }
}
