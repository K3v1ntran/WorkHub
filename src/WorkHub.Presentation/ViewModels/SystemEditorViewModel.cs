using WorkHub.Presentation.Mvvm;
using WorkHub.Core.Repositories;

namespace WorkHub.Presentation.ViewModels;

/// <summary>Add / Edit System dialog. One System, with checkboxes for the repository groups it belongs to.</summary>
public sealed class SystemEditorViewModel : ObservableObject
{
    private readonly SystemService _systems;
    private readonly SystemDetails? _existing;
    private string _systemCode;
    private string _systemName;
    private string _description;
    private bool _isActive;
    private string? _codeError;
    private string? _nameError;
    private string? _descriptionError;
    private string? _membershipsError;
    private string? _generalError;

    private SystemEditorViewModel(SystemService systems, SystemDetails? existing)
    {
        _systems = systems;
        _existing = existing;
        _systemCode = existing?.SystemCode ?? string.Empty;
        _systemName = existing?.SystemName ?? string.Empty;
        _description = existing?.Description ?? string.Empty;
        _isActive = existing?.IsActive ?? true;

        // No membership is pre-selected for a new System: the user chooses explicitly.
        Memberships = RepositoryCatalog.All.Select(info =>
        {
            var current = existing?.Memberships.FirstOrDefault(m => m.Type == info.Type);
            return new MembershipEditorViewModel(info, current);
        }).ToList();

        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ex => GeneralError = $"Could not save: {ex.Message}");
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public static SystemEditorViewModel ForCreate(SystemService systems) => new(systems, null);

    public static SystemEditorViewModel ForEdit(SystemService systems, SystemDetails existing) => new(systems, existing);

    public event EventHandler<bool>? CloseRequested;

    public bool IsEdit => _existing is not null;

    public string Title => IsEdit ? "Edit System" : "Add System";

    public long? SavedId { get; private set; }

    public IReadOnlyList<MembershipEditorViewModel> Memberships { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string SystemCode
    {
        get => _systemCode;
        set
        {
            if (SetProperty(ref _systemCode, value))
                OnPropertyChanged(nameof(IsCodeChanged));
        }
    }

    /// <summary>Editing the code is allowed; generated targets follow automatically because they derive from it.</summary>
    public bool IsCodeChanged => IsEdit && !string.Equals(_existing!.SystemCode, SystemCode.Trim(), StringComparison.Ordinal);

    public string SystemName
    {
        get => _systemName;
        set => SetProperty(ref _systemName, value);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public string? CodeError { get => _codeError; private set => SetProperty(ref _codeError, value); }

    public string? NameError { get => _nameError; private set => SetProperty(ref _nameError, value); }

    public string? DescriptionError { get => _descriptionError; private set => SetProperty(ref _descriptionError, value); }

    public string? MembershipsError { get => _membershipsError; private set => SetProperty(ref _membershipsError, value); }

    public string? GeneralError { get => _generalError; private set => SetProperty(ref _generalError, value); }

    public SystemDraft BuildDraft() => new(
        SystemCode,
        SystemName,
        Description,
        IsActive,
        Memberships.Select(m => m.ToDraft()).OfType<MembershipDraft>().ToList());

    private async Task SaveAsync()
    {
        ClearErrors();
        var draft = BuildDraft();
        var result = IsEdit
            ? await _systems.UpdateAsync(_existing!.Id, draft)
            : await _systems.CreateAsync(draft);

        if (result.Succeeded)
        {
            SavedId = result.Id;
            CloseRequested?.Invoke(this, true);
            return;
        }

        foreach (var error in result.Errors)
            ApplyError(error);
    }

    private void ApplyError(ValidationError error)
    {
        switch (error.Field)
        {
            case SystemDraftValidator.Fields.SystemCode: CodeError = error.Message; break;
            case SystemDraftValidator.Fields.SystemName: NameError = error.Message; break;
            case SystemDraftValidator.Fields.Description: DescriptionError = error.Message; break;
            case SystemDraftValidator.Fields.Memberships: MembershipsError = error.Message; break;
            default:
                var membership = Memberships.FirstOrDefault(m => SystemDraftValidator.Fields.CustomTarget(m.Type) == error.Field);
                if (membership is not null)
                    membership.Error = error.Message;
                else
                    GeneralError = error.Message;
                break;
        }
    }

    private void ClearErrors()
    {
        CodeError = NameError = DescriptionError = MembershipsError = GeneralError = null;
        foreach (var membership in Memberships)
            membership.Error = null;
    }
}

public sealed class MembershipEditorViewModel : ObservableObject
{
    private readonly bool _existedBefore;
    private bool _isSelected;
    private string _customTarget;
    private string? _error;

    public MembershipEditorViewModel(RepositoryTypeInfo info, MembershipDetails? current)
    {
        Info = info;
        _existedBefore = current is not null;
        _isSelected = current?.IsEnabled ?? false;
        _customTarget = current?.CustomTarget ?? string.Empty;
    }

    public RepositoryTypeInfo Info { get; }

    public RepositoryType Type => Info.Type;

    public string DisplayName => Info.DisplayName;

    public string Placeholder => $"Custom target (optional) — default: {(Info.VersionControl == VersionControlKind.Svn ? "SVN" : "GitBucket")} host + prefix + code";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string CustomTarget
    {
        get => _customTarget;
        set
        {
            if (SetProperty(ref _customTarget, value))
                OnPropertyChanged(nameof(HasCustomTarget));
        }
    }

    public bool HasCustomTarget => !string.IsNullOrWhiteSpace(CustomTarget);

    public string? Error
    {
        get => _error;
        set => SetProperty(ref _error, value);
    }

    /// <summary>
    /// Ticked → enabled row. Unticked → kept as a disabled row only if it existed before or carries a custom target
    /// (so the custom target is not lost); otherwise no row.
    /// </summary>
    public MembershipDraft? ToDraft() =>
        IsSelected || _existedBefore || HasCustomTarget
            ? new MembershipDraft(Type, IsSelected, HasCustomTarget ? CustomTarget : null)
            : null;
}
