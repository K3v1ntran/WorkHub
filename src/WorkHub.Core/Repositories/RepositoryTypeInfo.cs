namespace WorkHub.Core.Repositories;

/// <param name="Type">The built-in repository type.</param>
/// <param name="DisplayName">Fixed name shown in the UI.</param>
/// <param name="StorageCode">Stable code persisted in SQLite. Never change an existing value.</param>
/// <param name="VersionControl">Tooling used to open repositories of this type.</param>
public sealed record RepositoryTypeInfo(
    RepositoryType Type,
    string DisplayName,
    string StorageCode,
    VersionControlKind VersionControl);
