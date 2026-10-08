using WorkHub.Core.Workspace;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>New Folder, or rename a Folder/Project. Saves through <see cref="WorkspaceService"/>; errors keep the dialog open.</summary>
public sealed class WorkspaceNameEditorViewModel : ObservableObject
{
    private readonly WorkspaceService _workspace;
    private readonly long? _parentId;
    private readonly long? _nodeId;
    private string _name;
    private string? _error;

    private WorkspaceNameEditorViewModel(WorkspaceService workspace, string title, long? parentId, long? nodeId, string name, string? parentPath)
    {
        _workspace = workspace;
        _parentId = parentId;
        _nodeId = nodeId;
        _name = name;
        Title = title;
        LocationText = LocationOf(parentPath);
        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ex => Error = $"Could not save: {ex.Message}");
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public static WorkspaceNameEditorViewModel ForNewFolder(WorkspaceService workspace, long? parentId, string? parentPath) =>
        new(workspace, "New Folder", parentId, null, string.Empty, parentPath);

    public static WorkspaceNameEditorViewModel ForRename(WorkspaceService workspace, long nodeId, WorkspaceNodeType type, string currentName, string? parentPath) =>
        new(workspace, type == WorkspaceNodeType.Folder ? "Rename Folder" : "Rename Project", null, nodeId, currentName, parentPath);

    public event EventHandler<bool>? CloseRequested;

    public string Title { get; }

    public string LocationText { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>Set after a successful create.</summary>
    public WorkspaceNode? CreatedNode { get; private set; }

    /// <summary>The stored (trimmed) name after a successful rename.</summary>
    public string? SavedName { get; private set; }

    internal static string LocationOf(string? parentPath) =>
        parentPath is null ? "Location: Workspace (top level)" : $"Location: {parentPath}";

    private async Task SaveAsync()
    {
        Error = null;
        if (_nodeId is { } id)
        {
            var renamed = await _workspace.RenameAsync(id, Name);
            if (!renamed.Succeeded)
            {
                Error = renamed.Error;
                return;
            }

            SavedName = renamed.Value;
        }
        else
        {
            var created = await _workspace.CreateFolderAsync(_parentId, Name);
            if (!created.Succeeded)
            {
                Error = created.Error;
                return;
            }

            CreatedNode = created.Value;
        }

        CloseRequested?.Invoke(this, true);
    }
}

/// <summary>Create or edit a Project: name, status and optional details. Errors keep the dialog open.</summary>
public sealed class ProjectEditorViewModel : ObservableObject
{
    private readonly WorkspaceService _workspace;
    private readonly long? _parentId;
    private readonly ProjectDetails? _existing;
    private string _name;
    private ProjectStatusOption _status;
    private string _description;
    private string _owner;
    private string _projectManager;
    private string? _nameError;
    private string? _statusError;
    private string? _descriptionError;
    private string? _ownerError;
    private string? _projectManagerError;
    private string? _generalError;

    private ProjectEditorViewModel(WorkspaceService workspace, long? parentId, ProjectDetails? existing, string? parentPath)
    {
        _workspace = workspace;
        _parentId = parentId;
        _existing = existing;
        _name = existing?.Name ?? string.Empty;
        _status = Statuses.First(s => s.Status == (existing?.Status ?? ProjectStatuses.Default));
        _description = existing?.Description ?? string.Empty;
        _owner = existing?.Owner ?? string.Empty;
        _projectManager = existing?.ProjectManager ?? string.Empty;
        LocationText = WorkspaceNameEditorViewModel.LocationOf(parentPath);
        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ex => GeneralError = $"Could not save: {ex.Message}");
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public static ProjectEditorViewModel ForCreate(WorkspaceService workspace, long? parentId, string? parentPath) =>
        new(workspace, parentId, null, parentPath);

    public static ProjectEditorViewModel ForEdit(WorkspaceService workspace, ProjectDetails existing, string? parentPath) =>
        new(workspace, existing.ParentId, existing, parentPath);

    public event EventHandler<bool>? CloseRequested;

    public bool IsEdit => _existing is not null;

    public string Title => IsEdit ? "Edit Project" : "New Project";

    public string LocationText { get; }

    /// <summary>Selectable statuses. Archived is not offered in this phase.</summary>
    public IReadOnlyList<ProjectStatusOption> Statuses { get; } =
        ProjectStatuses.Editable.Select(s => new ProjectStatusOption(s, ProjectStatuses.DisplayName(s))).ToList();

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public ProjectStatusOption Status { get => _status; set => SetProperty(ref _status, value); }

