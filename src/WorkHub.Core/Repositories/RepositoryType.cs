namespace WorkHub.Core.Repositories;

/// <summary>
/// Built-in repository groups. These are application concepts, not user data:
/// they exist without any database rows and cannot be renamed or deleted by users.
/// </summary>
public enum RepositoryType
{
    SvnSystem = 1,
    SvnOracle = 2,
    GitSystem = 3,
    GitOracle = 4,
    Release = 5,
}

public enum VersionControlKind
{
    Svn,
    Git,
}
