namespace WorkHub.Core.Workspace;

/// <summary>Effective lifecycle state of a Workspace node (Phase 13 proposal revision 4 §7.1).</summary>
public enum LifecycleState
{
    Live,
    Archived,
    Trashed,
}

/// <summary>
/// The OWN markers of one node on an ancestor chain: <see cref="Trashed"/> is T(x) (a Project's <c>TrashedAt</c> or a Folder's
/// <c>FolderTrashedAt</c>), <see cref="Archived"/> is A(x) (a Project's <c>Status = 'Archived'</c> or a Folder's
/// <c>FolderArchivedAt</c>).
/// </summary>
public readonly record struct LifecycleMarkers(long Id, WorkspaceNodeType Type, bool Trashed, bool Archived);

/// <summary>
/// The effective lifecycle of a node and its root (§7.1). The single definition used by the tree, panels, search, reveal and
/// every write permission:
/// <list type="bullet">
/// <item>any T on the chain (the node or an ancestor) dominates: Trashed, root = the T-marked node CLOSEST TO THE TOP;</item>
/// <item>otherwise any A on the chain: Archived, root = the A-marked node closest to the top;</item>
/// <item>otherwise Live (no root).</item>
/// </list>
/// </summary>
public sealed record EffectiveLifecycle(LifecycleState State, long? RootId, WorkspaceNodeType? RootType)
{
    public static EffectiveLifecycle Live { get; } = new(LifecycleState.Live, null, null);

    public bool IsLive => State == LifecycleState.Live;

    /// <param name="chainTopDown">The top-level node first, the node itself last.</param>
    public static EffectiveLifecycle Of(IReadOnlyList<LifecycleMarkers> chainTopDown)
    {
        foreach (var node in chainTopDown)
        {
            if (node.Trashed)
                return new EffectiveLifecycle(LifecycleState.Trashed, node.Id, node.Type);
        }

        foreach (var node in chainTopDown)
        {
            if (node.Archived)
                return new EffectiveLifecycle(LifecycleState.Archived, node.Id, node.Type);
        }

        return Live;
    }
}

/// <summary>The two Folder lifecycle operations (M1: a marker on the Folder that roots the subtree).</summary>
public enum FolderLifecycleKind
{
    Archive,
    Trash,
}

/// <summary>
/// Identity of ONE committed Folder archive or trash event (schema v9): the Folder, the kind and the <c>LifecycleSerial</c> the
/// commit produced (1 or more). Undo acts only on this event (D13); Unarchive and Restore are state-based.
/// </summary>
public readonly record struct FolderLifecycleEvent(long NodeId, FolderLifecycleKind Kind, long Serial);

/// <summary>What the confirmation shows about the subtree (§7.4). Every count covers ALL depths below the Folder.</summary>
/// <param name="ItemsInProjectTrash">Content rows (folders, links, notes) of those Projects that are in their Project's Trash.</param>
public sealed record LifecycleCounts(
    int Folders,
    int Projects,
    int ArchivedProjects,
    int TrashedProjects,
    int Links,
    int Notes,
    int ItemsInProjectTrash);

/// <summary>
/// One computation of the confirmation (§7.4, <c>P13-FLC-3</c>): the preview shown and the token that identifies exactly what
/// it describes. The same store function produces it when the dialog opens and again inside the commit transaction; a
/// different token at commit means the confirmation is stale.
/// </summary>
/// <param name="Name">The Folder's original name, as shown.</param>
/// <param name="Location">The restore location: the ancestor path (original names, top-down), or null for the top level.</param>
/// <param name="ShownChildren">Up to three direct children, in tree order (original names).</param>
/// <param name="MoreChildren">Direct children not shown ("and N more").</param>
public sealed record LifecycleConfirmation(
    long RootId,
    FolderLifecycleKind Kind,
    string Name,
    string? Location,
    LifecycleCounts Counts,
    IReadOnlyList<string> ShownChildren,
    int MoreChildren,
    string Token)
{
    /// <summary>Version tag of the serialization the token hashes (revision 4: content rows carry <c>TrashEntryId</c>).</summary>
    public const string Version = "P13-FLC-3";

    /// <summary>The bound on <see cref="ShownChildren"/>: the dialog never lists a subtree.</summary>
    public const int MaxShownChildren = 3;
}

public enum FolderLifecycleOutcome
{
    /// <summary>The marker was written (commit only).</summary>
    Committed,
    NotFound,

    /// <summary>The node is a Project; Project Archive and Trash are separate operations.</summary>
    NotAFolder,

    /// <summary>Archive of a Folder that is already archived; nothing was written.</summary>
    AlreadyArchived,

    /// <summary>The Folder itself is in the Trash; nothing was written.</summary>
    FolderTrashed,

    /// <summary>Trash of an archived Folder: unarchive it first (D15); nothing was written.</summary>
    FolderArchived,

