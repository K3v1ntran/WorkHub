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
}
