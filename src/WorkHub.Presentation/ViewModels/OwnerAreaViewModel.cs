using System.Collections.ObjectModel;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Owner;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Owner Manager (user-defined launcher groups) inside the Repository tab.
/// Groups are loaded once at activation and then edited in place; only the selected group's items are loaded,
/// and after an item change only that group is re-queried. Nothing here reloads Repository data.
/// </summary>
public sealed class OwnerAreaViewModel : ObservableObject
{
    private readonly OwnerService _owners;
    private readonly IDialogService _dialogs;
    private readonly IAppLog _log;

    private OwnerGroupViewModel? _selectedGroup;
    private IReadOnlyList<OwnerItemListItem> _items = [];
    private OwnerItemListItem? _selectedItem;
    private bool _isLoading;
    private string? _bannerMessage;
    private string? _bannerTarget;
    private long? _bannerItemId;
    private CancellationTokenSource? _loadCts;
    private OwnerGroupViewModel? _groupBeforeFavorites;

    public OwnerAreaViewModel(OwnerService owners, IDialogService dialogs, IAppLog log)
    {
        _owners = owners;
        _dialogs = dialogs;
        _log = log;

        Action<Exception> onError = ex => ReportError("The Owner operation failed. Nothing else was affected.", ex);
        AddGroupCommand = new AsyncRelayCommand(AddGroupAsync, onError: onError);
        RenameGroupCommand = new AsyncRelayCommand(RenameGroupAsync, HasGroup, onError);
        DeleteGroupCommand = new AsyncRelayCommand(DeleteGroupAsync, HasGroup, onError);
        MoveGroupUpCommand = new AsyncRelayCommand(() => MoveGroupAsync(-1), HasGroup, onError);
        MoveGroupDownCommand = new AsyncRelayCommand(() => MoveGroupAsync(+1), HasGroup, onError);

        // Phase 16B: Favorites has no ordering of its own, so Add item, Move up/down and Move to group are disabled there.
        AddItemCommand = new AsyncRelayCommand(AddItemAsync, HasGroup, onError);
        OpenItemCommand = new AsyncRelayCommand(OpenSelectedAsync, HasItem, onError);
        EditItemCommand = new AsyncRelayCommand(() => EditItemAsync(SelectedItem?.Id), HasItem, onError);
        DeleteItemCommand = new AsyncRelayCommand(DeleteItemAsync, HasItem, onError);
        MoveItemToGroupCommand = new AsyncRelayCommand(MoveItemToGroupAsync, () => HasItem() && !IsFavoritesOpen && Groups.Count > 1, onError);
        MoveItemUpCommand = new AsyncRelayCommand(() => MoveItemAsync(-1), () => HasItem() && !IsFavoritesOpen, onError);
        MoveItemDownCommand = new AsyncRelayCommand(() => MoveItemAsync(+1), () => HasItem() && !IsFavoritesOpen, onError);
        CopyTargetCommand = new RelayCommand(() => _dialogs.CopyToClipboard(SelectedItem!.Target), HasItem);
        EditFromBannerCommand = new AsyncRelayCommand(() => EditItemAsync(_bannerItemId), () => _bannerItemId is not null, onError);
        DismissBannerCommand = new RelayCommand(ClearBanner);
        AddToFavoritesCommand = new AsyncRelayCommand(() => SetFavoriteAsync(true), () => SelectedItem is { IsFavorite: false }, onError);
        RemoveFromFavoritesCommand = new AsyncRelayCommand(() => SetFavoriteAsync(false), () => SelectedItem is { IsFavorite: true }, onError);
        ToggleFavoriteCommand = new AsyncRelayCommand(() => SetFavoriteAsync(!SelectedItem!.IsFavorite), HasItem, onError);
    }

    public ObservableCollection<OwnerGroupViewModel> Groups { get; } = [];

