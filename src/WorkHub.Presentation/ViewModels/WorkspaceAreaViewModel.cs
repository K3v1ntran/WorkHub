using System.Collections.ObjectModel;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Workspace tab: the logical tree of Folders and Projects plus the selected Project's details.
/// <list type="bullet">
/// <item>Nothing loads until the tab is first shown; then only root nodes load. A Folder's children load on its first
/// expand, and a load already in flight is reused rather than repeated.</item>
/// <item>The tree changes only after a write succeeded, and only the affected collections change (merged by Id).</item>
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

    /// <param name="content">Project content (ProjectFolders and Links); null leaves the content view out.</param>
    /// <param name="checker">The application-wide manual link checker.</param>
    public WorkspaceAreaViewModel(
        WorkspaceService workspace, IDialogService dialogs, IAppLog log,
        ProjectContentService? content = null, ILinkHealthChecker? checker = null)
    {
        _workspace = workspace;
        _dialogs = dialogs;
        _log = log;
        _content = content;
        _checker = checker;

        Action<Exception> onError = ex => ReportError("The Workspace operation failed. Nothing was changed.", ex);
        NewFolderCommand = new AsyncRelayCommand(NewFolderAsync, () => IsActivated, onError);
        NewProjectCommand = new AsyncRelayCommand(NewProjectAsync, () => IsActivated, onError);
        RenameCommand = new AsyncRelayCommand(RenameAsync, HasSelection, onError);
        EditProjectCommand = new AsyncRelayCommand(EditProjectAsync, () => SelectedNode?.IsProject == true, onError);
        MoveCommand = new AsyncRelayCommand(MoveAsync, HasSelection, onError);
        RetryLoadCommand = new AsyncRelayCommand(RetryLoadAsync, () => RootLoadError is not null && !IsLoadingRoots, onError);
        DismissBannerCommand = new RelayCommand(() => BannerMessage = null);
        Roots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<WorkspaceNodeViewModel> Roots { get; } = [];

    public AsyncRelayCommand NewFolderCommand { get; }

    public AsyncRelayCommand NewProjectCommand { get; }

    public AsyncRelayCommand RenameCommand { get; }

    public AsyncRelayCommand EditProjectCommand { get; }

    public AsyncRelayCommand MoveCommand { get; }

    public AsyncRelayCommand RetryLoadCommand { get; }

    public RelayCommand DismissBannerCommand { get; }

    /// <summary>Bound to the Workspace tab. The first selection starts the one root load.</summary>
    public bool IsTabSelected
    {
        get => _isTabSelected;
        set
        {
            if (SetProperty(ref _isTabSelected, value) && value)
                _ = ActivateAsync();
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
            NewFolderCommand.RaiseCanExecuteChanged();
            NewProjectCommand.RaiseCanExecuteChanged();
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

            var previous = _selectedNode;
            _selectedNode = value;
            if (previous is not null)
                previous.IsSelected = false;
            if (value is not null)
                value.IsSelected = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFolderSelected));
            RenameCommand.RaiseCanExecuteChanged();
            EditProjectCommand.RaiseCanExecuteChanged();
            MoveCommand.RaiseCanExecuteChanged();
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

    public string? BannerMessage { get => _bannerMessage; private set => SetProperty(ref _bannerMessage, value); }

    /// <summary>The most recent details load; lets tests await it.</summary>
    internal Task CurrentDetailsLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The root load (completed when none was started).</summary>
    internal Task RootLoad => _rootLoad ?? Task.CompletedTask;

    /// <summary>Starts the root load once. Later calls (tab switches) reuse it and never reload.</summary>
    public Task ActivateAsync() => _rootLoad ??= LoadRootsAsync();

    // ---- Loading ----------------------------------------------------------------------------------------------------

    private Task RetryLoadAsync() => _rootLoad = LoadRootsAsync();

    private async Task LoadRootsAsync()
    {
        IsLoadingRoots = true;
        RootLoadError = null;
        try
        {
            var roots = await _workspace.ListChildrenAsync(null);
            ReplaceChildren(Roots, null, roots);
            IsActivated = true;
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
        try
        {
            var children = await _workspace.ListChildrenAsync(node.Id);
            ReplaceChildren(node.Children, node, children);
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

    private void ReplaceChildren(ObservableCollection<WorkspaceNodeViewModel> target, WorkspaceNodeViewModel? parent, IReadOnlyList<WorkspaceNode> loaded)
    {
        // Keep anything merged while this load was pending (e.g. a node created meanwhile), then the loaded rows; by Id.
        var merged = target.Where(c => !c.IsPlaceholder && loaded.All(n => n.Id != c.Id)).ToList();
        target.Clear();
        foreach (var node in loaded)
            target.Add(GetOrCreate(node, parent));
        foreach (var extra in merged)
            InsertSorted(target, extra);
    }

    private WorkspaceNodeViewModel GetOrCreate(WorkspaceNode node, WorkspaceNodeViewModel? parent)
    {
        if (_nodes.TryGetValue(node.Id, out var existing))
        {
            existing.Name = node.Name;
            existing.Parent = parent;
            existing.SetHasChildren(node.HasChildren);
            return existing;
        }

        var created = new WorkspaceNodeViewModel(this, node, parent);
        _nodes[node.Id] = created;
        return created;
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
                DetailsMessage = "This project no longer exists.";
            else
                SelectedProject = details;
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

    private async Task NewFolderAsync()
    {
        var parent = CreationParent();
        var editor = WorkspaceNameEditorViewModel.ForNewFolder(_workspace, parent?.Id, parent?.Path);
        if (_dialogs.ShowWorkspaceNameEditor(editor) && editor.CreatedNode is { } created)
            await ApplyCreatedAsync(parent, created);
    }

    private async Task NewProjectAsync()
    {
        var parent = CreationParent();
        var editor = ProjectEditorViewModel.ForCreate(_workspace, parent?.Id, parent?.Path);
        if (_dialogs.ShowProjectEditor(editor) && editor.CreatedNode is { } created)
            await ApplyCreatedAsync(parent, created);
    }

    private Task RenameAsync()
    {
        var node = SelectedNode!;
        var editor = WorkspaceNameEditorViewModel.ForRename(_workspace, node.Id, node.Type, node.Name, node.Parent?.Path);
        if (_dialogs.ShowWorkspaceNameEditor(editor) && editor.SavedName is { } name)
        {
            ApplyName(node, name);
            if (ReferenceEquals(SelectedNode, node) && node.IsProject)
                StartDetailsLoad(); // name and UpdatedAt changed
        }

        return Task.CompletedTask;
    }

    private async Task EditProjectAsync()
    {
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

        var editor = ProjectEditorViewModel.ForEdit(_workspace, current, node.Parent?.Path);
        if (_dialogs.ShowProjectEditor(editor) && editor.SavedDetails is { } saved)
        {
            ApplyName(node, saved.Name);
            ShowSavedDetails(saved);
        }
    }

    private async Task MoveAsync()
    {
        var node = SelectedNode!;
        var targets = await _workspace.ListMoveTargetsAsync(node.Id);
        if (!ReferenceEquals(SelectedNode, node))
            return; // the selection changed while destinations were loading

        var editor = new WorkspaceMoveViewModel(_workspace, node.Id, node.Name, node.Parent?.Id, targets);
        if (_dialogs.ShowWorkspaceMove(editor) && editor.Moved)
            await ApplyMovedAsync(node, editor.MovedTo);
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

    private void ForgetSubtree(WorkspaceNodeViewModel node)
    {
        _nodes.Remove(node.Id);
        foreach (var child in node.Children.Where(c => !c.IsPlaceholder))
            ForgetSubtree(child);
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
