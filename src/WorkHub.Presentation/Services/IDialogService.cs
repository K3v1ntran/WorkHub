using WorkHub.Presentation.ViewModels;

namespace WorkHub.Presentation.Services;

/// <summary>
/// Everything view models need from WPF windows, message boxes, file pickers and the clipboard.
/// Keeps view models free of WPF types so they can be unit tested.
/// </summary>
public interface IDialogService
{
    /// <returns>True when the System was saved.</returns>
    bool ShowSystemEditor(SystemEditorViewModel viewModel);

    /// <returns>True when settings were saved.</returns>
    bool ShowSettings(SettingsViewModel viewModel);

    /// <returns>True when the group was created/renamed.</returns>
    bool ShowOwnerGroupEditor(OwnerGroupEditorViewModel viewModel);

    /// <returns>True when the launcher entry was saved.</returns>
    bool ShowOwnerItemEditor(OwnerItemEditorViewModel viewModel);

    /// <returns>True when a destination group was chosen.</returns>
    bool ShowMoveOwnerItem(MoveOwnerItemViewModel viewModel);

    /// <summary>Confirmation with an explicit action button (e.g. "Delete"). Cancel is the default button.</summary>
    bool Confirm(string title, string message, string confirmText);

    /// <returns>The selected file, or null when cancelled.</returns>
    string? PickExecutable(string title, string? initialPath);

    /// <param name="filter">Win32 file filter (e.g. "XML (*.xml)|*.xml"); null for all files.</param>
    string? PickFile(string title, string? initialPath, string? filter = null);

    string? PickFolder(string title, string? initialPath);

    void CopyToClipboard(string text);
}
