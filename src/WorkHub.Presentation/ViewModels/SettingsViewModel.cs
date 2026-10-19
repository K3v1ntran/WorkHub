using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;
using WorkHub.Core.LegacyImport;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Settings window, Repository category (SVN / Git pages). Works on a draft copy: nothing is persisted until Save,
/// and Cancel simply discards the draft. The preview uses <see cref="RepositoryTargetResolver"/>, the same code
/// that opens repositories.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    public const string DefaultSampleCode = "TEST001";

    private readonly SettingsService _settings;
    private readonly SystemService _systems;
    private readonly IDialogService _dialogs;
    private readonly Func<string?> _detectTortoise;
    private readonly RepositorySettings _draft;
    private RepositoryUsage _usage = RepositoryUsage.None;
    private SettingsPageItem _selectedPage;
    private string _sampleCode = DefaultSampleCode;
    private IReadOnlyList<TargetPreviewRow> _preview = [];
    private IReadOnlyList<SettingsIssue> _issues = [];
    private string? _saveError;
    private bool _isSaving;
    private readonly WorkspaceUiSettings _openedToolbars;
    private bool _showTreeToolbar;
    private bool _showSelectionToolbar;
    private bool _showContentToolbar;

    public SettingsViewModel(
        SettingsService settings,
        SystemService systems,
        LegacyImportService legacyImport,
        IDialogService dialogs,
        Func<string?> detectTortoise,
        SettingsSection initialSection = SettingsSection.Svn,
        BackupsViewModel? backups = null)
    {
        _settings = settings;
        _systems = systems;
        _dialogs = dialogs;
        _detectTortoise = detectTortoise;
        _draft = settings.CreateRepositoryDraft();
        var opened = settings.Current.Ui.Workspace;
        _openedToolbars = new WorkspaceUiSettings
        {
            ShowTreeToolbar = _showTreeToolbar = opened.ShowTreeToolbar,
            ShowSelectionToolbar = _showSelectionToolbar = opened.ShowSelectionToolbar,
            ShowContentToolbar = _showContentToolbar = opened.ShowContentToolbar,
        };
        Backups = backups;
        Pages =
        [
            new SettingsPageItem(SettingsPage.Svn, "SVN", "REPOSITORY"),
            new SettingsPageItem(SettingsPage.Git, "Git", "REPOSITORY"),
            new SettingsPageItem(SettingsPage.Workspace, "Workspace", "WORKSPACE"),
            new SettingsPageItem(SettingsPage.LegacyImport, "Import Legacy XML", "DATA"),
            .. backups is null ? Array.Empty<SettingsPageItem>() : [new SettingsPageItem(SettingsPage.Backups, "Backups", "DATA")],
        ];
        _selectedPage = PageFor(initialSection);
        LegacyImport = new LegacyImportViewModel(legacyImport, dialogs, () => !_isSaving);
        LegacyImport.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LegacyImportViewModel.IsImporting))
                OnCanCloseChanged();
        };

        if (backups is not null)
            backups.BeforeConfirm = ResolveUnsavedBeforeRestoreAsync;

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanClose, ex => SaveError = $"Could not save settings: {ex.Message}");
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false), () => CanClose);
        BrowseTortoiseCommand = new RelayCommand(BrowseTortoise);
        DetectTortoiseCommand = new RelayCommand(DetectTortoise);
        Refresh();
    }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>Settings navigation: Repository (SVN, Git) and Data (legacy import, backups).</summary>
    public IReadOnlyList<SettingsPageItem> Pages { get; }

    public LegacyImportViewModel LegacyImport { get; }

    /// <summary>Read-only Backups page (Phase 9); null when not configured. Loads when the page is first shown.</summary>
    public BackupsViewModel? Backups { get; }

    /// <summary>True once a legacy import committed while this window was open (callers refresh their lists).</summary>
    public bool DataChanged => LegacyImport.ImportedAny;

    /// <summary>
    /// False while a legacy import is in flight. The window must not close then: the caller reads <see cref="DataChanged"/>
    /// right after the dialog returns, and the import's result or error is only shown in this window. Save, Cancel/Escape
    /// and the window's own close (title bar, Alt+F4) all honour it.
    /// </summary>
    public bool CanClose => !LegacyImport.IsImporting;

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand BrowseTortoiseCommand { get; }

    public RelayCommand DetectTortoiseCommand { get; }

    public SettingsPageItem SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is null || !SetProperty(ref _selectedPage, value))
                return;

            OnPropertyChanged(nameof(SelectedSection));
            OnPropertyChanged(nameof(IsSvnSelected));
            OnPropertyChanged(nameof(IsGitSelected));
            OnPropertyChanged(nameof(IsLegacyImportSelected));
            OnPropertyChanged(nameof(IsBackupsSelected));
            OnPropertyChanged(nameof(IsWorkspaceSelected));
            OnPropertyChanged(nameof(IsRepositoryPageSelected));
            if (IsBackupsSelected)
                _ = Backups!.ActivateAsync();
        }
    }

    /// <summary>The Repository settings section shown (SVN when a non-repository page is selected).</summary>
    public SettingsSection SelectedSection
    {
        get => _selectedPage.Page == SettingsPage.Git ? SettingsSection.Git : SettingsSection.Svn;
        set => SelectedPage = PageFor(value);
    }

    public bool IsSvnSelected => _selectedPage.Page == SettingsPage.Svn;

    public bool IsGitSelected => _selectedPage.Page == SettingsPage.Git;

    public bool IsLegacyImportSelected => _selectedPage.Page == SettingsPage.LegacyImport;

    public bool IsBackupsSelected => _selectedPage.Page == SettingsPage.Backups;

    public bool IsWorkspaceSelected => _selectedPage.Page == SettingsPage.Workspace;

    /// <summary>Workspace page: "Show the tree toolbar" (draft; saved with Save).</summary>
    public bool ShowTreeToolbar
    {
        get => _showTreeToolbar;
        set
        {
            if (SetProperty(ref _showTreeToolbar, value))
                SaveError = null;
        }
    }

    /// <summary>Workspace page: "Show the selection toolbar" (draft; saved with Save).</summary>
    public bool ShowSelectionToolbar
    {
        get => _showSelectionToolbar;
        set
        {
            if (SetProperty(ref _showSelectionToolbar, value))
                SaveError = null;
        }
    }

    /// <summary>Workspace page: "Show the content toolbar" (draft; saved with Save).</summary>
    public bool ShowContentToolbar
    {
        get => _showContentToolbar;
        set
        {
            if (SetProperty(ref _showContentToolbar, value))
                SaveError = null;
        }
    }

    /// <summary>
    /// The toolbar values to save: only those the user changed in this dialog (Phase 13A §6.1 S3), each on its own. An
    /// unchanged toggle never overwrites a value saved meanwhile from a toolbar's menu in the main window.
    /// </summary>
    internal WorkspaceToolbarChanges ChangedToolbars => new()
    {
        Tree = _showTreeToolbar != _openedToolbars.ShowTreeToolbar ? _showTreeToolbar : null,
        Selection = _showSelectionToolbar != _openedToolbars.ShowSelectionToolbar ? _showSelectionToolbar : null,
        Content = _showContentToolbar != _openedToolbars.ShowContentToolbar ? _showContentToolbar : null,
    };

    public bool IsRepositoryPageSelected => IsSvnSelected || IsGitSelected;

    private SettingsPageItem PageFor(SettingsSection section) =>
        Pages.First(p => p.Page == (section == SettingsSection.Git ? SettingsPage.Git : SettingsPage.Svn));

    public string SvnHost { get => _draft.Svn.Host; set => Edit(() => _draft.Svn.Host = value); }

    public string SvnSystemPrefix { get => _draft.Svn.SystemPrefix; set => Edit(() => _draft.Svn.SystemPrefix = value); }

    public string SvnOraclePrefix { get => _draft.Svn.OraclePrefix; set => Edit(() => _draft.Svn.OraclePrefix = value); }

    public string ReleasePrefix { get => _draft.Svn.ReleasePrefix; set => Edit(() => _draft.Svn.ReleasePrefix = value); }

    public string TortoiseProcPath { get => _draft.Svn.TortoiseProcPath; set => Edit(() => _draft.Svn.TortoiseProcPath = value); }

    public string GitHost { get => _draft.Git.Host; set => Edit(() => _draft.Git.Host = value); }

    public string GitSystemPrefix { get => _draft.Git.SystemPrefix; set => Edit(() => _draft.Git.SystemPrefix = value); }

    public string GitOraclePrefix { get => _draft.Git.OraclePrefix; set => Edit(() => _draft.Git.OraclePrefix = value); }

    public string SampleCode
    {
        get => _sampleCode;
        set
        {
            if (SetProperty(ref _sampleCode, value))
                RefreshPreview();
        }
    }

    public IReadOnlyList<TargetPreviewRow> Preview { get => _preview; private set => SetProperty(ref _preview, value); }

    /// <summary>Errors block Save; warnings describe values needed by repository types currently in use.</summary>
    public IReadOnlyList<SettingsIssue> Issues
    {
        get => _issues;
        private set
        {
            if (SetProperty(ref _issues, value))
                OnPropertyChanged(nameof(HasIssues));
        }
    }

    public bool HasIssues => Issues.Count > 0;

    public string? SaveError { get => _saveError; private set => SetProperty(ref _saveError, value); }

    /// <summary>The draft as it would be saved (for tests and diagnostics).</summary>
    public RepositorySettings Draft => _draft.Clone();

    /// <summary>Loads which repository types are in use, so warnings only mention settings that matter.</summary>
    public async Task LoadUsageAsync()
    {
        _usage = await _systems.GetUsageAsync();
        Refresh();
    }

    private void Edit(Action apply, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        apply();
        OnPropertyChanged(property);
        SaveError = null;
        Refresh();
    }

    private void Refresh()
    {
        // Live check is pure string validation (no disk access on the UI thread); the executable's existence is checked on Save.
        Issues = RepositorySettingsValidator.Validate(_draft, _usage, fileExists: _ => true);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var settings = _draft.Trimmed();
        if (!SystemCode.TryNormalize(SampleCode, out var code, out var error))
        {
            Preview = RepositoryCatalog.All.Select(i => new TargetPreviewRow(i.DisplayName, $"Sample code: {error}", true)).ToList();
            return;
        }

        Preview = RepositoryCatalog.All.Select(info =>
        {
            var target = RepositoryTargetResolver.Resolve(info.Type, code, customTarget: null, settings);
            return new TargetPreviewRow(info.DisplayName, target.Target ?? target.Problem!.Message, !target.IsResolved);
        }).ToList();
    }

    private void SetSaving(bool saving)
    {
        _isSaving = saving;
        LegacyImport.RaiseCommands();
    }

    private void OnCanCloseChanged()
    {
        OnPropertyChanged(nameof(CanClose));
        SaveCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private async Task SaveAsync()
    {
        SaveError = null;
        SettingsSaveResult result;
        // While Save runs, Import cannot start: a successful Save closes the window, which must not happen mid-import.
        SetSaving(true);
        try
        {
            result = await _settings.SaveRepositoryAsync(_draft, _usage, ChangedToolbars);
        }
        finally
        {
            SetSaving(false);
        }

        Issues = result.Issues;
        if (result.Saved)
        {
            CloseRequested?.Invoke(this, true);
            return;
        }

        SaveError = result.Error ?? "Fix the errors below before saving.";
        var firstError = result.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error);
        if (firstError is not null)
            SelectedSection = firstError.Section;
    }

    /// <summary>True when the Repository draft differs from the saved settings (values compared after trimming).</summary>
    public bool HasUnsavedRepositoryChanges
    {
        get
        {
            var draft = _draft.Trimmed();
            var saved = _settings.Current.Repository.Trimmed();
            return draft.Svn.Host != saved.Svn.Host || draft.Svn.SystemPrefix != saved.Svn.SystemPrefix
                || draft.Svn.OraclePrefix != saved.Svn.OraclePrefix || draft.Svn.ReleasePrefix != saved.Svn.ReleasePrefix
                || draft.Svn.TortoiseProcPath != saved.Svn.TortoiseProcPath || draft.Git.Host != saved.Git.Host
                || draft.Git.SystemPrefix != saved.Git.SystemPrefix || draft.Git.OraclePrefix != saved.Git.OraclePrefix;
        }
    }

    /// <summary>
    /// Phase 10 Q5b: before a confirmed restore restarts WorkHub, unsaved Repository settings are saved, discarded or the
    /// restore is cancelled. Returns false to cancel the restore.
    /// </summary>
    public async Task<bool> ResolveUnsavedBeforeRestoreAsync()
    {
        if (!HasUnsavedRepositoryChanges)
            return true;

        switch (_dialogs.AskUnsavedChanges("Unsaved settings", "WorkHub restarts for the restore. Save your Repository settings first?"))
        {
            case UnsavedChangesChoice.Discard:
                return true;
            case UnsavedChangesChoice.Save:
                var result = await _settings.SaveRepositoryAsync(_draft, _usage, ChangedToolbars);
                if (result.Saved)
                    return true;
                Issues = result.Issues;
                SaveError = result.Error ?? "Fix the errors below before saving.";
                return false;
            default:
                return false;
        }
    }

    private void BrowseTortoise()
    {
        var path = _dialogs.PickExecutable("Select TortoiseProc.exe", TortoiseProcPath);
        if (path is not null)
            TortoiseProcPath = path;
    }

    private void DetectTortoise()
    {
        var path = _detectTortoise();
        if (path is not null)
            TortoiseProcPath = path;
        else
            SaveError = "TortoiseProc.exe was not found in the standard install locations. Use Browse to select it.";
    }
}

public sealed record TargetPreviewRow(string DisplayName, string Text, bool IsProblem);

public enum SettingsPage
{
    Svn,
    Git,
    Workspace,
    LegacyImport,
    Backups,
}

/// <summary>One entry of the Settings navigation, grouped under <see cref="Group"/> (REPOSITORY, DATA).</summary>
public sealed record SettingsPageItem(SettingsPage Page, string Title, string Group)
{
    public override string ToString() => Title;
}
