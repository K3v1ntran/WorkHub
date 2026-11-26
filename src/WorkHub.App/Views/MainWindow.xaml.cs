using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WorkHub.App.Services;
using WorkHub.Core.Settings;
using WorkHub.Presentation.Services;
using WorkHub.Presentation.ViewModels;

namespace WorkHub.App.Views;

/// <summary>View-only glue: focus handling and gestures that have no clean XAML binding. No business logic.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        // Phase 10 (R17): while a restore or Put back moves database files, the window does not close.
        Closing += (_, e) => e.Cancel |= Main?.IsCloseBlocked == true;
        // A placement applied before Show (e.g. maximized) raises no StateChanged; Loaded records it.
        Loaded += (_, _) => RememberState();
        StateChanged += (_, _) => RememberState();
    }

    /// <summary>Phase 16A: the process's one window-state saver (set by the composition root).</summary>
    internal WindowStateSaver? WindowStateSaver { get; init; }

    private WindowState _lastNonMinimizedState = WindowState.Normal;

    private void RememberState()
    {
        if (WindowState != WindowState.Minimized)
            _lastNonMinimizedState = WindowState;
    }

    /// <summary>
    /// Phase 16A trigger (a): after every <c>Closing</c> handler (including the <c>IsCloseBlocked</c> check) has run. A cancelled
    /// close saves nothing.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        WindowStateSaver?.OnWindowClosing(e.Cancel);
    }

    /// <summary>The snapshot for the window-state save, read on the UI thread at the trigger.</summary>
    internal UiStateChanges? CaptureUiState()
    {
        var (bounds, maximized) = WindowPlacement.Read(this, _lastNonMinimizedState);
        return new UiStateChanges
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = maximized,
            LastPrimaryTab = Main?.PrimaryTabToSave,
        };
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
        else if (PrimaryTabs.SelectedItem == WorkspaceTab && PrimaryTabs.IsEnabled && Main?.Workspace is { } workspace
                 && WorkspaceShortcut(e, workspace) is { } command)
        {
            // Phase 13A shortcuts (checked against every existing binding: no conflict). Same commands as the buttons.
            if (command.CanExecute(null))
                command.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+I (Info), Ctrl+Shift+A (Archived projects), Ctrl+Shift+T (Trashed projects) on the Workspace tab.</summary>
    private static ICommand? WorkspaceShortcut(KeyEventArgs e, WorkspaceAreaViewModel workspace) => (e.Key, Keyboard.Modifiers) switch
    {
        (Key.I, ModifierKeys.Control) => workspace.ToggleInfoCommand,
        (Key.A, ModifierKeys.Control | ModifierKeys.Shift) => workspace.OpenArchivedCommand,
        (Key.T, ModifierKeys.Control | ModifierKeys.Shift) => workspace.OpenTrashCommand,
        _ => null,
    };

    // ---- Workspace selection strip and Info drawer (view glue only) ------------------------------------------------------

    private void SelectionMoreButton_Click(object sender, RoutedEventArgs e) => ShowButtonMenu(SelectionMoreButton);

    private void TreeMoreButton_Click(object sender, RoutedEventArgs e) => ShowButtonMenu(TreeMoreButton);

    private void ContentMoreButton_Click(object sender, RoutedEventArgs e) => ShowButtonMenu(ContentMoreButton);

    private WorkspaceAreaViewModel? _observedWorkspace;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != DataContextProperty)
            return;
        if (_observedWorkspace is not null)
            _observedWorkspace.PropertyChanged -= OnWorkspacePropertyChanged;
        _observedWorkspace = Main?.Workspace;
        if (_observedWorkspace is not null)
            _observedWorkspace.PropertyChanged += OnWorkspacePropertyChanged;
    }

    /// <summary>
    /// Info focus (§5.4): opening moves focus into the drawer; closing returns it to the Info button, but only when focus was
    /// inside the drawer (a selection change in the tree keeps focus in the tree).
    /// </summary>
    private void OnWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WorkspaceAreaViewModel.IsInfoOpen) || _observedWorkspace is not { } workspace)
            return;
        if (workspace.IsInfoOpen)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => InfoCloseButton.Focus());
        }
        else if (_focusWasInInfo || InfoDrawer.IsKeyboardFocusWithin)
        {
            _focusWasInInfo = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => InfoToggle.Focus());
        }
    }

    private bool _focusWasInInfo;

    /// <summary>Remembers whether focus is inside the drawer while it is open (collapsing it must not erase that fact).</summary>
    private void InfoDrawer_IsKeyboardFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_observedWorkspace?.IsInfoOpen == true)
            _focusWasInInfo = (bool)e.NewValue;
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

    // ---- Phase 25 (proposal Revision 2 + R2a): the tree's hit-test contract (§2.3), background click (R2), drag and drop (R1) ----

    /// <summary>The drag data format: the dragged node's Id only (never a container; the tree recycles them).</summary>
    private const string WorkspaceNodeDragFormat = "WorkHub.WorkspaceNodeId";

    private (Point Start, long NodeId)? _treeDragCandidate;
    private int? _treeDragSession;
    private Point? _lastTreeDragPosition;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// Review fix (hover expiry): the Folder whose row area is under the pointer NOW, by the same §2.3 classifier — the current cursor
    /// position (the last DragOver position if it cannot be read) is hit-tested against the tree as it is laid out at this moment, so a
    /// layout change or a recycled container is seen without a new DragOver. Null when it is not a Folder row area.
    /// </summary>
    private long? FolderRowUnderPointer()
    {
        Point? position = _lastTreeDragPosition;
        try
        {
            if (PresentationSource.FromVisual(WorkspaceTree) is not null && GetCursorPos(out var cursor))
                position = WorkspaceTree.PointFromScreen(new Point(cursor.X, cursor.Y));
        }
        catch (InvalidOperationException)
        {
            // not connected to a presentation source: keep the last DragOver position
        }

        if (position is not { } p || p.X < 0 || p.Y < 0 || p.X >= WorkspaceTree.ActualWidth || p.Y >= WorkspaceTree.ActualHeight)
            return null;
        var visual = System.Windows.Media.VisualTreeHelper.HitTest(WorkspaceTree, p)?.VisualHit;
        return ClassifyTreePoint(visual) is { Kind: TreeHitKind.Row, Node: { IsPlaceholder: false, IsFolder: true } folder } ? folder.Id : null;
    }

    /// <summary>§2.3: classifies a pointer position from the event's original source (first ScrollBar / Expander / Row / the tree wins).</summary>
    private TreeHit ClassifyTreePoint(object? originalSource) => WorkspaceTreeHitTest.Classify(TreeHitChain(originalSource as DependencyObject));

    private IEnumerable<TreeHitElement> TreeHitChain(DependencyObject? d)
    {
        for (; d is not null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, WorkspaceTree))
            {
                yield return new TreeHitElement(TreeHitElementKind.Tree);
                yield break;
            }

            yield return d switch
            {
                System.Windows.Controls.Primitives.ScrollBar => new TreeHitElement(TreeHitElementKind.ScrollBar),
                System.Windows.Controls.Primitives.ToggleButton { Name: "Expander", TemplatedParent: TreeViewItem item } =>
                    new TreeHitElement(TreeHitElementKind.Expander, item.DataContext as WorkspaceNodeViewModel),
                Border { Name: "Row", TemplatedParent: TreeViewItem item } => new TreeHitElement(TreeHitElementKind.Row, item.DataContext as WorkspaceNodeViewModel),
                TreeViewItem item => new TreeHitElement(TreeHitElementKind.Item, item.DataContext as WorkspaceNodeViewModel),
                _ => new TreeHitElement(TreeHitElementKind.Other),
            };
        }
    }

    /// <summary>The nearest <see cref="TreeViewItem"/> of <paramref name="node"/> above the pointer (nested items included).</summary>
    private static TreeViewItem? RowItem(DependencyObject? d, WorkspaceNodeViewModel node)
    {
        for (; d is not null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem item && ReferenceEquals(item.DataContext, node))
                return item;
        }

        return null;
    }

    private void WorkspaceTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Main?.Workspace is not { } workspace)
            return;
        var hit = ClassifyTreePoint(e.OriginalSource);
        if (hit.IsNodeRow)
        {
            // WPF trees do not select on right-click; select the clicked row so the context menu acts on it (a Folder row stays the
            // target of New Folder… / New Project…).
            hit.Node!.IsSelected = true;
            RowItem(e.OriginalSource as DependencyObject, hit.Node)?.Focus();
        }
        else if (hit.Kind == TreeHitKind.Background)
        {
            workspace.ClearSelection(); // §2.2 step 2: the menu then creates at the top level
        }
    }

    private void WorkspaceTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _treeDragCandidate = null;
        if (Main?.Workspace is not { } workspace)
            return;
        var hit = ClassifyTreePoint(e.OriginalSource);
        switch (hit.Kind)
        {
            case TreeHitKind.Background:
                workspace.ClearSelection(); // §2.2 step 1 (with the Workspace Trash open, the Trash stays open)
                break;
            case TreeHitKind.Row when hit.IsNodeRow:
                // Clicking a row while the Workspace Trash is open shows that node again — also the row that is already selected,
                // which raises no selection change (the view model closes the panel on every other selection).
                if (workspace.IsTrashOpen)
                    workspace.CloseTrashCommand.Execute(null);
                if (workspace.CanStartDrag(hit.Node))
                    _treeDragCandidate = (e.GetPosition(WorkspaceTree), hit.Node!.Id);
                break;
        }
    }

    private void WorkspaceTree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _treeDragCandidate = null;

    /// <summary>§2.1 step 1: a left-button drag of a row area starts after the system drag threshold.</summary>
    private void WorkspaceTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_treeDragCandidate is not { } candidate || e.LeftButton != MouseButtonState.Pressed || Main?.Workspace is not { } workspace)
            return;
        var position = e.GetPosition(WorkspaceTree);
        if (Math.Abs(position.X - candidate.Start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - candidate.Start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _treeDragCandidate = null;
        if (workspace.BeginDragSession(candidate.NodeId) is not { } session)
            return;
        _treeDragSession = session;
        try
        {
            DragDrop.DoDragDrop(WorkspaceTree, new DataObject(WorkspaceNodeDragFormat, candidate.NodeId), DragDropEffects.Move);
        }
        finally
        {
            workspace.EndDragSession(session); // drop done, cancelled (Esc) or left the window
            _treeDragSession = null;
            _lastTreeDragPosition = null;
        }
    }

    /// <summary>The running drag of THIS window and its source Id; null for anything else (another window's drag, files, text).</summary>
    private (int Session, long SourceId)? CurrentTreeDrag(DragEventArgs e) =>
        _treeDragSession is { } session && e.Data.GetDataPresent(WorkspaceNodeDragFormat) && e.Data.GetData(WorkspaceNodeDragFormat) is long id
            ? (session, id)
            : null;

    /// <summary>§2.1 step 2: a Folder row area → that Folder; the background → the top level; anything else (Project row, expander, scroll bar) → none.</summary>
    private (bool IsTarget, long? FolderId, long? HoverFolderId) TreeDropTarget(DragEventArgs e)
    {
        var hit = ClassifyTreePoint(e.OriginalSource);
        return hit switch
        {
            { Kind: TreeHitKind.Row, Node: { IsPlaceholder: false, IsFolder: true } folder } => (true, folder.Id, folder.Id),
            { Kind: TreeHitKind.Background } => (true, null, null),
            _ => (false, null, null),
        };
    }

    private void WorkspaceTree_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (CurrentTreeDrag(e) is not { } drag || Main?.Workspace is not { } workspace)
            return;
        _lastTreeDragPosition = e.GetPosition(WorkspaceTree);
        var target = TreeDropTarget(e);
        if (target.HoverFolderId is { } hover)
            workspace.DragHoverEnter(drag.Session, hover, FolderRowUnderPointer);
        else
            workspace.DragHoverLeave(drag.Session);
        if (target.IsTarget && workspace.CanDrop(drag.Session, drag.SourceId, target.FolderId))
            e.Effects = DragDropEffects.Move;
    }

    private void WorkspaceTree_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also bubbles from elements inside the tree; only leaving the tree itself cancels the pending hover.
        var position = e.GetPosition(WorkspaceTree);
        if (position.X >= 0 && position.Y >= 0 && position.X < WorkspaceTree.ActualWidth && position.Y < WorkspaceTree.ActualHeight)
            return;
        if (CurrentTreeDrag(e) is { } drag)
            Main?.Workspace.DragHoverLeave(drag.Session);
    }

    private void WorkspaceTree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (CurrentTreeDrag(e) is not { } drag || Main?.Workspace is not { } workspace)
            return;
        var target = TreeDropTarget(e);
        if (!target.IsTarget)
        {
            workspace.DragHoverLeave(drag.Session);
            return;
        }

        // Review fix: the view model re-checks and takes the slot in ONE synchronous step (before BeginWorkspaceWrite and its first
        // await); Move is reported only when it accepted. The store's result is not awaited here.
        e.Effects = ToWpf(workspace.Drop(drag.Session, drag.SourceId, target.FolderId));
    }

    /// <summary>Move only for an accepted drop (slot taken); None for every rejected one.</summary>
    private static DragDropEffects ToWpf(WorkspaceDropEffect effect) =>
        effect == WorkspaceDropEffect.Move ? DragDropEffects.Move : DragDropEffects.None;

    private void RootDropStrip_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (CurrentTreeDrag(e) is not { } drag || Main?.Workspace is not { } workspace)
            return;
        workspace.DragHoverLeave(drag.Session);
        if (workspace.CanDrop(drag.Session, drag.SourceId, null))
            e.Effects = DragDropEffects.Move;
    }

    private void RootDropStrip_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (CurrentTreeDrag(e) is not { } drag || Main?.Workspace is not { } workspace)
            return;
        e.Effects = ToWpf(workspace.Drop(drag.Session, drag.SourceId, null)); // review fix: Move only when accepted
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

    /// <summary>
    /// Enter restores the selected Trash entry (the same command as the Restore button; it never runs twice at once).
    /// Shift+Del (Phase 14, this list only) opens the permanent-deletion confirmation for the selected entries.
    /// </summary>
    private void TrashList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ProjectContent is not { } content)
            return;

        var command = e.Key switch
        {
            Key.Enter when Keyboard.Modifiers == ModifierKeys.None => content.Trash.RestoreCommand,
            Key.Delete when Keyboard.Modifiers == ModifierKeys.Shift => content.Trash.DeletePermanentlyCommand,
            _ => (System.Windows.Input.ICommand?)null,
        };
        if (command is null)
            return;
        command.Execute(null);
        e.Handled = true;
    }

    private void TrashList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ProjectContent?.Trash.UpdateSelection(TrashList.SelectedItems.OfType<TrashEntryViewModel>());

    private void WorkspaceTrashList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Main is not { } main || e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        main.Workspace.Trash.RestoreCommand.Execute(null);
        e.Handled = true;
    }

    private void ArchivedProjectsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Main is not { } main || e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        main.Workspace.Archived.UnarchiveCommand.Execute(null);
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
