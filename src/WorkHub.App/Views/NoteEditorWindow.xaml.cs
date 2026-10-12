using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using WorkHub.Presentation.ViewModels;

namespace WorkHub.App.Views;

/// <summary>
/// View-only glue for the modal Note editor. Every close path (Close/Esc via the command, title-bar X, Alt+F4, system menu,
/// owner close) ends in <see cref="OnClosing"/>: unless the view model has approved, the close is cancelled and the request
/// is handed to <see cref="NoteEditorViewModel.RequestClose"/> AFTER this event returns (no dialog inside Closing).
/// </summary>
public partial class NoteEditorWindow : Window
{
    public NoteEditorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // New note: title first. Existing note: content, caret at the start.
            if (DataContext is NoteEditorViewModel { IsNew: true })
                TitleBox.Focus();
            else
            {
                ContentBox.Focus();
                ContentBox.CaretIndex = 0;
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is NoteEditorViewModel { IsCloseApproved: false } vm)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, vm.RequestClose);
        }

        base.OnClosing(e);
    }
}
