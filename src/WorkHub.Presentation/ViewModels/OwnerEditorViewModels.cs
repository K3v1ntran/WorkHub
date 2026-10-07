using WorkHub.Core.Owner;
using WorkHub.Core.Repositories;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>Create / rename an Owner group. Saving goes through <see cref="OwnerService"/>; errors keep the dialog open.</summary>
public sealed class OwnerGroupEditorViewModel : ObservableObject
{
    private readonly OwnerService _owners;
    private readonly OwnerGroupViewModel? _existing;
    private string _name;
    private string? _error;

    private OwnerGroupEditorViewModel(OwnerService owners, OwnerGroupViewModel? existing)
    {
        _owners = owners;
        _existing = existing;
        _name = existing?.Name ?? string.Empty;
        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ex => Error = $"Could not save: {ex.Message}");
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public static OwnerGroupEditorViewModel ForCreate(OwnerService owners) => new(owners, null);

    public static OwnerGroupEditorViewModel ForRename(OwnerService owners, OwnerGroupViewModel group) => new(owners, group);

    public event EventHandler<bool>? CloseRequested;

    public bool IsRename => _existing is not null;

    public string Title => IsRename ? "Rename Owner Group" : "Add Owner Group";

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    /// <summary>Set after a successful create.</summary>
    public OwnerGroup? CreatedGroup { get; private set; }

    /// <summary>The stored (trimmed) name after a successful save.</summary>
    public string? SavedName { get; private set; }

    private async Task SaveAsync()
    {
        Error = null;
        if (_existing is null)
        {
            var (group, error) = await _owners.CreateGroupAsync(Name);
            if (group is null)
            {
                Error = error;
                return;
            }

            CreatedGroup = group;
            SavedName = group.Name;
        }
        else
        {
            var error = await _owners.RenameGroupAsync(_existing.Id, Name);
            if (error is not null)
            {
                Error = error;
                return;
            }

            SavedName = Name.Trim();
        }

        CloseRequested?.Invoke(this, true);
    }
}

/// <summary>Add / Edit an Owner launcher entry. Password and CredentialId are intentionally not part of this form.</summary>
public sealed class OwnerItemEditorViewModel : ObservableObject
{
    private readonly OwnerService _owners;
    private readonly IDialogService _dialogs;
    private readonly long _groupId;
    private readonly OwnerItemDetails? _existing;
    private string _name;
    private OwnerActionType _actionType;
    private string _target;
    private string _arguments;
    private string _workingDirectory;
    private string _username;
    private string _note;
    private string? _nameError;
    private string? _targetError;
    private string? _argumentsError;
    private string? _workingDirectoryError;
    private string? _usernameError;
    private string? _noteError;
    private string? _generalError;

