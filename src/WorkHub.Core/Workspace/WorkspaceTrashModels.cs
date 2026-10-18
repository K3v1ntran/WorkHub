namespace WorkHub.Core.Workspace;

/// <summary>
/// A Project in the Workspace Trash. The entry is the node itself (its Id); <see cref="ParentId"/> is the original parent
/// (never changed while trashed) and <see cref="ParentPath"/> its current path, null for the root.
/// </summary>
public sealed record WorkspaceTrashEntry(long NodeId, string Name, long? ParentId, string? ParentPath, DateTimeOffset TrashedAt)
{
    /// <summary>The Project is also archived (a state only an earlier build or raw SQL creates); Restore brings it back archived.</summary>
    public bool IsArchived { get; init; }
}

public enum ProjectTrashOutcome
{
    Trashed,
    NotFound,
    /// <summary>The node is a Workspace Folder; folders are never trashed.</summary>
    NotAProject,
    AlreadyTrashed,

    /// <summary>The Project is archived (Phase 12 A7: Unarchive it first); nothing was written.</summary>
    Archived,
}

public sealed record ProjectTrashResult(ProjectTrashOutcome Outcome, WorkspaceTrashEntry? Entry)
{
    public static ProjectTrashResult Refused(ProjectTrashOutcome outcome) => new(outcome, null);
}

public enum ProjectRestoreOutcome
{
    Restored,
    NotFound,
    /// <summary>Not in the Trash (restored elsewhere meanwhile); nothing was written.</summary>
    NotTrashed,
    /// <summary>An active Folder or Project with the same name exists in the original parent; nothing was written.</summary>
    NameConflict,
}

/// <summary>The restored node as it now appears in the tree, and its parent's path (null for the root).</summary>
public sealed record RestoredProject(WorkspaceNode Node, string? ParentPath)
{
    /// <summary>The restored Project is archived (it was archived and trashed): it is not shown in the tree.</summary>
    public bool IsArchived { get; init; }
}

public sealed record ProjectRestoreResult(ProjectRestoreOutcome Outcome, RestoredProject? Restored, string? BlockingName)
{
    public static ProjectRestoreResult Refused(ProjectRestoreOutcome outcome, string? blockingName = null) => new(outcome, null, blockingName);
}

public enum FolderRemoveOutcome
{
    Removed,
    NotFound,
    NotAFolder,
    /// <summary>The folder has child rows (active nodes and/or trashed Projects); nothing was written.</summary>
    NotEmpty,
}

/// <param name="ActiveChildren">Visible children (active nodes that are not archived Projects).</param>
public sealed record FolderRemoveResult(FolderRemoveOutcome Outcome, int ActiveChildren, int TrashedProjects)
{
    /// <summary>Archived Projects directly in the folder (hidden from the tree; Phase 12 A11).</summary>
    public int ArchivedProjects { get; init; }

    public static FolderRemoveResult Of(FolderRemoveOutcome outcome) => new(outcome, 0, 0);
}

/// <summary>
/// A node's current type, parent and Trash state from a fresh read (stale-node reconciliation), with its child rows: active
/// nodes and trashed Projects (what decides whether a folder may be removed).
/// </summary>
public sealed record WorkspaceNodeState(long NodeId, WorkspaceNodeType Type, long? ParentId, bool Trashed)
{
    /// <summary>The Project is archived (Phase 12): it is not shown in the tree.</summary>
    public bool Archived { get; init; }

    /// <summary>Visible children: active nodes that are not archived Projects.</summary>
    public int ActiveChildren { get; init; }

    public int TrashedChildren { get; init; }

    /// <summary>Archived Projects directly under the folder.</summary>
    public int ArchivedChildren { get; init; }
}

/// <summary>The Project (or the Project owning the content) is in the Workspace Trash; nothing was written.</summary>
public sealed class ProjectTrashedException(long projectNodeId, Exception? inner = null)
    : Exception($"Project {projectNodeId} is in the Trash.", inner)
{
    public long ProjectNodeId { get; } = projectNodeId;
}
