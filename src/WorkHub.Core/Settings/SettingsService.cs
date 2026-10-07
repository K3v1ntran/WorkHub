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
/// </summary>
public sealed class SettingsService(ISettingsStore store, Func<string, bool>? fileExists = null) : ISettingsService
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private AppSettings _current = AppSettings.CreateDefault();

    public AppSettings Current => Volatile.Read(ref _current);

    public event EventHandler? Changed;

    public void Initialize(SettingsLoadResult loaded) => Volatile.Write(ref _current, loaded.Settings);

    public RepositorySettings CreateRepositoryDraft() => Current.Repository.Clone();

    public IReadOnlyList<SettingsIssue> ValidateRepository(RepositorySettings draft, RepositoryUsage usage) =>
        RepositorySettingsValidator.Validate(draft, usage, _fileExists);

    /// <summary>Validate (including the executable check) and write, all off the caller's thread. Nothing is written if any error exists.</summary>
    public async Task<SettingsSaveResult> SaveRepositoryAsync(RepositorySettings draft, RepositoryUsage usage)
    {
        var trimmed = draft.Trimmed();
        var next = Current.Clone();
        next.SettingsVersion = AppSettings.CurrentSettingsVersion;
        next.Repository = trimmed;

        var result = await Task.Run(() =>
        {
            var issues = RepositorySettingsValidator.Validate(trimmed, usage, _fileExists);
            if (issues.Any(i => i.Severity == IssueSeverity.Error))
                return new SettingsSaveResult(false, issues);

            try
            {
                store.Save(next);
                return new SettingsSaveResult(true, issues);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SettingsSaveResult(false, issues, $"Settings could not be saved: {ex.Message}");
            }
        }).ConfigureAwait(false);

        if (result.Saved)
        {
            Volatile.Write(ref _current, next);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }
}
