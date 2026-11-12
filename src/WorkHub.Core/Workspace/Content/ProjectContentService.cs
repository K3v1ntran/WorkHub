namespace WorkHub.Core.Workspace.Content;

/// <summary>
/// Project content use cases (ProjectFolders, Links, Notes and their Trash). The UI calls only this; it never touches SQL.
/// Content is WorkHub metadata: creating, renaming, editing or moving a ProjectFolder or Link never creates, moves,
/// renames or deletes anything on disk, and opening a Link only hands its target to Windows.
/// </summary>
public sealed class ProjectContentService(IProjectContentStore store, IProjectLinkLauncher launcher, IProjectTrashPurge? purge = null)
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
            Notes = listing.Notes.OrderBy(n => (WorkspaceNodeType.Folder, n.Title, n.Id), WorkspaceNodeOrder.Instance).ToList(),
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
            return WorkspaceResult<ProjectFolder>.Failed(ContainerProblem(ex.Problem), failure: ContainerFailure(ex.Problem));
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
        catch (ContentTrashedException)
        {
            return WorkspaceResult<ProjectFolder>.Failed(ItemInTrash);
        }
        catch (ProjectTrashedException)
        {
            return WorkspaceResult<ProjectFolder>.Failed(ProjectInTrash, failure: WorkspaceFailure.ProjectTrashed);
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
            return WorkspaceResult<ProjectLink>.Failed(ContainerProblem(ex.Problem), failure: ContainerFailure(ex.Problem));
        }
    }

    public async Task<WorkspaceResult<ProjectLink>> UpdateLinkAsync(long linkId, ProjectLinkDraft draft, CancellationToken cancellationToken = default)
    {
        var (normalized, field, error) = ProjectLinkValidator.Validate(draft);
        if (normalized is null)
            return WorkspaceResult<ProjectLink>.Failed(error!, field);

        try
        {
            return await store.UpdateLinkAsync(linkId, normalized, cancellationToken).ConfigureAwait(false) is { } saved
                ? WorkspaceResult<ProjectLink>.Ok(saved)
                : WorkspaceResult<ProjectLink>.Failed(LinkNoLongerExists);
        }
        catch (ContentTrashedException)
        {
            return WorkspaceResult<ProjectLink>.Failed(ItemInTrash);
        }
        catch (ProjectTrashedException)
        {
            return WorkspaceResult<ProjectLink>.Failed(ProjectInTrash, failure: WorkspaceFailure.ProjectTrashed);
        }
    }

    /// <summary>The logical location of a container ("Project › Folder › …"); null when it no longer exists.</summary>
    public Task<ProjectContainerPath?> GetContainerPathAsync(long projectNodeId, long? folderId, CancellationToken cancellationToken = default) =>
        store.GetContainerPathAsync(projectNodeId, folderId, cancellationToken);

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

    // ---- Notes ---------------------------------------------------------------------------------------------------------

    public Task<ProjectNote?> GetNoteAsync(long noteId, CancellationToken cancellationToken = default) =>
        store.GetNoteAsync(noteId, cancellationToken);

    /// <summary>Latest persisted state of the given notes from one read snapshot; absent notes no longer exist.</summary>
    public Task<IReadOnlyList<ProjectNote>> GetNotesAsync(IReadOnlyCollection<long> noteIds, CancellationToken cancellationToken = default) =>
        noteIds.Count == 0 ? Task.FromResult<IReadOnlyList<ProjectNote>>([]) : store.GetNotesAsync(noteIds, cancellationToken);

    public async Task<NoteWriteResult> CreateNoteAsync(long projectNodeId, long? folderId, ProjectNoteDraft draft, CancellationToken cancellationToken = default)
    {
        if (Invalid(draft, out var normalized) is { } invalid)
            return invalid;

        try
        {
            return NoteWriteResult.Ok(await store.CreateNoteAsync(projectNodeId, folderId, normalized!, cancellationToken).ConfigureAwait(false));
        }
        catch (ContentContainerException ex)
        {
            if (ex.Problem == ContentContainerProblem.ProjectTrashed)
                return NoteWriteResult.Failed(NoteWriteStatus.ProjectTrashed, NoteProjectInTrash);
            return NoteWriteResult.Failed(NoteWriteStatus.LocationMissing, ex.Problem switch
            {
                ContentContainerProblem.ProjectNotFound => "This project no longer exists. Your text is unchanged here.",
                ContentContainerProblem.FolderTrashed => "The folder for this note is in the Trash. Your text is unchanged here.",
                _ => "The folder for this note no longer exists. Your text is unchanged here.",
            });
        }
    }

    /// <summary>Saves Title/Content for the revision the editor holds. Never writes the note's container.</summary>
    public async Task<NoteWriteResult> SaveNoteAsync(long noteId, long expectedRevision, ProjectNoteDraft draft, CancellationToken cancellationToken = default)
    {
        if (Invalid(draft, out var normalized) is { } invalid)
            return invalid;

        var result = await store.SaveNoteAsync(noteId, expectedRevision, normalized!, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            NoteSaveStatus.Saved => NoteWriteResult.Ok(result.Note!),
            NoteSaveStatus.Conflict => NoteWriteResult.Failed(NoteWriteStatus.Conflict,
                $"This note was changed elsewhere since you opened it (now revision {result.CurrentRevision}). Your text is unchanged here.",
                currentRevision: result.CurrentRevision),
            NoteSaveStatus.Trashed => NoteWriteResult.Failed(NoteWriteStatus.Trashed, "This note is in the Trash. Your text is unchanged here."),
            NoteSaveStatus.ProjectTrashed => NoteWriteResult.Failed(NoteWriteStatus.ProjectTrashed, NoteProjectInTrash),
            _ => NoteWriteResult.Failed(NoteWriteStatus.NotFound, "This note no longer exists. Your text is unchanged here."),
        };
    }

    /// <summary>Recovery after Conflict/NotFound: inserts the draft as a new note (Phase 5B proposal §6.2 destination rules).</summary>
    public async Task<NoteWriteResult> SaveNoteAsNewAsync(long originalNoteId, long projectNodeId, long? lastKnownFolderId, ProjectNoteDraft draft, CancellationToken cancellationToken = default)
    {
        if (Invalid(draft, out var normalized) is { } invalid)
            return invalid;

        try
        {
            return NoteWriteResult.Ok(await store.SaveNoteAsNewAsync(originalNoteId, projectNodeId, lastKnownFolderId, normalized!, cancellationToken).ConfigureAwait(false));
        }
        catch (ContentContainerException ex) when (ex.Problem == ContentContainerProblem.ProjectTrashed)
        {
            // Save as new would write into the same Project, which is read-only while it is in the Trash.
            return NoteWriteResult.Failed(NoteWriteStatus.ProjectTrashed, "This project is in the Trash, so a new note cannot be saved in it. Copy your text instead.");
        }
        catch (ContentContainerException ex)
        {
            return NoteWriteResult.Failed(NoteWriteStatus.LocationMissing, ex.Problem == ContentContainerProblem.FolderTrashed
                ? "The note's location is in the Trash. Copy your text instead."
                : "The note's location no longer exists. Copy your text instead.");
        }
    }

    /// <param name="expectedFolderId">The note's container as observed when the Move dialog opened (null = Project root).</param>
    public async Task<ContentMoveResult> MoveNoteAsync(long noteId, long? expectedFolderId, long? targetFolderId, CancellationToken cancellationToken = default) =>
        Describe(await store.MoveNoteAsync(noteId, expectedFolderId, targetFolderId, cancellationToken).ConfigureAwait(false));

    private static NoteWriteResult? Invalid(ProjectNoteDraft draft, out ProjectNoteDraft? normalized)
    {
        var (valid, field, error) = ProjectNoteValidator.Validate(draft);
        normalized = valid;
        return valid is null ? NoteWriteResult.Failed(NoteWriteStatus.Invalid, error!, field) : null;
    }

    /// <summary>The link, active or trashed (<see cref="ProjectLink.IsTrashed"/>); null when it no longer exists.</summary>
    public Task<ProjectLink?> GetLinkAsync(long linkId, CancellationToken cancellationToken = default) =>
        store.GetLinkAsync(linkId, cancellationToken);

    /// <summary>Re-reads the link and opens its stored target off the caller's thread. Never modifies the target.</summary>
    public async Task<ProjectLinkOpenResult> OpenLinkAsync(long linkId, CancellationToken cancellationToken = default)
    {
        var link = await store.GetLinkAsync(linkId, cancellationToken).ConfigureAwait(false);
        if (link is null)
            return new ProjectLinkOpenResult(ProjectLinkOpenProblem.LinkNotFound, LinkNoLongerExists);
        if (link.IsProjectTrashed)
            return new ProjectLinkOpenResult(ProjectLinkOpenProblem.ProjectTrashed, ProjectInTrash);
        if (link.IsTrashed)
            return new ProjectLinkOpenResult(ProjectLinkOpenProblem.LinkTrashed, LinkInTrash);
        return await Task.Run(() => launcher.Launch(link), cancellationToken).ConfigureAwait(false);
    }

    // ---- Trash (schema v6) ---------------------------------------------------------------------------------------------

    /// <summary>Snapshot for the confirmation text only; the Trash itself acts on the item as it is when it writes (T11).</summary>
    /// <summary>Where the item is now and whether it is in the Trash (null = it no longer exists).</summary>
    public Task<ContentState?> GetContentStateAsync(ContentRef item, CancellationToken cancellationToken = default) =>
        store.GetContentStateAsync(item, cancellationToken);

    public Task<TrashCount> CountTrashSetAsync(ContentRef item, CancellationToken cancellationToken = default) =>
        store.CountTrashSetAsync(item, cancellationToken);

    /// <summary>
    /// Moves the item (a folder with its current active subtree) to the Trash. Only WorkHub metadata changes: nothing on disk
    /// is moved, renamed or deleted. The result's entry carries the ACTUAL item count.
    /// </summary>
    public async Task<TrashResult> TrashAsync(ContentRef item, CancellationToken cancellationToken = default)
    {
        var result = await store.TrashAsync(item, cancellationToken).ConfigureAwait(false);
        return result with
        {
            Error = result.Outcome switch
            {
                TrashOutcome.Trashed => null,
                TrashOutcome.NotFound => "This item no longer exists.",
                TrashOutcome.AlreadyTrashed => "This item is already in the Trash.",
                TrashOutcome.ProjectTrashed => "This project is in the Trash. Nothing was changed.",
                _ => TrashFailed,
            },
        };
    }

    /// <summary>The Project's Trash, newest first.</summary>
    public Task<IReadOnlyList<TrashEntry>> ListTrashAsync(long projectNodeId, CancellationToken cancellationToken = default) =>
        store.ListTrashAsync(projectNodeId, cancellationToken);

    /// <summary>Restores one entry to the container it came from (by Id, wherever it is now); refusals change nothing.</summary>
    public async Task<RestoreResult> RestoreAsync(long entryId, CancellationToken cancellationToken = default)
    {
        var result = await store.RestoreAsync(entryId, cancellationToken).ConfigureAwait(false);
        return result with
        {
            Error = result.Outcome switch
            {
                RestoreOutcome.Restored => null,
                RestoreOutcome.EntryNotFound => "This item is no longer in the Trash (it may have been restored in another window).",
                RestoreOutcome.ContainerTrashed => result.BlockingName is { Length: > 0 } blocker
                    ? $"The folder it came from is in the Trash. Restore “{blocker}” first."
                    : "The folder it came from is in the Trash. Restore that folder first.",
                RestoreOutcome.ContainerMissing => "The folder it came from no longer exists. The item is still in the Trash.",
                RestoreOutcome.NameConflict => "A folder with the same name already exists where it came from. Nothing was renamed; the item is still in the Trash.",
                RestoreOutcome.ProjectTrashed => "This project is in the Trash. Nothing was restored.",
                _ => RestoreFailed,
            },
        };
    }

    // ---- Phase 14: permanent deletion from a Project's Trash ------------------------------------------------------------

    /// <summary>One read snapshot: a Ready preview (plan, displayed values, token) or a typed refusal. Never writes.</summary>
    public Task<PurgePreviewResult> PreparePurgeAsync(PurgeRequest request, CancellationToken cancellationToken = default) =>
        Purge.PreparePurgeAsync(request, cancellationToken);

    /// <summary>PreDelete copy, then one revalidated deletion; cancellation is honoured until <see cref="PurgeStage.Deleting"/>.</summary>
    public Task<PurgeResult> PurgeAsync(PurgeRequest request, string confirmedToken, IProgress<PurgeStage>? progress = null, CancellationToken cancellationToken = default) =>
        Purge.PurgeAsync(request, confirmedToken, progress, cancellationToken);

    private IProjectTrashPurge Purge => purge ?? throw new InvalidOperationException("Permanent deletion is not configured.");

    /// <summary>Why a preview or a commit was refused (nothing was deleted).</summary>
    public static string PurgeRefusalText(PurgePreviewStatus status) => status switch
    {
        PurgePreviewStatus.EmptySelection => "Select one or more items in the Trash first.",
        PurgePreviewStatus.TrashEmpty => "The Trash is empty.",
        PurgePreviewStatus.ProjectNotFound => "This project no longer exists.",
        PurgePreviewStatus.ProjectTrashed => "This project is in the Trash. Nothing was deleted.",
        PurgePreviewStatus.EntryNotFound => "One of the selected items is no longer in the Trash (it may have been restored or deleted in another window). Nothing was deleted.",
        PurgePreviewStatus.AncestryUnverified or PurgePreviewStatus.TrashDataInconsistent => "WorkHub could not verify these items, so nothing was deleted.",
        _ => PurgeFailed,
    };

    /// <summary>The message for a purge result. A committed purge is always reported as committed.</summary>
    public static string PurgeResultText(PurgeResult result)
    {
        var kept = result.BackupKept ? $" A copy of the whole database was made first and is kept: {result.BackupFileName}." : string.Empty;
        return result.Outcome switch
        {
            PurgeOutcome.Purged when result.Deleted is { } counts =>
                $"Removed from the database: {Plural(counts.Entries, "entry", "entries")} ({Plural(counts.Folders, "folder", "folders")}, {Plural(counts.Links, "link", "links")}, {Plural(counts.Notes, "note", "notes")}). Backup made first: {result.BackupFileName}.",
            PurgeOutcome.Cancelled => "Cancelled. Nothing was deleted." + kept,
            PurgeOutcome.Refused when result.Refusal is { } refusal => PurgeRefusalText(refusal) + kept,
            PurgeOutcome.ConfirmationStale => "The Trash changed since you confirmed. Nothing was deleted. Review the items and confirm again." + kept,
            PurgeOutcome.BackupFailed => $"The backup could not be made ({result.Detail}). Nothing was deleted.",
            PurgeOutcome.DatabaseBusy => "The database is busy. Nothing was deleted. Try again." + kept,
            _ => PurgeFailed + kept,
        };
    }

    /// <summary>Unexpected purge failure: the deletion was rolled back.</summary>
    public const string PurgeFailed = "The items could not be deleted. Nothing was deleted.";

    // ---- Phase 15: Copy / Move to another Project -----------------------------------------------------------------------

    public Task<IReadOnlyList<CrossProjectTarget>> ListCrossProjectTargetsAsync(long sourceProjectNodeId, CancellationToken cancellationToken = default) =>
        store.ListCrossProjectTargetsAsync(sourceProjectNodeId, cancellationToken);

    /// <summary>One read snapshot: a Ready preview (displayed values and token) or the first typed refusal. Never writes.</summary>
    public Task<CrossProjectPreview> PrepareCrossProjectAsync(CrossProjectRequest request, CancellationToken cancellationToken = default) =>
        store.PrepareCrossProjectAsync(request, cancellationToken);

    /// <summary>One revalidated transaction: the copy and (Move) the source's Move to Trash, or nothing at all.</summary>
    public Task<CrossProjectResult> CrossProjectAsync(CrossProjectRequest request, string confirmedToken, CancellationToken cancellationToken = default) =>
        store.CrossProjectAsync(request, confirmedToken, cancellationToken);

    /// <summary>Why a preview or a commit was refused (nothing was copied or moved).</summary>
    public static string CrossProjectRefusalText(CrossProjectStatus status, string? rootName = null) => status switch
    {
        CrossProjectStatus.SourceNotFound => "This item no longer exists. Nothing was copied.",
        CrossProjectStatus.SourceProjectTrashed => "This project is in the Trash. Nothing was copied.",
        CrossProjectStatus.SourceTrashed => "This item is in the Trash. Nothing was copied.",
        CrossProjectStatus.TargetProjectNotFound => "The destination project no longer exists. Nothing was copied.",
        CrossProjectStatus.SameProject => "Choose another project. To move within this project, use Move to….",
        CrossProjectStatus.TargetProjectTrashed => "The destination project is in the Trash. Nothing was copied.",
        CrossProjectStatus.TargetFolderNotFound => "The destination folder no longer exists. Nothing was copied.",
        CrossProjectStatus.TargetFolderWrongProject => "The destination folder is not in the chosen project. Nothing was copied.",
        CrossProjectStatus.TargetFolderTrashed => "The destination folder is in the Trash. Nothing was copied.",
        CrossProjectStatus.InvalidName => "Enter a valid folder name (1–200 characters, no control characters).",
        CrossProjectStatus.NameConflict => rootName is { Length: > 0 }
            ? $"A folder named “{rootName}” already exists there. Choose another name; nothing is renamed or merged automatically."
            : "A folder with this name already exists there. Choose another name; nothing is renamed or merged automatically.",
        CrossProjectStatus.SourceUnverified => "WorkHub could not verify this folder's contents, so nothing was copied.",
        CrossProjectStatus.DepthExceeded => "The folders would be nested too deeply at the destination. Choose a destination nearer the top. Nothing was copied.",
        CrossProjectStatus.TooLarge => $"This is too much to copy at once (more than {CrossProjectLimits.MaxRows:N0} items or {CrossProjectLimits.MaxPayloadBytes / (1024 * 1024)} MB of text). Nothing was copied.",
        _ => CrossProjectFailed,
    };

    /// <summary>The message for a commit result. Only a committed result says something was copied or moved.</summary>
    public static string CrossProjectResultText(CrossProjectResult result) => result.Outcome switch
    {
        CrossProjectOutcome.Copied => $"Copied “{result.RootName}”{ItemsSuffix(result.Rows)} to {result.TargetPath}.",
        CrossProjectOutcome.Moved => $"Moved “{result.RootName}”{ItemsSuffix(result.Rows)} to {result.TargetPath}. The original is in the Trash of {result.SourceProjectName} and can be restored there.",
        CrossProjectOutcome.Refused when result.Refusal is { } refusal => CrossProjectRefusalText(refusal, result.RootName),
        CrossProjectOutcome.ConfirmationStale => "Something changed since you confirmed. Nothing was copied. Review and try again.",
        CrossProjectOutcome.DatabaseBusy => "The database is busy. Nothing was copied. Try again.",
        _ => CrossProjectFailed,
    };

    /// <summary>Unexpected failure: the transaction was rolled back.</summary>
    public const string CrossProjectFailed = "The item could not be copied. Nothing was changed.";

    private static string ItemsSuffix(int rows) => rows > 1 ? $" ({rows} items)" : string.Empty;

    private static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";
    /// <summary>Unexpected Trash failure (database busy, I/O): nothing was written.</summary>
    public const string TrashFailed = "Could not move to the Trash. Nothing was changed.";

    /// <summary>Unexpected Restore failure: the entry and its items are exactly as before.</summary>
    public const string RestoreFailed = "Could not restore. The item is still in the Trash.";

    private const string FolderNoLongerExists = "This folder no longer exists.";
    private const string LinkNoLongerExists = "This link no longer exists.";
    private const string LinkInTrash = "This link is in the Trash.";
    private const string ItemInTrash = "This item is in the Trash.";
    private const string ProjectInTrash = WorkspaceService.ProjectInTrash;
    private const string NoteProjectInTrash = "This project is in the Trash. Your text is unchanged here.";

    private static WorkspaceFailure? ContainerFailure(ContentContainerProblem problem) =>
        problem == ContentContainerProblem.ProjectTrashed ? WorkspaceFailure.ProjectTrashed : null;

    private static string DuplicateFolderName(string name) =>
        $"A folder named '{name}' already exists here (names are compared ignoring case and width).";

    private static string ContainerProblem(ContentContainerProblem problem) => problem switch
    {
        ContentContainerProblem.ProjectNotFound => "This project no longer exists.",
        ContentContainerProblem.ProjectTrashed => ProjectInTrash,
        ContentContainerProblem.WrongProject => "The destination folder belongs to another project.",
        ContentContainerProblem.FolderTrashed => "The destination folder is in the Trash.",
        _ => "The destination folder no longer exists.",
    };

    private static ContentMoveResult Describe(ContentMoveOutcome outcome) => new(outcome, outcome switch
    {
        ContentMoveOutcome.Moved or ContentMoveOutcome.AlreadyThere => null,
        ContentMoveOutcome.NotFound => "This item no longer exists.",
        ContentMoveOutcome.ItemTrashed => ItemInTrash,
        ContentMoveOutcome.DestinationTrashed => "The destination folder is in the Trash.",
        ContentMoveOutcome.MovedMeanwhile => "This item was moved meanwhile. Nothing was moved; close this window to see where it is now.",
        ContentMoveOutcome.DestinationNotFound => "The destination folder no longer exists.",
        ContentMoveOutcome.WrongProject => "The destination folder belongs to another project.",
        ContentMoveOutcome.Cycle => "A folder cannot be moved into itself or into one of its own subfolders.",
        ContentMoveOutcome.AncestryUnverified => "The move could not be verified as safe. Nothing was moved.",
        ContentMoveOutcome.Duplicate => "The destination already contains a folder with this name.",
        ContentMoveOutcome.ProjectTrashed => ProjectInTrash,
        _ => "The item could not be moved.",
    });
}
