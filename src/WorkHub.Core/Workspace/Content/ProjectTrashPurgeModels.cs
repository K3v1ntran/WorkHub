namespace WorkHub.Core.Workspace.Content;

/// <summary>Phase 14: what a permanent deletion from a Project's content Trash covers.</summary>
public enum PurgeMode
{
    /// <summary>The selected entries (1..n) plus every entry trashed separately inside them (the dependent closure).</summary>
    SelectedEntries,

    /// <summary>Every entry of the Project's Trash, re-enumerated when the deletion is finally validated.</summary>
    EntireTrash,
}

/// <summary>
/// A normalized purge request: selected ids are distinct and ascending, so selection order never matters. An
/// <see cref="PurgeMode.EntireTrash"/> request carries no ids (passing any is a programming error).
/// </summary>
public sealed record PurgeRequest
{
    private PurgeRequest(long projectNodeId, PurgeMode mode, IReadOnlyList<long> selectedEntryIds)
    {
        ProjectNodeId = projectNodeId;
        Mode = mode;
        SelectedEntryIds = selectedEntryIds;
    }

    public long ProjectNodeId { get; }

    public PurgeMode Mode { get; }

    /// <summary>The selected roots (not the expanded closure); empty for <see cref="PurgeMode.EntireTrash"/>.</summary>
    public IReadOnlyList<long> SelectedEntryIds { get; }

    public static PurgeRequest Selected(long projectNodeId, IEnumerable<long> entryIds) =>
        new(projectNodeId, PurgeMode.SelectedEntries, entryIds.Distinct().Order().ToList());

    public static PurgeRequest EntireTrash(long projectNodeId) => new(projectNodeId, PurgeMode.EntireTrash, []);

    /// <exception cref="ArgumentException"><see cref="PurgeMode.EntireTrash"/> with selected ids.</exception>
    public static PurgeRequest Create(long projectNodeId, PurgeMode mode, IEnumerable<long>? entryIds)
    {
        var ids = entryIds?.ToList() ?? [];
        if (mode == PurgeMode.EntireTrash && ids.Count > 0)
            throw new ArgumentException("An Empty Trash request takes no entry ids.", nameof(entryIds));
        return mode == PurgeMode.EntireTrash ? EntireTrash(projectNodeId) : Selected(projectNodeId, ids);
    }

    public bool Equals(PurgeRequest? other) =>
        other is not null && ProjectNodeId == other.ProjectNodeId && Mode == other.Mode && SelectedEntryIds.SequenceEqual(other.SelectedEntryIds);

    public override int GetHashCode() => HashCode.Combine(ProjectNodeId, Mode, SelectedEntryIds.Count);
}

/// <summary>
/// The typed state of a purge preview. <see cref="Ready"/> is the only success: a ready preview is neither a deletion nor a
/// refusal. Every other value is a refusal that wrote nothing; the same values report a refusal at commit time.
/// </summary>
public enum PurgePreviewStatus
{
    Ready,

    /// <summary>A selected-entries request without ids.</summary>
    EmptySelection,

    /// <summary>The Project's Trash has no entries (Empty Trash).</summary>
    TrashEmpty,

    ProjectNotFound,

    /// <summary>The Project's EFFECTIVE state is Trashed: in the Workspace Trash itself or below a trashed Workspace Folder.</summary>
    ProjectTrashed,

    /// <summary>A selected id is not (or no longer) an entry of this Project. The whole request is refused; no partial purge.</summary>
    EntryNotFound,

    /// <summary>A Workspace or content ancestry walk was broken, crossed Projects or hit its depth bound (fails closed).</summary>
    AncestryUnverified,

    /// <summary>The Trash rows do not match their entries (count, marker or origin invariant); fails closed.</summary>
    TrashDataInconsistent,
}

/// <summary>
/// One entry as the confirmation shows it, taken from the same snapshot as the preview's token. <see cref="ContainingEntryId"/>
/// is set for a dependent: an entry trashed separately inside a folder that another entry contains.
/// </summary>
/// <param name="Depth">0 for a listed root; 1.. for dependents below it (display indentation).</param>
public sealed record PurgeEntryView(
    long EntryId,
    bool Selected,
    long? ContainingEntryId,
    ContentKind Kind,
    long RootId,
    string RootName,
    int ItemCount,
    DateTimeOffset TrashedAt,
    ProjectContainerPath From,
    bool FromTrashed,
    int Depth);

