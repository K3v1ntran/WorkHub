using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Repository tab. Built-in groups come from <see cref="RepositoryCatalog"/>, never from the database.
/// Only the selected group's list is loaded; edits re-query that list alone (other groups are queried when selected),
/// so there is no global reload and no cached copy of other groups to go stale.
/// </summary>
public sealed class RepositoryAreaViewModel : ObservableObject
{
    public const int DefaultSearchDelayMs = 200;

    private readonly SystemService _systems;
    private readonly RepositoryOpenService _opener;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IAppLog _log;
    private readonly Func<string?> _detectTortoise;
    private readonly int _searchDelayMs;

    private RepositoryCategoryViewModel _selectedCategory;
    private IReadOnlyList<SystemListItem> _items = [];
    private SystemListItem? _selectedItem;
    private bool _hasMore;
    private bool _isLoading;
    private bool _isActivated;
    private bool _isSearchVisible;
    private string _searchText = string.Empty;
    private bool _showInactive;
    private string? _bannerMessage;
    private SettingsSection? _bannerFixSection;
    private CancellationTokenSource? _loadCts;

    public RepositoryAreaViewModel(
        SystemService systems,
        RepositoryOpenService opener,
        SettingsService settings,
        IDialogService dialogs,
        IAppLog log,
        Func<string?> detectTortoise,
        int searchDelayMs = DefaultSearchDelayMs)
    {
        _systems = systems;
        _opener = opener;
        _settings = settings;
        _dialogs = dialogs;
        _log = log;
        _detectTortoise = detectTortoise;
        _searchDelayMs = searchDelayMs;

        Categories = RepositoryCatalog.All.Select(info => new RepositoryCategoryViewModel(info)).ToList();
        _selectedCategory = Categories[0];

        Action<Exception> onError = ex => ReportError("The operation failed.", ex);
        OpenSelectedCommand = new AsyncRelayCommand(OpenSelectedAsync, HasSelection, onError);
        AddSystemCommand = new AsyncRelayCommand(AddSystemAsync, () => _isActivated, onError);
        EditSystemCommand = new AsyncRelayCommand(EditSelectedAsync, HasSelection, onError);
        ToggleActiveCommand = new AsyncRelayCommand(ToggleActiveAsync, HasSelection, onError);
        DeletePermanentlyCommand = new AsyncRelayCommand(DeleteSelectedAsync, HasSelection, onError);
        CopyTargetCommand = new RelayCommand(CopyTarget, HasSelection);
        OpenSettingsCommand = new AsyncRelayCommand(() => OpenSettingsAsync(null), () => _isActivated, onError);
        FixSettingsCommand = new AsyncRelayCommand(() => OpenSettingsAsync(_bannerFixSection), () => _bannerFixSection is not null, onError);
        DismissBannerCommand = new RelayCommand(ClearBanner);
        ShowSearchCommand = new RelayCommand(() => IsSearchVisible = true);
        CloseSearchCommand = new RelayCommand(CloseSearch);
    }

    public IReadOnlyList<RepositoryCategoryViewModel> Categories { get; }

    public AsyncRelayCommand OpenSelectedCommand { get; }

    public AsyncRelayCommand AddSystemCommand { get; }

    public AsyncRelayCommand EditSystemCommand { get; }

    public AsyncRelayCommand ToggleActiveCommand { get; }

    public AsyncRelayCommand DeletePermanentlyCommand { get; }

    public RelayCommand CopyTargetCommand { get; }

    public AsyncRelayCommand OpenSettingsCommand { get; }

    public AsyncRelayCommand FixSettingsCommand { get; }

    public RelayCommand DismissBannerCommand { get; }

    public RelayCommand ShowSearchCommand { get; }

    public RelayCommand CloseSearchCommand { get; }

    /// <summary>The most recent list load; lets tests await debounced searches.</summary>
    internal Task CurrentLoad { get; private set; } = Task.CompletedTask;

