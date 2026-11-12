namespace WorkHub.Core.Workspace.Content;

/// <summary>Phase 15: Copy leaves the original in place; Move copies, then puts the original in the source Project's Trash.</summary>
public enum CrossProjectMode
{
    Copy,
    Move,
}

/// <summary>
/// Copy or Move one content item (a folder with its ACTIVE subtree, a link or a note) to ANOTHER Project's root
/// (<see cref="TargetFolderId"/> null) or one of its active folders. <see cref="RootName"/> is the name the user confirmed for a
/// copied folder root (raw text; validated by the store with the existing Workspace name rules); null keeps the source name.
/// It is ignored for links and notes.
/// </summary>
public sealed record CrossProjectRequest(CrossProjectMode Mode, ContentRef Source, long TargetProjectNodeId, long? TargetFolderId, string? RootName = null);

/// <summary>
/// Preview and commit status. Refusals are evaluated in this order (proposal Revision 2 §3.6 with the §0.3 update), in the
/// preview and again under the commit lock, BEFORE the confirmation token is compared.
/// </summary>
public enum CrossProjectStatus
{
    Ready,
    SourceNotFound,
    SourceProjectTrashed,
    SourceTrashed,
    TargetProjectNotFound,
    SameProject,
    TargetProjectTrashed,
    TargetFolderNotFound,
    TargetFolderWrongProject,
    TargetFolderTrashed,
    InvalidName,
    NameConflict,
    SourceUnverified,
    DepthExceeded,
    TooLarge,
}

/// <summary>The provisional operation bounds (D-15.6): copied rows and copied UTF-8 payload.</summary>
public static class CrossProjectLimits
{
    public const int MaxRows = 10000;

    /// <summary>
    /// 32 MiB (33,554,432 bytes) of UTF-8: folder names (the root with its confirmed name), link names and targets, note titles and
    /// contents (D-15.6, decided 2026-11-12: a 64 MiB Move held the write lock for up to about 2 s; 32 MiB about 1 s).
    /// </summary>
    public const long MaxPayloadBytes = 32L * 1024 * 1024;
}

/// <summary>
/// One read snapshot of a cross-Project operation: what will be copied, what is skipped, both displayed paths and the token
/// binding all of it. <see cref="Token"/> is set only when <see cref="Status"/> is <see cref="CrossProjectStatus.Ready"/>.
/// <see cref="SuggestedName"/> is offered with <see cref="CrossProjectStatus.NameConflict"/>; it is only a suggestion.
/// </summary>
public sealed record CrossProjectPreview(CrossProjectStatus Status)
{
    public string? Token { get; init; }

    public string RootName { get; init; } = string.Empty;

    public int Folders { get; init; }

    public int Links { get; init; }

    public int Notes { get; init; }

    /// <summary>Content rows below the source root that are in the Trash (each counted once); never copied.</summary>
    public int SkippedItems { get; init; }

    /// <summary>Distinct Trash entries among <see cref="SkippedItems"/> (secondary text, D-15.7).</summary>
    public int SkippedEntries { get; init; }

    public long PayloadBytes { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    public string TargetPath { get; init; } = string.Empty;

    public string SourceProjectName { get; init; } = string.Empty;

    /// <summary>The source item's CURRENT name or title in this snapshot (bound by <see cref="Token"/>; the dialog title uses it).</summary>
    public string SourceName { get; init; } = string.Empty;

    public bool TargetArchived { get; init; }

    public string? SuggestedName { get; init; }

    public bool IsReady => Status == CrossProjectStatus.Ready;

    public int Rows => Folders + Links + Notes;
}

public enum CrossProjectOutcome
{
    Copied,
    Moved,
    Refused,
    ConfirmationStale,
    DatabaseBusy,
}

/// <summary>
/// A commit result. <see cref="NewRoot"/> is the copied root's identity in the destination (Open destination re-reads its
/// current location by it). <see cref="SourceTrashEntryId"/> is the source's Trash entry after a Move. Refused, stale and busy
/// results wrote nothing.
/// </summary>
public sealed record CrossProjectResult(CrossProjectOutcome Outcome)
{
    public CrossProjectStatus? Refusal { get; init; }

    public ContentRef? NewRoot { get; init; }

    public long TargetProjectNodeId { get; init; }

    public string TargetPath { get; init; } = string.Empty;

    public string RootName { get; init; } = string.Empty;

    public int Rows { get; init; }

    public long? SourceTrashEntryId { get; init; }

    public string SourceProjectName { get; init; } = string.Empty;

    public bool Committed => Outcome is CrossProjectOutcome.Copied or CrossProjectOutcome.Moved;
}

/// <summary>A Project that can receive copied content: live or archived (labelled), never the source, never trashed.</summary>
public sealed record CrossProjectTarget(long ProjectNodeId, string Path, bool Archived);