public sealed record PurgeCounts(int Entries, int Folders, int Links, int Notes)
{
    public int Items => Folders + Links + Notes;
}

/// <summary>
/// A ready preview. Every value a confirmation shows comes from <see cref="Entries"/>, <see cref="ProjectName"/>,
/// <see cref="EffectiveState"/> and <see cref="Counts"/>; <see cref="Token"/> (P14-PURGE-2) binds them and the exact deletion
/// plan, and is what the commit is checked against.
/// </summary>
public sealed record PurgePreview(
    PurgeRequest Request,
    string ProjectName,
    LifecycleState EffectiveState,
    IReadOnlyList<PurgeEntryView> Entries,
    PurgeCounts Counts,
    string Token);

public sealed record PurgePreviewResult(PurgePreviewStatus Status, PurgePreview? Preview)
{
    public bool IsReady => Status == PurgePreviewStatus.Ready;

    public static PurgePreviewResult Ready(PurgePreview preview) => new(PurgePreviewStatus.Ready, preview);

    public static PurgePreviewResult Refused(PurgePreviewStatus status) =>
        status == PurgePreviewStatus.Ready ? throw new ArgumentException("A refusal is never Ready.", nameof(status)) : new(status, null);
}

/// <summary>
/// Progress of one purge. <see cref="Deleting"/> is the non-cancellable stage: it is reported immediately before the final
/// cancellation check, after which the deletion runs to its committed or rolled-back end.
/// </summary>
public enum PurgeStage
{
    MakingBackup,
    BackupPublished,
    Deleting,
}

public enum PurgeOutcome
{
    /// <summary>Committed: the rows are gone from the active database (<see cref="PurgeResult.Committed"/> is true).</summary>
    Purged,

    /// <summary>Cancelled before deletion began. Nothing was deleted; a published PreDelete copy, if any, is kept.</summary>
    Cancelled,

    /// <summary>Refused by a typed rule (<see cref="PurgeResult.Refusal"/>); nothing was deleted.</summary>
    Refused,

    /// <summary>A displayed value or the deletion set changed since the confirmation; nothing was deleted, never resubmitted.</summary>
    ConfirmationStale,

    /// <summary>The PreDelete copy could not be made or validated; nothing was deleted.</summary>
    BackupFailed,

    /// <summary>Another connection held the database for writing; nothing was deleted.</summary>
    DatabaseBusy,

    /// <summary>An unexpected error or a count mismatch rolled the deletion back; nothing was deleted.</summary>
    Failed,
}

/// <param name="Committed">True only for <see cref="PurgeOutcome.Purged"/>. Every other outcome deleted nothing.</param>
/// <param name="BackupFileName">The published PreDelete copy (kept in every case once published), or null when none was published.</param>
public sealed record PurgeResult(
    PurgeOutcome Outcome,
    bool Committed,
    PurgePreviewStatus? Refusal = null,
    PurgeCounts? Deleted = null,
    string? BackupFileName = null,
    string? Detail = null)
{
    /// <summary>A valid PreDelete copy exists although nothing was deleted (cancelled, stale, busy or failed after publication).</summary>
    public bool BackupKept => !Committed && BackupFileName is not null;
}

/// <summary>
/// Phase 14: permanent deletion from a Project's content Trash. Rows leave the ACTIVE database only; the PreDelete copy and
/// other backups may still contain them, and nothing a link points to is ever touched.
/// </summary>
public interface IProjectTrashPurge
{
    /// <summary>One read snapshot: the plan, every displayed value and the token. Never writes.</summary>
    Task<PurgePreviewResult> PreparePurgeAsync(PurgeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Backup (PreDelete, validated, token rechecked in the backup's snapshot and in the copy), then ONE <c>BEGIN IMMEDIATE</c>
    /// transaction that revalidates everything and deletes. <paramref name="cancellationToken"/> is honoured until
    /// <see cref="PurgeStage.Deleting"/> begins; after that the committed or rolled-back result is reported.
    /// </summary>
    Task<PurgeResult> PurgeAsync(PurgeRequest request, string confirmedToken, IProgress<PurgeStage>? progress = null, CancellationToken cancellationToken = default);
}
