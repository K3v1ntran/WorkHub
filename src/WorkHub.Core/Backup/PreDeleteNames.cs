using System.Globalization;
using System.Text.RegularExpressions;

namespace WorkHub.Core.Backup;

/// <summary>
/// Phase 14 names in <c>Backups\PreDelete\</c>: <c>workhub_predelete_v{N}_{utc:yyyyMMdd-HHmmssfff}.db</c>, a fully validated
/// copy of the whole database made before a permanent deletion from a Project's Trash. Outside daily retention and kept
/// forever; restorable through the whole-database Restore.
/// </summary>
public static partial class PreDeleteNames
{
    private const string StampFormat = "yyyyMMdd-HHmmssfff";

    [GeneratedRegex(@"^workhub_predelete_v(\d{1,4})_(\d{8}-\d{9})\.db$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FinalPattern();

    [GeneratedRegex(@"^workhub_predelete_v\d{1,4}_\d{8}-\d{9}\.db\.[0-9a-f]{32}\.partial(-journal|-wal|-shm)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CandidatePattern();

    public static string Validated(int version, DateTimeOffset utcNow) =>
        $"workhub_predelete_v{version.ToString(CultureInfo.InvariantCulture)}_{utcNow.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}.db";

    public static string CandidateName(string finalName, Guid id) => $"{finalName}.{id:N}.partial";

    /// <summary>True for this feature's own leftover candidates and their sidecars (cleaned only under the PreDelete lock).</summary>
    public static bool IsOwnCandidate(string fileName) => CandidatePattern().IsMatch(fileName);

    public static bool TryParse(string fileName, out int version, out DateTimeOffset takenUtc)
    {
        version = 0;
        takenUtc = default;
        var match = FinalPattern().Match(fileName);
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups[2].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            return false;

        version = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        takenUtc = new DateTimeOffset(stamp, TimeSpan.Zero);
        return true;
    }
}
