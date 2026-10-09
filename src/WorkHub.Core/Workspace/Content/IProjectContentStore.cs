namespace WorkHub.Core.Workspace.Content;

/// <summary>
/// Persistence for Project content (schema v4: ProjectFolders, ProjectLinks). Implementations run I/O off the caller's
/// thread and make every multi-step change atomic. Nothing here touches files or folders on disk.
/// Names and drafts passed in are already normalized by <see cref="ProjectContentService"/>.
/// </summary>
public interface IProjectContentStore
{
    /// <summary>Direct child folders and links of a container (null folder = Project root), unsorted, plus its breadcrumb.</summary>
    Task<ProjectContentListResult> ListAsync(long projectNodeId, long? folderId, CancellationToken cancellationToken = default);

    /// <exception cref="ContentContainerException"/>
    /// <exception cref="DuplicateProjectFolderNameException"/>
    Task<ProjectFolder> CreateFolderAsync(long projectNodeId, long? parentFolderId, string name, CancellationToken cancellationToken = default);

    /// <returns>The stored folder, or null when it no longer exists.</returns>
    /// <exception cref="DuplicateProjectFolderNameException"/>
    Task<ProjectFolder?> RenameFolderAsync(long folderId, string name, CancellationToken cancellationToken = default);

    /// <exception cref="ContentContainerException"/>
    Task<ProjectLink> CreateLinkAsync(long projectNodeId, long? folderId, ProjectLinkDraft draft, CancellationToken cancellationToken = default);

    /// <returns>The stored link, or null when it no longer exists.</returns>
    Task<ProjectLink?> UpdateLinkAsync(long linkId, ProjectLinkDraft draft, CancellationToken cancellationToken = default);

    Task<ProjectLink?> GetLinkAsync(long linkId, CancellationToken cancellationToken = default);

    /// <summary>All folders of the Project with their paths, except <paramref name="movingFolderId"/> and its subtree.</summary>
    Task<IReadOnlyList<WorkspaceFolderPath>> ListFolderMoveTargetsAsync(long projectNodeId, long? movingFolderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a folder (with its subtree) within its Project, in ONE <c>BEGIN IMMEDIATE</c> transaction, following the
    /// revision 3 §8.1 sequence. <paramref name="expectedParentFolderId"/> is the container the caller observed.
    /// </summary>
    Task<ContentMoveOutcome> MoveFolderAsync(long folderId, long? expectedParentFolderId, long? targetParentFolderId, CancellationToken cancellationToken = default);

    /// <summary>Moves a link within its Project; same sequence as folders without the ancestry step.</summary>
    Task<ContentMoveOutcome> MoveLinkAsync(long linkId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default);
}

public sealed class DuplicateProjectFolderNameException(string name, Exception? inner = null)
    : Exception($"A folder named '{name}' already exists in this location.", inner)
{
    public string Name { get; } = name;
}

/// <summary>The Project or the containing folder is missing, or the folder belongs to another Project.</summary>
public sealed class ContentContainerException(ContentContainerProblem problem, Exception? inner = null)
    : Exception($"Project content container: {problem}.", inner)
{
    public ContentContainerProblem Problem { get; } = problem;
}
