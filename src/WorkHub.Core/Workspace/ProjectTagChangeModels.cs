namespace WorkHub.Core.Workspace;

// Phase 21 (proposal Revision 2 + R2a §3.3): rename and merge of one Project tag key across Projects. Schema v10, no migration.

/// <summary>How the Manage tags dialog asked for the change. The operation itself is derived from data (<see cref="TagChangeOperation"/>).</summary>
public enum TagChangeKind
{
    /// <summary>A typed new name (a new spelling, a new name, or the name of another tag in use = a merge).</summary>
    Rename,

    /// <summary>Another listed tag picked as the target; the new name is that tag's displayed spelling.</summary>
    MergeInto,
}

/// <summary>The operation the plan derived, decided in this order: same key → spelling change; key in use anywhere → merge; else rename.</summary>
public enum TagChangeOperation
{
    SpellingChange,
    Rename,
    Merge,
}

public enum TagChangeOutcome
{
    /// <summary>Spelling change committed (same key, new spelling; source <c>CreatedAt</c> kept).</summary>
    SpellingChanged,

    /// <summary>Rename to a key used nowhere committed.</summary>
    Renamed,

    /// <summary>Merge into a key already in use committed.</summary>
    Merged,

    /// <summary>The plan under the write lock differs from the confirmed one; nothing written; <see cref="TagChangeResult.Preview"/> is the fresh plan.</summary>
    Stale,

    /// <summary>The confirmed plan had no Project outside the Trash carrying the tag; nothing written.</summary>
    NothingToRename,

    /// <summary>The confirmed spelling change had every eligible row already spelled that way; nothing written.</summary>
    Unchanged,

    /// <summary>The new name is not a valid tag name; refused before any SQL.</summary>
    InvalidName,
}

/// <summary>One row of the Manage tags list: a key in use, its most used spelling and its Project counts (no token).</summary>
public sealed record TagInUse(string NameKey, string Spelling, int ChangeableProjects, int TrashedProjects);

/// <summary>The change the user chose: the source key and the new name (typed, or the target's displayed spelling).</summary>
public sealed record TagChangeRequest(TagChangeKind Kind, string FromKey, string NewName);

/// <summary>
/// The plan of a <see cref="TagChangeRequest"/> on one snapshot and its token (<c>WH-TAGPLAN/1</c>, §3.3.1). Counts:
/// <see cref="Replaced"/> rows deleted and re-inserted (new key or new spelling, source <c>CreatedAt</c> kept); <see cref="Merged"/>
/// source rows only deleted (the Project already has the target key); <see cref="AlreadySpelled"/> rows of a spelling change already
/// spelled exactly <see cref="NewName"/>; <see cref="Trashed"/> source rows of Projects in the Trash (directly or through a Folder).
/// </summary>
public sealed record TagChangePreview(
    TagChangeKind Kind,
    TagChangeOperation Operation,
    string FromKey,
    string NewKey,
    string NewName,
    int Replaced,
    int Merged,
    int AlreadySpelled,
    int Trashed,
    string Token)
{
    /// <summary>True when applying the plan would write something.</summary>
    public bool IsConfirmable => Replaced + Merged > 0;

    /// <summary>True when the preview describes exactly this request (same kind, source key and normalized new name).</summary>
    public bool Matches(TagChangeRequest request) =>
        request.Kind == Kind && string.Equals(request.FromKey, FromKey, StringComparison.Ordinal)
        && ProjectTagName.TryNormalize(request.NewName, out var name, out _) && string.Equals(name, NewName, StringComparison.Ordinal);
}

/// <summary>The result of applying a confirmed plan. <see cref="Preview"/> is the plan as applied, or the fresh plan of a Stale result.</summary>
public sealed record TagChangeResult(TagChangeOutcome Outcome, TagChangePreview? Preview, string? Error = null)
{
    public bool Committed => Outcome is TagChangeOutcome.SpellingChanged or TagChangeOutcome.Renamed or TagChangeOutcome.Merged;
}

/// <summary>
/// Phase 21 R2a: a Project editor Save sent its tags with a <see cref="ProjectDraft.TagsBaseline"/> that no longer equals the stored
/// tag set (compared as ordinal <c>(NameKey, Name)</c> pairs). Thrown before any write; the transaction is rolled back.
/// </summary>
public sealed class ProjectTagsChangedException(long nodeId)
    : Exception($"The tags of project node {nodeId} were changed after the editor read them.")
{
    public long NodeId { get; } = nodeId;
}
