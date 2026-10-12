namespace WorkHub.Core.Workspace.Content;

/// <summary>The three kinds of Project content. Folder, Link and Note Ids are independent, so the kind is always carried.</summary>
public enum ContentKind
{
    Folder,
    Link,
    Note,
}

/// <summary>Typed identity of one content row: (Kind, Id). The Id alone never identifies a row.</summary>
public readonly record struct ContentRef(ContentKind Kind, long Id);

/// <summary>
/// One Move-to-Trash action (schema v6 <c>ProjectTrashEntries</c>), as listed in a Project's Trash. Entries are immutable;
/// a successful Restore deletes them. <see cref="RootContainerFolderId"/> is the container the root was trashed from (null =
/// Project root); it never changes while the root is in the Trash, although the folder itself may have moved since.
/// </summary>
public sealed record TrashEntry(
    long Id,
    long ProjectNodeId,
    ContentRef Root,
    string RootName,
    long? RootContainerFolderId,
    int ItemCount,
    DateTimeOffset TrashedAt)
{
    /// <summary>Where the root will be restored to: the container's CURRENT path (null when it no longer exists).</summary>
    public ProjectContainerPath? ContainerPath { get; init; }

    /// <summary>The container is itself in the Trash (Restore is refused until it is restored).</summary>
    public bool ContainerTrashed { get; init; }
}

/// <summary>
/// Where one item is now, by typed identity: its Project, CURRENT container (null = Project root) and whether it is in the
/// Trash. A focused fresh read for reconciling a listed row after a refused write; null from the store = it no longer exists.
/// </summary>
public sealed record ContentState(ContentRef Item, long ProjectNodeId, long? ContainerFolderId, bool Trashed);

/// <summary>Snapshot for the confirmation text, read before the dialog. The write never relies on it (T11).</summary>
public sealed record TrashCount(bool Exists, bool Trashed, int ActiveItems);

public enum TrashOutcome
{
    Trashed,
    NotFound,

    /// <summary>The item is already in the Trash (for example trashed by another window); nothing was written.</summary>
    AlreadyTrashed,

    /// <summary>The folder's subtree walk hit its depth bound or a broken chain; nothing was written (fails closed).</summary>
    AncestryUnverified,
}

/// <param name="Committed">The database changed (true only for <see cref="TrashOutcome.Trashed"/>). It says nothing about
/// whether the view that asked may still show the result.</param>
/// <param name="Entry">The entry as written: the root's container and the ACTUAL item count from the transaction.</param>
public sealed record TrashResult(TrashOutcome Outcome, bool Committed, TrashEntry? Entry, string? Error)
{
    public static TrashResult Done(TrashEntry entry) => new(TrashOutcome.Trashed, true, entry, null);

    public static TrashResult Refused(TrashOutcome outcome) => new(outcome, false, null, null);
}

public enum RestoreOutcome
{
    Restored,

    /// <summary>The entry no longer exists (for example restored by another window); nothing was written.</summary>
    EntryNotFound,

    /// <summary>The container the root came from is itself in the Trash; <see cref="RestoreResult.BlockingName"/> names the entry root holding it.</summary>
    ContainerTrashed,

    /// <summary>The container the root came from no longer exists. Restore never falls back to the root.</summary>
    ContainerMissing,

    /// <summary>An active folder with the same name already exists in the container. Restore never renames.</summary>
    NameConflict,
}

/// <summary>
/// The restored root as committed, read in the Restore transaction: its typed identity, Project, CURRENT container and
/// container path, and exactly one of <see cref="Folder"/>, <see cref="Link"/> or <see cref="Note"/>.
/// </summary>
public sealed record RestoredRoot(
    ContentRef Root,
    long ProjectNodeId,
    long? ContainerFolderId,
    string ContainerPath,
    ProjectFolder? Folder,
    ProjectLink? Link,
    ProjectNoteSummary? Note)
{
    public string Name => Folder?.Name ?? Link?.Name ?? Note!.Title;
}

public sealed record RestoreResult(RestoreOutcome Outcome, bool Committed, RestoredRoot? Restored, string? Error, string? BlockingName)
{
    public static RestoreResult Done(RestoredRoot restored) => new(RestoreOutcome.Restored, true, restored, null, null);

    public static RestoreResult Refused(RestoreOutcome outcome, string? blockingName = null) => new(outcome, false, null, null, blockingName);
}

/// <summary>The item (or the row the write targets) is in the Trash: trashed rows are read-only.</summary>
public sealed class ContentTrashedException(Exception? inner = null)
    : Exception("This item is in the Trash.", inner);
