using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// New ProjectFolder or rename one. Same binding surface as <see cref="WorkspaceNameEditorViewModel"/> (the window is
/// shared). Saves through <see cref="ProjectContentService"/>; errors keep the dialog open with the typed name.
/// </summary>
public sealed class ProjectFolderNameEditorViewModel : ObservableObject
{
    private readonly ProjectContentService _content;
    private readonly long _projectNodeId;
    private readonly long? _parentFolderId;
    private readonly long? _folderId;
    private string _name;
    private string? _error;
    private bool _isSaving;

    private ProjectFolderNameEditorViewModel(ProjectContentService content, string title, long projectNodeId, long? parentFolderId, long? folderId, string name, string locationPath)
    {
        _content = content;
        _projectNodeId = projectNodeId;
        _parentFolderId = parentFolderId;
        _folderId = folderId;
        _name = name;
        Title = title;
        LocationText = $"Location: {locationPath}";
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false), () => CanClose);
    }

    /// <param name="locationPath">Display path of the container, e.g. "Alpha › Specs".</param>
    public static ProjectFolderNameEditorViewModel ForCreate(ProjectContentService content, long projectNodeId, long? parentFolderId, string locationPath) =>
        new(content, "New Folder", projectNodeId, parentFolderId, null, string.Empty, locationPath);

    public static ProjectFolderNameEditorViewModel ForRename(ProjectContentService content, ProjectFolder folder, string locationPath) =>
        new(content, "Rename Folder", folder.ProjectNodeId, folder.ParentFolderId, folder.Id, folder.Name, locationPath);

    public event EventHandler<bool>? CloseRequested;

    public string Title { get; }

    public string LocationText { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>True from Save until the write committed or failed.</summary>
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!SetProperty(ref _isSaving, value))
                return;
            OnPropertyChanged(nameof(CanClose));
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>False while a write is pending: Cancel, Escape, title-bar close and Alt+F4 wait for its outcome.</summary>
    public bool CanClose => !IsSaving;

    /// <summary>The stored folder after a successful create or rename.</summary>
    public ProjectFolder? SavedFolder { get; private set; }

    private async Task SaveAsync()
    {
        Error = null;
        IsSaving = true;
        WorkspaceResult<ProjectFolder> result;
        try
        {
            result = _folderId is { } id
                ? await _content.RenameFolderAsync(id, Name)
                : await _content.CreateFolderAsync(_projectNodeId, _parentFolderId, Name);
        }
        catch (Exception ex)
        {
            result = WorkspaceResult<ProjectFolder>.Failed($"Could not save: {ex.Message}");
        }

        if (!result.Succeeded)
        {
            Error = result.Error; // set before closing is allowed again
            IsSaving = false;
            return;
        }

        SavedFolder = result.Value;
        IsSaving = false;
        CloseRequested?.Invoke(this, true);
    }
}

/// <summary>Create or edit a Project Link (type, name, target). Errors keep the dialog open with the typed values.</summary>
public sealed class ProjectLinkEditorViewModel : ObservableObject
{
    private readonly ProjectContentService _content;
    private readonly IDialogService _dialogs;
    private readonly long _projectNodeId;
    private readonly long? _folderId;
    private readonly ProjectLink? _existing;
    private string _name;
    private ProjectLinkType _linkType;
    private string _target;
    private string? _nameError;
    private string? _targetError;
    private string? _generalError;
    private bool _isSaving;

    private ProjectLinkEditorViewModel(ProjectContentService content, IDialogService dialogs, long projectNodeId, long? folderId, ProjectLink? existing, string locationPath)
    {
        _content = content;
        _dialogs = dialogs;
        _projectNodeId = projectNodeId;
        _folderId = folderId;
        _existing = existing;
        _name = existing?.Name ?? string.Empty;
        _linkType = existing?.Type ?? ProjectLinkType.Folder;
        _target = existing?.Target ?? string.Empty;
        LocationText = $"Location: {locationPath}";
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false), () => CanClose);
        BrowseTargetCommand = new RelayCommand(BrowseTarget, () => LinkType != ProjectLinkType.Web && !IsSaving);
    }

    public static ProjectLinkEditorViewModel ForCreate(ProjectContentService content, IDialogService dialogs, long projectNodeId, long? folderId, string locationPath) =>
        new(content, dialogs, projectNodeId, folderId, null, locationPath);

    public static ProjectLinkEditorViewModel ForEdit(ProjectContentService content, IDialogService dialogs, ProjectLink existing, string locationPath) =>
        new(content, dialogs, existing.ProjectNodeId, existing.FolderId, existing, locationPath);

    public event EventHandler<bool>? CloseRequested;

    public bool IsEdit => _existing is not null;

    public string Title => IsEdit ? "Edit Link" : "New Link";

    public string LocationText { get; }

    public IReadOnlyList<ProjectLinkType> LinkTypes => ProjectLinkTypes.All;

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand BrowseTargetCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public ProjectLinkType LinkType
    {
        get => _linkType;
        set
        {
            if (!SetProperty(ref _linkType, value))
                return;
            OnPropertyChanged(nameof(TargetHint));
            BrowseTargetCommand.RaiseCanExecuteChanged();
            TargetError = null;
        }
    }

    public string TargetHint => LinkType switch
    {
        ProjectLinkType.Web => "Web address, e.g. https://portal.example.test/",
        ProjectLinkType.File => @"Full file path, e.g. C:\Docs\report.xlsx or \\server\share\report.xlsx",
        _ => @"Full folder path, e.g. C:\Projects or \\server\share\Projects",
    };

    public string Target { get => _target; set => SetProperty(ref _target, value); }

    public string? NameError { get => _nameError; private set => SetProperty(ref _nameError, value); }

    public string? TargetError { get => _targetError; private set => SetProperty(ref _targetError, value); }

    public string? GeneralError { get => _generalError; private set => SetProperty(ref _generalError, value); }

    /// <summary>True from Save until the write committed or failed.</summary>
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!SetProperty(ref _isSaving, value))
                return;
            OnPropertyChanged(nameof(CanClose));
            CancelCommand.RaiseCanExecuteChanged();
            BrowseTargetCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>False while a write is pending: Cancel, Escape, title-bar close and Alt+F4 wait for its outcome.</summary>
    public bool CanClose => !IsSaving;

    /// <summary>The stored link after a successful create or edit.</summary>
    public ProjectLink? SavedLink { get; private set; }

    private async Task SaveAsync()
    {
        NameError = TargetError = GeneralError = null;
        var draft = new ProjectLinkDraft(Name, LinkType, Target);
        IsSaving = true;
        WorkspaceResult<ProjectLink> result;
        try
        {
            result = _existing is null
                ? await _content.CreateLinkAsync(_projectNodeId, _folderId, draft)
                : await _content.UpdateLinkAsync(_existing.Id, draft);
        }
        catch (Exception ex)
        {
            result = WorkspaceResult<ProjectLink>.Failed($"Could not save: {ex.Message}");
        }

        if (!result.Succeeded)
        {
            switch (result.Field) // set before closing is allowed again
            {
                case ProjectLinkValidator.Fields.Name: NameError = result.Error; break;
                case ProjectLinkValidator.Fields.Target: TargetError = result.Error; break;
                default: GeneralError = result.Error; break;
            }

            IsSaving = false;
            return;
        }

        SavedLink = result.Value;
        IsSaving = false;
        CloseRequested?.Invoke(this, true);
    }

    private void BrowseTarget()
    {
        var current = string.IsNullOrWhiteSpace(Target) ? null : Target.Trim();
        var picked = LinkType switch
        {
            ProjectLinkType.File => _dialogs.PickFile("Select file", current),
            ProjectLinkType.Folder => _dialogs.PickFolder("Select folder", current),
            _ => null,
        };
        if (picked is null)
            return;

        Target = picked;
        if (string.IsNullOrWhiteSpace(Name))
            Name = Path.GetFileName(picked.TrimEnd('\\', '/')) is { Length: > 0 } leaf ? leaf : picked;
    }
}

