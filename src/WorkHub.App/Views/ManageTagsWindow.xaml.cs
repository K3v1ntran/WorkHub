using System.Windows;
using System.Windows.Input;
using WorkHub.Presentation.ViewModels;

namespace WorkHub.App.Views;

/// <summary>Phase 21 "Manage tags…". All behaviour lives in <see cref="Presentation.ViewModels.ManageTagsViewModel"/>.</summary>
public partial class ManageTagsWindow : Window
{
    public ManageTagsWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Phase 22 keyboard contract (proposal R2a §3.6, D3): a focused WPF Button raises Click on Enter, so in Delete everywhere…
    /// Enter on the focused Confirm is marked handled and never applies. Space on the focused, enabled Confirm still activates it (the
    /// deliberate keyboard confirmation). Esc stays the Close / cancel path. Rename / Merge keep their Phase 21 keyboard behaviour.
    /// </summary>
    private void ConfirmButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ManageTagsViewModel { IsDeleteMode: true })
            e.Handled = true;
    }
}