    private OwnerItemEditorViewModel(OwnerService owners, IDialogService dialogs, long groupId, OwnerItemDetails? existing)
    {
        _owners = owners;
        _dialogs = dialogs;
        _groupId = groupId;
        _existing = existing;
        _name = existing?.Name ?? string.Empty;
        _actionType = existing?.ActionType ?? OwnerActionType.Web;
        _target = existing?.Target ?? string.Empty;
        _arguments = existing?.Arguments ?? string.Empty;
        _workingDirectory = existing?.WorkingDirectory ?? string.Empty;
        _username = existing?.Username ?? string.Empty;
        _note = existing?.Note ?? string.Empty;

        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ex => GeneralError = $"Could not save: {ex.Message}");
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
        BrowseTargetCommand = new RelayCommand(BrowseTarget, () => ActionType != OwnerActionType.Web);
        BrowseWorkingDirectoryCommand = new RelayCommand(BrowseWorkingDirectory);
    }

    public static OwnerItemEditorViewModel ForCreate(OwnerService owners, IDialogService dialogs, long groupId) =>
        new(owners, dialogs, groupId, null);

    public static OwnerItemEditorViewModel ForEdit(OwnerService owners, IDialogService dialogs, OwnerItemDetails existing) =>
        new(owners, dialogs, existing.GroupId, existing);

    public event EventHandler<bool>? CloseRequested;

    public bool IsEdit => _existing is not null;

    public string Title => IsEdit ? "Edit Launcher" : "Add Launcher";

    public IReadOnlyList<OwnerActionType> ActionTypes { get; } = Enum.GetValues<OwnerActionType>();

    public long? SavedId { get; private set; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand BrowseTargetCommand { get; }

    public RelayCommand BrowseWorkingDirectoryCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public OwnerActionType ActionType
    {
        get => _actionType;
        set
        {
            if (!SetProperty(ref _actionType, value))
                return;

            OnPropertyChanged(nameof(IsExecutable));
            OnPropertyChanged(nameof(TargetHint));
            BrowseTargetCommand.RaiseCanExecuteChanged();
            TargetError = null;
        }
    }

    /// <summary>Arguments and Working Directory are shown (and saved) only for Executable.</summary>
    public bool IsExecutable => ActionType == OwnerActionType.Executable;

    public string TargetHint => ActionType switch
    {
        OwnerActionType.Web => "Web address, e.g. https://portal.example.test/",
        OwnerActionType.File => @"Full file path, e.g. C:\Docs\report.xlsx or \\server\share\report.xlsx",
        OwnerActionType.Folder => @"Full folder path, e.g. C:\Projects or \\server\share\Projects",
        _ => @"Full program path (.exe), e.g. C:\Tools\tool.exe",
    };

    public string Target { get => _target; set => SetProperty(ref _target, value); }

    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }

    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }

    public string Username { get => _username; set => SetProperty(ref _username, value); }

    public string Note { get => _note; set => SetProperty(ref _note, value); }

    public string? NameError { get => _nameError; private set => SetProperty(ref _nameError, value); }

    public string? TargetError { get => _targetError; private set => SetProperty(ref _targetError, value); }

    public string? ArgumentsError { get => _argumentsError; private set => SetProperty(ref _argumentsError, value); }

    public string? WorkingDirectoryError { get => _workingDirectoryError; private set => SetProperty(ref _workingDirectoryError, value); }

    public string? UsernameError { get => _usernameError; private set => SetProperty(ref _usernameError, value); }

    public string? NoteError { get => _noteError; private set => SetProperty(ref _noteError, value); }

    public string? GeneralError { get => _generalError; private set => SetProperty(ref _generalError, value); }

    public OwnerItemDraft BuildDraft() => new(
        Name,
        ActionType,
        Target,
        IsExecutable ? Arguments : null,
        IsExecutable ? WorkingDirectory : null,
        Username,
        Note);

    private async Task SaveAsync()
    {
        NameError = TargetError = ArgumentsError = WorkingDirectoryError = UsernameError = NoteError = GeneralError = null;
        var result = IsEdit
            ? await _owners.UpdateItemAsync(_existing!.Id, BuildDraft())
            : await _owners.CreateItemAsync(_groupId, BuildDraft());

        if (result.Succeeded)
        {
            SavedId = result.Id;
            CloseRequested?.Invoke(this, true);
            return;
        }

        foreach (var error in result.Errors)
        {
            switch (error.Field)
            {
                case OwnerItemValidator.Fields.Name: NameError = error.Message; break;
                case OwnerItemValidator.Fields.Target: TargetError = error.Message; break;
                case OwnerItemValidator.Fields.Arguments: ArgumentsError = error.Message; break;
                case OwnerItemValidator.Fields.WorkingDirectory: WorkingDirectoryError = error.Message; break;
                case OwnerItemValidator.Fields.Username: UsernameError = error.Message; break;
                case OwnerItemValidator.Fields.Note: NoteError = error.Message; break;
                default: GeneralError = error.Message; break;
            }
        }
    }

    private void BrowseTarget()
    {
        var current = string.IsNullOrWhiteSpace(Target) ? null : Target.Trim();
        var picked = ActionType switch
        {
            OwnerActionType.File => _dialogs.PickFile("Select file", current),
            OwnerActionType.Folder => _dialogs.PickFolder("Select folder", current),
            OwnerActionType.Executable => _dialogs.PickExecutable("Select program", current),
            _ => null,
        };
        if (picked is not null)
            Target = picked;
    }

    private void BrowseWorkingDirectory()
    {
        var picked = _dialogs.PickFolder("Select working directory", string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim());
        if (picked is not null)
            WorkingDirectory = picked;
    }
}

/// <summary>"Move to…" chooser. Only selects a destination; the move itself is performed by <see cref="OwnerAreaViewModel"/>.</summary>
public sealed class MoveOwnerItemViewModel : ObservableObject
{
    private OwnerGroupViewModel? _selectedGroup;

    public MoveOwnerItemViewModel(string itemName, IEnumerable<OwnerGroupViewModel> destinations)
    {
        ItemName = itemName;
        Destinations = destinations.ToList();
        _selectedGroup = Destinations.FirstOrDefault();
        MoveCommand = new RelayCommand(() => CloseRequested?.Invoke(this, true), () => SelectedGroup is not null);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public string ItemName { get; }

    public string Title => $"Move “{ItemName}” to…";

    public IReadOnlyList<OwnerGroupViewModel> Destinations { get; }

    public OwnerGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
                MoveCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand MoveCommand { get; }

    public RelayCommand CancelCommand { get; }
}

/// <summary>Sidebar row for a user-defined Owner group. Name is mutable so rename updates in place.</summary>
public sealed class OwnerGroupViewModel(long id, string name) : ObservableObject
{
    private string _name = name;

    public long Id { get; } = id;

    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public override string ToString() => Name;
}
