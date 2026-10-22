namespace WorkHub.Core.Workspace;

/// <summary>
/// Persistence for the logical Workspace tree. Implementations run I/O off the caller's thread and make every
/// multi-row change atomic. Nothing here ever touches files or folders on disk.
/// Names passed in are already normalized (<see cref="WorkspaceName.TryNormalize"/>); keys are computed with
/// <see cref="WorkspaceName.Key"/>.
/// </summary>
public interface IWorkspaceStore
{
    /// <summary>
    /// The shown direct children of <paramref name="parentId"/> (null = root), unsorted, read in ONE read transaction with the
    /// parent's effective state (schema v9, §7.9 C3): a parent that is archived or trashed, itself or through an ancestor, is
    /// <see cref="ChildrenReadStatus.ParentNotLive"/> and lists nothing.
    /// </summary>
    Task<WorkspaceChildrenResult> ListChildrenAsync(long? parentId, CancellationToken cancellationToken = default);

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

    // ---- Folder Archive and Folder Trash (schema v9; Phase 13 proposal revision 4 §7) ------------------------------------------
    // M1: one marker on the Folder that roots the subtree; descendants are never written. Every Workspace node write above
    // requires the node's EFFECTIVE state to be Live (an archived or trashed ancestor refuses it: WorkspaceNotLiveException or
    // the AncestorNotLive / ParentArchived / ParentTrashed outcomes). The Trash and Archived listings above list the OUTERMOST
    // roots of both kinds; their counts are the number of rows they list.

    /// <summary>
    /// The confirmation (§7.4) for archiving or trashing the Folder, from one read transaction: the preview and its token, or the
    /// refusal that makes a dialog pointless. Never writes.
    /// </summary>
    Task<FolderLifecyclePreview> PrepareFolderLifecycleAsync(long nodeId, FolderLifecycleKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// In ONE <c>BEGIN IMMEDIATE</c> transaction: requires a live Folder under a live chain, recomputes the confirmation and
    /// compares its token with <paramref name="confirmationToken"/> (a mismatch is <see cref="FolderLifecycleOutcome.ConfirmationStale"/>
    /// with the fresh preview), then writes ONLY <c>FolderArchivedAt</c> and <c>LifecycleSerial + 1</c> on that Folder. Every
    /// non-Committed outcome writes nothing.
    /// </summary>
    Task<FolderLifecycleResult> ArchiveFolderAsync(long nodeId, string confirmationToken, CancellationToken cancellationToken = default);

    /// <summary>As <see cref="ArchiveFolderAsync"/>, writing <c>FolderTrashedAt</c>; an archived Folder is refused (D15). The name is freed.</summary>
    Task<FolderLifecycleResult> TrashFolderAsync(long nodeId, string confirmationToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// State-based: in ONE <c>BEGIN IMMEDIATE</c> transaction clears the Folder's archive marker if it is listed in the Archived
    /// panel (archived, no ancestor archived or trashed). Descendant markers are untouched; the serial is unchanged.
    /// </summary>
    Task<FolderClearResult> UnarchiveFolderAsync(long nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Strict Restore: in ONE <c>BEGIN IMMEDIATE</c> transaction clears the Folder's trash marker at its ORIGINAL parent and name,
    /// only if that chain is live and no live sibling holds the name. Nothing is created, renamed or moved.
    /// </summary>
    Task<FolderClearResult> RestoreFolderAsync(long nodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event-specific Undo (D13): clears the marker of <paramref name="lifecycleEvent"/> only if the Folder still carries it with
    /// exactly that serial, under the same rules as Unarchive / Restore. Every non-Cleared outcome writes nothing.
    /// </summary>
    Task<FolderClearResult> UndoFolderLifecycleAsync(FolderLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default);
}

public sealed class DuplicateWorkspaceNameException(string name, Exception? inner = null)
    : Exception($"'{name}' already exists in this location.", inner)
{
    public string Name { get; } = name;

    /// <summary>The sibling holding the name is an archived Project (hidden from the tree; Phase 12 A6).</summary>
    public bool BlockerArchived { get; init; }

    /// <summary>The sibling holding the name is an archived Folder (hidden from the tree; D10: its name stays reserved).</summary>
    public bool BlockerArchivedFolder { get; init; }
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
