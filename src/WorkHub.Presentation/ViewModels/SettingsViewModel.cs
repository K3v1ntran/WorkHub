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

    public SettingsViewModel(
        SettingsService settings,
        SystemService systems,
        LegacyImportService legacyImport,
        IDialogService dialogs,
        Func<string?> detectTortoise,
        SettingsSection initialSection = SettingsSection.Svn)
    {
        _settings = settings;
        _systems = systems;
        _dialogs = dialogs;
        _detectTortoise = detectTortoise;
        _draft = settings.CreateRepositoryDraft();
        Pages =
        [
            new SettingsPageItem(SettingsPage.Svn, "SVN", "REPOSITORY"),
            new SettingsPageItem(SettingsPage.Git, "Git", "REPOSITORY"),
            new SettingsPageItem(SettingsPage.LegacyImport, "Import Legacy XML", "DATA"),
        ];
        _selectedPage = PageFor(initialSection);
        LegacyImport = new LegacyImportViewModel(legacyImport, dialogs);

        SaveCommand = new AsyncRelayCommand(SaveAsync, onError: ex => SaveError = $"Could not save settings: {ex.Message}");
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
        BrowseTortoiseCommand = new RelayCommand(BrowseTortoise);
        DetectTortoiseCommand = new RelayCommand(DetectTortoise);
        Refresh();
    }

    public event EventHandler<bool>? CloseRequested;

    /// <summary>Settings navigation: Repository (SVN, Git) and Data (legacy import).</summary>
    public IReadOnlyList<SettingsPageItem> Pages { get; }

    public LegacyImportViewModel LegacyImport { get; }

    /// <summary>True once a legacy import committed while this window was open (callers refresh their lists).</summary>
    public bool DataChanged => LegacyImport.ImportedAny;

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
            OnPropertyChanged(nameof(IsRepositoryPageSelected));
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

    public bool IsRepositoryPageSelected => !IsLegacyImportSelected;

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

    private async Task SaveAsync()
    {
        SaveError = null;
        var result = await _settings.SaveRepositoryAsync(_draft, _usage);
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
    LegacyImport,
}

/// <summary>One entry of the Settings navigation, grouped under <see cref="Group"/> (REPOSITORY, DATA).</summary>
public sealed record SettingsPageItem(SettingsPage Page, string Title, string Group)
{
    public override string ToString() => Title;
}
