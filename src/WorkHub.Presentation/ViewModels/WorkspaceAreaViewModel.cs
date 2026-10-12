using System.Collections.ObjectModel;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Workspace tab: the logical tree of Folders and Projects plus the selected Project's details.
/// <list type="bullet">
/// <item>Nothing loads until the tab is first shown; then only root nodes load. A Folder's children load on its first
/// expand, and a load already in flight is reused rather than repeated. Re-expanding a loaded folder does not re-read it.</item>
/// <item>The tree changes after a successful write (only the affected collections, merged by Id), and during a search
/// reveal, which reconciles ONLY the branches on the revealed path to one fresh read snapshot (<see cref="RevealAsync"/>).</item>
/// <item>One instance per node Id, placed in exactly one collection (<see cref="Roots"/> or its Parent's Children):
/// every relocation removes the old placement first (<see cref="Place"/>).</item>
/// <item>Asynchronous results never overwrite newer state: details loads carry a version and are dropped (including
/// their failures) once the selection or the saved state moved on; mutations wait for a pending child load of the
/// folder they touch before merging.</item>
/// </list>
/// Workspace is metadata only: nothing here creates, moves or renames files or folders on disk.
/// </summary>
public sealed class WorkspaceAreaViewModel : ObservableObject
{
    private readonly WorkspaceService _workspace;
    private readonly IDialogService _dialogs;
    private readonly IAppLog _log;
    private readonly Dictionary<long, WorkspaceNodeViewModel> _nodes = [];

    private Task? _rootLoad;
    private bool _isTabSelected;
    private bool _isActivated;
    private bool _isLoadingRoots;
    private string? _rootLoadError;
    private WorkspaceNodeViewModel? _selectedNode;
    private ProjectDetails? _selectedProject;
    private bool _isLoadingDetails;
    private string? _detailsMessage;
    private int _detailsVersion;
    private CancellationTokenSource? _detailsCts;
    private string? _bannerMessage;
    private readonly ProjectContentService? _content;
    private readonly ILinkHealthChecker? _checker;
    private readonly LinkCheckCache _checkCache = new();
    private ProjectContentViewModel? _selectedContent;
    private readonly LatestOnlyRunner<SearchItemRef, RevealResolution>? _resolveRunner;
    private int _rootsStamp;
    private int _revealVersion;
    private int _revealSequence;
    /// <summary>
    /// Node Id → the sequence at which a reveal or a committed lifecycle result last placed or removed it. A lazy load that
    /// started earlier skips such rows unless the node already sits in its branch (Phase 8 proposal §8.3).
    /// </summary>
    private readonly Dictionary<long, int> _touchedByReveal = [];
    private bool _applyingReveal;
    private bool _quietSelection;

    // ---- Workspace lifecycle (Phase 8) ----
    private bool _isLifecycleBusy;
    private int _visit;
    private int _lifecycleCompletions;
    private int _selectionVersion;
    private UndoOffer? _undo;
    private bool _isTrashOpen;

    /// <summary>The Undo of the latest Move to Trash, bound to the Workspace visit in which that Trash started.</summary>
    private sealed record UndoOffer(long NodeId, string Name, int Visit, string Message);

