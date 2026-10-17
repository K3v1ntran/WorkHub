using WorkHub.Core.Startup;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Phase 11 backstop: no test root may be, contain or lie inside the real profile (%LOCALAPPDATA%\WorkHub). Compares
/// normalized path segments only (no alias resolution); the developer launch script does the alias-resolving checks.
/// </summary>
internal static class DefaultProfileGuard
{
    private static readonly string DefaultRoot = WorkHubPaths.ForCurrentUser().Root; // compared only, never opened

    public static void EnsureIsolated(string root)
    {
        if (Overlaps(root, DefaultRoot))
            throw new InvalidOperationException($"Refusing a test root that overlaps the real WorkHub profile: {root}");
    }

    /// <summary>Equal, inside, or containing; ordinal, case-insensitive, after full-path normalization.</summary>
    public static bool Overlaps(string path, string defaultRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var real = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultRoot));
        return string.Equals(full, real, StringComparison.OrdinalIgnoreCase) || IsInside(full, real) || IsInside(real, full);
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
