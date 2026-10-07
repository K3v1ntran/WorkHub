namespace WorkHub.Core.Repositories;

/// <summary>
/// Normalization rules for <c>Systems.SystemCode</c>.
/// Codes become part of repository URLs, so case is preserved (SVN/Git paths are case-sensitive)
/// while uniqueness is enforced case-insensitively by the database.
/// </summary>
public static class SystemCode
{
    public const int MaxLength = 100;

    private static readonly char[] ForbiddenChars = ['/', '\\', '?', '#', '%', '"', '<', '>', '|', '*', ':'];

    public static bool TryNormalize(string? raw, out string normalized, out string? error)
    {
        normalized = (raw ?? string.Empty).Trim();
        error = null;

        if (normalized.Length == 0)
            error = "System code is required.";
        else if (normalized.Length > MaxLength)
            error = $"System code must be at most {MaxLength} characters.";
        else if (normalized.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            error = "System code must not contain whitespace.";
        else if (normalized.IndexOfAny(ForbiddenChars) >= 0)
            error = "System code contains characters that are not allowed in repository paths.";

        return error is null;
    }

    public static string Normalize(string? raw) =>
        TryNormalize(raw, out var normalized, out var error)
            ? normalized
            : throw new ArgumentException(error, nameof(raw));
}