/// <summary>
/// "Move to…" for a ProjectFolder, Link or Note, bound to the shared Workspace move window. The source container observed when
/// the dialog opened is captured once and sent unchanged with every submit (revision 3 §8.1). After a
/// "moved meanwhile" refusal the dialog's observation is stale, so Move stays disabled.
/// </summary>
public sealed class ProjectContentMoveViewModel : ObservableObject
{
    private readonly ProjectContentService _content;
    private readonly ProjectContentItemKind _kind;
    private readonly long _itemId;
    private readonly long? _observedContainerId;
    private WorkspaceMoveDestination? _selectedDestination;
    private string? _error;
    private bool _isMoving;
    private bool _isStale;

    public ProjectContentMoveViewModel(
        ProjectContentService content, ProjectContentItemKind kind, long itemId, string itemName, long? observedContainerId,
        string projectName, IEnumerable<WorkspaceFolderPath> folders)
    {
        _content = content;
        _kind = kind;
        _itemId = itemId;
        _observedContainerId = observedContainerId;
        NodeName = itemName;
        Destinations =
        [
            new WorkspaceMoveDestination(null, $"{projectName} (project root)"),
            .. folders.Select(f => new WorkspaceMoveDestination(f.Id, f.Path)),
        ];
        _selectedDestination = Destinations.FirstOrDefault(d => d.FolderId == observedContainerId);
        MoveCommand = new AsyncRelayCommand(MoveAsync, () => SelectedDestination is not null && !IsStale);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false), () => CanClose);
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
            if (!IsStale)
                Error = null;
            MoveCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncRelayCommand MoveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>True from Move until the write committed or failed.</summary>
    public bool IsMoving
    {
        get => _isMoving;
        private set
        {
            if (!SetProperty(ref _isMoving, value))
                return;
            OnPropertyChanged(nameof(CanClose));
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>False while the move is pending: Cancel, Escape, title-bar close and Alt+F4 wait for its outcome.</summary>
    public bool CanClose => !IsMoving;

    /// <summary>The item was moved by someone else after this dialog opened; the caller reloads the content view.</summary>
    public bool IsStale
    {
        get => _isStale;
        private set
        {
            if (SetProperty(ref _isStale, value))
                MoveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>True when the item actually moved (false for "already there").</summary>
    public bool Moved { get; private set; }

    public long? MovedTo { get; private set; }

    private async Task MoveAsync()
    {
        Error = null;
        var destination = SelectedDestination!;
        IsMoving = true;
        ContentMoveResult result;
        try
        {
            result = _kind switch
            {
                ProjectContentItemKind.Folder => await _content.MoveFolderAsync(_itemId, _observedContainerId, destination.FolderId),
                ProjectContentItemKind.Link => await _content.MoveLinkAsync(_itemId, _observedContainerId, destination.FolderId),
                _ => await _content.MoveNoteAsync(_itemId, _observedContainerId, destination.FolderId),
            };
        }
        catch (Exception ex)
        {
            result = new ContentMoveResult(ContentMoveOutcome.NotFound, $"Could not move: {ex.Message}");
        }

        if (!result.Succeeded)
        {
            Error = result.Error; // set before closing is allowed again
            IsStale = result.IsStale;
            IsMoving = false;
            return;
        }

        Moved = result.Moved;
        MovedTo = destination.FolderId;
        IsMoving = false;
        CloseRequested?.Invoke(this, true);
    }
}
