using System.IO;
using System.Windows;
using Microsoft.Win32;
using WorkHub.Presentation.Services;
using WorkHub.Presentation.ViewModels;
using WorkHub.App.Views;

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

    public bool Confirm(string title, string message) =>
        MessageBox.Show(ActiveWindow(), message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
        == MessageBoxResult.OK;

    public string? PickExecutable(string title, string? initialPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "TortoiseProc.exe|TortoiseProc.exe|Programs (*.exe)|*.exe",
            CheckFileExists = true,
        };

        var directory = string.IsNullOrWhiteSpace(initialPath) ? null : Path.GetDirectoryName(initialPath);
        if (directory is not null && Directory.Exists(directory))
            dialog.InitialDirectory = directory;

        return dialog.ShowDialog(ActiveWindow()) == true ? dialog.FileName : null;
    }

    public void CopyToClipboard(string text) => Clipboard.SetText(text);

    private static Window? ActiveWindow() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
}
