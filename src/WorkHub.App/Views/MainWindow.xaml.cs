using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WorkHub.Presentation.ViewModels;

namespace WorkHub.App.Views;

/// <summary>View-only glue: focus handling and gestures that have no clean XAML binding. No business logic.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private RepositoryAreaViewModel? Repository => (DataContext as MainViewModel)?.Repository;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && PrimaryTabs.SelectedItem == RepositoryTab && PrimaryTabs.IsEnabled)
        {
            ShowAndFocusSearch();
            e.Handled = true;
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => ShowAndFocusSearch();

    private void ShowAndFocusSearch()
    {
        Repository?.ShowSearchCommand.Execute(null);
        // The box becomes visible on the next layout pass; focus it then.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        });
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Repository?.CloseSearchCommand.Execute(null);
                if (Repository?.IsSearchVisible == false)
                    FocusList();
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Enter:
                FocusList();
                e.Handled = true;
                break;
        }
    }

    private void FocusList()
    {
        if (SystemList.SelectedItem is not null && SystemList.ItemContainerGenerator.ContainerFromItem(SystemList.SelectedItem) is ListViewItem row)
            row.Focus();
        else
            SystemList.Focus();
    }

    private void SystemList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row opens; double-clicking the scrollbar or empty space does nothing.
        if (ItemsControl.ContainerFromElement(SystemList, (DependencyObject)e.OriginalSource) is ListViewItem)
            Repository?.OpenSelectedCommand.Execute(null);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = MoreButton.ContextMenu!;
        menu.DataContext = MoreButton.DataContext;
        menu.PlacementTarget = MoreButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
