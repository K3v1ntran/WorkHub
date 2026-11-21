namespace WorkHub.Core.Workspace;

// Phase 22 (proposal Revision 2 + R2a §3.3): delete one Project tag key everywhere. Schema v10, no migration. Own records, outcome
// and token format (WH-TAGDEL/1) so a rename/merge token can never authorize a delete and vice versa (D5).

public enum TagDeleteOutcome
{
    /// <summary>The confirmed plan was applied: every eligible row of the key was deleted in one transaction.</summary>
    Deleted,

    /// <summary>The plan under the write lock differs from the confirmed one (checked FIRST); nothing written; <see cref="TagDeleteResult.Preview"/> is the fresh plan.</summary>
    Stale,

    /// <summary>The token matched but the plan has no Project outside the Trash carrying the key; nothing written.</summary>
    NothingToDelete,

    /// <summary>The key is empty or white space; refused by the service before the store is called.</summary>
    InvalidKey,
}

/// <summary>
/// The plan of deleting <see cref="FromKey"/> everywhere on one snapshot and its token (<c>WH-TAGDEL/1</c>, §3.3).
/// <see cref="Deleted"/>: rows of Projects that are not in the Trash (live or archived, effective state) — these would be deleted;
/// <see cref="Trashed"/>: rows of Projects in the Trash (directly or through a Folder) — these are kept. <see cref="Spelling"/> is the
/// most used stored spelling (for messages only; not part of the token).
/// </summary>
public sealed record TagDeletePreview(string FromKey, string Spelling, int Deleted, int Trashed, string Token)
{
    /// <summary>True when applying the plan would delete something.</summary>
    public bool IsConfirmable => Deleted > 0;

    /// <summary>True when the preview describes exactly this key.</summary>
    public bool Matches(string fromKey) => string.Equals(fromKey, FromKey, StringComparison.Ordinal);
}

/// <summary>The result of applying a confirmed delete plan. <see cref="Preview"/> is the plan as applied, or the fresh plan of a Stale / NothingToDelete result.</summary>
public sealed record TagDeleteResult(TagDeleteOutcome Outcome, TagDeletePreview? Preview, string? Error = null)
{
    public bool Committed => Outcome == TagDeleteOutcome.Deleted;
}
