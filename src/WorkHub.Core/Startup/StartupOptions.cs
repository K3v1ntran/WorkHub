namespace WorkHub.Core.Startup;

/// <summary>
/// Command-line options. The only option is <c>--data-root &lt;path&gt;</c>, which runs WorkHub against an isolated
/// data root (for example for testing) instead of %LOCALAPPDATA%\WorkHub.
/// <para>
/// Parsing runs before any folder, log or database is created. An explicit override that is missing, malformed or
/// unsafe is an error: WorkHub never falls back to the default profile when an override was requested.
/// </para>
/// </summary>
public sealed class StartupOptions
{
    public const string DataRootOption = "--data-root";

    /// <summary>Top-level entries WorkHub itself creates in a data root; anything else marks a foreign folder.</summary>
    public static readonly IReadOnlyList<string> WorkHubRootEntries = ["Data", "Config", "Logs", "Backups", "Security"];

    private StartupOptions(WorkHubPaths paths, bool isCustomDataRoot)
    {
        Paths = paths;
        IsCustomDataRoot = isCustomDataRoot;
    }

    public WorkHubPaths Paths { get; }

    /// <summary>True when <c>--data-root</c> selected the root (shown in the UI and logged inside that root).</summary>
    public bool IsCustomDataRoot { get; }

    /// <param name="args">Process arguments, excluding the executable.</param>
    /// <param name="defaultRoot">The real per-user root (%LOCALAPPDATA%\WorkHub); an override may never point into it.</param>
    public static StartupOptionsResult Parse(IReadOnlyList<string> args, string defaultRoot)
    {
        string? rawRoot = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], DataRootOption, StringComparison.OrdinalIgnoreCase))
                return StartupOptionsResult.Failed($"Unknown argument '{args[i]}'. The only supported option is {DataRootOption} <path>.");
            if (rawRoot is not null)
                return StartupOptionsResult.Failed($"{DataRootOption} may be given only once.");
            if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                return StartupOptionsResult.Failed($"{DataRootOption} requires a folder path.");
            rawRoot = args[++i];
        }

        if (rawRoot is null)
            return StartupOptionsResult.Ok(new StartupOptions(new WorkHubPaths(defaultRoot), isCustomDataRoot: false));

        var error = ValidateDataRoot(rawRoot, defaultRoot, out var fullPath);
        return error is null
            ? StartupOptionsResult.Ok(new StartupOptions(new WorkHubPaths(fullPath!), isCustomDataRoot: true))
            : StartupOptionsResult.Failed($"{DataRootOption} '{rawRoot}' cannot be used: {error}");
    }

    private static string? ValidateDataRoot(string raw, string defaultRoot, out string? fullPath)
    {
        fullPath = null;
        var value = raw.Trim();
        if (value.Any(char.IsControl) || value.Contains("://", StringComparison.Ordinal))
            return "it is not a folder path.";
        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
            return "network (UNC) and device paths are not supported; use a local folder such as C:\\WorkHubTest.";
        if (!Path.IsPathFullyQualified(value) || value.Length < 3 || !char.IsAsciiLetter(value[0]) || value[1] != ':')
            return "it must be a full local path such as C:\\WorkHubTest.";
        if (value[2..].IndexOfAny(['<', '>', '"', '|', '*', '?', ':']) >= 0 || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return "it contains characters that are not allowed in a folder path.";

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "it is not a valid folder path.";
        }

        if (Path.GetPathRoot(full) is { } driveRoot && string.Equals(Path.TrimEndingDirectorySeparator(driveRoot), full, StringComparison.OrdinalIgnoreCase))
            return "a drive root cannot be a WorkHub data folder.";

        var real = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultRoot));
        if (string.Equals(full, real, StringComparison.OrdinalIgnoreCase) || IsInside(full, real) || IsInside(real, full))
            return $"it overlaps the normal WorkHub data folder ({real}). Choose a separate folder.";

        if (File.Exists(full))
            return "a file with that name exists.";

        if (Directory.Exists(full))
        {
            var foreign = Directory.EnumerateFileSystemEntries(full)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => !WorkHubRootEntries.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (foreign is not null)
                return $"the folder is not empty and is not a WorkHub data folder (found '{foreign}').";
        }
        else if (Path.GetDirectoryName(full) is not { } parent || !Directory.Exists(parent))
        {
            return "its parent folder does not exist.";
        }

        fullPath = full;
        return null;
    }

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed record StartupOptionsResult(StartupOptions? Options, string? Error)
{
    public bool IsValid => Options is not null;

    public static StartupOptionsResult Ok(StartupOptions options) => new(options, null);

    public static StartupOptionsResult Failed(string error) => new(null, error);
}