    /// <param name="content">Project content (ProjectFolders and Links); null leaves the content view out.</param>
    /// <param name="checker">The application-wide manual link checker.</param>
    /// <param name="search">Workspace search; null leaves search out.</param>
    /// <param name="searchDelayMs">Search debounce (tests shorten it).</param>
    public WorkspaceAreaViewModel(
        WorkspaceService workspace, IDialogService dialogs, IAppLog log,
        ProjectContentService? content = null, ILinkHealthChecker? checker = null,
        WorkspaceSearchService? search = null, int searchDelayMs = WorkspaceSearchViewModel.DefaultDelayMs)
    {
        _workspace = workspace;
        _dialogs = dialogs;
        _log = log;
        _content = content;
        _checker = checker;
        if (search is not null)
        {
            _resolveRunner = new LatestOnlyRunner<SearchItemRef, RevealResolution>((item, token) => search.ResolveAsync(item, token));
            Search = new WorkspaceSearchViewModel(search, this, log, searchDelayMs);
        }

        Roots.CollectionChanged += (_, _) => _rootsStamp++;

        Action<Exception> onError = ex => ReportError("The Workspace operation failed. Nothing was changed.", ex);
        // D15: every Workspace write command is disabled while a lifecycle write (Trash, Restore, Undo, Remove) is pending.
        NewFolderCommand = new AsyncRelayCommand(NewFolderAsync, CanWrite, onError);
        NewProjectCommand = new AsyncRelayCommand(NewProjectAsync, CanWrite, onError);
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => CanWrite() && HasSelection(), onError);
        EditProjectCommand = new AsyncRelayCommand(EditProjectAsync, () => CanWrite() && SelectedNode?.IsProject == true, onError);
        MoveCommand = new AsyncRelayCommand(MoveAsync, () => CanWrite() && HasSelection(), onError);
        TrashProjectCommand = new AsyncRelayCommand(TrashSelectedProjectAsync, () => CanWrite() && SelectedNode?.IsProject == true, onError);
        RemoveFolderCommand = new AsyncRelayCommand(RemoveSelectedFolderAsync, () => CanWrite() && SelectedNode?.IsFolder == true, onError);
        DeleteCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => CanWrite() && HasSelection(), onError);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => _undo is not null && !IsLifecycleBusy, onError);
        OpenTrashCommand = new AsyncRelayCommand(OpenTrashAsync, () => IsActivated && !IsTrashOpen, onError);
        CloseTrashCommand = new RelayCommand(CloseTrash, () => IsTrashOpen);
        Trash = new WorkspaceTrashViewModel(this);
        RetryLoadCommand = new AsyncRelayCommand(RetryLoadAsync, () => RootLoadError is not null && !IsLoadingRoots, onError);
        DismissBannerCommand = new RelayCommand(() => BannerMessage = null);
        Roots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<WorkspaceNodeViewModel> Roots { get; } = [];

    /// <summary>The Workspace search box and its results; null when search is not wired.</summary>
    public WorkspaceSearchViewModel? Search { get; }

    public AsyncRelayCommand NewFolderCommand { get; }

    public AsyncRelayCommand NewProjectCommand { get; }

    public AsyncRelayCommand RenameCommand { get; }

    public AsyncRelayCommand EditProjectCommand { get; }

    public AsyncRelayCommand MoveCommand { get; }

    /// <summary>Move the selected Project to the Workspace Trash (confirmed).</summary>
    public AsyncRelayCommand TrashProjectCommand { get; }

    /// <summary>Permanently remove the selected Workspace Folder if it is truly empty (confirmed; D2).</summary>
    public AsyncRelayCommand RemoveFolderCommand { get; }

    /// <summary>Del in the tree: Project → Move to Trash; Folder → Remove folder (D10).</summary>
    public AsyncRelayCommand DeleteCommand { get; }

    /// <summary>Restores the Project the latest Move to Trash of this visit put in the Trash (D9).</summary>
    public AsyncRelayCommand UndoCommand { get; }

    /// <summary>Opens the Workspace Trash ("Trashed projects (n)") in the right pane (D6).</summary>
    public AsyncRelayCommand OpenTrashCommand { get; }

    /// <summary>Back: closes the Workspace Trash and shows the unchanged selection again.</summary>
    public RelayCommand CloseTrashCommand { get; }

    public WorkspaceTrashViewModel Trash { get; }

    public bool IsTrashOpen
    {
        get => _isTrashOpen;
        private set
        {
            if (!SetProperty(ref _isTrashOpen, value))
                return;
            OpenTrashCommand.RaiseCanExecuteChanged();
            CloseTrashCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>A lifecycle write is pending (the slot is taken).</summary>
    public bool IsLifecycleBusy
    {
        get => _isLifecycleBusy;
        private set
        {
            if (!SetProperty(ref _isLifecycleBusy, value))
                return;
            RaiseWriteCommands();
            Trash.RaiseCommands();
        }
    }

    /// <summary>The banner offers Undo (the latest Move to Trash of the current visit).</summary>
    public bool IsUndoAvailable => _undo is not null;

    internal WorkspaceService Workspace => _workspace;

    internal IAppLog Log => _log;

    /// <summary>Lifecycle results applied so far (Trash, Restore, Undo, Remove, stale-node reconciliation).</summary>
    internal int LifecycleCompletions => _lifecycleCompletions;

    /// <summary>The most recent lifecycle operation; lets tests await it.</summary>
    internal Task CurrentLifecycleOperation { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent stale-node reconciliation; lets tests await it.</summary>
    internal Task CurrentReconciliation { get; private set; } = Task.CompletedTask;

    public AsyncRelayCommand RetryLoadCommand { get; }

    public RelayCommand DismissBannerCommand { get; }

    /// <summary>Bound to the Workspace tab. The first selection starts the one root load.</summary>
    public bool IsTabSelected
    {
        get => _isTabSelected;
        set
        {
            if (!SetProperty(ref _isTabSelected, value))
                return;
            if (value)
            {
                _ = ActivateAsync();
            }
            else
            {
                InvalidateReveal(); // leaving the tab cancels a reveal in flight
                EndVisit(); // committed lifecycle results still reconcile the tree; only Undo and its selection are visit-bound
            }
        }
    }

    /// <summary>True once the root nodes have loaded.</summary>
    public bool IsActivated
    {
        get => _isActivated;
        private set
        {
            if (!SetProperty(ref _isActivated, value))
                return;
            OnPropertyChanged(nameof(IsEmpty));
            RaiseWriteCommands();
            OpenTrashCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoadingRoots
    {
        get => _isLoadingRoots;
        private set
        {
            if (SetProperty(ref _isLoadingRoots, value))
                RetryLoadCommand.RaiseCanExecuteChanged();
        }
    }

    public string? RootLoadError
    {
        get => _rootLoadError;
        private set
        {
            if (SetProperty(ref _rootLoadError, value))
                RetryLoadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmpty => IsActivated && Roots.Count == 0;

    public WorkspaceNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (value?.IsPlaceholder == true || ReferenceEquals(_selectedNode, value))
                return;
            if (!_applyingReveal && !_quietSelection)
                InvalidateReveal(); // a manual selection wins over a reveal still resolving
            if (value is not null && IsTrashOpen)
                CloseTrash(); // selecting a tree node shows that node instead of the Workspace Trash

            _selectionVersion++;
            var previous = _selectedNode;
            _selectedNode = value;
            if (previous is not null)
                previous.IsSelected = false;
            if (value is not null)
                value.IsSelected = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFolderSelected));
            RaiseWriteCommands();
            StartDetailsLoad();
            UpdateContent();
        }
    }

    public bool IsFolderSelected => SelectedNode?.IsFolder == true;

    /// <summary>Content (ProjectFolders and Links) of the selected Project; null for Folders, no selection, or when content is not wired.</summary>
    public ProjectContentViewModel? SelectedContent
    {
        get => _selectedContent;
        private set => SetProperty(ref _selectedContent, value);
    }

    /// <summary>Keeps the content view while the same Project stays selected (rename, edit, move); replaces it otherwise.</summary>
    private void UpdateContent()
    {
        var project = SelectedNode is { IsProject: true } node ? node : null;
        if (project is not null && SelectedContent?.ProjectNodeId == project.Id)
            return;

        SelectedContent?.Deactivate();
        if (project is null || _content is null || _checker is null)
        {
            SelectedContent = null;
            return;
        }

        var content = new ProjectContentViewModel(_content, _checker, _dialogs, _log, _checkCache, project.Id, project.Name);
        var projectId = project.Id;
        content.ProjectTrashedObserved = () => _ = ReconcileNodeAsync(projectId); // trashed by another window
        SelectedContent = content;
        _ = content.ActivateAsync();
    }

    /// <summary>Details of the selected Project, once loaded (null for Folders and while loading).</summary>
    public ProjectDetails? SelectedProject
    {
        get => _selectedProject;
        private set
        {
            if (!SetProperty(ref _selectedProject, value))
                return;
            OnPropertyChanged(nameof(SelectedProjectStatus));
        }
    }

    public string? SelectedProjectStatus => SelectedProject is null ? null : ProjectStatuses.DisplayName(SelectedProject.Status);

    public bool IsLoadingDetails { get => _isLoadingDetails; private set => SetProperty(ref _isLoadingDetails, value); }

    /// <summary>"No longer exists" / load failure for the CURRENT selection only.</summary>
    public string? DetailsMessage { get => _detailsMessage; private set => SetProperty(ref _detailsMessage, value); }

    /// <summary>The banner text. The Undo button sits in this banner: dismissing or replacing its message withdraws the Undo.</summary>
    public string? BannerMessage
    {
        get => _bannerMessage;
        private set
        {
            if (SetProperty(ref _bannerMessage, value) && _undo is not null && value != _undo.Message)
                ClearUndo();
        }
    }

    /// <summary>The most recent details load; lets tests await it.</summary>
    internal Task CurrentDetailsLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The root load (completed when none was started).</summary>
    internal Task RootLoad => _rootLoad ?? Task.CompletedTask;

    /// <summary>Starts the root load once. Later calls (tab switches) reuse it and never reload.</summary>
    public Task ActivateAsync() => _rootLoad ??= LoadRootsAsync();

    // ---- Loading ----------------------------------------------------------------------------------------------------

    private Task RetryLoadAsync()
    {
        InvalidateReveal();
        EndVisit();
        return _rootLoad = LoadRootsAsync();
    }

    private async Task LoadRootsAsync()
    {
        IsLoadingRoots = true;
        RootLoadError = null;
        try
        {
            var roots = await _workspace.ListChildrenAsync(null);
            ReplaceChildren(Roots, null, roots, _revealSequence);
            IsActivated = true;
            _ = Trash.RefreshCount();
        }
        catch (Exception ex)
        {
            _log.Error("Workspace root load failed.", ex);
            RootLoadError = "Workspace could not be loaded. Nothing was changed.";
        }
        finally
        {
            IsLoadingRoots = false;
        }
    }

    internal void OnExpanded(WorkspaceNodeViewModel node) => _ = EnsureChildrenLoadedAsync(node);

    /// <summary>Loads a Folder's children once; concurrent callers share the load in flight. False if it failed.</summary>
    internal Task<bool> EnsureChildrenLoadedAsync(WorkspaceNodeViewModel node)
    {
        if (!node.IsFolder || node.IsChildrenLoaded)
            return Task.FromResult(node.IsFolder);
        if (node.ChildrenLoad is { } inFlight)
            return inFlight;

        var load = LoadChildrenAsync(node);
        if (!load.IsCompleted)
            node.ChildrenLoad = load;
        return load;
    }

    private async Task<bool> LoadChildrenAsync(WorkspaceNodeViewModel node)
    {
        var reconcileStamp = node.ReconcileStamp;
        var revealSequence = _revealSequence;
        try
        {
            var children = await _workspace.ListChildrenAsync(node.Id);
            if (node.ReconcileStamp != reconcileStamp)
                return node.IsChildrenLoaded; // a reveal reconciled this branch to a newer snapshot meanwhile: never undo it
            ReplaceChildren(node.Children, node, children, revealSequence);
            node.IsChildrenLoaded = true;
            return true;
        }
        catch (Exception ex)
        {
            ReportError($"The contents of “{node.Name}” could not be loaded.", ex);
            node.CollapseAfterFailedLoad();
            return false;
        }
        finally
        {
            node.ChildrenLoad = null;
        }
    }

    /// <param name="revealSequence">The reveal sequence when the load STARTED: rows of nodes that a later reveal placed or
    /// removed are skipped unless the node already sits in this branch, so an older lazy load can neither pull a revealed
    /// node back to its former parent nor resurrect one the reveal found gone or elsewhere.</param>
    private void ReplaceChildren(ObservableCollection<WorkspaceNodeViewModel> target, WorkspaceNodeViewModel? parent, IReadOnlyList<WorkspaceNode> loaded, int revealSequence)
    {
        var present = target.Where(c => !c.IsPlaceholder).Select(c => c.Id).ToHashSet();
        loaded = loaded.Where(n => !_touchedByReveal.TryGetValue(n.Id, out var seq) || seq <= revealSequence || present.Contains(n.Id)).ToList();
        // Keep anything merged while this load was pending (e.g. a node created meanwhile), then the loaded rows; by Id.
        var merged = target.Where(c => !c.IsPlaceholder && loaded.All(n => n.Id != c.Id)).ToList();
        target.Clear();
        foreach (var node in loaded)
        {
            var vm = GetOrCreate(node, parent);
            Detach(vm, except: target); // a cached instance found under another parent moves here; it never appears twice
            vm.Parent = parent;
            target.Add(vm);
        }

        foreach (var extra in merged)
            InsertSorted(target, extra);
    }

    /// <summary>The cached instance for the node (name and child flag refreshed), or a new one under <paramref name="parent"/>.
    /// Never changes the placement of an existing instance; callers place it.</summary>
    private WorkspaceNodeViewModel GetOrCreate(WorkspaceNode node, WorkspaceNodeViewModel? parent)
    {
        if (_nodes.TryGetValue(node.Id, out var existing))
        {
            existing.Name = node.Name;
            existing.SetHasChildren(node.HasChildren);
            return existing;
        }

        var created = new WorkspaceNodeViewModel(this, node, parent);
        _nodes[node.Id] = created;
        return created;
    }

    /// <summary>The collection an instance is placed in according to its Parent (Roots for top-level nodes).</summary>
    private ObservableCollection<WorkspaceNodeViewModel> CollectionOf(WorkspaceNodeViewModel node) => node.Parent?.Children ?? Roots;

    /// <summary>Removes the instance from its current collection unless that is <paramref name="except"/>.</summary>
    private void Detach(WorkspaceNodeViewModel node, ObservableCollection<WorkspaceNodeViewModel>? except = null)
    {
        var current = CollectionOf(node);
        if (!ReferenceEquals(current, except))
            current.Remove(node);
    }

    /// <summary>
    /// One-placement invariant: puts the instance under <paramref name="parent"/> at its sorted position, removing it from
    /// its former collection first. An instance already in place is moved only if its sort position changed (e.g. renamed
    /// elsewhere), so its identity, expansion and loaded children are kept.
    /// </summary>
    private void Place(WorkspaceNodeViewModel node, WorkspaceNodeViewModel? parent)
    {
        var target = parent?.Children ?? Roots;
        Detach(node, except: target);
        node.Parent = parent;
        var index = target.IndexOf(node);
        if (index < 0)
        {
            InsertSorted(target, node);
            return;
        }

        var sorted = target.Count(s => !ReferenceEquals(s, node) && !s.IsPlaceholder && WorkspaceNodeOrder.Instance.Compare(s.SortKey, node.SortKey) < 0);
        if (sorted != index)
            target.Move(index, sorted);
    }

    // ---- Selection and details --------------------------------------------------------------------------------------

    internal void OnSelectionChanged(WorkspaceNodeViewModel node, bool isSelected)
    {
        if (isSelected)
            SelectedNode = node;
        else if (ReferenceEquals(SelectedNode, node))
            SelectedNode = null;
    }

    private void StartDetailsLoad()
    {
        var version = ++_detailsVersion;
        _detailsCts?.Cancel();
        _detailsCts = null;
        SelectedProject = null;
        DetailsMessage = null;

        if (SelectedNode is not { IsProject: true } node)
        {
            IsLoadingDetails = false;
            CurrentDetailsLoad = Task.CompletedTask;
            return;
        }

        IsLoadingDetails = true;
        var cts = _detailsCts = new CancellationTokenSource();
        CurrentDetailsLoad = LoadDetailsAsync(node.Id, version, cts.Token);
    }

    private async Task LoadDetailsAsync(long nodeId, int version, CancellationToken token)
    {
        try
        {
            var details = await _workspace.GetProjectAsync(nodeId, token);
            if (version != _detailsVersion)
                return; // a newer selection or saved state owns the pane now
            if (details is null)
            {
                DetailsMessage = "This project no longer exists.";
            }
            else if (details.IsTrashed)
            {
                // Trashed by another window: this read is fresh, so the node leaves the tree now (by Id, §8.4).
                ApplyRemoved(nodeId);
                BannerMessage = WorkspaceService.ProjectInTrash;
            }
            else
            {
                SelectedProject = details;
            }
        }
        catch (Exception ex)
        {
            if (version != _detailsVersion)
                return; // stale failures never show against a different selection
            if (ex is not OperationCanceledException)
                _log.Error("Project details could not be loaded.", ex);
            DetailsMessage = "Project details could not be loaded.";
        }
        finally
        {
            if (version == _detailsVersion)
                IsLoadingDetails = false;
        }
    }

    /// <summary>Shows freshly saved details and invalidates any older load still in flight.</summary>
    private void ShowSavedDetails(ProjectDetails saved)
    {
        if (SelectedNode?.Id != saved.NodeId)
            return;
        _detailsVersion++;
        _detailsCts?.Cancel();
        _detailsCts = null;
        DetailsMessage = null;
        IsLoadingDetails = false;
        SelectedProject = saved;
        CurrentDetailsLoad = Task.CompletedTask;
    }

    // ---- Commands -----------------------------------------------------------------------------------------------------

    private bool HasSelection() => SelectedNode is not null;

    /// <summary>New items go into the selected Folder, beside the selected Project, or at the root.</summary>
    private WorkspaceNodeViewModel? CreationParent() => SelectedNode switch
    {
        { IsFolder: true } folder => folder,
        { IsProject: true } project => project.Parent,
        _ => null,
    };

    private bool CanWrite() => IsActivated && !IsLifecycleBusy;

    private void RaiseWriteCommands()
    {
        NewFolderCommand.RaiseCanExecuteChanged();
        NewProjectCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        EditProjectCommand.RaiseCanExecuteChanged();
        MoveCommand.RaiseCanExecuteChanged();
        TrashProjectCommand.RaiseCanExecuteChanged();
        RemoveFolderCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Every Workspace write starts here: a reveal in flight is cancelled and an offered Undo is withdrawn.</summary>
    private void BeginWorkspaceWrite()
    {
        InvalidateReveal();
        ClearUndo();
    }

    private async Task NewFolderAsync()
    {
        BeginWorkspaceWrite();
        var parent = CreationParent();
        var editor = WorkspaceNameEditorViewModel.ForNewFolder(_workspace, parent?.Id, parent?.Path);
        if (_dialogs.ShowWorkspaceNameEditor(editor) && editor.CreatedNode is { } created)
            await ApplyCreatedAsync(parent, created);
        else if (editor.Refusal == WorkspaceFailure.ParentNotFound && parent is not null)
            await ReconcileNodeAsync(parent.Id, "The destination folder no longer exists. Nothing was created.");
    }

    private async Task NewProjectAsync()
    {
        BeginWorkspaceWrite();
        var parent = CreationParent();
        var editor = ProjectEditorViewModel.ForCreate(_workspace, parent?.Id, parent?.Path);
        if (_dialogs.ShowProjectEditor(editor) && editor.CreatedNode is { } created)
            await ApplyCreatedAsync(parent, created);
        else if (editor.Refusal == WorkspaceFailure.ParentNotFound && parent is not null)
            await ReconcileNodeAsync(parent.Id, "The destination folder no longer exists. Nothing was created.");
    }

    private async Task RenameAsync()
    {
        BeginWorkspaceWrite();
        var node = SelectedNode!;
        var editor = WorkspaceNameEditorViewModel.ForRename(_workspace, node.Id, node.Type, node.Name, node.Parent?.Path);
        if (_dialogs.ShowWorkspaceNameEditor(editor) && editor.SavedName is { } name)
        {
            ApplyName(node, name);
            if (ReferenceEquals(SelectedNode, node) && node.IsProject)
                StartDetailsLoad(); // name and UpdatedAt changed
        }
        else if (editor.Refusal is WorkspaceFailure.ProjectTrashed or WorkspaceFailure.NodeNotFound)
        {
            await ReconcileNodeAsync(node.Id); // the dialog kept its values; now a fresh typed read decides
        }
    }

    private async Task EditProjectAsync()
    {
        BeginWorkspaceWrite();
        var node = SelectedNode!;
        // Edit is available as soon as a Project is selected; it reads the current state itself if the pane is still loading.
        var current = SelectedProject is { } shown && shown.NodeId == node.Id ? shown : await _workspace.GetProjectAsync(node.Id);
        if (!ReferenceEquals(SelectedNode, node))
            return; // the selection changed while the project was loading
        if (current is null)
        {
            BannerMessage = "This project no longer exists.";
            return;
        }

        if (current.IsTrashed)
        {
            ApplyRemoved(node.Id); // trashed by another window: a fresh read, so reconcile now
            BannerMessage = WorkspaceService.ProjectInTrash;
            return;
        }

        var editor = ProjectEditorViewModel.ForEdit(_workspace, current, node.Parent?.Path);
        if (_dialogs.ShowProjectEditor(editor) && editor.SavedDetails is { } saved)
        {
            ApplyName(node, saved.Name);
            ShowSavedDetails(saved);
        }
        else if (editor.Refusal is WorkspaceFailure.ProjectTrashed or WorkspaceFailure.NodeNotFound)
        {
            await ReconcileNodeAsync(node.Id);
        }
    }

    private async Task MoveAsync()
    {
        BeginWorkspaceWrite();
        var node = SelectedNode!;
        var targets = await _workspace.ListMoveTargetsAsync(node.Id);
        if (!ReferenceEquals(SelectedNode, node))
            return; // the selection changed while destinations were loading

        var editor = new WorkspaceMoveViewModel(_workspace, node.Id, node.Name, node.Parent?.Id, targets);
        if (_dialogs.ShowWorkspaceMove(editor) && editor.Moved)
            await ApplyMovedAsync(node, editor.MovedTo);
        else if (editor.Refusal is WorkspaceFailure.ProjectTrashed or WorkspaceFailure.NodeNotFound)
            await ReconcileNodeAsync(node.Id);
        else if (editor.Refusal == WorkspaceFailure.ParentNotFound && editor.RefusedDestinationId is { } destination)
            await ReconcileNodeAsync(destination, "The destination folder no longer exists. Nothing was moved.");
    }

    // ---- Workspace lifecycle: Project Trash, Undo, Restore, empty-folder removal (Phase 8 proposal revision 2 §8) --------
    //
    // Timing: nothing in the UI changes before the store commits. Results are applied on the UI thread, by node Id, whenever
    // they arrive (also after the tab was left): the tree is a projection of committed state. Applying one first touches the
    // node (lazy loads that started earlier skip it) and counts a lifecycle completion (reveals and Trash reads that started
    // earlier are re-read). Only Undo and its automatic selection are bound to the Workspace visit.

    private Task DeleteSelectedAsync() => SelectedNode switch
    {
        { IsProject: true } => TrashSelectedProjectAsync(),
        { IsFolder: true } => RemoveSelectedFolderAsync(),
        _ => Task.CompletedTask,
    };

    private Task TrashSelectedProjectAsync() => CurrentLifecycleOperation = TrashProjectCoreAsync();

    private async Task TrashProjectCoreAsync()
    {
        if (!CanWrite() || SelectedNode is not { IsProject: true } node)
            return; // direct invocations while a lifecycle write is pending issue no second store call

        var (id, name, visit) = (node.Id, node.Name, _visit); // the visit is captured when the command starts
        BeginWorkspaceWrite();
        if (!_dialogs.Confirm("Move to Trash",
                $"Move project “{name}” to the Trash? Its folders, links and notes stay with it and come back when you restore it. Nothing on disk is touched.",
                "Move to Trash"))
            return;

        IsLifecycleBusy = true;
        BannerMessage = $"Moving “{name}” to the Trash…";
        try
        {
            ProjectTrashResult result;
            try
            {
                result = await _workspace.TrashProjectAsync(id);
            }
            catch (Exception ex)
            {
                _log.Error("Move project to Trash failed.", ex); // operation only; no names
                BannerMessage = $"Could not move “{name}” to the Trash. Nothing was changed.";
                return; // nothing was suspended, so nothing needs restoring
            }

            switch (result.Outcome)
            {
                case ProjectTrashOutcome.Trashed:
                    ApplyRemoved(id);
                    Search?.RemoveResult(new SearchItemRef(SearchResultKind.Project, id));
                    BannerMessage = $"Moved “{name}” to the Trash.";
                    if (visit == _visit)
                        OfferUndo(new UndoOffer(id, name, visit, BannerMessage!)); // never for a Trash started in an ended visit
                    break;
                case ProjectTrashOutcome.AlreadyTrashed:
                    ApplyRemoved(id);
                    BannerMessage = WorkspaceService.ProjectInTrash;
                    break;
                case ProjectTrashOutcome.NotFound:
                    ApplyRemoved(id);
                    BannerMessage = "This project no longer exists.";
                    break;
                default:
                    BannerMessage = "Only projects can be moved to the Trash.";
                    break;
            }

            Trash.OnLifecycleCompleted();
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    private Task UndoAsync() => CurrentLifecycleOperation = UndoCoreAsync();

    private async Task UndoCoreAsync()
    {
        if (IsLifecycleBusy || _undo is not { } undo || undo.Visit != _visit)
            return;

        var (visit, selection) = (_visit, _selectionVersion); // captured at the Undo click
        BeginWorkspaceWrite(); // also withdraws this Undo: it is offered once
        IsLifecycleBusy = true;
        BannerMessage = $"Restoring “{undo.Name}”…";
        try
        {
            var (notice, placed) = await RestoreCoreAsync(undo.NodeId, undo.Name);
            BannerMessage = notice;
            // Selected only while the Undo's visit is current and the user has not selected anything since the click.
            if (placed is not null && visit == _visit && selection == _selectionVersion && _nodes.TryGetValue(undo.NodeId, out var restored) && ReferenceEquals(restored, placed))
                SelectedNode = placed;
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    /// <summary>Restore from the Workspace Trash panel. Never changes the selection.</summary>
    /// <returns>The notice for the panel.</returns>
    internal async Task<string> RestoreFromTrashAsync(long nodeId, string name)
    {
        if (IsLifecycleBusy)
            return string.Empty;

        BeginWorkspaceWrite();
        IsLifecycleBusy = true;
        try
        {
            var operation = RestoreCoreAsync(nodeId, name);
            CurrentLifecycleOperation = operation;
            return (await operation).Notice;
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    /// <summary>The Restore store call and its by-Id application; the caller holds the lifecycle slot.</summary>
    private async Task<(string Notice, WorkspaceNodeViewModel? Placed)> RestoreCoreAsync(long nodeId, string name)
    {
        ProjectRestoreResult result;
        try
        {
            result = await _workspace.RestoreProjectAsync(nodeId);
        }
        catch (Exception ex)
        {
            _log.Error("Restore project from the Trash failed.", ex); // operation only; no names
            return ($"Could not restore “{name}”. It is still in the Trash.", null);
        }

        WorkspaceNodeViewModel? placed = null;
        string notice;
        switch (result.Outcome)
        {
            case ProjectRestoreOutcome.Restored:
                placed = await ApplyRestoredAsync(result.Restored!.Node);
                notice = $"Restored “{result.Restored.Node.Name}” to {result.Restored.ParentPath ?? "Workspace (top level)"}.";
                Trash.RemoveEntry(nodeId);
                break;
            case ProjectRestoreOutcome.NameConflict:
                notice = $"The folder it came from already contains “{result.BlockingName ?? name}”. Rename or move that item first; nothing was restored.";
                break;
            case ProjectRestoreOutcome.NotTrashed:
                notice = $"“{name}” is no longer in the Trash (it may have been restored in another window).";
                Trash.RemoveEntry(nodeId);
                Touch(nodeId); // reads that started earlier are re-read; the tree shows it with the next load of its parent
                break;
            default:
                notice = "This project no longer exists.";
                Trash.RemoveEntry(nodeId);
                ApplyRemoved(nodeId);
                break;
        }

        Trash.OnLifecycleCompleted();
        return (notice, placed);
    }

    private Task RemoveSelectedFolderAsync() => CurrentLifecycleOperation = RemoveFolderCoreAsync();

    private async Task RemoveFolderCoreAsync()
    {
        if (!CanWrite() || SelectedNode is not { IsFolder: true } node)
            return;

        var (id, name) = (node.Id, node.Name);
        BeginWorkspaceWrite();

        // A fresh read decides whether a confirmation is shown at all (§4): only a folder with no child row qualifies.
        var sequence = _revealSequence;
        WorkspaceNodeState? state;
        try
        {
            state = await _workspace.GetNodeStateAsync(id);
        }
        catch (Exception ex)
        {
            ReportError("The folder could not be checked. Nothing was changed.", ex);
            return;
        }

        if (!ReferenceEquals(SelectedNode, node) || IsLifecycleBusy)
            return; // the selection changed (or another lifecycle write started) while the folder was being checked
        if (state is null)
        {
            if (!IsTouchedSince(id, sequence))
                ApplyRemoved(id);
            BannerMessage = "This folder no longer exists.";
            return;
        }

        if (state.ActiveChildren + state.TrashedChildren > 0)
        {
            BannerMessage = NotEmptyText(name, state.ActiveChildren, state.TrashedChildren);
            return;
        }

        if (!_dialogs.Confirm("Remove folder",
                $"Remove the empty folder “{name}”? This removes only the WorkHub folder and cannot be undone. Nothing on disk is touched.",
                "Remove"))
            return;

        IsLifecycleBusy = true;
        try
        {
            FolderRemoveResult result;
            try
            {
                result = await _workspace.RemoveEmptyFolderAsync(id);
            }
            catch (Exception ex)
            {
                _log.Error("Remove empty folder failed.", ex);
                BannerMessage = $"Could not remove “{name}”. Nothing was changed.";
                return;
            }

            switch (result.Outcome)
            {
                case FolderRemoveOutcome.Removed:
                    ApplyRemoved(id);
                    Search?.RemoveResult(new SearchItemRef(SearchResultKind.WorkspaceFolder, id));
                    BannerMessage = $"Removed the empty folder “{name}”.";
                    break;
                case FolderRemoveOutcome.NotEmpty:
                    if (_nodes.TryGetValue(id, out var folder))
                        folder.SetHasChildren(true);
                    BannerMessage = NotEmptyText(name, result.ActiveChildren, result.TrashedProjects);
                    break;
                case FolderRemoveOutcome.NotFound:
                    ApplyRemoved(id);
                    BannerMessage = "This folder no longer exists.";
                    break;
                default:
                    BannerMessage = "Only folders can be removed.";
                    break;
            }
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    private static string NotEmptyText(string name, int active, int trashed) => active > 0
        ? $"“{name}” is not empty ({(active == 1 ? "1 item" : $"{active} items")}). Move or remove them first."
        : $"“{name}” still holds {(trashed == 1 ? "1 project" : $"{trashed} projects")} in the Trash. Restore {(trashed == 1 ? "it" : "them")} and move {(trashed == 1 ? "it" : "them")} elsewhere first.";

    /// <summary>
    /// The completion boundary (§8.3.1): marks the node so lazy loads that started earlier skip it, and counts a lifecycle
    /// completion so reveal snapshots and Trash reads that started earlier are read again.
    /// </summary>
    private void Touch(long nodeId)
    {
        _touchedByReveal[nodeId] = ++_revealSequence;
        _lifecycleCompletions++;
    }

    private bool IsTouchedSince(long nodeId, int sequence) => _touchedByReveal.TryGetValue(nodeId, out var touched) && touched > sequence;

    /// <summary>
    /// A committed removal from the tree (trashed Project, removed folder, node found trashed or gone): by Id, wherever its
    /// instance is. Clears the selection only if that node is still selected — which deactivates exactly the content view
    /// that is current at that moment (it belongs to the node). Other selections, views, visits and drafts are untouched.
    /// </summary>
    private void ApplyRemoved(long nodeId)
    {
        Touch(nodeId);
        _ = Trash.RefreshCount(); // a Project may have entered the Trash (here or in another window)
        if (!_nodes.TryGetValue(nodeId, out var node))
            return;
        if (ReferenceEquals(SelectedNode, node))
            ClearSelectionQuietly(); // BEFORE the row leaves: a tree whose selected row is removed selects that row's parent itself
        CollectionOf(node).Remove(node);
        ForgetSubtree(node);
    }

    /// <summary>Clears the selection without cancelling a reveal in flight (it is not a user selection).</summary>
    private void ClearSelectionQuietly()
    {
        _quietSelection = true;
        try
        {
            SelectedNode = null;
        }
        finally
        {
            _quietSelection = false;
        }
    }

    /// <summary>
    /// A committed Restore: if the parent's child load is in flight it is awaited FIRST, then the node is touched and merged
    /// by Id, so it appears exactly once whether that load's snapshot predates the commit or not (§8.3.2).
    /// </summary>
    /// <returns>The placed instance, or null when its parent is not loaded in this tree.</returns>
    private async Task<WorkspaceNodeViewModel?> ApplyRestoredAsync(WorkspaceNode restored)
    {
        WorkspaceNodeViewModel? parent = null;
        if (restored.ParentId is { } parentId && _nodes.TryGetValue(parentId, out parent) && parent.ChildrenLoad is { } pending)
            await pending;

        Touch(restored.Id);
        if (restored.ParentId is { } id && (!_nodes.TryGetValue(id, out parent) || !parent.IsChildrenLoaded))
        {
            parent?.SetHasChildren(true); // appears with the folder's next load
            return null;
        }

        var node = GetOrCreate(restored, parent);
        if ((parent?.Children ?? Roots).All(c => c.Id != node.Id))
            Place(node, parent);
        return node;
    }

    /// <summary>
    /// Stale-node reconciliation (§8.4): a fresh typed read of the node, applied by Id unless a newer completion or reveal
    /// already reconciled it. Trashed or missing → removed; active under another parent → placed there if loaded, else
    /// forgotten; active in place → nothing.
    /// </summary>
    internal Task ReconcileNodeAsync(long nodeId, string? missingMessage = null) =>
        CurrentReconciliation = ReconcileNodeCoreAsync(nodeId, missingMessage);

    private async Task ReconcileNodeCoreAsync(long nodeId, string? missingMessage)
    {
        var sequence = _revealSequence;
        WorkspaceNodeState? state;
        try
        {
            state = await _workspace.GetNodeStateAsync(nodeId);
        }
        catch (Exception ex)
        {
            _log.Error("A Workspace node could not be re-read after a refused change.", ex); // the dialog already showed the refusal
            return;
        }

        if (IsTouchedSince(nodeId, sequence))
            return; // a newer lifecycle result or reveal already applied a fresher state
        if (state is null || state.Trashed)
        {
            ApplyRemoved(nodeId);
            BannerMessage = state is null ? missingMessage ?? "This item no longer exists." : WorkspaceService.ProjectInTrash;
            return;
        }

        if (!_nodes.TryGetValue(nodeId, out var node) || node.Parent?.Id == state.ParentId)
            return; // not shown here, or active in place
        Touch(nodeId);
        if (state.ParentId is null || (_nodes.TryGetValue(state.ParentId.Value, out var parent) && parent.IsChildrenLoaded))
        {
            Place(node, state.ParentId is null ? null : _nodes[state.ParentId.Value]);
            return;
        }

        if (ReferenceEquals(SelectedNode, node))
            ClearSelectionQuietly(); // before the row leaves (see ApplyRemoved)
        CollectionOf(node).Remove(node); // its new parent is not loaded here: it appears when that folder loads
        ForgetSubtree(node);
    }

    private void OfferUndo(UndoOffer offer)
    {
        _undo = offer;
        OnPropertyChanged(nameof(IsUndoAvailable));
        UndoCommand.RaiseCanExecuteChanged();
    }

    private void ClearUndo()
    {
        if (_undo is null)
            return;
        _undo = null;
        OnPropertyChanged(nameof(IsUndoAvailable));
        UndoCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Leaving the tab (or Retry) ends the Workspace visit: its Undo is withdrawn and can never return.</summary>
    private void EndVisit()
    {
        _visit++;
        ClearUndo();
    }

    private Task OpenTrashAsync()
    {
        if (!IsActivated || IsTrashOpen)
            return Task.CompletedTask;
        IsTrashOpen = true; // the selection and its content view stay as they are, hidden behind the panel
        return Trash.Open();
    }

    private void CloseTrash()
    {
        if (!IsTrashOpen)
            return;
        Trash.Close();
        IsTrashOpen = false;
    }

    // ---- Search reveal (Phase 6 proposal §4.5) -------------------------------------------------------------------------

    /// <summary>Cancels a reveal in flight at its next step (new search, manual selection, write command, retry, leaving the tab).</summary>
    internal void InvalidateReveal() => _revealVersion++;

    /// <summary>The last reveal's work; lets tests await it.</summary>
    internal Task<WorkspaceRevealOutcome> CurrentReveal { get; private set; } = Task.FromResult(WorkspaceRevealOutcome.Superseded);

    /// <summary>
    /// Reveals a search result in its CURRENT location, identified by (Kind, Id):
    /// <list type="number">
    /// <item>takes a reveal version and the branch stamps of every cached collection;</item>
    /// <item>resolves through the bounded runner (one running + newest pending) — one read snapshot of the ancestor chain
    /// and every branch on it;</item>
    /// <item>gives up silently if the version moved on; NotFound and Trashed are reported;</item>
    /// <item>if a branch on the path changed locally during the read, or a lazy load of one is still in flight, retries
    /// once; a second time reports <see cref="WorkspaceRevealOutcome.WorkspaceChanged"/> and changes nothing;</item>
    /// <item>applies the snapshot synchronously (no await) to the path branches only, expands the ancestors and selects the
    /// Folder/Project;</item>
    /// <item>for content, opens the item's container in the Project's content view and selects the row of that kind and Id.</item>
    /// </list>
    /// Never writes, never reloads the whole tree, never opens a link or the note editor.
    /// </summary>
    internal Task<WorkspaceRevealOutcome> RevealAsync(SearchItemRef item) => CurrentReveal = RevealCoreAsync(item);

    private async Task<WorkspaceRevealOutcome> RevealCoreAsync(SearchItemRef item)
    {
        if (_resolveRunner is null)
            return WorkspaceRevealOutcome.Failed;
        var version = ++_revealVersion;
        await ActivateAsync();
        if (!IsActivated || version != _revealVersion)
            return IsActivated ? WorkspaceRevealOutcome.Superseded : WorkspaceRevealOutcome.Failed;

        SearchRevealTarget? target = null;
        for (var attempt = 1; attempt <= 2 && target is null; attempt++)
        {
            var stamps = CaptureStamps();
            var outcome = await _resolveRunner.Submit(item);
            if (version != _revealVersion || outcome.Status == RunStatus.Superseded)
                return WorkspaceRevealOutcome.Superseded;
            if (outcome.Status == RunStatus.Failed)
            {
                if (outcome.Error is not WorkspaceSearchBusyException)
                    _log.Error("A search result could not be resolved.", outcome.Error!); // no query text or content
                return outcome.Error is WorkspaceSearchBusyException ? WorkspaceRevealOutcome.Busy : WorkspaceRevealOutcome.Failed;
            }

            if (outcome.Value!.Status != RevealResolveStatus.Found)
            {
                // A negative answer is as stale as a snapshot when a committed lifecycle result was applied after this
                // resolve started (e.g. a Restore): it counts as the detected change and uses the same bounded retry.
                if (_lifecycleCompletions != stamps.Completions)
                    continue;
                return outcome.Value.Status switch
                {
                    RevealResolveStatus.Trashed => WorkspaceRevealOutcome.Trashed,
                    RevealResolveStatus.ProjectTrashed => WorkspaceRevealOutcome.ProjectTrashed,
                    _ => WorkspaceRevealOutcome.NotFound,
                };
            }

            var candidate = outcome.Value.Target!;
            var pendingLoads = PathNodes(candidate).Select(n => n.ChildrenLoad).OfType<Task<bool>>().ToList();
            if (pendingLoads.Count > 0)
            {
                await Task.WhenAll(pendingLoads); // an older lazy load must land (or be discarded) before we apply
                if (version != _revealVersion)
                    return WorkspaceRevealOutcome.Superseded;
                continue;
            }

            if (PathChanged(candidate, stamps))
                continue;
            target = candidate;
        }

        if (target is null)
            return WorkspaceRevealOutcome.WorkspaceChanged;

        ApplyReveal(target);
        if (target.Content is not { } content)
            return WorkspaceRevealOutcome.Selected;

        if (SelectedContent is not { } view || view.ProjectNodeId != content.ProjectNodeId)
            return WorkspaceRevealOutcome.Failed; // content view not wired
        var kind = content.Kind switch
        {
            SearchResultKind.ProjectFolder => ProjectContentItemKind.Folder,
            SearchResultKind.Link => ProjectContentItemKind.Link,
            SearchResultKind.Note => ProjectContentItemKind.Note,
            _ => throw new InvalidOperationException($"{content.Kind} is not a content kind."),
        };
        var revealed = await view.RevealAsync(content.ContainerFolderId, kind, content.Id);
        if (version != _revealVersion)
            return WorkspaceRevealOutcome.Superseded;
        return revealed switch
        {
            ContentRevealOutcome.Selected => WorkspaceRevealOutcome.Selected,
            ContentRevealOutcome.MovedMeanwhile => WorkspaceRevealOutcome.MovedMeanwhile,
            ContentRevealOutcome.ContainerMissing => WorkspaceRevealOutcome.ContainerMissing,
            ContentRevealOutcome.LoadFailed => WorkspaceRevealOutcome.Failed,
            _ => WorkspaceRevealOutcome.Superseded,
        };
    }

    /// <summary>The roots stamp, every cached branch stamp and the lifecycle-completion count, taken BEFORE a resolve.</summary>
    private (int Roots, int Completions, Dictionary<long, int> Nodes) CaptureStamps() =>
        (_rootsStamp, _lifecycleCompletions, _nodes.Values.ToDictionary(n => n.Id, n => n.BranchStamp));

    /// <summary>Cached instances whose Children the snapshot reconciles (the ancestors), plus the target's current parent.</summary>
    private IEnumerable<WorkspaceNodeViewModel> PathNodes(SearchRevealTarget target)
    {
        foreach (var id in target.WorkspaceAncestorIds)
        {
            if (_nodes.TryGetValue(id, out var node))
                yield return node;
        }

        if (_nodes.TryGetValue(target.WorkspaceNodeId, out var self) && self.Parent is { } parent)
            yield return parent;
    }

    /// <summary>
    /// A path branch, the roots, or ANY committed lifecycle result applied since the stamps were taken: the snapshot may predate
    /// that commit (even for an unloaded branch whose stamps cannot show it), so it is never applied as is (§8.3.3).
    /// </summary>
    private bool PathChanged(SearchRevealTarget target, (int Roots, int Completions, Dictionary<long, int> Nodes) before) =>
        _rootsStamp != before.Roots
        || _lifecycleCompletions != before.Completions
        || PathNodes(target).Any(n => !before.Nodes.TryGetValue(n.Id, out var stamp) || stamp != n.BranchStamp);

    /// <summary>
    /// Synchronous, root level first. Loaded collections on the path become exactly the snapshot (a Complete level removes
    /// what is no longer there; an incomplete level only adds/relocates); unloaded ones are filled from the snapshot.
    /// Removed instances that the snapshot does not place elsewhere on the path are forgotten (their new location is in a
    /// branch this reveal does not read). Other branches are untouched, except that a relocated instance leaves its former
    /// collection.
    /// </summary>
    private void ApplyReveal(SearchRevealTarget target)
    {
        _applyingReveal = true;
        try
        {
            var sequence = ++_revealSequence;
            var placedOnPath = target.Branches.SelectMany(b => b.Children).Select(c => c.Id).ToHashSet();
            var removed = new List<WorkspaceNodeViewModel>(); // forgotten only after EVERY level is placed (see below)
            foreach (var branch in target.Branches)
            {
                var parent = branch.ParentId is { } parentId ? _nodes[parentId] : null; // placed by the previous level
                var collection = parent?.Children ?? Roots;
                var loaded = parent is null || parent.IsChildrenLoaded;
                if (loaded && branch.Complete)
                {
                    var fresh = branch.Children.Select(c => c.Id).ToHashSet();
                    foreach (var stale in collection.Where(c => !c.IsPlaceholder && !fresh.Contains(c.Id)).ToList())
                    {
                        collection.Remove(stale);
                        _touchedByReveal[stale.Id] = sequence;
                        if (!placedOnPath.Contains(stale.Id))
                            removed.Add(stale);
                    }
                }

                foreach (var row in branch.Children)
                {
                    Place(GetOrCreate(row, parent), parent);
                    _touchedByReveal[row.Id] = sequence;
                }

                if (parent is not null && !parent.IsChildrenLoaded && branch.Complete)
                {
                    foreach (var placeholder in parent.Children.Where(c => c.IsPlaceholder).ToList())
                        parent.Children.Remove(placeholder);
                    parent.IsChildrenLoaded = true;
                }

                if (parent is not null)
                    parent.ReconcileStamp++; // roots never lazy-reload after activation, so only folders need the guard
            }

            // An obsolete cached ancestor may still list descendants that this snapshot keeps elsewhere. Forgetting it only now
            // (they have been relocated out of its Children by then) and never forgetting a retained node keeps their instances,
            // loaded subtrees and expansion, whatever the order of the snapshot rows.
            foreach (var node in removed)
                ForgetSubtree(node, keep: placedOnPath);

            foreach (var id in target.WorkspaceAncestorIds)
                _nodes[id].IsExpanded = true;
            SelectedNode = _nodes[target.WorkspaceNodeId];
        }
        finally
        {
            _applyingReveal = false;
        }
    }

    // ---- Targeted tree updates (only after a successful write) ----------------------------------------------------

    private async Task ApplyCreatedAsync(WorkspaceNodeViewModel? parent, WorkspaceNode created)
    {
        WorkspaceNodeViewModel vm;
        if (parent is null)
        {
            vm = GetOrCreate(created, null);
            MergeInto(Roots, vm);
        }
        else
        {
            parent.SetHasChildren(true);
            if (!await EnsureChildrenLoadedAsync(parent))
                return; // the node exists; it appears when the folder loads
            vm = GetOrCreate(created, parent);
            MergeInto(parent.Children, vm);
            parent.IsExpanded = true;
        }

        SelectedNode = vm;
    }

    private void ApplyName(WorkspaceNodeViewModel node, string name)
    {
        node.Name = name;
        if (SelectedContent?.ProjectNodeId == node.Id)
            SelectedContent.ProjectName = name;
        var siblings = node.Parent?.Children ?? Roots;
        var oldIndex = siblings.IndexOf(node);
        if (oldIndex < 0)
            return;

        var newIndex = siblings.Count(s => !ReferenceEquals(s, node) && WorkspaceNodeOrder.Instance.Compare(s.SortKey, node.SortKey) < 0);
        if (newIndex != oldIndex)
            siblings.Move(oldIndex, newIndex);
        if (ReferenceEquals(SelectedNode, node))
            node.IsSelected = true; // keep the selection across the re-sort
    }

    private async Task ApplyMovedAsync(WorkspaceNodeViewModel node, long? targetId)
    {
        var oldParent = node.Parent;
        if (oldParent?.ChildrenLoad is { } pendingSource)
            await pendingSource;
        (oldParent?.Children ?? Roots).Remove(node);

        if (targetId is null)
        {
            node.Parent = null;
            MergeInto(Roots, node);
        }
        else if (_nodes.TryGetValue(targetId.Value, out var target))
        {
            node.Parent = target;
            target.SetHasChildren(true);
            if (!await EnsureChildrenLoadedAsync(target))
            {
                ForgetSubtree(node);
                SelectedNode = null;
                return;
            }

            MergeInto(target.Children, node);
            target.IsExpanded = true;
        }
        else
        {
            // Destination folder not loaded anywhere in the tree: the node now lives below a collapsed, unloaded folder.
            ForgetSubtree(node);
            SelectedNode = null;
            return;
        }

        if (ReferenceEquals(SelectedNode, node))
        {
            node.IsSelected = true;
            StartDetailsLoad();
        }
        else
        {
            SelectedNode = node;
        }
    }

    /// <param name="keep">Nodes (with their subtrees) that must stay registered because they are placed elsewhere.</param>
    private void ForgetSubtree(WorkspaceNodeViewModel node, IReadOnlySet<long>? keep = null)
    {
        if (keep?.Contains(node.Id) == true)
            return;
        _nodes.Remove(node.Id);
        foreach (var child in node.Children.Where(c => !c.IsPlaceholder))
            ForgetSubtree(child, keep);
    }

    private static void MergeInto(ObservableCollection<WorkspaceNodeViewModel> target, WorkspaceNodeViewModel node)
    {
        if (target.Any(c => c.Id == node.Id && !c.IsPlaceholder))
            return;
        InsertSorted(target, node);
    }

    private static void InsertSorted(ObservableCollection<WorkspaceNodeViewModel> target, WorkspaceNodeViewModel node)
    {
        if (target.Count == 1 && target[0].IsPlaceholder)
            target.Clear();
        var index = 0;
        while (index < target.Count && WorkspaceNodeOrder.Instance.Compare(target[index].SortKey, node.SortKey) < 0)
            index++;
        target.Insert(index, node);
    }

    private void ReportError(string message, Exception ex)
    {
        _log.Error(message, ex);
        BannerMessage = message;
    }
}