    public RepositoryCategoryViewModel SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !SetProperty(ref _selectedCategory, value))
                return;

            OnPropertyChanged(nameof(EmptyStateText));
            SelectedItem = null;
            StartLoad(delayMs: 0);
        }
    }

    public IReadOnlyList<SystemListItem> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
                OnListStateChanged();
        }
    }

    public SystemListItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;

            OnPropertyChanged(nameof(SelectedIsActive));
            OpenSelectedCommand.RaiseCanExecuteChanged();
            EditSystemCommand.RaiseCanExecuteChanged();
            ToggleActiveCommand.RaiseCanExecuteChanged();
            DeletePermanentlyCommand.RaiseCanExecuteChanged();
            CopyTargetCommand.RaiseCanExecuteChanged();
        }
    }

    public bool SelectedIsActive => SelectedItem?.IsActive ?? true;

    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                OnListStateChanged();
        }
    }

    public bool IsSearchVisible
    {
        get => _isSearchVisible;
        set => SetProperty(ref _isSearchVisible, value);
    }

    /// <summary>Contains-match on SystemCode / SystemName within the selected group, debounced.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
                return;

            OnPropertyChanged(nameof(EmptyStateText));
            StartLoad(_searchDelayMs);
        }
    }

    public bool ShowInactive
    {
        get => _showInactive;
        set
        {
            if (SetProperty(ref _showInactive, value))
                StartLoad(delayMs: 0);
        }
    }

    public bool IsEmpty => !IsLoading && _isActivated && Items.Count == 0;

    public string EmptyStateText => !string.IsNullOrWhiteSpace(SearchText)
        ? $"No systems in {SelectedCategory.DisplayName} match \"{SearchText.Trim()}\"."
        : $"No systems in {SelectedCategory.DisplayName} yet. Use “Add System” to create one.";

    public string CountText => HasMore
        ? $"Showing first {Items.Count}"
        : Items.Count == 1 ? "1 system" : $"{Items.Count} systems";

    public string? BannerMessage { get => _bannerMessage; private set => SetProperty(ref _bannerMessage, value); }

    public bool BannerCanFixSettings => _bannerFixSection is not null;

    /// <summary>Called once startup initialization has succeeded.</summary>
    public Task ActivateAsync()
    {
        _isActivated = true;
        AddSystemCommand.RaiseCanExecuteChanged();
        OpenSettingsCommand.RaiseCanExecuteChanged();
        StartLoad(delayMs: 0);
        return CurrentLoad;
    }

    public Task ReloadAsync(long? selectId = null)
    {
        StartLoad(delayMs: 0, selectId);
        return CurrentLoad;
    }

    private void StartLoad(int delayMs, long? selectId = null)
    {
        if (!_isActivated)
            return;

        // Supersede any in-flight or pending (debounced) load; its results will be discarded.
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        CurrentLoad = LoadAsync(delayMs, selectId ?? SelectedItem?.Id, cts);
    }

    private async Task LoadAsync(int delayMs, long? selectId, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, token);

            IsLoading = true;
            var query = new SystemQuery(SelectedCategory.Type, SearchText, ShowInactive);
            var result = await _systems.QueryAsync(query, token);
            token.ThrowIfCancellationRequested();

            HasMore = result.HasMore;
            Items = result.Items;
            SelectedItem = result.Items.FirstOrDefault(i => i.Id == selectId) ?? result.Items.FirstOrDefault();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer load.
        }
        catch (Exception ex)
        {
            ReportError("Systems could not be loaded.", ex);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                _loadCts = null;
            }
        }
    }

    private async Task OpenSelectedAsync()
    {
        if (SelectedItem is not { } item)
            return;

        var result = await _opener.OpenAsync(SelectedCategory.Type, item);
        if (result.Succeeded)
            ClearBanner();
        else
            ShowBanner(result.Message ?? "The repository could not be opened.", result.FixIn);
    }

    private async Task AddSystemAsync()
    {
        var editor = SystemEditorViewModel.ForCreate(_systems);
        if (_dialogs.ShowSystemEditor(editor))
            await ReloadAsync(editor.SavedId);
    }

    private async Task EditSelectedAsync()
    {
        if (SelectedItem is not { } item)
            return;

        var details = await _systems.GetAsync(item.Id);
        if (details is null)
        {
            ShowBanner($"{item.SystemCode} no longer exists.", null);
            await ReloadAsync();
            return;
        }

        var editor = SystemEditorViewModel.ForEdit(_systems, details);
        if (_dialogs.ShowSystemEditor(editor))
            await ReloadAsync(item.Id);
    }

    private async Task ToggleActiveAsync()
    {
        if (SelectedItem is not { } item)
            return;

        if (item.IsActive && !_dialogs.Confirm("Deactivate System",
                $"Deactivate {item.SystemCode} ({item.SystemName})?\n\nIt stays stored and is hidden from repository lists. " +
                "Turn on “Show inactive systems” to see or reactivate it.", "Deactivate"))
            return;

        await _systems.SetActiveAsync(item.Id, !item.IsActive);
        await ReloadAsync(item.Id);
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedItem is not { } item)
            return;

        if (!_dialogs.Confirm("Delete System permanently",
                $"Permanently delete {item.SystemCode} ({item.SystemName}) from WorkHub?\n\n" +
                "This removes the System and its repository memberships from every group. " +
                "External SVN/Git repositories are not affected.\n\nTo hide it instead, use Deactivate.", "Delete"))
            return;

        await _systems.DeletePermanentlyAsync(item.Id);
        await ReloadAsync();
    }

    private void CopyTarget()
    {
        if (SelectedItem is not { } item)
            return;

        var target = _opener.Resolve(SelectedCategory.Type, item);
        if (target.Problem is { } problem)
            ShowBanner(problem.Message, problem.FixIn);
        else
            _dialogs.CopyToClipboard(target.Target!);
    }

    private async Task OpenSettingsAsync(SettingsSection? section)
    {
        var viewModel = new SettingsViewModel(_settings, _systems, _dialogs, _detectTortoise, section ?? SettingsSection.Svn);
        await viewModel.LoadUsageAsync();
        if (_dialogs.ShowSettings(viewModel))
            ClearBanner();
    }

    private void CloseSearch()
    {
        if (SearchText.Length > 0)
            SearchText = string.Empty;
        else
            IsSearchVisible = false;
    }

    private void ShowBanner(string? message, SettingsSection? fixIn)
    {
        _bannerFixSection = fixIn;
        BannerMessage = message;
        OnPropertyChanged(nameof(BannerCanFixSettings));
        FixSettingsCommand.RaiseCanExecuteChanged();
    }

    private void ClearBanner() => ShowBanner(null, null);

    private void ReportError(string message, Exception ex)
    {
        _log.Error(message, ex);
        ShowBanner(message, null);
    }

    private bool HasSelection() => SelectedItem is not null;

    private void OnListStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
    }
}

public sealed class RepositoryCategoryViewModel(RepositoryTypeInfo info)
{
    public RepositoryType Type => info.Type;

    public string DisplayName => info.DisplayName;

    public string VersionControl => info.VersionControl.ToString().ToUpperInvariant();
}
