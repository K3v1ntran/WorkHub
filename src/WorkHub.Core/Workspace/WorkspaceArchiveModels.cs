namespace WorkHub.Core.Workspace;

/// <summary>
/// An archived Project as listed in the Archived projects panel: the node itself (its Id), its CURRENT parent and that
/// parent's path (null for the root), and its last update. Archive keeps the parent, name and UpdatedAt unchanged.
/// </summary>
public sealed record ArchivedProjectEntry(long NodeId, string Name, long? ParentId, string? ParentPath, DateTimeOffset UpdatedAt);

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
}

public sealed record ArchiveUndoResult(ArchiveUndoOutcome Outcome, RestoredProject? Restored)
{
    public static ArchiveUndoResult Refused(ArchiveUndoOutcome outcome) => new(outcome, null);
}

/// <summary>The Project is archived: its node is not renamed, edited, moved or trashed (Phase 12 A3); nothing was written.</summary>
public sealed class ProjectArchivedException(long projectNodeId, Exception? inner = null)
    : Exception($"Project {projectNodeId} is archived.", inner)
{
    public long ProjectNodeId { get; } = projectNodeId;
}
