namespace WorkHub.Core.Workspace;

/// <summary>
/// Persistence for the logical Workspace tree. Implementations run I/O off the caller's thread and make every
/// multi-row change atomic. Nothing here ever touches files or folders on disk.
/// Names passed in are already normalized (<see cref="WorkspaceName.TryNormalize"/>); keys are computed with
/// <see cref="WorkspaceName.Key"/>.
/// </summary>
public interface IWorkspaceStore
{
    /// <summary>Direct children of <paramref name="parentId"/> (null = root), unsorted.</summary>
    Task<IReadOnlyList<WorkspaceNode>> ListChildrenAsync(long? parentId, CancellationToken cancellationToken = default);

    /// <exception cref="DuplicateWorkspaceNameException"/>
    /// <exception cref="WorkspaceParentException"/>
    Task<WorkspaceNode> CreateFolderAsync(long? parentId, string name, CancellationToken cancellationToken = default);

    /// <summary>Inserts the node and its details in one transaction.</summary>
    /// <exception cref="DuplicateWorkspaceNameException"/>
    /// <exception cref="WorkspaceParentException"/>
    Task<WorkspaceNode> CreateProjectAsync(long? parentId, string projectId, ProjectDraft draft, CancellationToken cancellationToken = default);

    /// <returns>False when the node no longer exists.</returns>
    /// <exception cref="DuplicateWorkspaceNameException"/>
    Task<bool> RenameAsync(long nodeId, string name, CancellationToken cancellationToken = default);

    Task<ProjectDetails?> GetProjectAsync(long nodeId, CancellationToken cancellationToken = default);

    /// <summary>Updates details and the node's Name/NameKey/UpdatedAt in one transaction.</summary>
    /// <returns>The stored state, or null when the Project no longer exists.</returns>
    /// <exception cref="DuplicateWorkspaceNameException"/>
    Task<ProjectDetails?> UpdateProjectAsync(long nodeId, ProjectDraft draft, CancellationToken cancellationToken = default);

    /// <summary>All Folders except <paramref name="movingNodeId"/> and its subtree, with their paths.</summary>
    Task<IReadOnlyList<WorkspaceFolderPath>> ListMoveTargetsAsync(long movingNodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// In ONE <c>BEGIN IMMEDIATE</c> transaction: validates the node, the destination (a Folder, or null for root) and
    /// ancestry (no move into itself or a descendant), then updates ParentId. Any failure changes nothing.
    /// </summary>
    /// <returns>False when the node is already under that parent (nothing written).</returns>
    /// <exception cref="WorkspaceNodeNotFoundException"/>
    /// <exception cref="WorkspaceParentException"/>
    /// <exception cref="WorkspaceMoveCycleException"/>
    /// <exception cref="DuplicateWorkspaceNameException"/>
    Task<bool> MoveAsync(long nodeId, long? targetParentId, CancellationToken cancellationToken = default);
}

public sealed class DuplicateWorkspaceNameException(string name, Exception? inner = null)
    : Exception($"'{name}' already exists in this location.", inner)
{
    public string Name { get; } = name;
}

public enum WorkspaceParentProblem
{
    NotFound,
    NotAFolder,
}

public sealed class WorkspaceParentException(long parentId, WorkspaceParentProblem problem, Exception? inner = null)
    : Exception($"Workspace parent {parentId}: {problem}.", inner)
{
    public long ParentId { get; } = parentId;

    public WorkspaceParentProblem Problem { get; } = problem;
}

public sealed class WorkspaceNodeNotFoundException(long nodeId)
    : Exception($"Workspace node {nodeId} does not exist.")
{
    public long NodeId { get; } = nodeId;
}

public sealed class WorkspaceMoveCycleException(long nodeId, long targetId)
    : Exception($"Workspace node {nodeId} cannot be moved into {targetId}, which is itself or inside it.")
{
    public long NodeId { get; } = nodeId;

    public long TargetId { get; } = targetId;
}
