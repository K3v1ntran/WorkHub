using System.Collections.ObjectModel;
using System.Windows.Input;
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
    private bool _isSaving;

    private WorkspaceNameEditorViewModel(WorkspaceService workspace, string title, long? parentId, long? nodeId, string name, string? parentPath)
    {
        _workspace = workspace;
        _parentId = parentId;
        _nodeId = nodeId;
        _name = name;
        Title = title;
        LocationText = LocationOf(parentPath);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false), () => CanClose);
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

    /// <summary>Set after a successful create.</summary>
    public WorkspaceNode? CreatedNode { get; private set; }

    /// <summary>The stored (trimmed) name after a successful rename.</summary>
    public string? SavedName { get; private set; }

    /// <summary>The typed reason of the last refused submit (for example ProjectTrashed or ParentNotFound); the caller reconciles
    /// the node or parent by a fresh read after the dialog closes.</summary>
    public WorkspaceFailure? Refusal { get; private set; }

    internal static string LocationOf(string? parentPath) =>
        parentPath is null ? "Location: Workspace (top level)" : $"Location: {parentPath}";

    private async Task SaveAsync()
    {
        Error = null;
        IsSaving = true;
        bool saved;
        try
        {
            saved = await WriteAsync();
        }
        catch (Exception ex)
        {
            Error = $"Could not save: {ex.Message}"; // set before closing is allowed again
            saved = false;
        }
        finally
        {
            IsSaving = false;
        }

        if (saved)
            CloseRequested?.Invoke(this, true);
    }

    private async Task<bool> WriteAsync()
    {
        if (_nodeId is { } id)
        {
            var renamed = await _workspace.RenameAsync(id, Name);
            Error = renamed.Error;
            Refusal = renamed.Failure ?? Refusal;
            SavedName = renamed.Value;
            return renamed.Succeeded;
        }

        var created = await _workspace.CreateFolderAsync(_parentId, Name);
        Error = created.Error;
        Refusal = created.Failure ?? Refusal;
        CreatedNode = created.Value;
        return created.Succeeded;
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
    private bool _isSaving;
    private string _tagInput = string.Empty;
    private string? _tagError;
    private string? _tagNote;
    private IReadOnlyList<string> _suggestions = [];
    private IReadOnlyList<ProjectTag> _tagBaseline;
    private IReadOnlyList<ProjectTag>? _presentedTags;
    private bool _isTagConflictPending;

    private ProjectEditorViewModel(WorkspaceService workspace, long? parentId, ProjectDetails? existing, string? parentPath)
    {
        _workspace = workspace;
        _parentId = parentId;
        _existing = existing;
        _name = existing?.Name ?? string.Empty;
        // An archived Project never reaches the editor (the area checks first); should one slip through, the editor shows the
        // default status and the store still refuses the save (ProjectArchived) instead of the constructor failing.
        _status = Statuses.FirstOrDefault(s => s.Status == existing?.Status) ?? Statuses.First(s => s.Status == ProjectStatuses.Default);
        _description = existing?.Description ?? string.Empty;
        _owner = existing?.Owner ?? string.Empty;
        _projectManager = existing?.ProjectManager ?? string.Empty;
        LocationText = WorkspaceNameEditorViewModel.LocationOf(parentPath);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsTagConflictPending);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false), () => CanClose);
        AddTagCommand = new RelayCommand(() => AddPendingTag(), () => CanAddTag);
        UseCurrentTagsCommand = new RelayCommand(UseCurrentTags, () => IsTagConflictPending);
        KeepMyTagsCommand = new RelayCommand(KeepMyTags, () => IsTagConflictPending);
        foreach (var tag in existing?.Tags ?? [])
            Tags.Add(Chip(tag.Name));
        _tagBaseline = existing?.Tags ?? []; // R2a: the set read when the editor opened; replaced only by a review button
    }

    // ---- Tag baseline (Phase 21 R2a, proposal ﾂｧ3.3.4) ------------------------------------------------------------------------------

    /// <summary>
    /// The tag set the user last accepted: the set read when the editor opened, then the set presented after a refused Save once the
    /// user chose Use current tags or Keep my tags. It decides whether a Save sends tags (chips 竕 baseline) and is sent with them as
    /// <see cref="ProjectDraft.TagsBaseline"/>. Completing the fresh read after a refusal does not change it.
    /// </summary>
    public IReadOnlyList<ProjectTag> TagBaseline => _tagBaseline;

    /// <summary>True after a Save was refused with <see cref="WorkspaceFailure.TagsChanged"/> until the user picks a review button; Save is disabled.</summary>
    public bool IsTagConflictPending
    {
        get => _isTagConflictPending;
        private set
        {
            if (!SetProperty(ref _isTagConflictPending, value))
                return;
            SaveCommand.RaiseCanExecuteChanged();
            UseCurrentTagsCommand.RaiseCanExecuteChanged();
            KeepMyTagsCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The tag set read after the refusal and shown to the user (null before a refusal, or when it could not be read).</summary>
    public IReadOnlyList<ProjectTag>? PresentedTags { get => _presentedTags; private set { if (SetProperty(ref _presentedTags, value)) OnPropertyChanged(nameof(CurrentTagsText)); } }

    /// <summary>"Current tags: 窶ｦ" under the chips while a conflict is pending.</summary>
    public string? CurrentTagsText => PresentedTags is { } tags
        ? "Current tags: " + (tags.Count == 0 ? "(none)" : string.Join(", ", tags.Select(t => t.Name)))
        : null;

    /// <summary>Chips and baseline := the presented set; the other fields of the draft are kept.</summary>
    public RelayCommand UseCurrentTagsCommand { get; }

    /// <summary>Chips kept; baseline := the presented set, so the next Save sends the chips even when they equal the set first read.</summary>
    public RelayCommand KeepMyTagsCommand { get; }

    private void UseCurrentTags()
    {
        if (PresentedTags is not { } presented)
            return;
        Tags.Clear();
        foreach (var tag in presented)
            Tags.Add(Chip(tag.Name));
        LimitChanged();
        AcceptPresented(presented);
    }

    private void KeepMyTags()
    {
        if (PresentedTags is { } presented)
            AcceptPresented(presented);
    }

    private void AcceptPresented(IReadOnlyList<ProjectTag> presented)
    {
        _tagBaseline = presented;
        OnPropertyChanged(nameof(TagBaseline));
        TagError = null;
        PresentedTags = null;
        IsTagConflictPending = false;
    }

    /// <summary>Chips and baseline compared as ordinal sets of (NameKey, Name) pairs.</summary>
    private bool ChipsEqualBaseline()
    {
        var chips = Tags.Select(c => (ProjectTagName.Key(c.Name), c.Name)).ToHashSet();
        return chips.SetEquals(_tagBaseline.Select(t => (t.NameKey, t.Name)));
    }

    /// <summary>After <see cref="WorkspaceFailure.TagsChanged"/>: keep the draft, read the Project and present its tags (read only).</summary>
    private async Task PresentCurrentTagsAsync()
    {
        IsTagConflictPending = true;
        ProjectDetails? fresh;
        try
        {
            fresh = await _workspace.GetProjectAsync(_existing!.NodeId);
        }
        catch (Exception ex)
        {
            // A failed read says nothing about the Project: no NodeNotFound and no tag set is presented. Draft, chips and baseline are
            // kept; Save stays disabled because there is no set to review. Cancel closes and the area reconciles (Refusal TagsChanged).
            GeneralError = $"Could not read the project's current tags: {ex.Message}";
            return;
        }

        if (fresh is null)
        {
            // The read completed and found no Project.
            Refusal = WorkspaceFailure.NodeNotFound;
            GeneralError = "This project no longer exists. Nothing was saved.";
            return; // Save stays disabled; Cancel closes and the area reconciles
        }

        if (!fresh.Effective.IsLive)
        {
            // Not editable any more (itself or through a Folder): the existing refusal; the area reconciles after the dialog closes.
            var own = fresh.Effective.RootId == fresh.NodeId;
            var trashed = fresh.Effective.State == LifecycleState.Trashed;
            Refusal = !own ? WorkspaceFailure.AncestorNotLive : trashed ? WorkspaceFailure.ProjectTrashed : WorkspaceFailure.ProjectArchived;
            GeneralError = trashed ? WorkspaceService.ProjectInTrash : WorkspaceService.ProjectIsArchived;
            return;
        }

        PresentedTags = fresh.Tags;
    }

    // ---- Tags (Phase 17 proposal R2b §4.1-§4.3, R2a-3) ---------------------------------------------------------------------------

    /// <summary>The project's tags as chips, in the order added (the store keeps its own display order).</summary>
    public ObservableCollection<TagChipViewModel> Tags { get; } = [];

    /// <summary>Text typed in the tag box that is not a chip yet.</summary>
    public string TagInput
    {
        get => _tagInput;
        set
        {
            if (!SetProperty(ref _tagInput, value ?? string.Empty))
                return;
            TagError = null;
            OnPropertyChanged(nameof(FilteredSuggestions));
        }
    }

    /// <summary>Why the text in the tag box was not added (invalid name, 21st tag). Nothing is saved while it is shown by Save.</summary>
    public string? TagError { get => _tagError; private set => SetProperty(ref _tagError, value); }

    /// <summary>A note that is not an error ("Already tagged “Infra”.").</summary>
    public string? TagNote { get => _tagNote; private set => SetProperty(ref _tagNote, value); }

    /// <summary>Enter, comma, or a picked suggestion: adds the tag box text as a chip.</summary>
    public RelayCommand AddTagCommand { get; }

    public bool IsTagLimitReached => Tags.Count >= ProjectTagRules.MaxPerProject;

    /// <summary>
    /// False at 20 chips (R2b §4.3, §5.3): the tag box, Enter/comma and the suggestions are disabled until a chip is removed. Chips
    /// can still be removed and a valid set of 20 saved; Save still refuses pending text that would be a 21st tag (V1c).
    /// </summary>
    public bool CanAddTag => !IsTagLimitReached;

    /// <summary>"At most 20 tags." while the limit is reached (shown beside the disabled box); null otherwise.</summary>
    public string? TagLimitText => IsTagLimitReached ? ProjectTagRules.TooManyMessage : null;

    /// <summary>
    /// Up to 10 suggestions for the tag box text: tags of other projects whose key contains the typed key, not yet a chip. Empty
    /// while the text is not a valid tag (for example a lone surrogate: no key is computed from it) or at 20 chips.
    /// </summary>
    public IReadOnlyList<TagChipViewModel> FilteredSuggestions
    {
        get
        {
            if (!CanAddTag || _suggestions.Count == 0 || TagInput.Trim().Length == 0)
                return [];
            if (!ProjectTagName.TryNormalize(TagInput, out var typed, out _))
                return []; // invalid text (malformed UTF-16, control character, too long) is reported on add or Save, not here
            var key = ProjectTagName.Key(typed);
            var taken = Tags.Select(t => ProjectTagName.Key(t.Name)).ToHashSet(StringComparer.Ordinal);
            return _suggestions
                .Select(s => (Name: s, Key: ProjectTagName.Key(s)))
                .Where(s => s.Key.Contains(key, StringComparison.Ordinal) && !taken.Contains(s.Key))
                .OrderBy(s => s.Key.StartsWith(key, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(s => s.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(s => new TagChipViewModel(s.Name, new RelayCommand(() => PickSuggestion(s.Name))))
                .ToList();
        }
    }

    /// <summary>Loads the suggestions once (the window calls it when it opens). A failure only means no suggestions.</summary>
    public async Task LoadSuggestionsAsync()
    {
        try
        {
            // The project being edited is not a source of its own suggestions (null for a new Project).
            _suggestions = await _workspace.ListTagSuggestionsAsync(_existing?.NodeId);
        }
        catch (Exception)
        {
            _suggestions = [];
        }

        OnPropertyChanged(nameof(FilteredSuggestions));
    }

    /// <summary>Backspace in an empty tag box.</summary>
    public void RemoveLastTag()
    {
        if (Tags.Count > 0)
            RemoveTag(Tags[^1]);
    }

    /// <summary>Adds the tag box text as a chip. Returns false when the text could not be added (invalid, 21st).</summary>
    public bool AddPendingTag() => AddTagText(TagInput) is not (PendingTag.Invalid or PendingTag.Full);

    private void PickSuggestion(string name)
    {
        TagInput = name;
        AddPendingTag();
    }

    private PendingTag AddTagText(string text)
    {
        TagError = null;
        TagNote = null;
        if (text.Trim().Length == 0)
            return PendingTag.Empty; // only white space: no pending tag, no chip, no message; the box text is left as typed (R2a-3)

        if (!ProjectTagName.TryNormalize(text, out var name, out var error))
        {
            TagError = error; // the text stays in the box
            return PendingTag.Invalid;
        }

        var key = ProjectTagName.Key(name);
        if (Tags.FirstOrDefault(t => ProjectTagName.Key(t.Name) == key) is { } existing)
        {
            TagNote = $"Already tagged “{existing.Name}”.";
            TagInput = string.Empty;
            return PendingTag.Duplicate;
        }

        if (IsTagLimitReached)
        {
            TagError = ProjectTagRules.TooManyMessage;
            return PendingTag.Full;
        }

        Tags.Add(Chip(name));
        TagInput = string.Empty;
        LimitChanged();
        return PendingTag.Added;
    }

    private void RemoveTag(TagChipViewModel chip)
    {
        Tags.Remove(chip);
        TagError = null;
        TagNote = null;
        LimitChanged();
    }

    private void LimitChanged()
    {
        OnPropertyChanged(nameof(IsTagLimitReached));
        OnPropertyChanged(nameof(CanAddTag));
        OnPropertyChanged(nameof(TagLimitText));
        OnPropertyChanged(nameof(FilteredSuggestions));
        AddTagCommand.RaiseCanExecuteChanged();
    }

    private TagChipViewModel Chip(string name)
    {
        TagChipViewModel? chip = null;
        chip = new TagChipViewModel(name, new RelayCommand(() => RemoveTag(chip!)));
        return chip;
    }

    private enum PendingTag { Empty, Added, Duplicate, Invalid, Full }

    public static ProjectEditorViewModel ForCreate(WorkspaceService workspace, long? parentId, string? parentPath) =>
        new(workspace, parentId, null, parentPath);

    public static ProjectEditorViewModel ForEdit(WorkspaceService workspace, ProjectDetails existing, string? parentPath) =>
        new(workspace, existing.ParentId, existing, parentPath);

    public event EventHandler<bool>? CloseRequested;

    public bool IsEdit => _existing is not null;

    public string Title => IsEdit ? "Edit Project" : "New Project";

    public string LocationText { get; }

    /// <summary>Selectable statuses. Archived is never offered: only Archive sets it (Phase 12).</summary>
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

    /// <summary>Set after a successful create.</summary>
    public WorkspaceNode? CreatedNode { get; private set; }

    /// <summary>The stored state after a successful edit.</summary>
    public ProjectDetails? SavedDetails { get; private set; }

    /// <summary>The typed reason of the last refused submit; the caller reconciles by a fresh read after the dialog closes.</summary>
    public WorkspaceFailure? Refusal { get; private set; }

    private async Task SaveAsync()
    {
        NameError = StatusError = DescriptionError = OwnerError = ProjectManagerError = GeneralError = null;
        // Text still in the tag box (§4.2.1): valid → a chip, then save; only white space → no pending tag; invalid or a 21st tag
        // → the dialog stays open and nothing is written (no service call).
        if (AddTagText(TagInput) is PendingTag.Invalid or PendingTag.Full)
            return;

        // Phase 21 R2a: an edit sends tags only when the chips differ from the accepted baseline, and then with that baseline.
        var draft = _existing is not null && ChipsEqualBaseline()
            ? new ProjectDraft(Name, Status.Status, Description, Owner, ProjectManager)
            : new ProjectDraft(Name, Status.Status, Description, Owner, ProjectManager, Tags.Select(t => t.Name).ToList(),
                _existing is null ? null : _tagBaseline);

        IsSaving = true;
        string? field, error;
        WorkspaceFailure? failure = null; // this Save's own typed failure; Refusal also keeps an earlier one for the area
        try
        {
            if (_existing is null)
            {
                var created = await _workspace.CreateProjectAsync(_parentId, draft);
                (field, error, failure) = (created.Field, created.Error, created.Failure);
                Refusal = failure ?? Refusal;
                CreatedNode = created.Value;
            }
            else
            {
                var updated = await _workspace.UpdateProjectAsync(_existing.NodeId, draft);
                (field, error, failure) = (updated.Field, updated.Error, updated.Failure);
                Refusal = failure ?? Refusal;
                SavedDetails = updated.Value;
            }
        }
        catch (Exception ex)
        {
            (field, error) = (null, $"Could not save: {ex.Message}");
        }

        if (error is null)
        {
            IsSaving = false;
            CloseRequested?.Invoke(this, true);
            return;
        }

        ShowError(field, error); // set before closing is allowed again
        // Only THIS Save's TagsChanged opens the review: a Refusal left from an earlier Save (for example a later Owner validation
        // error, which has no typed failure) must not re-read and re-present the tags.
        if (failure == WorkspaceFailure.TagsChanged && _existing is not null)
            await PresentCurrentTagsAsync();
        IsSaving = false;
    }

    private void ShowError(string? field, string error)
    {
        switch (field)
        {
            case ProjectDraftValidator.Fields.Name: NameError = error; break;
            case ProjectDraftValidator.Fields.Status: StatusError = error; break;
            case ProjectDraftValidator.Fields.Description: DescriptionError = error; break;
            case ProjectDraftValidator.Fields.Owner: OwnerError = error; break;
            case ProjectDraftValidator.Fields.ProjectManager: ProjectManagerError = error; break;
            case ProjectDraftValidator.Fields.Tags: TagError = error; break;
            default: GeneralError = error; break;
        }
    }
}

public sealed record ProjectStatusOption(ProjectStatus Status, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// A tag chip (Phase 17). In the Project editor <see cref="Command"/> removes the chip (or adds a suggestion); in the Info drawer it
/// searches the Workspace for the tag's display name.
/// </summary>
public sealed record TagChipViewModel(string Name, ICommand Command);

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
    private bool _isMoving;

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
        MoveCommand = new AsyncRelayCommand(MoveAsync, () => SelectedDestination is not null);
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

    /// <summary>True when the node actually moved (false for "already there").</summary>
    public bool Moved { get; private set; }

    public long? MovedTo { get; private set; }

    /// <summary>The typed reason of the last refused Move, and the destination it was refused for.</summary>
    public WorkspaceFailure? Refusal { get; private set; }

    public long? RefusedDestinationId { get; private set; }

    private async Task MoveAsync()
    {
        Error = null;
        var destination = SelectedDestination!;
        IsMoving = true;
        WorkspaceResult<bool> result;
        try
        {
            result = await _workspace.MoveAsync(_nodeId, destination.FolderId);
        }
        catch (Exception ex)
        {
            result = WorkspaceResult<bool>.Failed($"Could not move: {ex.Message}");
        }

        if (!result.Succeeded)
        {
            Error = result.Error; // set before closing is allowed again
            if (result.Failure is { } failure)
                (Refusal, RefusedDestinationId) = (failure, destination.FolderId);
            IsMoving = false;
            return;
        }

        Moved = result.Value;
        MovedTo = destination.FolderId;
        IsMoving = false;
        CloseRequested?.Invoke(this, true);
    }
}

public sealed record WorkspaceMoveDestination(long? FolderId, string Path)
{
    public override string ToString() => Path;
}