    /// <summary>An ancestor Folder is archived or in the Trash (<see cref="FolderLifecycleResult.Blocker"/>); nothing was written.</summary>
    AncestorNotLive,

    /// <summary>The subtree changed since the confirmation (§7.4); nothing was written; <see cref="FolderLifecycleResult.Fresh"/> is the new preview.</summary>
    ConfirmationStale,
}

/// <summary>A confirmation ready to show, or why the operation is refused before any dialog.</summary>
public sealed record FolderLifecyclePreview(LifecycleConfirmation? Confirmation, FolderLifecycleOutcome? Refusal)
{
    /// <summary><see cref="FolderLifecycleOutcome.AncestorNotLive"/>: the ancestor's effective state and its root.</summary>
    public EffectiveLifecycle? Blocker { get; init; }

    public static FolderLifecyclePreview Refused(FolderLifecycleOutcome refusal, EffectiveLifecycle? blocker = null) => new(null, refusal) { Blocker = blocker };
}

/// <param name="Event">The committed event (the Undo token).</param>
/// <param name="Fresh"><see cref="FolderLifecycleOutcome.ConfirmationStale"/>: the preview as it is now (never resubmitted automatically).</param>
public sealed record FolderLifecycleResult(FolderLifecycleOutcome Outcome, FolderLifecycleEvent? Event, LifecycleConfirmation? Fresh)
{
    public EffectiveLifecycle? Blocker { get; init; }

    public static FolderLifecycleResult Refused(FolderLifecycleOutcome outcome, EffectiveLifecycle? blocker = null) => new(outcome, null, null) { Blocker = blocker };
}

/// <summary>Outcome of clearing a Folder's marker: Unarchive, Restore, or the Undo of one event.</summary>
public enum FolderClearOutcome
{
    Cleared,
    NotFound,
    NotAFolder,

    /// <summary>Unarchive (or Undo of an archive): the Folder is not archived any more; nothing was written.</summary>
    NotArchived,

    /// <summary>Restore (or Undo of a trash): the Folder is not in the Trash any more; nothing was written.</summary>
    NotTrashed,

    /// <summary>Undo: marked again by a LATER event (a different serial); this Undo no longer applies; nothing was written.</summary>
    MarkedAgain,

    /// <summary>An ancestor is archived: the Folder is reached through it; nothing was written.</summary>
    ParentArchived,

    /// <summary>An ancestor is in the Trash; nothing was written.</summary>
    ParentTrashed,

    /// <summary>Restore: a live sibling holds the name in the original parent; nothing was renamed or moved.</summary>
    NameConflict,
}

/// <summary>The Folder as it now appears in the tree (collapsed and unloaded; descendants load lazily) and its parent's path.</summary>
public sealed record RestoredFolder(WorkspaceNode Node, string? ParentPath);

public sealed record FolderClearResult(FolderClearOutcome Outcome, RestoredFolder? Folder, string? BlockingName)
{
    public static FolderClearResult Refused(FolderClearOutcome outcome, string? blockingName = null) => new(outcome, null, blockingName);
}

public enum ChildrenReadStatus
{
    Listed,

    /// <summary>The parent, or one of its ancestors, is archived or in the Trash (§7.9 C3); no rows were returned.</summary>
    ParentNotLive,

    /// <summary>The parent no longer exists.</summary>
    ParentMissing,
}

/// <summary>
/// A typed child read (§7.9 C3), evaluated with the parent's effective state in the SAME read transaction as the rows.
/// </summary>
/// <param name="ParentState"><see cref="ChildrenReadStatus.ParentNotLive"/>: the parent's effective state and its root.</param>
/// <param name="ChainFromRoot"><see cref="ChildrenReadStatus.ParentNotLive"/>: node Ids from that root down to the parent.</param>
public sealed record WorkspaceChildrenResult(
    ChildrenReadStatus Status,
    IReadOnlyList<WorkspaceNode> Nodes,
    EffectiveLifecycle? ParentState,
    IReadOnlyList<long> ChainFromRoot)
{
    public static WorkspaceChildrenResult Listed(IReadOnlyList<WorkspaceNode> nodes) => new(ChildrenReadStatus.Listed, nodes, null, []);

    public static WorkspaceChildrenResult Missing { get; } = new(ChildrenReadStatus.ParentMissing, [], null, []);
}

/// <summary>
/// The node, or an ancestor of it, is archived or in the Trash (§7.1: Workspace node writes need an effective state of Live);
/// nothing was written.
/// </summary>
public sealed class WorkspaceNotLiveException(long nodeId, EffectiveLifecycle state, Exception? inner = null)
    : Exception($"Workspace node {nodeId} is {state.State} (root {state.RootId}).", inner)
{
    public long NodeId { get; } = nodeId;

    public EffectiveLifecycle State { get; } = state;
}
