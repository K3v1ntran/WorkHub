using WorkHub.Presentation.ViewModels;

namespace WorkHub.Presentation.Services;

/// <summary>
/// Everything view models need from WPF windows, message boxes and the clipboard.
/// Keeps view models free of WPF types so they can be unit tested.
/// </summary>
public interface IDialogService
{
    /// <returns>True when the System was saved.</returns>
    bool ShowSystemEditor(SystemEditorViewModel viewModel);

    /// <returns>True when settings were saved.</returns>
    bool ShowSettings(SettingsViewModel viewModel);

    bool Confirm(string title, string message);

    /// <returns>The selected file, or null when cancelled.</returns>
    string? PickExecutable(string title, string? initialPath);

    void CopyToClipboard(string text);
}
