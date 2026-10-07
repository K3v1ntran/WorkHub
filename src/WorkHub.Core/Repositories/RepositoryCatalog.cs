namespace WorkHub.Core.Repositories;

/// <summary>
/// Single source of truth for the fixed repository groups, in display order.
/// </summary>
public static class RepositoryCatalog
{
    public static IReadOnlyList<RepositoryTypeInfo> All { get; } =
    [
        new(RepositoryType.SvnSystem, "SVN SYSTEM", "SVN_SYSTEM", VersionControlKind.Svn),
        new(RepositoryType.SvnOracle, "SVN ORACLE", "SVN_ORACLE", VersionControlKind.Svn),
        new(RepositoryType.GitSystem, "GIT SYSTEM", "GIT_SYSTEM", VersionControlKind.Git),
        new(RepositoryType.GitOracle, "GIT ORACLE", "GIT_ORACLE", VersionControlKind.Git),
        new(RepositoryType.Release, "RELEASE", "RELEASE", VersionControlKind.Svn),
    ];

    public static RepositoryTypeInfo Get(RepositoryType type) =>
        All.FirstOrDefault(info => info.Type == type)
        ?? throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown repository type.");

    public static bool TryFromStorageCode(string? code, out RepositoryType type)
    {
        foreach (var info in All)
        {
            if (string.Equals(info.StorageCode, code, StringComparison.Ordinal))
            {
                type = info.Type;
                return true;
            }
        }

        type = default;
        return false;
    }
}