    public string Description { get => _description; set => SetProperty(ref _description, value); }

    public string Owner { get => _owner; set => SetProperty(ref _owner, value); }

    public string ProjectManager { get => _projectManager; set => SetProperty(ref _projectManager, value); }

    public string? NameError { get => _nameError; private set => SetProperty(ref _nameError, value); }

    public string? StatusError { get => _statusError; private set => SetProperty(ref _statusError, value); }

    public string? DescriptionError { get => _descriptionError; private set => SetProperty(ref _descriptionError, value); }

    public string? OwnerError { get => _ownerError; private set => SetProperty(ref _ownerError, value); }

    public string? ProjectManagerError { get => _projectManagerError; private set => SetProperty(ref _projectManagerError, value); }

    public string? GeneralError { get => _generalError; private set => SetProperty(ref _generalError, value); }

    /// <summary>Set after a successful create.</summary>
    public WorkspaceNode? CreatedNode { get; private set; }

    /// <summary>The stored state after a successful edit.</summary>
    public ProjectDetails? SavedDetails { get; private set; }

    private async Task SaveAsync()
    {
        NameError = StatusError = DescriptionError = OwnerError = ProjectManagerError = GeneralError = null;
        var draft = new ProjectDraft(Name, Status.Status, Description, Owner, ProjectManager);

        string? field, error;
        if (_existing is null)
        {
            var created = await _workspace.CreateProjectAsync(_parentId, draft);
            (field, error) = (created.Field, created.Error);
            CreatedNode = created.Value;
        }
        else
        {
            var updated = await _workspace.UpdateProjectAsync(_existing.NodeId, draft);
            (field, error) = (updated.Field, updated.Error);
            SavedDetails = updated.Value;
        }

        if (error is null)
        {
            CloseRequested?.Invoke(this, true);
            return;
        }

        switch (field)
        {
            case ProjectDraftValidator.Fields.Name: NameError = error; break;
            case ProjectDraftValidator.Fields.Status: StatusError = error; break;
            case ProjectDraftValidator.Fields.Description: DescriptionError = error; break;
            case ProjectDraftValidator.Fields.Owner: OwnerError = error; break;
            case ProjectDraftValidator.Fields.ProjectManager: ProjectManagerError = error; break;
            default: GeneralError = error; break;
        }
    }
}

public sealed record ProjectStatusOption(ProjectStatus Status, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// "Move to…": pick the top level or a Folder (the node's own subtree is not offered), then move through
/// <see cref="WorkspaceService"/>. A rejected move (cycle, name conflict, vanished node) keeps the dialog open.
/// </summary>
public sealed class WorkspaceMoveViewModel : ObservableObject
{
    private readonly WorkspaceService _workspace;
    private readonly long _nodeId;
    private WorkspaceMoveDestination? _selectedDestination;
    private string? _error;

    public WorkspaceMoveViewModel(WorkspaceService workspace, long nodeId, string nodeName, long? currentParentId, IEnumerable<WorkspaceFolderPath> folders)
    {
        _workspace = workspace;
        _nodeId = nodeId;
        NodeName = nodeName;
        Destinations =
        [
            new WorkspaceMoveDestination(null, "Workspace (top level)"),
            .. folders.Select(f => new WorkspaceMoveDestination(f.Id, f.Path)),
        ];
        _selectedDestination = Destinations.FirstOrDefault(d => d.FolderId == currentParentId);
        MoveCommand = new AsyncRelayCommand(MoveAsync, () => SelectedDestination is not null, ex => Error = $"Could not move: {ex.Message}");
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public string NodeName { get; }

    public string Title => $"Move “{NodeName}” to…";

    public IReadOnlyList<WorkspaceMoveDestination> Destinations { get; }

    public WorkspaceMoveDestination? SelectedDestination
    {
        get => _selectedDestination;
        set
        {
            if (!SetProperty(ref _selectedDestination, value))
                return;
            Error = null;
            MoveCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncRelayCommand MoveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>True when the node actually moved (false for "already there").</summary>
    public bool Moved { get; private set; }

    public long? MovedTo { get; private set; }

    private async Task MoveAsync()
    {
        Error = null;
        var destination = SelectedDestination!;
        var result = await _workspace.MoveAsync(_nodeId, destination.FolderId);
        if (!result.Succeeded)
        {
            Error = result.Error;
            return;
        }

        Moved = result.Value;
        MovedTo = destination.FolderId;
        CloseRequested?.Invoke(this, true);
    }
}

public sealed record WorkspaceMoveDestination(long? FolderId, string Path)
{
    public override string ToString() => Path;
}
