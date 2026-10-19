using WorkHub.Core.Repositories;

namespace WorkHub.Core.Settings;

public interface ISettingsService
{
    /// <summary>Current saved settings. Treat as read-only; edit a clone and save it explicitly.</summary>
    AppSettings Current { get; }

    event EventHandler? Changed;
}

public sealed record SettingsSaveResult(bool Saved, IReadOnlyList<SettingsIssue> Issues, string? Error = null);

/// <summary>
/// Holds the in-memory settings snapshot and performs explicit Save. Editing happens on a draft copy,
/// so Cancel needs no undo: an unsaved draft simply never reaches <see cref="Current"/> or disk.
/// <para>
/// Phase 13A save rules: saves of one instance run one at a time (a gate); each save changes only its own members of the
/// file as it is on disk at that moment (the store merges under a cross-process lock); and after a successful save
/// <see cref="Current"/> is the document that was actually written, including what another process saved meanwhile. A failed
/// save changes neither the file nor <see cref="Current"/>. There is no file watching: another open WorkHub sees a change
/// at its next save or start.
/// </para>
/// </summary>
public sealed class SettingsService(ISettingsStore store, Func<string, bool>? fileExists = null) : ISettingsService
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = AppSettings.CreateDefault();

    public AppSettings Current => Volatile.Read(ref _current);

    /// <summary>Raised after a successful save, on the context that awaited the save (the UI thread for UI callers).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Sets the settings loaded at startup. Raises <see cref="Changed"/>: view models are created before startup finishes
    /// loading, and must re-read preferences (for example the Workspace toolbar) once the real settings are known.
    /// </summary>
    public void Initialize(SettingsLoadResult loaded)
    {
        Volatile.Write(ref _current, loaded.Settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public RepositorySettings CreateRepositoryDraft() => Current.Repository.Clone();

    public IReadOnlyList<SettingsIssue> ValidateRepository(RepositorySettings draft, RepositoryUsage usage) =>
        RepositorySettingsValidator.Validate(draft, usage, _fileExists);

    /// <summary>
    /// The Settings dialog's Save: validate (including the executable check) and write the Repository section and, when
    /// given, the Workspace toolbar preferences the user changed in the dialog. Nothing is written if any error exists. As
    /// before, this explicit save may replace an unreadable settings file (a copy of it is kept).
    /// </summary>
    public async Task<SettingsSaveResult> SaveRepositoryAsync(RepositorySettings draft, RepositoryUsage usage, WorkspaceToolbarChanges? toolbars = null)
    {
        var trimmed = draft.Trimmed();
        var issues = await Task.Run(() => RepositorySettingsValidator.Validate(trimmed, usage, _fileExists));
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
            return new SettingsSaveResult(false, issues);

        return await CommitAsync(
            new SettingsEdit { Repository = trimmed, Toolbars = toolbars is { IsEmpty: false } ? toolbars : null, AllowReplacingUnreadableFile = true },
            issues);
    }

    /// <summary>
    /// The quick toolbar action (a toolbar's right-click menu): saves only that toolbar's preference; the other two stay as
    /// saved. It never replaces a settings file that cannot be read; that needs an explicit Save in Settings.
    /// </summary>
    public Task<SettingsSaveResult> SaveWorkspaceToolbarAsync(WorkspaceToolbar toolbar, bool show) =>
        CommitAsync(new SettingsEdit { Toolbars = WorkspaceToolbarChanges.For(toolbar, show) }, []);

    private async Task<SettingsSaveResult> CommitAsync(SettingsEdit edit, IReadOnlyList<SettingsIssue> issues)
    {
        SettingsSaveResult result;
        await _gate.WaitAsync();
        try
        {
            var fallback = Current; // read inside the gate: the latest saved state of this instance
            SettingsUpdateResult update;
            try
            {
                update = await Task.Run(() => store.Update(edit, fallback));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.Text.Json.JsonException)
            {
                return new SettingsSaveResult(false, issues, $"Settings could not be saved: {ex.Message}");
            }

            result = update.Status switch
            {
                SettingsUpdateStatus.Saved => new SettingsSaveResult(true, issues),
                SettingsUpdateStatus.FileUnreadable => new SettingsSaveResult(false, issues,
                    "settings.json could not be read, so this change was not saved. Open Settings and save there to replace the file (a copy of it is kept)."),
                _ => new SettingsSaveResult(false, issues,
                    "Settings are being saved by another WorkHub window. Nothing was changed; try again."),
            };
            if (result.Saved)
                Volatile.Write(ref _current, update.Settings!);
        }
        finally
        {
            _gate.Release();
        }

        if (result.Saved)
            Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }
}
