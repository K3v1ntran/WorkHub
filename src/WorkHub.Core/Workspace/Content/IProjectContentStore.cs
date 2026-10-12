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

    /// <summary>
    /// The Project name and folder breadcrumb of a container, from one read transaction. Null when the Project or the folder
    /// no longer exists, or the folder belongs to another Project. Never writes.
    /// </summary>
    Task<ProjectContainerPath?> GetContainerPathAsync(long projectNodeId, long? folderId, CancellationToken cancellationToken = default);

    /// <summary>All folders of the Project with their paths, except <paramref name="movingFolderId"/> and its subtree.</summary>
    Task<IReadOnlyList<WorkspaceFolderPath>> ListFolderMoveTargetsAsync(long projectNodeId, long? movingFolderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a folder (with its subtree) within its Project, in ONE <c>BEGIN IMMEDIATE</c> transaction, following the
    /// revision 3 §8.1 sequence. <paramref name="expectedParentFolderId"/> is the container the caller observed.
    /// </summary>
    Task<ContentMoveOutcome> MoveFolderAsync(long folderId, long? expectedParentFolderId, long? targetParentFolderId, CancellationToken cancellationToken = default);

    /// <summary>Moves a link within its Project; same sequence as folders without the ancestry step.</summary>
    Task<ContentMoveOutcome> MoveLinkAsync(long linkId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default);

    // ---- Notes (schema v5) -------------------------------------------------------------------------------------------

    Task<ProjectNote?> GetNoteAsync(long noteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The latest persisted state of the given notes, read on one connection in one read transaction (a consistent
    /// snapshot). Notes that no longer exist are simply absent from the result. Never writes.
    /// </summary>
    Task<IReadOnlyList<ProjectNote>> GetNotesAsync(IReadOnlyCollection<long> noteIds, CancellationToken cancellationToken = default);

    /// <summary>Inserts a note (Revision 1) after checking the Project and folder inside one <c>BEGIN IMMEDIATE</c> transaction.</summary>
    /// <exception cref="ContentContainerException"/>
    Task<ProjectNote> CreateNoteAsync(long projectNodeId, long? folderId, ProjectNoteDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes ONLY Title, Content, Revision + 1 and UpdatedAt, guarded by <paramref name="expectedRevision"/>; when nothing
    /// was updated, NotFound vs Conflict is classified inside the same transaction. Never writes the container.
    /// </summary>
    Task<NoteStoreSaveResult> SaveNoteAsync(long noteId, long expectedRevision, ProjectNoteDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovery: in ONE <c>BEGIN IMMEDIATE</c> transaction resolves the destination — the original note's CURRENT container
    /// if it still exists, otherwise <paramref name="lastKnownFolderId"/> if that container still exists in
    /// <paramref name="projectNodeId"/> (NULL = the Project root) — and inserts a new note. Never modifies the original and
    /// never falls back to the root for a missing folder.
    /// </summary>
    /// <exception cref="ContentContainerException">No valid destination; nothing was written.</exception>
    Task<ProjectNote> SaveNoteAsNewAsync(long originalNoteId, long projectNodeId, long? lastKnownFolderId, ProjectNoteDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Moves a note within its Project (FolderId and UpdatedAt only; Revision unchanged); same sequence as links.</summary>
    Task<ContentMoveOutcome> MoveNoteAsync(long noteId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default);
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
