namespace WorkHub.Core.Workspace;

/// <summary>
/// An archived Project as listed in the Archived projects panel: the node itself (its Id), its CURRENT parent and that
/// parent's path (null for the root), and its last update. Archive keeps the parent, name and UpdatedAt unchanged.
/// </summary>
public sealed record ArchivedProjectEntry(long NodeId, string Name, long? ParentId, string? ParentPath, DateTimeOffset UpdatedAt)
{
    /// <summary>A Project, or (schema v9) a Folder that roots an archived subtree.</summary>
    public WorkspaceNodeType Type { get; init; } = WorkspaceNodeType.Project;

    /// <summary>When a Folder was archived (its <c>FolderArchivedAt</c>); null for Projects.</summary>
    public DateTimeOffset? ArchivedAt { get; init; }
}

/// <summary>
/// Identity of ONE committed archive event (schema v8): the Project, the <c>ArchiveSerial</c> the Archive committed (always
/// 1 or more) and the status it had before. Undo acts only on this event; ordinary Unarchive is state-based.
/// </summary>
public readonly record struct ArchiveEvent(long NodeId, long Serial, ProjectStatus PreviousStatus);

public enum ProjectArchiveOutcome
{
    Archived,
    NotFound,

    /// <summary>The node is a Workspace Folder; folders are never archived.</summary>
    NotAProject,

    /// <summary>Already archived (for example by another window); nothing was written.</summary>
    AlreadyArchived,

    /// <summary>The Project is in the Workspace Trash (checked first); nothing was written.</summary>
    ProjectTrashed,

    /// <summary>An ancestor Folder is archived or in the Trash (§7.1); nothing was written.</summary>
    AncestorNotLive,
}

/// <param name="Entry">The archived Project as it now appears in the panel.</param>
/// <param name="Event">The committed archive event (the Undo token).</param>
public sealed record ProjectArchiveResult(ProjectArchiveOutcome Outcome, ArchivedProjectEntry? Entry, ArchiveEvent? Event)
{
    public static ProjectArchiveResult Refused(ProjectArchiveOutcome outcome) => new(outcome, null, null);
}

public enum ProjectUnarchiveOutcome
{
    Unarchived,
    NotFound,
    NotAProject,

    /// <summary>Not archived (unarchived elsewhere meanwhile); nothing was written.</summary>
    NotArchived,

    /// <summary>The Project is in the Workspace Trash; nothing was written.</summary>
    ProjectTrashed,

    /// <summary>An ancestor Folder is archived (§7.1: unarchive the outermost one); nothing was written.</summary>
    ParentArchived,

    /// <summary>An ancestor Folder is in the Trash; nothing was written.</summary>
    ParentTrashed,
}

/// <param name="Unarchived">The Project as it now appears in the tree, and its parent's path.</param>
public sealed record ProjectUnarchiveResult(ProjectUnarchiveOutcome Outcome, RestoredProject? Unarchived)
{
    public static ProjectUnarchiveResult Refused(ProjectUnarchiveOutcome outcome) => new(outcome, null);
}

public enum ArchiveUndoOutcome
{
    Undone,

    /// <summary>The node no longer exists or is not a Project; nothing was written.</summary>
    NotFound,

    /// <summary>The Project is in the Workspace Trash; nothing was written.</summary>
    ProjectTrashed,

    /// <summary>Not archived any more (unarchived or undone elsewhere); nothing was written.</summary>
    NotArchived,

    /// <summary>Archived again by a LATER archive event (a different serial); this Undo no longer applies; nothing was written.</summary>
    ArchivedAgain,

    /// <summary>An ancestor Folder is archived; nothing was written.</summary>
    ParentArchived,

    /// <summary>An ancestor Folder is in the Trash; nothing was written.</summary>
    ParentTrashed,
}

public sealed record ArchiveUndoResult(ArchiveUndoOutcome Outcome, RestoredProject? Restored)
{
    public static ArchiveUndoResult Refused(ArchiveUndoOutcome outcome) => new(outcome, null);
}

// ---- Phase 19: Bulk Archive of Projects (proposal Revision 2 + R2a–R2d, §4.3) ------------------------------------------

/// <summary>One ticked row of the Bulk Archive dialog: the Project and the place the user confirmed (null = the top level).</summary>
public readonly record struct BulkArchiveTarget(long ProjectId, long? ExpectedParentId);

public enum BulkArchiveOutcome
{
    Archived,
    NotFound,
    NotAProject,

    /// <summary>The Project's current parent is not the confirmed place; nothing was written and no event was created.</summary>
    MovedElsewhere,

    ProjectTrashed,
    AlreadyArchived,

    /// <summary>
    /// The Project is still in the confirmed place and that place (the folder itself or an ancestor) is archived or trashed;
    /// nothing was written. Only this outcome stops a batch.
    /// </summary>
    LocationNotLive,
}

/// <param name="Entry">Archived only: the Project as it now appears in the Archived panel.</param>
/// <param name="Event">Archived only: the committed archive event (the Bulk Undo token).</param>
/// <param name="CurrentParentId">MovedElsewhere: the parent seen at the refusal (Details only; never used to place a row).</param>
/// <param name="LocationState">LocationNotLive: Archived or Trashed.</param>
/// <param name="MarkedNodeId">LocationNotLive: the confirmed folder itself or the outermost marked ancestor.</param>
public sealed record BulkArchiveResult(
    BulkArchiveOutcome Outcome,
    ArchivedProjectEntry? Entry,
    ArchiveEvent? Event,
    long? CurrentParentId,
    LifecycleState? LocationState,
    long? MarkedNodeId)
{
    public static BulkArchiveResult Refused(BulkArchiveOutcome outcome) => new(outcome, null, null, null, null, null);
}

/// <summary>A live Project directly in one place, as the Bulk Archive dialog lists it.</summary>
public sealed record BulkArchiveCandidate(long NodeId, string Name, ProjectStatus Status, string? Owner, DateTimeOffset UpdatedAt);

/// <summary>
/// The Bulk Archive dialog's read (one read transaction): the place's own effective state and its live Projects, at most
/// <see cref="BulkArchiveCandidates.MaxRows"/>; <see cref="Truncated"/> when more exist.
/// </summary>
public sealed record BulkArchiveCandidates(ChildrenReadStatus Status, IReadOnlyList<BulkArchiveCandidate> Projects, bool Truncated)
{
    /// <summary>D6: the dialog lists at most this many rows.</summary>
    public const int MaxRows = 500;
}

/// <summary>The Project is archived: its node is not renamed, edited, moved or trashed (Phase 12 A3); nothing was written.</summary>
public sealed class ProjectArchivedException(long projectNodeId, Exception? inner = null)
    : Exception($"Project {projectNodeId} is archived.", inner)
{
    public long ProjectNodeId { get; } = projectNodeId;
}
