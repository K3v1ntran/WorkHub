namespace WorkHub.Core.Workspace.Search;

/// <summary>What a search hit is. The declaration order is also the display rank.</summary>
public enum SearchResultKind
{
    WorkspaceFolder,
    Project,
    ProjectFolder,
    Link,
    Note,
}

/// <summary>The field a hit is reported as matching in. A Note's Title is <see cref="Name"/>.</summary>
public enum SearchField
{
    Name,
    Owner,
    ProjectManager,
    Description,
    Target,
    Content,
}

/// <summary>
/// Identity of a search result. ProjectFolders, ProjectLinks and ProjectNotes have independent Id spaces (WorkspaceFolder
/// and Project share WorkspaceNodes Ids, but the kind is still carried and checked). Equality is (Kind, Id); Id alone is
/// never used to look anything up, select a row or classify a result.
/// </summary>
public readonly record struct SearchItemRef(SearchResultKind Kind, long Id)
{
    public bool IsContent => Kind is SearchResultKind.ProjectFolder or SearchResultKind.Link or SearchResultKind.Note;
}

/// <summary>A parsed query: 1 to <see cref="WorkspaceSearchText.MaxTerms"/> folded, distinct terms; all must match.</summary>
public sealed record WorkspaceSearchQuery(IReadOnlyList<string> Terms)
{
    /// <summary>The input was longer than <see cref="WorkspaceSearchText.MaxQueryLength"/> or had more terms than allowed.</summary>
    public bool WasShortened { get; init; }
}

/// <summary>One result row. <see cref="Location"/> is display-only: activation always re-resolves.</summary>
public sealed record WorkspaceSearchHit(
    SearchItemRef Item,
    long? ProjectNodeId,
    string Name,
    string Location,
    SearchField MatchedField,
    string? Excerpt)
{
    /// <summary>The hit is an archived Project or content of one (schema v8): it is found, marked, and revealed in the Archived panel.</summary>
    public bool InArchivedProject { get; init; }
}

/// <summary>At most <c>limit</c> hits in display order; <see cref="Truncated"/> = more matched than were returned.</summary>
public sealed record WorkspaceSearchResult(IReadOnlyList<WorkspaceSearchHit> Hits, bool Truncated);

/// <summary>
/// Fresh children of one Workspace level, read in the reveal snapshot. <see cref="Complete"/> is false when the level hit
/// the per-level row bound; such a level may add or relocate the path node but never remove anything.
/// </summary>
public sealed record WorkspaceBranchSnapshot(long? ParentId, IReadOnlyList<WorkspaceNode> Children, bool Complete);

/// <summary>Where a content item is now. <see cref="Kind"/> is ProjectFolder, Link or Note.</summary>
public sealed record ContentRevealTarget(long ProjectNodeId, long? ContainerFolderId, SearchResultKind Kind, long Id);

/// <summary>Everything needed to reveal an item, from one read transaction.</summary>
public sealed record SearchRevealTarget(
    SearchItemRef Item,
    IReadOnlyList<long> WorkspaceAncestorIds,
    long WorkspaceNodeId,
    IReadOnlyList<WorkspaceBranchSnapshot> Branches,
    ContentRevealTarget? Content);

public enum RevealResolveStatus
{
    Found,

    /// <summary>No row of that kind and Id exists (or a Workspace node's type no longer matches the kind).</summary>
    NotFound,

    /// <summary>
    /// The content row of that kind and Id is in the Trash (schema v6), or the Project itself is in the Workspace Trash
    /// (schema v7); it is not revealed.
    /// </summary>
    Trashed,

    /// <summary>The content row's Project is in the Workspace Trash (schema v7); it is not revealed.</summary>
    ProjectTrashed,

    /// <summary>
    /// The Project itself is archived (schema v8). No snapshot is read: <see cref="RevealResolution.Target"/> is null and
    /// <see cref="RevealResolution.ArchivedProjectNodeId"/> is the Project's node Id.
    /// </summary>
    Archived,

    /// <summary>
    /// The content row's Project is archived (schema v8). No container, ancestors or branches are read;
    /// <see cref="RevealResolution.ArchivedProjectNodeId"/> is the owning Project's node Id.
    /// </summary>
    ProjectArchived,
}

public sealed record RevealResolution(RevealResolveStatus Status, SearchRevealTarget? Target)
{
    /// <summary><see cref="RevealResolveStatus.Archived"/>: the item's own node Id. <see cref="RevealResolveStatus.ProjectArchived"/>:
    /// the owning Project's node Id. Otherwise null.</summary>
    public long? ArchivedProjectNodeId { get; init; }

    public static RevealResolution Archived(long projectNodeId) =>
        new(RevealResolveStatus.Archived, null) { ArchivedProjectNodeId = projectNodeId };

    public static RevealResolution ProjectArchived(long projectNodeId) =>
        new(RevealResolveStatus.ProjectArchived, null) { ArchivedProjectNodeId = projectNodeId };

    public static RevealResolution NotFound { get; } = new(RevealResolveStatus.NotFound, null);

    public static RevealResolution Trashed { get; } = new(RevealResolveStatus.Trashed, null);

    public static RevealResolution ProjectTrashed { get; } = new(RevealResolveStatus.ProjectTrashed, null);

    public static RevealResolution Found(SearchRevealTarget target) => new(RevealResolveStatus.Found, target);
}

/// <summary>
/// The database stayed locked by another connection for the whole wall-clock budget of a search or resolve
/// (Phase 6 amendment A1). Nothing partial was returned.
/// </summary>
public sealed class WorkspaceSearchBusyException(TimeSpan budget)
    : Exception($"The database stayed busy for {budget.TotalSeconds:0.#} seconds.")
{
    public TimeSpan Budget { get; } = budget;
}
