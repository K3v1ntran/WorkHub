namespace WorkHub.Infrastructure.Launching;

/// <summary>
/// Looks for TortoiseProc.exe in the standard install folders only. Used by the Settings "Detect" button;
/// the path is never assumed — the user still saves it explicitly.
/// </summary>
public static class TortoiseLocator
{
    public static string? Detect(Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };

        return roots
            .Where(root => !string.IsNullOrEmpty(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => Path.Combine(root!, "TortoiseSVN", "bin", "TortoiseProc.exe"))
            .FirstOrDefault(fileExists);
    }
}
