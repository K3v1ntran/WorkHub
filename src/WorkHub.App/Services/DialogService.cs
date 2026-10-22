using System.IO;
using System.Windows;
using Microsoft.Win32;
using WorkHub.App.Views;
using WorkHub.Presentation.Services;
using WorkHub.Presentation.ViewModels;

namespace WorkHub.App.Services;

public sealed class DialogService : IDialogService
{
    public bool ShowSystemEditor(SystemEditorViewModel viewModel)
    {
        var window = new SystemEditorWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, saved) => window.DialogResult = saved;
        return window.ShowDialog() == true;
    }

    public bool ShowSettings(SettingsViewModel viewModel)
    {
        var window = new SettingsWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, saved) => window.DialogResult = saved;
        // Title-bar close and Alt+F4 bypass the commands; keep the window open while an import is in flight.
        window.Closing += (_, e) => e.Cancel |= !viewModel.CanClose;
        return window.ShowDialog() == true;
    }

    public bool ShowOwnerGroupEditor(OwnerGroupEditorViewModel viewModel)
    {
        var window = new OwnerGroupEditorWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, saved) => window.DialogResult = saved;
        return window.ShowDialog() == true;
    }

    public bool ShowOwnerItemEditor(OwnerItemEditorViewModel viewModel)
    {
        var window = new OwnerItemEditorWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, saved) => window.DialogResult = saved;
        return window.ShowDialog() == true;
    }

    public bool ShowMoveOwnerItem(MoveOwnerItemViewModel viewModel)
    {
        var window = new MoveOwnerItemWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, moved) => window.DialogResult = moved;
        return window.ShowDialog() == true;
    }

    public bool ShowWorkspaceNameEditor(WorkspaceNameEditorViewModel viewModel)
    {
        var window = new WorkspaceNameEditorWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, saved) => window.DialogResult = saved;
        // Title-bar close and Alt+F4 bypass the commands; keep the window open while the write is pending.
        window.Closing += (_, e) => e.Cancel |= !viewModel.CanClose;
        return window.ShowDialog() == true;
    }

    public bool ShowProjectEditor(ProjectEditorViewModel viewModel)
    {
        var window = new ProjectEditorWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, saved) => window.DialogResult = saved;
        window.Closing += (_, e) => e.Cancel |= !viewModel.CanClose;
        return window.ShowDialog() == true;
    }

    public bool ShowWorkspaceMove(WorkspaceMoveViewModel viewModel)
    {
        var window = new WorkspaceMoveWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, moved) => window.DialogResult = moved;
        window.Closing += (_, e) => e.Cancel |= !viewModel.CanClose;
        return window.ShowDialog() == true;
    }

    public bool ShowUnarchive(UnarchiveProjectViewModel viewModel)
    {
        var window = new UnarchiveProjectWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, confirmed) => window.DialogResult = confirmed;
        return window.ShowDialog() == true;
    }

    public bool ShowFolderLifecycleConfirmation(FolderLifecycleConfirmationViewModel viewModel)
    {
        var window = new FolderLifecycleWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, confirmed) => window.DialogResult = confirmed;
        return window.ShowDialog() == true;
    }

    public bool ShowProjectFolderNameEditor(ProjectFolderNameEditorViewModel viewModel)
    {
        // Same window as the Workspace name editor: the view model exposes the same binding surface.
        var window = new WorkspaceNameEditorWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, saved) => window.DialogResult = saved;
        window.Closing += (_, e) => e.Cancel |= !viewModel.CanClose;
        return window.ShowDialog() == true;
    }

    public bool ShowProjectLinkEditor(ProjectLinkEditorViewModel viewModel)
    {
        var window = new ProjectLinkEditorWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, saved) => window.DialogResult = saved;
        window.Closing += (_, e) => e.Cancel |= !viewModel.CanClose;
        return window.ShowDialog() == true;
    }

    public bool ShowProjectContentMove(ProjectContentMoveViewModel viewModel)
    {
        // Same window as the Workspace move dialog: the view model exposes the same binding surface.
        var window = new WorkspaceMoveWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, moved) => window.DialogResult = moved;
        window.Closing += (_, e) => e.Cancel |= !viewModel.CanClose;
        return window.ShowDialog() == true;
    }

    public bool ShowNoteEditor(NoteEditorViewModel viewModel)
    {
        // The window cancels every close until the view model approves (see NoteEditorWindow.OnClosing).
        var window = new NoteEditorWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, savedAny) => window.DialogResult = savedAny;
        window.ShowDialog();
        return viewModel.SavedAny;
    }

    public UnsavedChangesChoice AskUnsavedChanges(string title, string message)
    {
        var window = new UnsavedChangesWindow(title, message) { Owner = ActiveWindow() };
        window.ShowDialog();
        return window.Choice; // closed with X / Esc / Cancel: Cancel
    }

    public bool Confirm(string title, string message, string confirmText) =>
        new ConfirmWindow(title, message, confirmText) { Owner = ActiveWindow() }.ShowDialog() == true;

    public string? PickExecutable(string title, string? initialPath) =>
        PickFileCore(title, initialPath, "Programs (*.exe)|*.exe");

    public string? PickFile(string title, string? initialPath, string? filter = null) =>
        PickFileCore(title, initialPath, filter ?? "All files (*.*)|*.*");

    public string? PickFolder(string title, string? initialPath)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            dialog.InitialDirectory = initialPath;
        return dialog.ShowDialog(ActiveWindow()) == true ? dialog.FolderName : null;
    }

    public void CopyToClipboard(string text) => Clipboard.SetText(text);

    public bool ShowRestoreConfirmation(RestoreConfirmationViewModel viewModel)
    {
        var window = new RestoreConfirmationWindow { DataContext = viewModel, Owner = ActiveWindow() };
        viewModel.CloseRequested += (_, confirmed) => window.DialogResult = confirmed;
        return window.ShowDialog() == true;
    }

    public void ShowRestoreBackups(BackupsViewModel viewModel)
    {
        var window = new RestoreBackupsWindow { DataContext = viewModel, Owner = ActiveWindow() };
        // While a request holds the Restore lock (staging or the confirmation is open) the window stays open.
        window.Closing += (_, e) => e.Cancel |= viewModel.IsRestoring;
        _ = viewModel.ActivateAsync();
        window.ShowDialog();
    }

    public void ShowMessage(string title, string message) =>
        MessageBox.Show(ActiveWindow() ?? Application.Current.MainWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private static string? PickFileCore(string title, string? initialPath, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        var directory = string.IsNullOrWhiteSpace(initialPath) ? null : Path.GetDirectoryName(initialPath);
        if (directory is not null && Directory.Exists(directory))
            dialog.InitialDirectory = directory;

        return dialog.ShowDialog(ActiveWindow()) == true ? dialog.FileName : null;
    }

    private static Window? ActiveWindow() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
}
