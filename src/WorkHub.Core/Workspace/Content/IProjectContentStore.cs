namespace WorkHub.Core.Workspace.Content;

/// <summary>
/// Persistence for Project content (schema v4: ProjectFolders, ProjectLinks; v5: notes; v6: Trash). Implementations run
/// I/O off the caller's thread and make every multi-step change atomic. Nothing here touches files or folders on disk.
/// Listings, Move destinations and writes see ACTIVE rows only; trashed rows are read-only and are reported distinctly
/// (<see cref="ContentContainerProblem.FolderTrashed"/>, <see cref="ContentTrashedException"/>, the move/save outcomes and
/// the <c>IsTrashed</c> flag of single-item reads).
/// Names and drafts passed in are already normalized by <see cref="ProjectContentService"/>.
/// </summary>
public interface IProjectContentStore
{
    /// <summary>
    /// Direct ACTIVE child folders, links and notes of a container (null folder = Project root), unsorted, plus its
    /// breadcrumb. A trashed folder is <see cref="ContentContainerProblem.FolderTrashed"/>.
    /// </summary>
    Task<ProjectContentListResult> ListAsync(long projectNodeId, long? folderId, CancellationToken cancellationToken = default);

    /// <exception cref="ContentContainerException"/>
    /// <exception cref="DuplicateProjectFolderNameException"/>
    Task<ProjectFolder> CreateFolderAsync(long projectNodeId, long? parentFolderId, string name, CancellationToken cancellationToken = default);

    /// <returns>The stored folder, or null when it no longer exists.</returns>
    /// <exception cref="DuplicateProjectFolderNameException"/>
    /// <exception cref="ContentTrashedException"/>
    Task<ProjectFolder?> RenameFolderAsync(long folderId, string name, CancellationToken cancellationToken = default);

    /// <exception cref="ContentContainerException"/>
    Task<ProjectLink> CreateLinkAsync(long projectNodeId, long? folderId, ProjectLinkDraft draft, CancellationToken cancellationToken = default);

    /// <returns>The stored link, or null when it no longer exists.</returns>
    /// <exception cref="ContentTrashedException"/>
    Task<ProjectLink?> UpdateLinkAsync(long linkId, ProjectLinkDraft draft, CancellationToken cancellationToken = default);

    /// <summary>The link, active or trashed (<see cref="ProjectLink.IsTrashed"/>); null when it no longer exists.</summary>
    Task<ProjectLink?> GetLinkAsync(long linkId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The Project name and folder breadcrumb of a container, from one read transaction. Null when the Project or the folder
    /// no longer exists, or the folder belongs to another Project. Never writes.
    /// </summary>
    Task<ProjectContainerPath?> GetContainerPathAsync(long projectNodeId, long? folderId, CancellationToken cancellationToken = default);

    /// <summary>All ACTIVE folders of the Project with their paths, except <paramref name="movingFolderId"/> and its subtree.</summary>
    Task<IReadOnlyList<WorkspaceFolderPath>> ListFolderMoveTargetsAsync(long projectNodeId, long? movingFolderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a folder (with its subtree) within its Project, in ONE <c>BEGIN IMMEDIATE</c> transaction, following the
    /// revision 3 §8.1 sequence. <paramref name="expectedParentFolderId"/> is the container the caller observed.
    /// </summary>
    Task<ContentMoveOutcome> MoveFolderAsync(long folderId, long? expectedParentFolderId, long? targetParentFolderId, CancellationToken cancellationToken = default);

    /// <summary>Moves a link within its Project; same sequence as folders without the ancestry step.</summary>
    Task<ContentMoveOutcome> MoveLinkAsync(long linkId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default);

    // ---- Notes (schema v5) -------------------------------------------------------------------------------------------

    /// <summary>The note, active or trashed (<see cref="ProjectNote.IsTrashed"/>); null when it no longer exists.</summary>
    Task<ProjectNote?> GetNoteAsync(long noteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest persisted state of the given notes, read on one connection in one read transaction (a consistent
    /// snapshot). Notes that no longer exist are simply absent from the result; trashed notes are returned with
    /// <see cref="ProjectNote.IsTrashed"/>. Never writes.
    /// </summary>
    Task<IReadOnlyList<ProjectNote>> GetNotesAsync(IReadOnlyCollection<long> noteIds, CancellationToken cancellationToken = default);

    /// <summary>Inserts a note (Revision 1) after checking the Project and folder inside one <c>BEGIN IMMEDIATE</c> transaction.</summary>
    /// <exception cref="ContentContainerException"/>
    Task<ProjectNote> CreateNoteAsync(long projectNodeId, long? folderId, ProjectNoteDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes ONLY Title, Content, Revision + 1 and UpdatedAt, guarded by <paramref name="expectedRevision"/>; when nothing
    /// was updated, NotFound / Trashed / Conflict is classified inside the same transaction. Never writes the container
    /// and never writes a trashed note.
    /// </summary>
    Task<NoteStoreSaveResult> SaveNoteAsync(long noteId, long expectedRevision, ProjectNoteDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovery: in ONE <c>BEGIN IMMEDIATE</c> transaction resolves the destination — the original note's CURRENT container
    /// if the original still exists (even when the original is in the Trash), otherwise <paramref name="lastKnownFolderId"/>
    /// if that container still exists in <paramref name="projectNodeId"/> (NULL = the Project root) — and inserts a new note
    /// there only if that container is ACTIVE. Never modifies the original and never falls back to the root for a missing
    /// or trashed folder.
    /// </summary>
    /// <exception cref="ContentContainerException">No valid destination; nothing was written.</exception>
    Task<ProjectNote> SaveNoteAsNewAsync(long originalNoteId, long projectNodeId, long? lastKnownFolderId, ProjectNoteDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Moves a note within its Project (FolderId and UpdatedAt only; Revision unchanged); same sequence as links.</summary>
    Task<ContentMoveOutcome> MoveNoteAsync(long noteId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default);

    // ---- Trash (schema v6) -------------------------------------------------------------------------------------------

    /// <summary>The item's current Project, container and Trash state (trashed rows included); null when it no longer exists. Never writes.</summary>
    Task<ContentState?> GetContentStateAsync(ContentRef item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshot for the confirmation text, from one read transaction: whether the item exists, is already in the Trash, and
    /// how many active items it currently holds (the item itself included; a folder's active subtree). Never writes.
    /// </summary>
    Task<TrashCount> CountTrashSetAsync(ContentRef item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the item (a folder with its CURRENT active subtree) to the Trash in ONE <c>BEGIN IMMEDIATE</c> transaction
    /// (identity-based, T11): inserts one entry whose <c>ItemCount</c> is the size of the set determined inside the
    /// transaction and writes ONLY the markers (no container, name, content, revision or timestamp change). Rows already in
    /// the Trash keep their own entry. Every non-Trashed outcome writes nothing.
    /// </summary>
    Task<TrashResult> TrashAsync(ContentRef item, CancellationToken cancellationToken = default);

    /// <summary>The Project's Trash entries, newest first, with each root's container path, from one read transaction.</summary>
    Task<IReadOnlyList<TrashEntry>> ListTrashAsync(long projectNodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores one entry to the container its root was trashed from (by Id, at its current location) in ONE
    /// <c>BEGIN IMMEDIATE</c> transaction: clears the entry's markers, reads the root back and deletes the entry. A trashed
    /// or missing container, or an active sibling folder with the same name, refuses the whole restore; nothing is created,
    /// renamed or moved.
    /// </summary>
    Task<RestoreResult> RestoreAsync(long entryId, CancellationToken cancellationToken = default);
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
