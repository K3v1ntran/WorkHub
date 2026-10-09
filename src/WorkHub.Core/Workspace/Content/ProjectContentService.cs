namespace WorkHub.Core.Workspace.Content;

/// <summary>
/// Project content use cases (ProjectFolders and Links). The UI calls only this; it never touches SQL.
/// Content is WorkHub metadata: creating, renaming, editing or moving a ProjectFolder or Link never creates, moves,
/// renames or deletes anything on disk, and opening a Link only hands its target to Windows.
/// </summary>
public sealed class ProjectContentService(IProjectContentStore store, IProjectLinkLauncher launcher)
{
    public const string NameField = ProjectLinkValidator.Fields.Name;

    /// <summary>Folders and links of one container, each sorted by name (culture-aware, ignoring case and width), then Id.</summary>
    public async Task<ProjectContentListResult> ListAsync(long projectNodeId, long? folderId, CancellationToken cancellationToken = default)
    {
        var result = await store.ListAsync(projectNodeId, folderId, cancellationToken).ConfigureAwait(false);
        if (result.Listing is not { } listing)
            return result;

        return ProjectContentListResult.Ok(listing with
        {
            Folders = listing.Folders.OrderBy(f => (WorkspaceNodeType.Folder, f.Name, f.Id), WorkspaceNodeOrder.Instance).ToList(),
            Links = listing.Links.OrderBy(l => (WorkspaceNodeType.Folder, l.Name, l.Id), WorkspaceNodeOrder.Instance).ToList(),
        });
    }

    public async Task<WorkspaceResult<ProjectFolder>> CreateFolderAsync(long projectNodeId, long? parentFolderId, string? name, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceName.TryNormalize(name, out var normalized, out var error))
            return WorkspaceResult<ProjectFolder>.Failed(error!, NameField);

