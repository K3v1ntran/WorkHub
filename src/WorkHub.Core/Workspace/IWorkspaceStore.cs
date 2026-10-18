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

    // ---- Workspace Trash (schema v7) -----------------------------------------------------------------------------------
    // Listings above show ACTIVE nodes only. Rename, UpdateProject and Move of a trashed Project throw
    // ProjectTrashedException and write nothing. No operation deletes a Project; only RemoveEmptyFolderAsync deletes a row.

    /// <summary>
    /// In ONE <c>BEGIN IMMEDIATE</c> transaction sets only the node's marker (identity-based: wherever the Project is now).
    /// Every non-Trashed outcome writes nothing.
    /// </summary>
    Task<ProjectTrashResult> TrashProjectAsync(long nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// In ONE <c>BEGIN IMMEDIATE</c> transaction clears only the marker, in the ORIGINAL parent (by Id, wherever it is now);
    /// an active sibling with the same name refuses. Nothing is created, renamed or moved.
    /// </summary>
    Task<ProjectRestoreResult> RestoreProjectAsync(long nodeId, CancellationToken cancellationToken = default);

    /// <summary>Trashed Projects, newest first, with their original parents' current paths, from one read transaction.</summary>
    Task<IReadOnlyList<WorkspaceTrashEntry>> ListTrashAsync(CancellationToken cancellationToken = default);

    Task<int> CountTrashAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes a Workspace Folder that has no child row at all (no active node, no trashed Project), in ONE
    /// <c>BEGIN IMMEDIATE</c> transaction. Metadata only; nothing on disk is touched.
    /// </summary>
    Task<FolderRemoveResult> RemoveEmptyFolderAsync(long nodeId, CancellationToken cancellationToken = default);

    /// <summary>The node's current state (trashed included); null when it no longer exists. Never writes.</summary>
    Task<WorkspaceNodeState?> GetNodeStateAsync(long nodeId, CancellationToken cancellationToken = default);

    // ---- Project Archive (schema v8) -----------------------------------------------------------------------------------
    // Listings above show VISIBLE nodes only (active, not archived). Rename, UpdateProject and Move of an archived Project
    // throw ProjectArchivedException, and TrashProject refuses it, writing nothing. Archived Projects keep their sibling name.

    /// <summary>
    /// In ONE <c>BEGIN IMMEDIATE</c> transaction sets <c>Status = 'Archived'</c> and raises <c>ArchiveSerial</c> by one (nothing
    /// else changes: not the node, its name, parent or UpdatedAt). Every non-Archived outcome writes nothing.
    /// </summary>
    Task<ProjectArchiveResult> ArchiveProjectAsync(long nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// State-based Unarchive: in ONE <c>BEGIN IMMEDIATE</c> transaction sets the given (editable) status on an archived
    /// Project, wherever it is now; <c>ArchiveSerial</c> is unchanged. Every non-Unarchived outcome writes nothing.
    /// </summary>
    Task<ProjectUnarchiveResult> UnarchiveProjectAsync(long nodeId, ProjectStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event-specific Undo: in ONE <c>BEGIN IMMEDIATE</c> transaction restores <see cref="ArchiveEvent.PreviousStatus"/> only if
    /// the Project is still archived with exactly <see cref="ArchiveEvent.Serial"/>; <c>ArchiveSerial</c> is unchanged.
    /// Every non-Undone outcome writes nothing.
    /// </summary>
    Task<ArchiveUndoResult> UndoArchiveAsync(ArchiveEvent archiveEvent, CancellationToken cancellationToken = default);

    /// <summary>Archived Projects that are not in the Trash, by name, with their parents' current paths, from one read transaction.</summary>
    Task<IReadOnlyList<ArchivedProjectEntry>> ListArchivedAsync(CancellationToken cancellationToken = default);

    Task<int> CountArchivedAsync(CancellationToken cancellationToken = default);
}

public sealed class DuplicateWorkspaceNameException(string name, Exception? inner = null)
    : Exception($"'{name}' already exists in this location.", inner)
{
    public string Name { get; } = name;

    /// <summary>The sibling holding the name is an archived Project (hidden from the tree; Phase 12 A6).</summary>
    public bool BlockerArchived { get; init; }
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
