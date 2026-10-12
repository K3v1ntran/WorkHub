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
        catch (DuplicateWorkspaceNameException)
        {
            return WorkspaceResult<WorkspaceNode>.Failed(DuplicateName(normalized), NameField, WorkspaceFailure.DuplicateName);
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
        catch (DuplicateWorkspaceNameException)
        {
            return WorkspaceResult<WorkspaceNode>.Failed(DuplicateName(normalized.Name!), NameField, WorkspaceFailure.DuplicateName);
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
        catch (DuplicateWorkspaceNameException)
        {
            return WorkspaceResult<string>.Failed(DuplicateName(normalized), NameField, WorkspaceFailure.DuplicateName);
        }
        catch (ProjectTrashedException)
        {
            return WorkspaceResult<string>.Failed(ProjectInTrash, failure: WorkspaceFailure.ProjectTrashed);
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
        catch (DuplicateWorkspaceNameException)
        {
            return WorkspaceResult<ProjectDetails>.Failed(DuplicateName(normalized.Name!), NameField, WorkspaceFailure.DuplicateName);
        }
        catch (ProjectTrashedException)
        {
            return WorkspaceResult<ProjectDetails>.Failed(ProjectInTrash, failure: WorkspaceFailure.ProjectTrashed);
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
            return WorkspaceResult<bool>.Failed($"The destination already contains a folder or project named '{ex.Name}'.", failure: WorkspaceFailure.DuplicateName);
        }
        catch (ProjectTrashedException)
        {
            return WorkspaceResult<bool>.Failed(ProjectInTrash, failure: WorkspaceFailure.ProjectTrashed);
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

    internal static string NewProjectId() => Guid.NewGuid().ToString("N");

    public const string ProjectInTrash = "This project is in the Trash.";

    private const string NoLongerExists = "This folder or project no longer exists.";

    private static string DuplicateName(string name) =>
        $"A folder or project named '{name}' already exists here (names are compared ignoring case and width).";

    private static WorkspaceResult<T> ParentFailed<T>(WorkspaceParentProblem problem) => problem == WorkspaceParentProblem.NotFound
        ? WorkspaceResult<T>.Failed("The destination folder no longer exists.", failure: WorkspaceFailure.ParentNotFound)
        : WorkspaceResult<T>.Failed("Only folders can contain folders and projects.", failure: WorkspaceFailure.ParentNotAFolder);
}