    /// <summary>Phase 16B: the fixed Favorites entry (shown above the groups only while at least one favorite exists; D4).</summary>
    public OwnerGroupViewModel FavoritesEntry { get; } = new(0, "Favorites") { IsFavorites = true };

    /// <summary>Phase 16B: the sidebar's Favorites list: <see cref="FavoritesEntry"/> while favorites exist, otherwise empty.</summary>
    public ObservableCollection<OwnerGroupViewModel> FavoritesEntries { get; } = [];

    public bool HasFavorites => FavoritesEntries.Count > 0;

    /// <summary>Phase 16B: the Favorites entry is the open Owner entry.</summary>
    public bool IsFavoritesOpen => _selectedGroup?.IsFavorites == true;

    /// <summary>
    /// The group list's selection: the open group, or null while Favorites or a Repository group is open. Setting null is
    /// ignored (the list clears its selection when another entry becomes active).
    /// </summary>
    public OwnerGroupViewModel? SelectedOwnerGroup
    {
        get => IsFavoritesOpen ? null : _selectedGroup;
        set
        {
            if (value is not null)
                SelectedGroup = value;
        }
    }

    /// <summary>The Favorites list's selection (see <see cref="SelectedOwnerGroup"/>).</summary>
    public OwnerGroupViewModel? SelectedFavoritesEntry
    {
        get => IsFavoritesOpen ? FavoritesEntry : null;
        set
        {
            if (value is not null)
                SelectedGroup = value;
        }
    }

    /// <summary>Toolbar: "Add to Favorites" or "Remove from Favorites" for the selected launcher.</summary>
    public string FavoriteActionText => SelectedItem?.IsFavorite == true ? "Remove from Favorites" : "Add to Favorites";

    public AsyncRelayCommand AddToFavoritesCommand { get; }

    public AsyncRelayCommand RemoveFromFavoritesCommand { get; }

    public AsyncRelayCommand ToggleFavoriteCommand { get; }

    public AsyncRelayCommand AddGroupCommand { get; }

    public AsyncRelayCommand RenameGroupCommand { get; }

    public AsyncRelayCommand DeleteGroupCommand { get; }

    public AsyncRelayCommand MoveGroupUpCommand { get; }

    public AsyncRelayCommand MoveGroupDownCommand { get; }

    public AsyncRelayCommand AddItemCommand { get; }

    public AsyncRelayCommand OpenItemCommand { get; }

    public AsyncRelayCommand EditItemCommand { get; }

    public AsyncRelayCommand DeleteItemCommand { get; }

    public AsyncRelayCommand MoveItemToGroupCommand { get; }

    public AsyncRelayCommand MoveItemUpCommand { get; }

    public AsyncRelayCommand MoveItemDownCommand { get; }

    public RelayCommand CopyTargetCommand { get; }

    public AsyncRelayCommand EditFromBannerCommand { get; }

    public RelayCommand DismissBannerCommand { get; }

    /// <summary>The most recent item load; lets tests await it.</summary>
    internal Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Selecting a group loads only that group's items; selecting <see cref="FavoritesEntry"/> loads the favorites. Null when
    /// a Repository group is active.
    /// </summary>
    public OwnerGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            var previous = _selectedGroup;
            if (!SetProperty(ref _selectedGroup, value))
                return;

