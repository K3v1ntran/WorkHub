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
