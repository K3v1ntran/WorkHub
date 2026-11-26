using System.Windows;
using System.Windows.Controls;
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

    /// <summary>
    /// Phase 24 (D4): Enter in the Projects with this tag list = Show in Workspace (when enabled). It is always marked handled, so
    /// the default Close button never takes an Enter pressed in this list. Enter anywhere else is unchanged.
    /// </summary>
    private void ProjectsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ManageTagsViewModel viewModel)
            return;
        e.Handled = true;
        if (viewModel.ShowInWorkspaceCommand.CanExecute(null))
            viewModel.ShowInWorkspaceCommand.Execute(null);
    }

    /// <summary>Phase 24 (D4): a double-click on a list row = Show in Workspace (when enabled).</summary>
    private void ProjectsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || ItemsControl.ContainerFromElement(ProjectsList, source) is not ListBoxItem)
            return; // not on a row (scroll bar, empty area)
        if (DataContext is ManageTagsViewModel viewModel && viewModel.ShowInWorkspaceCommand.CanExecute(null))
            viewModel.ShowInWorkspaceCommand.Execute(null);
    }
}
