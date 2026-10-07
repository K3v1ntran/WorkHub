namespace WorkHub.Core.Repositories;

/// <summary>
/// Validation rules shared by settings, custom targets and the resolver.
/// Only network URL schemes are accepted, so a target can never point the launcher at a local program.
/// </summary>
public static class RepositoryUrlRules
{
    private static readonly string[] SvnSchemes = ["http", "https", "svn", "svn+ssh"];
    private static readonly string[] WebSchemes = ["http", "https"];

    public static IReadOnlyList<string> SchemesFor(VersionControlKind kind) =>
        kind == VersionControlKind.Svn ? SvnSchemes : WebSchemes;

    /// <summary>Base URL for an SVN or GitBucket host. No query or fragment, because a prefix and code are appended.</summary>
    public static bool IsValidHost(string? value, VersionControlKind kind) =>
        TryParse(value, kind, out var uri) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    /// <summary>Full repository target (a custom target or a resolved one).</summary>
    public static bool IsValidTarget(string? value, VersionControlKind kind) => TryParse(value, kind, out _);

    /// <summary>Prefixes become part of the URL path, so they follow the SystemCode character policy.</summary>
    public static bool IsValidPrefix(string? value) =>
        string.IsNullOrEmpty(value) || value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static string DescribeSchemes(VersionControlKind kind) =>
        string.Join(", ", SchemesFor(kind).Select(s => s + "://"));

    private static bool TryParse(string? value, VersionControlKind kind, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return false;

        return Uri.TryCreate(value, UriKind.Absolute, out uri!)
               && SchemesFor(kind).Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)
               && !string.IsNullOrEmpty(uri.Host)
               && string.IsNullOrEmpty(uri.UserInfo);
    }
}
