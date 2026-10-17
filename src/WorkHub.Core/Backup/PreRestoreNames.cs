using System.Globalization;
using System.Text.RegularExpressions;

namespace WorkHub.Core.Backup;

/// <summary>
/// Phase 10 names in <c>Backups\PreRestore\</c>: <c>workhub_prerestore_v{N}_{utc:yyyyMMdd-HHmmssfff}.db</c> (validated
/// safety backup) and <c>workhub_preserved_v{N}_{utc}.db</c> (faithful preservation copy, not a validated backup). Both
/// stay outside daily retention and are kept forever.
/// </summary>
public static partial class PreRestoreNames
{
    private const string StampFormat = "yyyyMMdd-HHmmssfff";

    [GeneratedRegex(@"^workhub_(prerestore|preserved)_v(\d{1,4})_(\d{8}-\d{9})\.db$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FinalPattern();

    [GeneratedRegex(@"^workhub_(prerestore|preserved)_v\d{1,4}_\d{8}-\d{9}\.db\.[0-9a-f]{32}\.partial(-journal|-wal|-shm)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CandidatePattern();

    public static string Validated(int version, DateTimeOffset utcNow) =>
        $"workhub_prerestore_v{version.ToString(CultureInfo.InvariantCulture)}_{utcNow.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}.db";

    public static string Preserved(int version, DateTimeOffset utcNow) =>
        $"workhub_preserved_v{version.ToString(CultureInfo.InvariantCulture)}_{utcNow.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}.db";

    public static string CandidateName(string finalName, Guid id) => $"{finalName}.{id:N}.partial";

    /// <summary>True for this feature's own leftover candidates and their sidecars (cleaned only under exclusive Presence).</summary>
    public static bool IsOwnCandidate(string fileName) => CandidatePattern().IsMatch(fileName);

    public static bool TryParse(string fileName, out BackupKind kind, out int version, out DateTimeOffset takenUtc)
    {
        kind = BackupKind.Unrecognized;
        version = 0;
        takenUtc = default;
        var match = FinalPattern().Match(fileName);
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups[3].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
            return false;

        kind = string.Equals(match.Groups[1].Value, "prerestore", StringComparison.OrdinalIgnoreCase) ? BackupKind.PreRestore : BackupKind.Preserved;
        version = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        takenUtc = new DateTimeOffset(stamp, TimeSpan.Zero);
        return true;
    }
}
