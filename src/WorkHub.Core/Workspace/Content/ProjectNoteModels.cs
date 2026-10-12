namespace WorkHub.Core.Workspace.Content;

/// <summary>A Note as listed in a container: no content (content is loaded for the preview and the editor only).</summary>
public sealed record ProjectNoteSummary(
    long Id,
    long ProjectNodeId,
    long? FolderId,
    string Title,
    long Revision,
    DateTimeOffset UpdatedAt);

/// <summary>A persisted plain-text Note. <see cref="FolderId"/> null = Project root. <see cref="Revision"/> is the content revision.</summary>
public sealed record ProjectNote(
    long Id,
    long ProjectNodeId,
    long? FolderId,
    string Title,
    string Content,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public ProjectNoteSummary Summary => new(Id, ProjectNodeId, FolderId, Title, Revision, UpdatedAt);
}

/// <summary>User-editable Note fields (validated by <see cref="ProjectNoteValidator"/>).</summary>
public sealed record ProjectNoteDraft(string? Title, string? Content);

public enum NoteSaveStatus
{
    Saved,

    /// <summary>The note no longer exists.</summary>
    NotFound,

    /// <summary>The note exists at a different revision than the caller expected (changed elsewhere).</summary>
    Conflict,
}

/// <summary>Store-level outcome of a revision-guarded save, classified inside the save transaction.</summary>
public sealed record NoteStoreSaveResult(NoteSaveStatus Status, ProjectNote? Note, long? CurrentRevision)
{
    public static NoteStoreSaveResult Saved(ProjectNote note) => new(NoteSaveStatus.Saved, note, null);

    public static NoteStoreSaveResult NotFound { get; } = new(NoteSaveStatus.NotFound, null, null);

    public static NoteStoreSaveResult Conflict(long currentRevision) => new(NoteSaveStatus.Conflict, null, currentRevision);
}

public enum NoteWriteStatus
{
    Saved,

    /// <summary>Validation failed (see <see cref="NoteWriteResult.Field"/>); nothing was written.</summary>
    Invalid,

    NotFound,

    Conflict,

    /// <summary>The Project or folder the note belongs in no longer exists (create / save as new); nothing was written.</summary>
    LocationMissing,

    /// <summary>The write failed unexpectedly (database busy, I/O); nothing was written.</summary>
    Error,
}

/// <summary>Service-level outcome of a Note create / save / save-as-new, with a user-facing message for failures.</summary>
public sealed record NoteWriteResult(NoteWriteStatus Status, ProjectNote? Note, string? Field, string? Error, long? CurrentRevision)
{
    public bool Succeeded => Status == NoteWriteStatus.Saved;

    /// <summary>Copy draft / Save as new note are offered for these outcomes.</summary>
    public bool OffersRecovery => Status is NoteWriteStatus.NotFound or NoteWriteStatus.Conflict;

    public static NoteWriteResult Ok(ProjectNote note) => new(NoteWriteStatus.Saved, note, null, null, null);

    public static NoteWriteResult Failed(NoteWriteStatus status, string error, string? field = null, long? currentRevision = null) =>
        new(status, null, field, error, currentRevision);
}

/// <summary>
/// Validates a <see cref="ProjectNoteDraft"/>. Title follows <see cref="WorkspaceName"/> (trimmed, 1–200, no control
/// characters). Content is plain text kept EXACTLY as typed (no trimming, no line-break or Unicode normalization), may be
/// empty, at most <see cref="MaxContentLength"/> UTF-16 units, and must not contain U+0000 or ill-formed UTF-16 (which
/// would not round-trip through SQLite's UTF-8 storage).
/// </summary>
public static class ProjectNoteValidator
{
    public const int MaxContentLength = 100000;

    public static class Fields
    {
        public const string Title = nameof(Title);
        public const string Content = nameof(Content);
    }

    public static (ProjectNoteDraft? Draft, string? Field, string? Error) Validate(ProjectNoteDraft draft)
    {
        if (!WorkspaceName.TryNormalize(draft.Title, out var title, out var titleError))
            return (null, Fields.Title, titleError);

        var content = draft.Content ?? string.Empty;
        if (content.Length > MaxContentLength)
            return (null, Fields.Content, $"Content must be at most {MaxContentLength:N0} characters (it has {content.Length:N0}).");
        if (content.Contains('\0'))
            return (null, Fields.Content, "Content contains a null character, which cannot be saved.");
        if (!IsWellFormedUtf16(content))
            return (null, Fields.Content, "Content contains invalid characters.");

        return (new ProjectNoteDraft(title, content), null, null);
    }

    private static bool IsWellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                i++;
            else if (char.IsSurrogate(value[i]))
                return false;
        }

        return true;
    }
}
