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

    /// <returns>True when the folder was created or the node renamed.</returns>
    bool ShowWorkspaceNameEditor(WorkspaceNameEditorViewModel viewModel);

    /// <returns>True when the Project was created or saved.</returns>
    bool ShowProjectEditor(ProjectEditorViewModel viewModel);

    /// <returns>True when the move was confirmed (see <see cref="WorkspaceMoveViewModel.Moved"/>).</returns>
    bool ShowWorkspaceMove(WorkspaceMoveViewModel viewModel);

    /// <summary>Phase 12: the Unarchive confirmation with its status choice. Cancel is the default.</summary>
    /// <returns>True when the user chose Unarchive (the status is <see cref="UnarchiveProjectViewModel.Status"/>).</returns>
    bool ShowUnarchive(UnarchiveProjectViewModel viewModel);

    /// <summary>
    /// Phase 13: the Folder Archive / Folder Trash confirmation (bounded preview of one computed confirmation, fixed maximum
    /// size). Cancel is the default; Enter and Esc both mean Cancel.
    /// </summary>
    /// <returns>True only when the user clicked the action button.</returns>
    bool ShowFolderLifecycleConfirmation(FolderLifecycleConfirmationViewModel viewModel);

    /// <returns>True when the ProjectFolder was created or renamed.</returns>
    bool ShowProjectFolderNameEditor(ProjectFolderNameEditorViewModel viewModel);

    /// <returns>True when the Link was created or saved.</returns>
    bool ShowProjectLinkEditor(ProjectLinkEditorViewModel viewModel);

    /// <returns>True when the move was confirmed (see <see cref="ProjectContentMoveViewModel.Moved"/>).</returns>
    bool ShowProjectContentMove(ProjectContentMoveViewModel viewModel);

    /// <summary>
    /// Shows the modal Note editor. Every close path (Close, Esc, title-bar X, Alt+F4, system menu, owner close) must go
    /// through <see cref="NoteEditorViewModel.RequestClose"/>; the window closes only once the view model approves.
    /// </summary>
    /// <returns>True when something was persisted during this editor session. Callers must NOT use it to decide whether to
    /// reconcile (see <see cref="NoteEditorViewModel.TouchedNoteIds"/>).</returns>
    bool ShowNoteEditor(NoteEditorViewModel viewModel);

    /// <summary>Save / Don't save / Cancel prompt. Closing the prompt window means <see cref="UnsavedChangesChoice.Cancel"/>.</summary>
    UnsavedChangesChoice AskUnsavedChanges(string title, string message);

    /// <summary>Confirmation with an explicit action button (e.g. "Delete"). Cancel is the default button.</summary>
    bool Confirm(string title, string message, string confirmText);

    /// <returns>The selected file, or null when cancelled.</returns>
    string? PickExecutable(string title, string? initialPath);

    /// <param name="filter">Win32 file filter (e.g. "XML (*.xml)|*.xml"); null for all files.</param>
    string? PickFile(string title, string? initialPath, string? filter = null);

    string? PickFolder(string title, string? initialPath);

    void CopyToClipboard(string text);

    /// <summary>Phase 10: the restore confirmation (shown while the Restore lock is held). Cancel is the default.</summary>
    /// <returns>True when the user chose Restore and restart.</returns>
    bool ShowRestoreConfirmation(RestoreConfirmationViewModel viewModel);

    /// <summary>Phase 10: the backups list with Restore…, opened from the Failed shell (Settings is unreachable there).</summary>
    void ShowRestoreBackups(BackupsViewModel viewModel);

    /// <summary>An information or error message with an OK button.</summary>
    void ShowMessage(string title, string message);
}

public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel,
}
