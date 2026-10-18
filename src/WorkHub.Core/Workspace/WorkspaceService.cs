namespace WorkHub.Core.Workspace;

/// <summary>
/// Workspace use cases. The UI calls only this; it never touches SQL.
/// Workspace is a logical layer: creating, renaming or moving a Folder or Project changes WorkHub metadata only,
/// never files or folders on disk. Independent of Repository and Owner Manager.
/// </summary>
public sealed class WorkspaceService(IWorkspaceStore store)
{
    public const string NameField = ProjectDraftValidator.Fields.Name;

    /// <summary>Children in tree order (Folders first, then name).</summary>
    public async Task<IReadOnlyList<WorkspaceNode>> ListChildrenAsync(long? parentId, CancellationToken cancellationToken = default)
    {
        var nodes = await store.ListChildrenAsync(parentId, cancellationToken).ConfigureAwait(false);
        return nodes.OrderBy(n => (n.Type, n.Name, n.Id), WorkspaceNodeOrder.Instance).ToList();
    }

    public async Task<WorkspaceResult<WorkspaceNode>> CreateFolderAsync(long? parentId, string? name, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceName.TryNormalize(name, out var normalized, out var error))
            return WorkspaceResult<WorkspaceNode>.Failed(error!, NameField);

        try
        {
            return WorkspaceResult<WorkspaceNode>.Ok(await store.CreateFolderAsync(parentId, normalized, cancellationToken).ConfigureAwait(false));
        }
        catch (DuplicateWorkspaceNameException ex)
        {
            return WorkspaceResult<WorkspaceNode>.Failed(DuplicateName(normalized, ex.BlockerArchived), NameField, WorkspaceFailure.DuplicateName);
        }
        catch (WorkspaceParentException ex)
        {
            return ParentFailed<WorkspaceNode>(ex.Problem);
        }
    }

    /// <summary>Creates the Project node and its details atomically, with a new immutable ProjectId.</summary>
    public async Task<WorkspaceResult<WorkspaceNode>> CreateProjectAsync(long? parentId, ProjectDraft draft, CancellationToken cancellationToken = default)
    {
        var (normalized, field, error) = ProjectDraftValidator.Validate(draft);
        if (normalized is null)
            return WorkspaceResult<WorkspaceNode>.Failed(error!, field);

        try
        {
            var node = await store.CreateProjectAsync(parentId, NewProjectId(), normalized, cancellationToken).ConfigureAwait(false);
            return WorkspaceResult<WorkspaceNode>.Ok(node);
        }
        catch (DuplicateWorkspaceNameException ex)
        {
            return WorkspaceResult<WorkspaceNode>.Failed(DuplicateName(normalized.Name!, ex.BlockerArchived), NameField, WorkspaceFailure.DuplicateName);
        }
        catch (WorkspaceParentException ex)
        {
            return ParentFailed<WorkspaceNode>(ex.Problem);
        }
    }

    /// <returns>The stored (trimmed) name on success.</returns>
    public async Task<WorkspaceResult<string>> RenameAsync(long nodeId, string? name, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceName.TryNormalize(name, out var normalized, out var error))
            return WorkspaceResult<string>.Failed(error!, NameField);

        try
        {
            return await store.RenameAsync(nodeId, normalized, cancellationToken).ConfigureAwait(false)
                ? WorkspaceResult<string>.Ok(normalized)
                : WorkspaceResult<string>.Failed(NoLongerExists, failure: WorkspaceFailure.NodeNotFound);
        }
        catch (DuplicateWorkspaceNameException ex)
        {
            return WorkspaceResult<string>.Failed(DuplicateName(normalized, ex.BlockerArchived), NameField, WorkspaceFailure.DuplicateName);
        }
        catch (ProjectTrashedException)
        {
            return WorkspaceResult<string>.Failed(ProjectInTrash, failure: WorkspaceFailure.ProjectTrashed);
        }
        catch (ProjectArchivedException)
        {
            return WorkspaceResult<string>.Failed(ProjectIsArchived, failure: WorkspaceFailure.ProjectArchived);
        }
    }

    /// <summary>The Project's details (<see cref="ProjectDetails.IsTrashed"/> when it is in the Trash); null when it no longer exists.</summary>
    public Task<ProjectDetails?> GetProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        store.GetProjectAsync(nodeId, cancellationToken);

    /// <returns>The stored Project state on success.</returns>
    public async Task<WorkspaceResult<ProjectDetails>> UpdateProjectAsync(long nodeId, ProjectDraft draft, CancellationToken cancellationToken = default)
    {
        var (normalized, field, error) = ProjectDraftValidator.Validate(draft);
        if (normalized is null)
            return WorkspaceResult<ProjectDetails>.Failed(error!, field);

        try
        {
            return await store.UpdateProjectAsync(nodeId, normalized, cancellationToken).ConfigureAwait(false) is { } saved
                ? WorkspaceResult<ProjectDetails>.Ok(saved)
                : WorkspaceResult<ProjectDetails>.Failed(NoLongerExists, failure: WorkspaceFailure.NodeNotFound);
        }
        catch (DuplicateWorkspaceNameException ex)
        {
            return WorkspaceResult<ProjectDetails>.Failed(DuplicateName(normalized.Name!, ex.BlockerArchived), NameField, WorkspaceFailure.DuplicateName);
        }
        catch (ProjectTrashedException)
        {
            return WorkspaceResult<ProjectDetails>.Failed(ProjectInTrash, failure: WorkspaceFailure.ProjectTrashed);
        }
        catch (ProjectArchivedException)
        {
            return WorkspaceResult<ProjectDetails>.Failed(ProjectIsArchived, failure: WorkspaceFailure.ProjectArchived);
        }
    }

    /// <summary>Folders the node may move into (its own subtree excluded), sorted by path.</summary>
    public async Task<IReadOnlyList<WorkspaceFolderPath>> ListMoveTargetsAsync(long nodeId, CancellationToken cancellationToken = default)
    {
        var folders = await store.ListMoveTargetsAsync(nodeId, cancellationToken).ConfigureAwait(false);
        return folders.OrderBy(f => (WorkspaceNodeType.Folder, f.Path, f.Id), WorkspaceNodeOrder.Instance).ToList();
    }

    /// <summary>Moves a node under a Folder (or to the root). Metadata only; nothing on disk moves.</summary>
    /// <returns>True when moved, false when it was already there (nothing written).</returns>
    public async Task<WorkspaceResult<bool>> MoveAsync(long nodeId, long? targetParentId, CancellationToken cancellationToken = default)
    {
        try
        {
            return WorkspaceResult<bool>.Ok(await store.MoveAsync(nodeId, targetParentId, cancellationToken).ConfigureAwait(false));
        }
        catch (WorkspaceNodeNotFoundException)
        {
            return WorkspaceResult<bool>.Failed(NoLongerExists, failure: WorkspaceFailure.NodeNotFound);
        }
        catch (WorkspaceParentException ex)
        {
            return ParentFailed<bool>(ex.Problem);
        }
        catch (WorkspaceMoveCycleException)
        {
            return WorkspaceResult<bool>.Failed("A folder cannot be moved into itself or into one of its own subfolders.", failure: WorkspaceFailure.MoveCycle);
        }
        catch (DuplicateWorkspaceNameException ex)
        {
            return WorkspaceResult<bool>.Failed(ex.BlockerArchived
                ? $"The destination already contains an archived project named '{ex.Name}'. Unarchive or rename it first."
                : $"The destination already contains a folder or project named '{ex.Name}'.", failure: WorkspaceFailure.DuplicateName);
        }
        catch (ProjectTrashedException)
        {
            return WorkspaceResult<bool>.Failed(ProjectInTrash, failure: WorkspaceFailure.ProjectTrashed);
        }
        catch (ProjectArchivedException)
        {
            return WorkspaceResult<bool>.Failed(ProjectIsArchived, failure: WorkspaceFailure.ProjectArchived);
        }
    }

    // ---- Workspace Trash -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves a Project to the Workspace Trash: only its marker changes. Its details, folders, links, notes and Project-level
    /// Trash entries stay exactly as they are (read-only while trashed). Nothing on disk is touched.
    /// </summary>
    public Task<ProjectTrashResult> TrashProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        store.TrashProjectAsync(nodeId, cancellationToken);

    /// <summary>Restores a trashed Project into its original parent, or refuses (nothing is renamed, created or moved).</summary>
    public Task<ProjectRestoreResult> RestoreProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        store.RestoreProjectAsync(nodeId, cancellationToken);

    public Task<IReadOnlyList<WorkspaceTrashEntry>> ListTrashAsync(CancellationToken cancellationToken = default) =>
        store.ListTrashAsync(cancellationToken);

    public Task<int> CountTrashAsync(CancellationToken cancellationToken = default) =>
        store.CountTrashAsync(cancellationToken);

    /// <summary>Permanently removes a truly empty Workspace Folder (metadata only).</summary>
    public Task<FolderRemoveResult> RemoveEmptyFolderAsync(long nodeId, CancellationToken cancellationToken = default) =>
        store.RemoveEmptyFolderAsync(nodeId, cancellationToken);

    public Task<WorkspaceNodeState?> GetNodeStateAsync(long nodeId, CancellationToken cancellationToken = default) =>
        store.GetNodeStateAsync(nodeId, cancellationToken);

    // ---- Project Archive (schema v8) -----------------------------------------------------------------------------------

    /// <summary>
    /// Archives a Project: only its status (→ Archived) and archive serial change. Its node, name, parent, details, folders,
    /// links, notes and Project-level Trash stay exactly as they are; its content stays writable (A3). Nothing on disk is touched.
    /// </summary>
    public Task<ProjectArchiveResult> ArchiveProjectAsync(long nodeId, CancellationToken cancellationToken = default) =>
        store.ArchiveProjectAsync(nodeId, cancellationToken);

    /// <summary>State-based Unarchive to a status the user chose (one of <see cref="ProjectStatuses.Editable"/>).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The status is not editable; nothing was called or written.</exception>
    public Task<ProjectUnarchiveResult> UnarchiveProjectAsync(long nodeId, ProjectStatus status, CancellationToken cancellationToken = default)
    {
        if (!ProjectStatuses.IsEditable(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unarchive needs an editable status.");
        return store.UnarchiveProjectAsync(nodeId, status, cancellationToken);
    }

    /// <summary>Undoes exactly one archive event (its serial), restoring the status it had before.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a committed archive event; nothing was called or written.</exception>
    public Task<ArchiveUndoResult> UndoArchiveAsync(ArchiveEvent archiveEvent, CancellationToken cancellationToken = default)
    {
        if (archiveEvent.Serial < 1)
            throw new ArgumentOutOfRangeException(nameof(archiveEvent), archiveEvent.Serial, "An archive event has a serial of 1 or more.");
        if (!ProjectStatuses.IsEditable(archiveEvent.PreviousStatus))
            throw new ArgumentOutOfRangeException(nameof(archiveEvent), archiveEvent.PreviousStatus, "The previous status must be editable.");
        return store.UndoArchiveAsync(archiveEvent, cancellationToken);
    }

    public Task<IReadOnlyList<ArchivedProjectEntry>> ListArchivedAsync(CancellationToken cancellationToken = default) =>
        store.ListArchivedAsync(cancellationToken);

    public Task<int> CountArchivedAsync(CancellationToken cancellationToken = default) =>
        store.CountArchivedAsync(cancellationToken);

    internal static string NewProjectId() => Guid.NewGuid().ToString("N");

    public const string ProjectInTrash = "This project is in the Trash.";

    public const string ProjectIsArchived = "This project is archived. Nothing was changed.";

    private const string NoLongerExists = "This folder or project no longer exists.";

    private static string DuplicateName(string name, bool blockerArchived) => blockerArchived
        ? $"An archived project named '{name}' already exists here. Unarchive it or choose another name."
        : $"A folder or project named '{name}' already exists here (names are compared ignoring case and width).";

    private static WorkspaceResult<T> ParentFailed<T>(WorkspaceParentProblem problem) => problem == WorkspaceParentProblem.NotFound
        ? WorkspaceResult<T>.Failed("The destination folder no longer exists.", failure: WorkspaceFailure.ParentNotFound)
        : WorkspaceResult<T>.Failed("Only folders can contain folders and projects.", failure: WorkspaceFailure.ParentNotAFolder);
}
