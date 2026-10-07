namespace WorkHub.Core.Repositories;

/// <summary>
/// Normalization rules for <c>Systems.SystemCode</c>.
/// Codes become part of repository URLs, so case is preserved (SVN/Git paths are case-sensitive)
/// while uniqueness is enforced case-insensitively by the database (<c>COLLATE NOCASE</c> UNIQUE).
/// Codes are limited to ASCII letters, digits, '.', '_' and '-'; the database enforces the same set,
/// because NOCASE only folds ASCII letters.
/// </summary>
public static class SystemCode
{
    public const int MaxLength = 100;

    public static bool TryNormalize(string? raw, out string normalized, out string? error)
    {
        normalized = (raw ?? string.Empty).Trim();
        error = null;

        if (normalized.Length == 0)
            error = "System code is required.";
        else if (normalized.Length > MaxLength)
            error = $"System code must be at most {MaxLength} characters.";
        else if (!normalized.All(IsAllowed))
            error = "System code may contain only letters A-Z, digits, '.', '_' and '-'.";

        return error is null;
    }

    public static string Normalize(string? raw) =>
        TryNormalize(raw, out var normalized, out var error)
            ? normalized
            : throw new ArgumentException(error, nameof(raw));

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';
}