        try
        {
            return WorkspaceResult<ProjectFolder>.Ok(await store.CreateFolderAsync(projectNodeId, parentFolderId, normalized, cancellationToken).ConfigureAwait(false));
        }
        catch (DuplicateProjectFolderNameException)
        {
            return WorkspaceResult<ProjectFolder>.Failed(DuplicateFolderName(normalized), NameField);
        }
        catch (ContentContainerException ex)
        {
            return WorkspaceResult<ProjectFolder>.Failed(ContainerProblem(ex.Problem));
        }
    }

    public async Task<WorkspaceResult<ProjectFolder>> RenameFolderAsync(long folderId, string? name, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceName.TryNormalize(name, out var normalized, out var error))
            return WorkspaceResult<ProjectFolder>.Failed(error!, NameField);

        try
        {
            return await store.RenameFolderAsync(folderId, normalized, cancellationToken).ConfigureAwait(false) is { } saved
                ? WorkspaceResult<ProjectFolder>.Ok(saved)
                : WorkspaceResult<ProjectFolder>.Failed(FolderNoLongerExists);
        }
        catch (DuplicateProjectFolderNameException)
        {
            return WorkspaceResult<ProjectFolder>.Failed(DuplicateFolderName(normalized), NameField);
        }
    }

    public async Task<WorkspaceResult<ProjectLink>> CreateLinkAsync(long projectNodeId, long? folderId, ProjectLinkDraft draft, CancellationToken cancellationToken = default)
    {
        var (normalized, field, error) = ProjectLinkValidator.Validate(draft);
        if (normalized is null)
            return WorkspaceResult<ProjectLink>.Failed(error!, field);

        try
        {
            return WorkspaceResult<ProjectLink>.Ok(await store.CreateLinkAsync(projectNodeId, folderId, normalized, cancellationToken).ConfigureAwait(false));
        }
        catch (ContentContainerException ex)
        {
            return WorkspaceResult<ProjectLink>.Failed(ContainerProblem(ex.Problem));
        }
    }

    public async Task<WorkspaceResult<ProjectLink>> UpdateLinkAsync(long linkId, ProjectLinkDraft draft, CancellationToken cancellationToken = default)
    {
        var (normalized, field, error) = ProjectLinkValidator.Validate(draft);
        if (normalized is null)
            return WorkspaceResult<ProjectLink>.Failed(error!, field);

        return await store.UpdateLinkAsync(linkId, normalized, cancellationToken).ConfigureAwait(false) is { } saved
            ? WorkspaceResult<ProjectLink>.Ok(saved)
            : WorkspaceResult<ProjectLink>.Failed(LinkNoLongerExists);
    }

    /// <summary>Folders a folder (or a link, when <paramref name="movingFolderId"/> is null) may move into, sorted by path.</summary>
    public async Task<IReadOnlyList<WorkspaceFolderPath>> ListFolderMoveTargetsAsync(long projectNodeId, long? movingFolderId, CancellationToken cancellationToken = default)
    {
        var folders = await store.ListFolderMoveTargetsAsync(projectNodeId, movingFolderId, cancellationToken).ConfigureAwait(false);
        return folders.OrderBy(f => (WorkspaceNodeType.Folder, f.Path, f.Id), WorkspaceNodeOrder.Instance).ToList();
    }

    /// <param name="expectedParentFolderId">The folder's container as observed when the Move dialog opened (null = Project root).</param>
    public async Task<ContentMoveResult> MoveFolderAsync(long folderId, long? expectedParentFolderId, long? targetParentFolderId, CancellationToken cancellationToken = default) =>
        Describe(await store.MoveFolderAsync(folderId, expectedParentFolderId, targetParentFolderId, cancellationToken).ConfigureAwait(false));

    /// <param name="expectedFolderId">The link's container as observed when the Move dialog opened (null = Project root).</param>
    public async Task<ContentMoveResult> MoveLinkAsync(long linkId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default) =>
        Describe(await store.MoveLinkAsync(linkId, expectedFolderId, targetFolderId, cancellationToken).ConfigureAwait(false));

    /// <summary>Re-reads the link and opens its stored target off the caller's thread. Never modifies the target.</summary>
    public async Task<ProjectLinkOpenResult> OpenLinkAsync(long linkId, CancellationToken cancellationToken = default)
    {
        var link = await store.GetLinkAsync(linkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return new ProjectLinkOpenResult(ProjectLinkOpenProblem.LinkNotFound, LinkNoLongerExists);
        return await Task.Run(() => launcher.Launch(link), cancellationToken).ConfigureAwait(false);
    }

    private const string FolderNoLongerExists = "This folder no longer exists.";
    private const string LinkNoLongerExists = "This link no longer exists.";

    private static string DuplicateFolderName(string name) =>
        $"A folder named '{name}' already exists here (names are compared ignoring case and width).";

    private static string ContainerProblem(ContentContainerProblem problem) => problem switch
    {
        ContentContainerProblem.ProjectNotFound => "This project no longer exists.",
        ContentContainerProblem.WrongProject => "The destination folder belongs to another project.",
        _ => "The destination folder no longer exists.",
    };

    private static ContentMoveResult Describe(ContentMoveOutcome outcome) => new(outcome, outcome switch
    {
        ContentMoveOutcome.Moved or ContentMoveOutcome.AlreadyThere => null,
        ContentMoveOutcome.NotFound => "This item no longer exists.",
        ContentMoveOutcome.MovedMeanwhile => "This item was moved meanwhile. Nothing was moved; close this window to see where it is now.",
        ContentMoveOutcome.DestinationNotFound => "The destination folder no longer exists.",
        ContentMoveOutcome.WrongProject => "The destination folder belongs to another project.",
        ContentMoveOutcome.Cycle => "A folder cannot be moved into itself or into one of its own subfolders.",
        ContentMoveOutcome.AncestryUnverified => "The move could not be verified as safe. Nothing was moved.",
        ContentMoveOutcome.Duplicate => "The destination already contains a folder with this name.",
        _ => "The item could not be moved.",
    });
}
