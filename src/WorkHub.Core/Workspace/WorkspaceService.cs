namespace WorkHub.Core.Workspace;

/// <summary>
/// Workspace use cases. The UI calls only this; it never touches SQL.
/// Workspace is a logical layer: creating, renaming or moving a Folder or Project changes WorkHub metadata only,
/// never files or folders on disk. Independent of Repository and Owner Manager.
/// </summary>
public sealed class WorkspaceService(IWorkspaceStore store)
{
    public const string NameField = ProjectDraftValidator.Fields.Name;

    /// <summary>
    /// Children in tree order (Folders first, then name), or the typed reason there are none to show: the parent is archived or
    /// in the Trash (itself or through an ancestor), or gone (§7.9 C3).
    /// </summary>
    public async Task<WorkspaceChildrenResult> ListChildrenAsync(long? parentId, CancellationToken cancellationToken = default)
    {
        var result = await store.ListChildrenAsync(parentId, cancellationToken).ConfigureAwait(false);
        return result.Status == ChildrenReadStatus.Listed
            ? result with { Nodes = result.Nodes.OrderBy(n => (n.Type, n.Name, n.Id), WorkspaceNodeOrder.Instance).ToList() }
            : result;
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
            return WorkspaceResult<WorkspaceNode>.Failed(DuplicateName(normalized, ex), NameField, WorkspaceFailure.DuplicateName);
        }
        catch (WorkspaceParentException ex)
        {
            return ParentFailed<WorkspaceNode>(ex.Problem);
        }
        catch (WorkspaceNotLiveException)
        {
            return WorkspaceResult<WorkspaceNode>.Failed(DestinationNotLiveText("created"), failure: WorkspaceFailure.AncestorNotLive);
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
            return WorkspaceResult<WorkspaceNode>.Failed(DuplicateName(normalized.Name!, ex), NameField, WorkspaceFailure.DuplicateName);
        }
        catch (WorkspaceParentException ex)
        {
            return ParentFailed<WorkspaceNode>(ex.Problem);
        }
        catch (WorkspaceNotLiveException)
        {
            return WorkspaceResult<WorkspaceNode>.Failed(DestinationNotLiveText("created"), failure: WorkspaceFailure.AncestorNotLive);
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
            return WorkspaceResult<string>.Failed(DuplicateName(normalized, ex), NameField, WorkspaceFailure.DuplicateName);
        }
        catch (ProjectTrashedException)
        {
            return WorkspaceResult<string>.Failed(ProjectInTrash, failure: WorkspaceFailure.ProjectTrashed);
        }
        catch (ProjectArchivedException)
        {
            return WorkspaceResult<string>.Failed(ProjectIsArchived, failure: WorkspaceFailure.ProjectArchived);
        }
        catch (WorkspaceNotLiveException ex)
        {
            return WorkspaceResult<string>.Failed(NotLiveText(ex), failure: WorkspaceFailure.AncestorNotLive);
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
            return WorkspaceResult<ProjectDetails>.Failed(DuplicateName(normalized.Name!, ex), NameField, WorkspaceFailure.DuplicateName);
        }
        catch (ProjectTrashedException)
        {
            return WorkspaceResult<ProjectDetails>.Failed(ProjectInTrash, failure: WorkspaceFailure.ProjectTrashed);
        }
        catch (ProjectArchivedException)
        {
            return WorkspaceResult<ProjectDetails>.Failed(ProjectIsArchived, failure: WorkspaceFailure.ProjectArchived);
        }
        catch (WorkspaceNotLiveException ex)
        {
            return WorkspaceResult<ProjectDetails>.Failed(NotLiveText(ex), failure: WorkspaceFailure.AncestorNotLive);
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
            return WorkspaceResult<bool>.Failed(ex.BlockerArchivedFolder
                ? $"The destination already contains an archived folder named '{ex.Name}'. Unarchive or rename it first."
                : ex.BlockerArchived
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
        catch (WorkspaceNotLiveException ex) when (ex.NodeId == nodeId)
        {
            return WorkspaceResult<bool>.Failed(NotLiveText(ex), failure: WorkspaceFailure.AncestorNotLive);
        }
        catch (WorkspaceNotLiveException)
        {
            return WorkspaceResult<bool>.Failed(DestinationNotLiveText("moved"), failure: WorkspaceFailure.DestinationNotLive);
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

    // ---- Folder Archive and Folder Trash (schema v9) ---------------------------------------------------------------------------

    /// <summary>The confirmation to show for archiving or trashing a Folder (its preview and token), or why it is refused now.</summary>
    public Task<FolderLifecyclePreview> PrepareFolderLifecycleAsync(long nodeId, FolderLifecycleKind kind, CancellationToken cancellationToken = default) =>
        store.PrepareFolderLifecycleAsync(nodeId, kind, cancellationToken);

    /// <summary>
    /// Archives (or trashes) the Folder that roots a subtree: only that Folder's marker and serial change; every descendant keeps
    /// its own state. Commits only if the subtree still matches <paramref name="confirmationToken"/> (§7.4). Nothing on disk is
    /// touched.
    /// </summary>
    /// <exception cref="ArgumentException">No confirmation token; nothing was called or written.</exception>
    public Task<FolderLifecycleResult> MarkFolderAsync(long nodeId, FolderLifecycleKind kind, string confirmationToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(confirmationToken))
            throw new ArgumentException("A folder lifecycle change needs the token of a shown confirmation.", nameof(confirmationToken));
        return kind == FolderLifecycleKind.Archive
            ? store.ArchiveFolderAsync(nodeId, confirmationToken, cancellationToken)
            : store.TrashFolderAsync(nodeId, confirmationToken, cancellationToken);
    }

    /// <summary>Unarchive (state-based) or strict Restore of a Folder at its original parent and name.</summary>
    public Task<FolderClearResult> ClearFolderAsync(long nodeId, FolderLifecycleKind kind, CancellationToken cancellationToken = default) =>
        kind == FolderLifecycleKind.Archive ? store.UnarchiveFolderAsync(nodeId, cancellationToken) : store.RestoreFolderAsync(nodeId, cancellationToken);

    /// <summary>Undoes exactly one Folder archive or trash event (its serial).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a committed event; nothing was called or written.</exception>
    public Task<FolderClearResult> UndoFolderLifecycleAsync(FolderLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
    {
        if (lifecycleEvent.Serial < 1)
            throw new ArgumentOutOfRangeException(nameof(lifecycleEvent), lifecycleEvent.Serial, "A folder lifecycle event has a serial of 1 or more.");
        return store.UndoFolderLifecycleAsync(lifecycleEvent, cancellationToken);
    }

    internal static string NewProjectId() => Guid.NewGuid().ToString("N");

    public const string ProjectInTrash = "This project is in the Trash.";

    public const string ProjectIsArchived = "This project is archived. Nothing was changed.";

    private const string NoLongerExists = "This folder or project no longer exists.";

    private static string DuplicateName(string name, DuplicateWorkspaceNameException ex) =>
        ex.BlockerArchivedFolder
            ? $"An archived folder named '{name}' already exists here. Unarchive it or choose another name."
            : ex.BlockerArchived
            ? $"An archived project named '{name}' already exists here. Unarchive it or choose another name."
            : $"A folder or project named '{name}' already exists here (names are compared ignoring case and width).";

    /// <summary>The node itself is a marked Folder, or something above it is archived or in the Trash.</summary>
    private static string NotLiveText(WorkspaceNotLiveException ex) =>
        (ex.State.RootId == ex.NodeId, ex.State.State) switch
        {
            (true, LifecycleState.Trashed) => "This folder is in the Trash. Nothing was changed.",
            (true, _) => "This folder is archived. Nothing was changed.",
            (false, LifecycleState.Trashed) => "This item's folder is in the Trash. Nothing was changed.",
            _ => "This item's folder is archived. Nothing was changed.",
        };

    private static string DestinationNotLiveText(string verb) => $"The destination folder is archived or in the Trash. Nothing was {verb}.";

    private static WorkspaceResult<T> ParentFailed<T>(WorkspaceParentProblem problem) => problem == WorkspaceParentProblem.NotFound
        ? WorkspaceResult<T>.Failed("The destination folder no longer exists.", failure: WorkspaceFailure.ParentNotFound)
        : WorkspaceResult<T>.Failed("Only folders can contain folders and projects.", failure: WorkspaceFailure.ParentNotAFolder);
}
