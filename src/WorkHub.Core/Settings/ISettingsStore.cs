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
}

/// <summary>The changes of one save. A null member is not changed.</summary>
public sealed record SettingsEdit
{
    /// <summary>The whole Repository section as edited in Settings (members unknown to this build are kept).</summary>
    public RepositorySettings? Repository { get; init; }

    /// <summary>The Workspace toolbar preferences to change (each non-null member is written on its own).</summary>
    public WorkspaceToolbarChanges? Toolbars { get; init; }

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

public enum SettingsUpdateStatus
{
    Saved,

    /// <summary>settings.json could not be read and this save may not replace it; nothing was written.</summary>
    FileUnreadable,

    /// <summary>Another WorkHub process held the settings lock for too long; nothing was written.</summary>
    LockTimeout,
}

/// <param name="Settings">The settings as saved (set when <paramref name="Status"/> is Saved).</param>
public sealed record SettingsUpdateResult(SettingsUpdateStatus Status, AppSettings? Settings = null);
