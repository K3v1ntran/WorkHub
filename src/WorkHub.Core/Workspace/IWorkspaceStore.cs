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

    // ---- Phase 19: Bulk Archive of Projects (additive; ArchiveProjectAsync above is unchanged) -------------------------------

    /// <summary>
    /// The Bulk Archive dialog's list: the live Projects directly in <paramref name="parentId"/> (null = the top level), in ONE
    /// read transaction with the place's effective state (a place that is not live lists nothing), at most
    /// <see cref="BulkArchiveCandidates.MaxRows"/>. Never writes.
    /// </summary>
    Task<BulkArchiveCandidates> ListBulkArchiveCandidatesAsync(long? parentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Location-bound Archive (proposal §4.3): in ONE <c>BEGIN IMMEDIATE</c> transaction, in this order — node missing →
    /// NotFound; not a Project → NotAProject; current parent ≠ <see cref="BulkArchiveTarget.ExpectedParentId"/> → MovedElsewhere;
    /// in the Trash → ProjectTrashed; already archived → AlreadyArchived; the (confirmed) parent chain not live →
    /// LocationNotLive; otherwise the same guarded status/serial UPDATE as <see cref="ArchiveProjectAsync"/>. Every non-Archived
    /// outcome writes nothing and creates no event.
    /// </summary>
    Task<BulkArchiveResult> ArchiveProjectInPlaceAsync(BulkArchiveTarget target, CancellationToken cancellationToken = default);

    // ---- Phase 20: Bulk Unarchive of Projects (additive; UnarchiveProjectAsync above is unchanged and state-based) ---------------

    /// <summary>
    /// The Bulk Unarchive dialog's list: the archived Project roots the Archived panel lists (Folders excluded), each with its
    /// <c>ArchiveSerial</c>, from ONE read transaction, in the panel's order, at most <see cref="BulkUnarchiveCandidates.MaxRows"/>.
    /// Never writes.
    /// </summary>
    Task<BulkUnarchiveCandidates> ListBulkUnarchiveCandidatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Serial-bound Unarchive (proposal §4.3.2): in ONE <c>BEGIN IMMEDIATE</c> transaction, in this order — node missing → NotFound;
    /// not a Project → NotAProject; in the Trash → ProjectTrashed; not archived → NotArchived; archived with a serial other than
    /// <see cref="BulkUnarchiveTarget.ExpectedSerial"/> → ArchivedAgain; an ancestor trashed / archived → ParentTrashed /
    /// ParentArchived; otherwise sets the (editable) status, guarded by <c>Status = 'Archived' AND ArchiveSerial = expected</c>.
    /// <c>ArchiveSerial</c> is never written. Every non-Unarchived outcome writes nothing.
    /// </summary>
    Task<BulkUnarchiveResult> UnarchiveProjectBoundAsync(BulkUnarchiveTarget target, ProjectStatus status, CancellationToken cancellationToken = default);

    // ---- Project Tags (schema v10; Phase 17 proposal R2b §4.2) --------------------------------------------------------------------

    /// <summary>
    /// Tag suggestions: one display name per distinct key over the tags of Projects that are not in the Workspace Trash (directly
    /// or through an ancestor Folder; archived ones are included), each in its most used stored spelling (ties: ordinal), in key
    /// order, at most <paramref name="limit"/>. <paramref name="excludeProjectNodeId"/> (the Project being edited) is left out BEFORE the
    /// spellings are counted and the limit applied; null (a new Project) uses every eligible Project.
    /// </summary>
    Task<IReadOnlyList<string>> ListTagSuggestionsAsync(long? excludeProjectNodeId = null, int limit = 200, CancellationToken cancellationToken = default);

    // ---- Phase 21: rename and merge of a tag across Projects (additive; schema v10, no migration) ----------------------------------

    /// <summary>
    /// The Manage tags list from ONE read transaction: one row per tag key in use, its most used spelling (ties: ordinal), the
    /// number of Projects carrying it that are not in the Trash (live or archived, effective state) and of those in the Trash
    /// (directly or through a Folder), in key order. Carries no token. Never writes.
    /// </summary>
    Task<IReadOnlyList<TagInUse>> ListTagsInUseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The plan and token (<c>WH-TAGPLAN/1</c>) of <paramref name="request"/> from ONE read transaction (proposal §3.3.1). The new
    /// name must already be a valid tag name (<see cref="ProjectTagName.TryNormalize"/>). Never writes.
    /// </summary>
    /// <exception cref="ArgumentException">The new name is not a valid tag name.</exception>
    Task<TagChangePreview> PrepareTagChangeAsync(TagChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// In ONE <c>BEGIN IMMEDIATE</c> transaction (proposal §3.3.2): rebuilds the plan with the same function as
    /// <see cref="PrepareTagChangeAsync"/> and compares its token with <paramref name="token"/> FIRST (a difference is
    /// <see cref="TagChangeOutcome.Stale"/> with the fresh plan); then an empty plan is NothingToRename and an all-spelled spelling
    /// change is Unchanged; otherwise all deletes, then all inserts, keeping each source row's stored <c>CreatedAt</c> text. Projects
    /// in the Trash (directly or through a Folder) are never written; <c>Projects</c> / <c>WorkspaceNodes</c> are never written. Any
    /// error rolls everything back. Every non-committed outcome writes nothing.
    /// </summary>
    Task<TagChangeResult> ApplyTagChangeAsync(TagChangeRequest request, string token, CancellationToken cancellationToken = default);

    // ---- Phase 22: delete a tag everywhere (additive; schema v10, no migration) -------------------------------------------------

    /// <summary>
    /// The delete plan and token (<c>WH-TAGDEL/1</c>) of <paramref name="fromKey"/> from ONE read transaction (proposal §3.3). A key
    /// nobody carries yields an empty plan (not confirmable) with its own token. Never writes.
    /// </summary>
    /// <exception cref="ArgumentException">The key is empty or white space.</exception>
    Task<TagDeletePreview> PrepareTagDeleteAsync(string fromKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// In ONE <c>BEGIN IMMEDIATE</c> transaction (proposal §3.3): rebuilds the plan with the same function as
    /// <see cref="PrepareTagDeleteAsync"/> and compares its token with <paramref name="token"/> FIRST (any difference, also on an empty
    /// plan, is <see cref="TagDeleteOutcome.Stale"/> with the fresh plan); then a plan without eligible rows is NothingToDelete;
    /// otherwise deletes every eligible row of the key. Projects in the Trash (directly or through a Folder) keep the key;
    /// <c>Projects</c> / <c>WorkspaceNodes</c> are never written. Any error rolls everything back. Every non-Deleted outcome writes
    /// nothing.
    /// </summary>
    /// <exception cref="ArgumentException">The key is empty or white space.</exception>
    Task<TagDeleteResult> ApplyTagDeleteAsync(string fromKey, string token, CancellationToken cancellationToken = default);

    // ---- Phase 24: Projects with this tag (additive, read only; schema v10, no migration) -------------------------------------------

    /// <summary>
    /// The Projects carrying <paramref name="nameKey"/> from ONE deferred read transaction (proposal §3.3): every <c>ProjectTags</c> row
    /// of the key with the Project's name, folder path, stored spelling and effective state (the <c>NodeLifecycle</c> of tag
    /// eligibility). The totals are counted over all rows BEFORE the cap; the rows are sorted by <see cref="TaggedProjectOrder"/> and
    /// the first <paramref name="limit"/> are kept. An unknown key yields no rows and zero totals. Never writes; reads no content.
    /// </summary>
    Task<TaggedProjectsResult> ListProjectsWithTagAsync(string nameKey, int limit, CancellationToken cancellationToken = default);

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