            if (value?.IsFavorites == true && previous is { IsFavorites: false })
                _groupBeforeFavorites = previous; // §5.3: where to return when the last favorite goes
            else if (value?.IsFavorites == true && previous is null)
                _groupBeforeFavorites = null;
            SelectedItem = null;
            ClearBanner();
            RaiseGroupCommands();
            OnPropertyChanged(nameof(EmptyStateText));
            OnPropertyChanged(nameof(IsFavoritesOpen));
            OnPropertyChanged(nameof(SelectedOwnerGroup));
            OnPropertyChanged(nameof(SelectedFavoritesEntry));
            if (value is null)
            {
                _loadCts?.Cancel();
                Items = []; // release the previous group's rows
            }
            else
            {
                StartLoad(null);
            }
        }
    }

    public IReadOnlyList<OwnerItemListItem> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
                OnListStateChanged();
        }
    }

    public OwnerItemListItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;

            RaiseItemCommands();
        }
    }

    private void RaiseItemCommands()
    {
        OpenItemCommand.RaiseCanExecuteChanged();
        EditItemCommand.RaiseCanExecuteChanged();
        DeleteItemCommand.RaiseCanExecuteChanged();
        MoveItemToGroupCommand.RaiseCanExecuteChanged();
        MoveItemUpCommand.RaiseCanExecuteChanged();
        MoveItemDownCommand.RaiseCanExecuteChanged();
        CopyTargetCommand.RaiseCanExecuteChanged();
        AddToFavoritesCommand.RaiseCanExecuteChanged();
        RemoveFromFavoritesCommand.RaiseCanExecuteChanged();
        ToggleFavoriteCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(FavoriteActionText));
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                OnListStateChanged();
        }
    }

    public bool IsEmpty => SelectedGroup is not null && !IsLoading && Items.Count == 0;

    public string EmptyStateText => SelectedGroup switch
    {
        null => string.Empty,
        { IsFavorites: true } => "No favorites.",
        _ => $"No launchers in {SelectedGroup.Name} yet. Use “Add Item” to add a web page, file, folder or program.",
    };

    public string CountText => Items.Count == 1 ? "1 item" : $"{Items.Count} items";

    public string? BannerMessage { get => _bannerMessage; private set => SetProperty(ref _bannerMessage, value); }

    public string? BannerTarget { get => _bannerTarget; private set => SetProperty(ref _bannerTarget, value); }

    public bool BannerCanEdit => _bannerItemId is not null;

    /// <summary>Loads the group list (not items). Called once after startup initialization.</summary>
    public async Task ActivateAsync()
    {
        try
        {
            var groups = await _owners.ListGroupsAsync();
            Groups.Clear();
            foreach (var group in groups)
                Groups.Add(new OwnerGroupViewModel(group.Id, group.Name));
            await RefreshFavoritesPresenceAsync();
        }
        catch (Exception ex)
        {
            // Owner problems must not affect Repository: report and continue.
            ReportError("Owner groups could not be loaded.", ex);
        }
    }

    private void StartLoad(long? selectId)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        CurrentLoad = LoadAsync(SelectedGroup!, selectId, cts);
    }

    private async Task LoadAsync(OwnerGroupViewModel entry, long? selectId, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            IsLoading = true;
            var items = entry.IsFavorites
                ? await _owners.ListFavoritesAsync(token)
                : await _owners.ListItemsAsync(entry.Id, token);
            token.ThrowIfCancellationRequested();
            if (entry.IsFavorites && items.Count == 0)
            {
                await LeaveEmptyFavoritesAsync(entry, token); // §5.3: Favorites never stays open (or reappears) empty
                return;
            }

            Items = items;
            SelectedItem = items.FirstOrDefault(i => i.Id == selectId) ?? items.FirstOrDefault();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!IsCurrentLoad(cts))
        {
            // A load the user already left (another group, Favorites again or a Repository group) failed late, e.g. the
            // empty-Favorites group query. It is logged only: the banner belongs to the screen now shown.
            _log.Warn($"A superseded launcher load failed and was ignored ({ex.GetType().Name}: {ex.Message}).");
        }
        catch (Exception ex)
        {
            ReportError("Launchers could not be loaded.", ex);
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

    /// <summary>The load of <paramref name="cts"/> is still the one the screen shows: not cancelled and not replaced.</summary>
    private bool IsCurrentLoad(CancellationTokenSource cts) => !cts.IsCancellationRequested && ReferenceEquals(_loadCts, cts);

    private Task ReloadItemsAsync(long? selectId)
    {
        if (SelectedGroup is null)
            return Task.CompletedTask;
        StartLoad(selectId);
        return CurrentLoad;
    }

    // ---- Favorites (Phase 16B) ------------------------------------------------------------------------------------

    /// <summary>Shows or hides the Favorites entry from the database (after changes made in this window).</summary>
    private async Task RefreshFavoritesPresenceAsync()
    {
        var favorites = await _owners.ListFavoritesAsync();
        SetHasFavorites(favorites.Count > 0);
    }

    private void SetHasFavorites(bool value)
    {
        if (value == HasFavorites)
            return;
        if (value)
            FavoritesEntries.Add(FavoritesEntry);
        else
            FavoritesEntries.Clear();
        OnPropertyChanged(nameof(HasFavorites));
    }

    /// <summary>
    /// §5.3 (D14): Favorites is open and a reload found no favorite (after Remove from Favorites, a confirmed Delete, an
    /// "already deleted" refusal, or another window's change). In this order: the entry disappears; the launcher selection
    /// is cleared; the group open before Favorites is selected if it still exists, otherwise the first existing group;
    /// with no group left the Owner area becomes inactive (the Repository group shown before comes back). A message shown
    /// by the refusal is kept.
    /// <para>
    /// <paramref name="token"/> belongs to the load that found Favorites empty. Selecting another Owner group, Favorites
    /// again or a Repository group cancels it (<see cref="SelectedGroup"/>), so a group list that arrives after the user moved
    /// on is dropped: it changes neither <see cref="Groups"/>, the selection, the launcher list nor the banner of the screen
    /// now shown.
    /// </para>
    /// </summary>
    private async Task LeaveEmptyFavoritesAsync(OwnerGroupViewModel favorites, CancellationToken token)
    {
        var keptMessage = BannerMessage;
        SetHasFavorites(false);
        SelectedItem = null;
        Items = [];
        var groups = await _owners.ListGroupsAsync(token); // groups another window deleted are gone from the database
        if (token.IsCancellationRequested || !ReferenceEquals(_selectedGroup, favorites))
            return; // stale: the user left Favorites while the groups were read
        SyncGroups(groups);
        var target = Groups.FirstOrDefault(g => g.Id == _groupBeforeFavorites?.Id) ?? Groups.FirstOrDefault();
        _groupBeforeFavorites = null;
        SelectedGroup = target; // null → RepositoryNavigationViewModel falls back to the Repository group
        if (keptMessage is not null)
            ShowBanner(keptMessage, null, null);
        if (target is not null)
            await CurrentLoad;
    }

    /// <summary>Makes <see cref="Groups"/> match the database order, keeping the view models of groups that still exist.</summary>
    private void SyncGroups(IReadOnlyList<OwnerGroup> groups)
    {
        var existing = Groups.ToDictionary(g => g.Id);
        var updated = groups.Select(g =>
        {
            if (existing.TryGetValue(g.Id, out var vm))
            {
                vm.Name = g.Name;
                return vm;
            }

            return new OwnerGroupViewModel(g.Id, g.Name);
        }).ToList();
        if (updated.SequenceEqual(Groups))
            return;
        Groups.Clear();
        foreach (var group in updated)
            Groups.Add(group);
        MoveItemToGroupCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Add to / Remove from Favorites: no confirmation; only the mark changes (D5, §5.2).</summary>
    private async Task SetFavoriteAsync(bool favorite)
    {
        var item = SelectedItem!;
        var result = await _owners.SetFavoriteAsync(item.Id, favorite);
        if (result == OwnerFavoriteResult.NotFound)
            ShowBanner("This launcher no longer exists.", null, null);
        else
            ClearBanner();

        if (IsFavoritesOpen)
        {
            await ReloadItemsAsync(favorite ? item.Id : null); // an empty result leaves Favorites (§5.3)
            return;
        }

        await ReloadItemsAsync(item.Id);
        await RefreshFavoritesPresenceAsync(); // a group stays open; only the entry appears or disappears (F13)
    }

    // ---- Groups -------------------------------------------------------------------------------------------------

    private Task AddGroupAsync()
    {
        var editor = OwnerGroupEditorViewModel.ForCreate(_owners);
        if (_dialogs.ShowOwnerGroupEditor(editor) && editor.CreatedGroup is { } created)
        {
            var group = new OwnerGroupViewModel(created.Id, created.Name);
            Groups.Add(group);
            SelectedGroup = group;
            MoveItemToGroupCommand.RaiseCanExecuteChanged();
        }

        return Task.CompletedTask;
    }

    private Task RenameGroupAsync()
    {
        var group = SelectedGroup!;
        var editor = OwnerGroupEditorViewModel.ForRename(_owners, group);
        if (_dialogs.ShowOwnerGroupEditor(editor) && editor.SavedName is { } name)
        {
            group.Name = name; // same Id, same items, same selection
            OnPropertyChanged(nameof(EmptyStateText));
        }

        return Task.CompletedTask;
    }

    private async Task DeleteGroupAsync()
    {
        var group = SelectedGroup!;
        var count = await _owners.CountItemsAsync(group.Id);
        var message = count == 0
            ? $"Delete \"{group.Name}\"?\n\nThe group is empty."
            : $"Delete \"{group.Name}\"?\n\nThis will remove {count} launcher {(count == 1 ? "entry" : "entries")} from WorkHub.\n" +
              "External files, folders, websites and programs will NOT be deleted.";
        if (!_dialogs.Confirm("Delete Owner Group", message, "Delete"))
            return;

        if (!await _owners.DeleteGroupAsync(group.Id))
            ShowBanner("This group no longer exists.", null, null);

        var index = Groups.IndexOf(group);
        Groups.Remove(group);
        SelectedGroup = Groups.Count == 0 ? null : Groups[Math.Min(index, Groups.Count - 1)];
        MoveItemToGroupCommand.RaiseCanExecuteChanged();
        await RefreshFavoritesPresenceAsync(); // its favorites went with it (cascade)
    }

    private async Task MoveGroupAsync(int offset)
    {
        var group = SelectedGroup!;
        if (!await _owners.MoveGroupAsync(group.Id, offset))
            return;

        var from = Groups.IndexOf(group);
        Groups.Move(from, Math.Clamp(from + offset, 0, Groups.Count - 1));
    }

    // ---- Items --------------------------------------------------------------------------------------------------

    private async Task AddItemAsync()
    {
        var editor = OwnerItemEditorViewModel.ForCreate(_owners, _dialogs, SelectedGroup!.Id);
        if (_dialogs.ShowOwnerItemEditor(editor))
            await ReloadItemsAsync(editor.SavedId);
    }

    private async Task EditItemAsync(long? itemId)
    {
        if (itemId is not { } id)
            return;

        var details = await _owners.GetItemAsync(id);
        if (details is null)
        {
            ShowBanner("This launcher entry no longer exists.", null, null);
            await ReloadItemsAsync(null);
            return;
        }

        var editor = OwnerItemEditorViewModel.ForEdit(_owners, _dialogs, details);
        if (_dialogs.ShowOwnerItemEditor(editor))
        {
            ClearBanner();
            await ReloadItemsAsync(id);
        }
    }

    private async Task DeleteItemAsync()
    {
        var item = SelectedItem!;
        var fromFavorites = IsFavoritesOpen;
        if (!_dialogs.Confirm("Delete Launcher", DeleteMessage(item, fromFavorites), "Delete"))
            return; // Cancel (the default button) writes nothing

        await _owners.DeleteItemAsync(item.Id);
        await ReloadItemsAsync(null); // in Favorites, an empty result leaves Favorites (§5.3)
        if (!fromFavorites && item.IsFavorite)
            await RefreshFavoritesPresenceAsync();
    }

    /// <summary>
    /// §5.2 (D6): from Favorites, Delete names the launcher's group and says it removes the launcher from WorkHub, not only
    /// from Favorites. From a group: today's text, plus one line when the launcher is a favorite.
    /// </summary>
    internal static string DeleteMessage(OwnerItemListItem item, bool fromFavorites)
    {
        var notDeleted = $"Only the WorkHub entry is removed. The {Describe(item.ActionType)} itself is NOT deleted.";
        if (fromFavorites)
        {
            var group = item.GroupName ?? string.Empty;
            return $"Delete \"{item.Name}\" from the group \"{group}\"?\n\n" +
                   $"This removes the launcher from WorkHub, not only from Favorites. To keep it in \"{group}\", choose Cancel and use Remove from Favorites.\n\n" +
                   notDeleted;
        }

        return $"Remove \"{item.Name}\" from WorkHub?\n\n{notDeleted}" + (item.IsFavorite ? "\nIt is also removed from Favorites." : string.Empty);
    }

    private async Task MoveItemToGroupAsync()
    {
        var item = SelectedItem!;
        var chooser = new MoveOwnerItemViewModel(item.Name, Groups.Where(g => g.Id != item.GroupId));
        if (!_dialogs.ShowMoveOwnerItem(chooser) || chooser.SelectedGroup is not { } destination)
            return;

        if (!await _owners.MoveItemToGroupAsync(item.Id, destination.Id))
            ShowBanner("The launcher could not be moved (it or the destination group no longer exists).", null, null);

        await ReloadItemsAsync(null);
    }

    private async Task MoveItemAsync(int offset)
    {
        var item = SelectedItem!;
        if (await _owners.MoveItemAsync(item.Id, offset))
            await ReloadItemsAsync(item.Id);
    }

    private async Task OpenSelectedAsync()
    {
        var item = SelectedItem!;
        var result = await _owners.OpenAsync(item.Id);
        if (result.Succeeded)
            ClearBanner();
        else
            ShowBanner(result.Message ?? "The target could not be opened.", item.Target,
                result.Problem == OwnerLaunchProblem.ItemNotFound ? null : item.Id);
    }

    // ---- Banner / helpers ---------------------------------------------------------------------------------------

    private void ShowBanner(string? message, string? target, long? itemId)
    {
        _bannerItemId = itemId;
        BannerMessage = message;
        BannerTarget = target;
        OnPropertyChanged(nameof(BannerCanEdit));
        EditFromBannerCommand.RaiseCanExecuteChanged();
    }

    private void ClearBanner() => ShowBanner(null, null, null);

    private void ReportError(string message, Exception ex)
    {
        _log.Error(message, ex);
        ShowBanner(message, null, null);
    }

    /// <summary>A real group is open (not Favorites): group commands and Add item.</summary>
    private bool HasGroup() => SelectedGroup is { IsFavorites: false };

    private bool HasItem() => SelectedItem is not null;

    private void RaiseGroupCommands()
    {
        RenameGroupCommand.RaiseCanExecuteChanged();
        DeleteGroupCommand.RaiseCanExecuteChanged();
        MoveGroupUpCommand.RaiseCanExecuteChanged();
        MoveGroupDownCommand.RaiseCanExecuteChanged();
        AddItemCommand.RaiseCanExecuteChanged();
        MoveItemToGroupCommand.RaiseCanExecuteChanged();
        MoveItemUpCommand.RaiseCanExecuteChanged();
        MoveItemDownCommand.RaiseCanExecuteChanged();
    }

    private void OnListStateChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
    }

    private static string Describe(OwnerActionType type) => type switch
    {
        OwnerActionType.Web => "website",
        OwnerActionType.File => "file",
        OwnerActionType.Folder => "folder",
        _ => "program",
    };
}
