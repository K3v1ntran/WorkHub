namespace WorkHub.Core.Settings;

public enum SettingsLoadStatus
{
    /// <summary>No settings file existed; defaults were written.</summary>
    Created,

    /// <summary>The existing settings file was read.</summary>
    Loaded,

    /// <summary>The existing file could not be parsed. Defaults are used in memory and the file is left untouched.</summary>
    InvalidFileUsingDefaults,
}

/// <param name="UpgradedFromVersion">Set when an older settings format was converted in memory (the file is rewritten only on the next explicit save).</param>
public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsLoadStatus Status,
    string? Problem = null,
    int? UpgradedFromVersion = null);

public interface ISettingsStore
{
    /// <summary>Loads settings, writing a defaults file only when none exists.</summary>
    SettingsLoadResult LoadOrCreate();

    /// <summary>
    /// Atomically replaces the settings file. If the current file is unreadable it is preserved
    /// next to the new one before being replaced.
    /// </summary>
    void Save(AppSettings settings);

    /// <summary>
    /// Phase 13A section-scoped save. Under the cross-process settings lock: re-reads the file, applies only
    /// <paramref name="edit"/> to the JSON document (every other member, known or not, is kept as it is), writes the result
    /// atomically and returns the settings exactly as saved. <paramref name="fallback"/> is the starting document when the
    /// file is missing, or when an unreadable file may be replaced (<see cref="SettingsEdit.AllowReplacingUnreadableFile"/>).
    /// </summary>
    /// <exception cref="IOException">The file could not be written; nothing was changed.</exception>
    SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback);

    /// <summary>
    /// Phase 16 (D12): <see cref="Update(SettingsEdit, AppSettings)"/> with a budget for everything <b>before</b> writing,
    /// measured on the budget's monotonic timestamp (never on the wall clock). The cross-process lock wait lasts at most the
    /// store's lock timeout or what is left of <paramref name="budget"/>, whichever is shorter, and the budget is checked once
    /// more just before the temp file is created. A budget that runs out before writing returns
    /// <see cref="SettingsUpdateStatus.DeadlineExpired"/> and writes nothing. Once writing has started it runs to completion
    /// (temp file, then atomic replace). A store without budget support ignores it.
    /// </summary>
    SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback, SaveBudget budget) => Update(edit, fallback);

    /// <summary>
    /// Phase 16: a read-only look at the saved UI state (window placement and start tab) for use before the window is
    /// shown. Never creates, locks or writes a file or folder. Null when the file is missing or cannot be read.
    /// </summary>
    UiSettings? PeekUi() => null;
}

/// <summary>The changes of one save. A null member is not changed.</summary>
public sealed record SettingsEdit
{
    /// <summary>The whole Repository section as edited in Settings (members unknown to this build are kept).</summary>
    public RepositorySettings? Repository { get; init; }

    /// <summary>The Workspace toolbar preferences to change (each non-null member is written on its own).</summary>
    public WorkspaceToolbarChanges? Toolbars { get; init; }

    /// <summary>Phase 16: the window placement and start tab saved when the main window closes.</summary>
    public UiStateChanges? Ui { get; init; }

    /// <summary>
    /// Phase 18: "Save notes automatically". Only <c>ui.notes.autosave</c> is written (<c>ui.notes</c> is created when missing);
    /// every other member, known or not, keeps its value.
    /// </summary>
    public bool? NotesAutoSave { get; init; }

    /// <summary>
    /// Only an explicit Settings-dialog save may replace a settings file that cannot be read (a copy of it is kept). Quick
    /// actions such as the toolbar menu never do.
    /// </summary>
    public bool AllowReplacingUnreadableFile { get; init; }
}

/// <summary>Workspace toolbar preferences to save. A null member is not changed.</summary>
public sealed record WorkspaceToolbarChanges
{
    public bool? Tree { get; init; }

    public bool? Selection { get; init; }

    public bool? Content { get; init; }

    public bool IsEmpty => Tree is null && Selection is null && Content is null;

    public static WorkspaceToolbarChanges For(WorkspaceToolbar toolbar, bool show) => toolbar switch
    {
        WorkspaceToolbar.Tree => new WorkspaceToolbarChanges { Tree = show },
        WorkspaceToolbar.Selection => new WorkspaceToolbarChanges { Selection = show },
        WorkspaceToolbar.Content => new WorkspaceToolbarChanges { Content = show },
        _ => throw new ArgumentOutOfRangeException(nameof(toolbar)),
    };
}

/// <summary>
/// Phase 16: the UI state of one close. Only these members are written: <c>ui.window.{left, top, width, height,
/// isMaximized}</c> and, when <see cref="LastPrimaryTab"/> is set, <c>ui.lastPrimaryTab</c>. Everything else in the file,
/// including unknown members inside <c>ui.window</c>, keeps its value.
/// </summary>
public sealed record UiStateChanges
{
    public required double Left { get; init; }

    public required double Top { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public required bool IsMaximized { get; init; }

    /// <summary>"Workspace" or "Repository"; null leaves the saved tab unchanged (the shell never reached Ready).</summary>
    public string? LastPrimaryTab { get; init; }
}

public enum SettingsUpdateStatus
{
    Saved,

    /// <summary>settings.json could not be read and this save may not replace it; nothing was written.</summary>
    FileUnreadable,

    /// <summary>Another WorkHub process held the settings lock for too long; nothing was written.</summary>
    LockTimeout,

    /// <summary>Phase 16 (D12): the deadline passed before writing started; nothing was written.</summary>
    DeadlineExpired,
}

/// <param name="Settings">The settings as saved (set when <paramref name="Status"/> is Saved).</param>
public sealed record SettingsUpdateResult(SettingsUpdateStatus Status, AppSettings? Settings = null);
