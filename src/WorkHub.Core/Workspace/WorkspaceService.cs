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
            return WorkspaceResult<WorkspaceNode>.Failed(DuplicateName(normalized), NameField);
        }
        catch (WorkspaceParentException ex)
        {
            return WorkspaceResult<WorkspaceNode>.Failed(ParentProblem(ex.Problem));
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
            return WorkspaceResult<WorkspaceNode>.Failed(DuplicateName(normalized.Name!), NameField);
        }
        catch (WorkspaceParentException ex)
        {
            return WorkspaceResult<WorkspaceNode>.Failed(ParentProblem(ex.Problem));
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
                : WorkspaceResult<string>.Failed(NoLongerExists);
        }
        catch (DuplicateWorkspaceNameException)
        {
            return WorkspaceResult<string>.Failed(DuplicateName(normalized), NameField);
        }
    }

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
                : WorkspaceResult<ProjectDetails>.Failed(NoLongerExists);
        }
        catch (DuplicateWorkspaceNameException)
        {
            return WorkspaceResult<ProjectDetails>.Failed(DuplicateName(normalized.Name!), NameField);
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
            return WorkspaceResult<bool>.Failed(NoLongerExists);
        }
        catch (WorkspaceParentException ex)
        {
            return WorkspaceResult<bool>.Failed(ParentProblem(ex.Problem));
        }
        catch (WorkspaceMoveCycleException)
        {
            return WorkspaceResult<bool>.Failed("A folder cannot be moved into itself or into one of its own subfolders.");
        }
        catch (DuplicateWorkspaceNameException ex)
        {
            return WorkspaceResult<bool>.Failed($"The destination already contains a folder or project named '{ex.Name}'.");
        }
    }

    internal static string NewProjectId() => Guid.NewGuid().ToString("N");

    private const string NoLongerExists = "This folder or project no longer exists.";

    private static string DuplicateName(string name) =>
        $"A folder or project named '{name}' already exists here (names are compared ignoring case and width).";

    private static string ParentProblem(WorkspaceParentProblem problem) => problem == WorkspaceParentProblem.NotFound
        ? "The destination folder no longer exists."
        : "Only folders can contain folders and projects.";
}
