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

    private MainViewModel? Main => DataContext as MainViewModel;

    private RepositoryAreaViewModel? Repository => Main?.Repository;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && PrimaryTabs.SelectedItem == RepositoryTab && PrimaryTabs.IsEnabled
            && Main?.Navigation.IsRepositoryActive == true)
        {
            ShowAndFocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && PrimaryTabs.SelectedItem == WorkspaceTab && PrimaryTabs.IsEnabled
                 && WorkspaceSearch is not null)
        {
            WorkspaceSearchBox.Focus();
            WorkspaceSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    // ---- Workspace search (view glue only; behavior lives in WorkspaceSearchViewModel) --------------------------------

    private WorkspaceSearchViewModel? WorkspaceSearch => Main?.Workspace.Search;

    private void WorkspaceSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (WorkspaceSearch is not { } search)
            return;
        switch (e.Key)
        {
            case Key.Escape:
                if (search.IsActive)
                {
                    search.ClearCommand.Execute(null);
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, BringSelectedTreeNodeIntoView);
                }
                else
                {
                    FocusWorkspaceTree();
                }

                e.Handled = true;
                break;
            case Key.Down:
            case Key.Enter:
                if (search.Results.Count > 0)
                {
                    search.SelectedResult ??= search.Results[0];
                    FocusSearchResult();
                }

                e.Handled = true;
                break;
        }
    }

    private void WorkspaceSearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (WorkspaceSearch is not { } search)
            return;
        switch (e.Key)
        {
            case Key.Enter:
                search.ActivateSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up when WorkspaceSearchResults.SelectedIndex <= 0:
            case Key.Escape:
                WorkspaceSearchBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private void WorkspaceSearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(WorkspaceSearchResults, (DependencyObject)e.OriginalSource) is ListBoxItem)
            WorkspaceSearch?.ActivateSelectedCommand.Execute(null);
    }

    private void FocusSearchResult()
    {
        var list = WorkspaceSearchResults;
        list.ScrollIntoView(list.SelectedItem);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item)
            item.Focus();
        else
            list.Focus();
    }

    private void FocusWorkspaceTree()
    {
        if (FindTreeItem(WorkspaceTree, Main?.Workspace.SelectedNode) is { } item)
            item.Focus();
        else
            WorkspaceTree.Focus();
    }

    /// <summary>A revealed (or otherwise selected) node scrolls into view once its container exists.</summary>
    private void WorkspaceTree_ItemSelected(object sender, RoutedEventArgs e) => (e.OriginalSource as TreeViewItem)?.BringIntoView();

    private void BringSelectedTreeNodeIntoView() => FindTreeItem(WorkspaceTree, Main?.Workspace.SelectedNode)?.BringIntoView();

    /// <summary>The realized container of <paramref name="node"/>, following its (expanded) ancestors; null when not realized.</summary>
    private static TreeViewItem? FindTreeItem(ItemsControl root, WorkspaceNodeViewModel? node)
    {
        if (node is null)
            return null;
        var chain = new Stack<WorkspaceNodeViewModel>();
        for (var n = node; n is not null; n = n.Parent)
            chain.Push(n);

        ItemsControl? container = root;
        TreeViewItem? item = null;
        while (chain.Count > 0 && container is not null)
        {
            item = container.ItemContainerGenerator.ContainerFromItem(chain.Pop()) as TreeViewItem;
            container = item;
        }

        return chain.Count == 0 ? item : null;
    }

    private void ContentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A row selected by a search reveal (or code) scrolls into view.
        if (ContentList.SelectedItem is { } selected)
            ContentList.ScrollIntoView(selected);
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

    private void OwnerItemList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(OwnerItemList, (DependencyObject)e.OriginalSource) is ListViewItem)
            Main?.Owner.OpenItemCommand.Execute(null);
    }

    private void WorkspaceTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // WPF trees do not select on right-click; select the clicked node so the context menu acts on it.
        for (var d = e.OriginalSource as DependencyObject; d is not null;
             d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem { DataContext: WorkspaceNodeViewModel node } item)
            {
                node.IsSelected = true;
                item.Focus();
                return;
            }
        }
    }

    private void WorkspaceTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking a node while the Workspace Trash is open shows that node again — also the node that is already selected,
        // which raises no selection change (the view model closes the panel on every other selection).
        if (Main?.Workspace is not { IsTrashOpen: true } workspace)
            return;
        for (var d = e.OriginalSource as DependencyObject; d is not null;
             d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem { DataContext: WorkspaceNodeViewModel { IsPlaceholder: false } })
            {
                workspace.CloseTrashCommand.Execute(null);
                return;
            }
        }
    }

    private ProjectContentViewModel? ProjectContent => Main?.Workspace.SelectedContent;

    private void ContentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double-click on a row opens (folder: enter; link: open target).
        if (ItemsControl.ContainerFromElement(ContentList, (DependencyObject)e.OriginalSource) is ListViewItem)
            ProjectContent?.OpenCommand.Execute(null);
    }

    private void ContentList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ProjectContent is not { } content)
            return;

        var command = e.Key switch
        {
            Key.Enter when Keyboard.Modifiers == ModifierKeys.None => content.OpenCommand,
            Key.F2 when Keyboard.Modifiers == ModifierKeys.None => content.EditCommand,
            Key.Delete when Keyboard.Modifiers == ModifierKeys.None => content.MoveToTrashCommand, // always confirmed
            Key.Back when Keyboard.Modifiers == ModifierKeys.None => content.GoUpCommand,
            Key.System when e.SystemKey == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt => content.GoUpCommand,
            _ => (System.Windows.Input.ICommand?)null,
        };
        if (command is null)
            return;

        command.Execute(null);
        e.Handled = true;
        if (command == content.GoUpCommand || command == content.OpenCommand)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ContentList.Focus()); // keep keyboard navigation in the list
    }

    /// <summary>Enter restores the selected Trash entry (the same command as the Restore button; it never runs twice at once).</summary>
    private void TrashList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ProjectContent is not { } content || e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        content.Trash.RestoreCommand.Execute(null);
        e.Handled = true;
    }

    private void WorkspaceTrashList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Main is not { } main || e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        main.Workspace.Trash.RestoreCommand.Execute(null);
        e.Handled = true;
    }

    private void OwnerMoreButton_Click(object sender, RoutedEventArgs e) => ShowButtonMenu(OwnerMoreButton);

    private void MoreButton_Click(object sender, RoutedEventArgs e) => ShowButtonMenu(MoreButton);

    private static void ShowButtonMenu(Button button)
    {
        var menu = button.ContextMenu!;
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
