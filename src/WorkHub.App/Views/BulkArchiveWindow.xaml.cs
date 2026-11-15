using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkHub.Presentation.ViewModels;

namespace WorkHub.App.Views;

/// <summary>Phase 19 "Archive projects…" checklist. All behaviour lives in <see cref="BulkArchiveViewModel"/>.</summary>
public partial class BulkArchiveWindow : Window
{
    public BulkArchiveWindow()
    {
        InitializeComponent();
    }

    /// <summary>Space on the focused row toggles its checkbox (the checkboxes themselves are not focusable).</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (e.OriginalSource is ListBoxItem { DataContext: BulkArchiveRowViewModel row } && DataContext is BulkArchiveViewModel viewModel)
        {
            viewModel.Toggle(row);
            e.Handled = true;
        }
    }
}
