namespace WorkHub.Core.Workspace;

// Phase 24 (proposal Revision 2 + R2a §3.3): the read-only "Projects with this tag" list of Manage tags. Schema v10, no migration.

/// <summary>
/// A listed Project's effective state, from the same <c>NodeLifecycle</c> as tag eligibility (Phases 21–23): <see cref="Trashed"/>
/// means exactly "keeps the tag" (in the Workspace Trash, directly or through a Folder).
/// </summary>
public enum TaggedProjectState
{
    /// <summary>Not archived and not in the Trash.</summary>
    Live,

    /// <summary>Archived itself, or inside an archived Folder (and not in the Trash).</summary>
    Archived,

    /// <summary>In the Workspace Trash, directly or through a Folder. Listed for the totals; never revealed.</summary>
    Trashed,
}

/// <summary>
/// One Project carrying the tag key. <see cref="Path"/> is the joined folder path from the top level down (empty at the top
/// level); <see cref="StoredSpelling"/> is the Project's own stored tag spelling.
/// </summary>
public sealed record TaggedProject(long ProjectNodeId, string Name, string Path, string StoredSpelling, TaggedProjectState State);

/// <summary>
/// The list result of ONE read snapshot. The totals are counted over every row of the key BEFORE the cap:
/// <see cref="EligibleTotal"/> = Live + Archived, <see cref="TrashedTotal"/> = Trashed.
/// </summary>
public sealed record TaggedProjectsResult(IReadOnlyList<TaggedProject> Rows, int EligibleTotal, int TrashedTotal)
{
    /// <summary>The rows left out by the cap (shown as "and N more").</summary>
    public int Hidden => EligibleTotal + TrashedTotal - Rows.Count;
}

/// <summary>
/// The total order of the list (§3.3, R2a), applied to the full set before the cap: eligible (Live and Archived) before Trashed;
/// then the folder path segment by segment with <see cref="CompareNames"/> (a prefix comes first, so top-level Projects precede
/// every Project in a Folder); then the Project name with <see cref="CompareNames"/>; then <c>ProjectNodeId</c> ascending.
/// </summary>
public sealed class TaggedProjectOrder : IComparer<TaggedProjectOrder.Key>
{
    public static readonly TaggedProjectOrder Instance = new();

    /// <summary>The sort key of one row: its folder names from the top level down (empty at the top level).</summary>
    public sealed record Key(TaggedProjectState State, IReadOnlyList<string> Folders, string Name, long ProjectNodeId);

    /// <summary>
    /// <c>N(a, b)</c>: exactly the name part of <see cref="WorkspaceNodeOrder"/> — invariant culture ignoring case and width, then
    /// ordinal. Not "NFKC + upper case, then ordinal" (that is how <c>NameKey</c> is built for uniqueness).
    /// </summary>
    public static int CompareNames(string a, string b) =>
        WorkspaceNodeOrder.Instance.Compare((WorkspaceNodeType.Folder, a, 0), (WorkspaceNodeType.Folder, b, 0));

    public int Compare(Key? x, Key? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var byGroup = (x.State == TaggedProjectState.Trashed).CompareTo(y.State == TaggedProjectState.Trashed);
        if (byGroup != 0)
            return byGroup;

        var common = Math.Min(x.Folders.Count, y.Folders.Count);
        for (var i = 0; i < common; i++)
        {
            var bySegment = CompareNames(x.Folders[i], y.Folders[i]);
            if (bySegment != 0)
                return bySegment;
        }

        var byDepth = x.Folders.Count.CompareTo(y.Folders.Count);
        if (byDepth != 0)
            return byDepth;

        var byName = CompareNames(x.Name, y.Name);
        return byName != 0 ? byName : x.ProjectNodeId.CompareTo(y.ProjectNodeId);
    }
}
