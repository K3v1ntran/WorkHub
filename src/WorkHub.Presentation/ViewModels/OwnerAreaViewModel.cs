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

        AddItemCommand = new AsyncRelayCommand(AddItemAsync, HasGroup, onError);
        OpenItemCommand = new AsyncRelayCommand(OpenSelectedAsync, HasItem, onError);
        EditItemCommand = new AsyncRelayCommand(() => EditItemAsync(SelectedItem?.Id), HasItem, onError);
        DeleteItemCommand = new AsyncRelayCommand(DeleteItemAsync, HasItem, onError);
        MoveItemToGroupCommand = new AsyncRelayCommand(MoveItemToGroupAsync, () => HasItem() && Groups.Count > 1, onError);
        MoveItemUpCommand = new AsyncRelayCommand(() => MoveItemAsync(-1), HasItem, onError);
        MoveItemDownCommand = new AsyncRelayCommand(() => MoveItemAsync(+1), HasItem, onError);
        CopyTargetCommand = new RelayCommand(() => _dialogs.CopyToClipboard(SelectedItem!.Target), HasItem);
        EditFromBannerCommand = new AsyncRelayCommand(() => EditItemAsync(_bannerItemId), () => _bannerItemId is not null, onError);
        DismissBannerCommand = new RelayCommand(ClearBanner);
    }

    public ObservableCollection<OwnerGroupViewModel> Groups { get; } = [];

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

    /// <summary>Selecting a group loads only that group's items. Null when a Repository group is active.</summary>
    public OwnerGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value))
                return;

            SelectedItem = null;
            ClearBanner();
            RaiseGroupCommands();
            OnPropertyChanged(nameof(EmptyStateText));
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

            OpenItemCommand.RaiseCanExecuteChanged();
            EditItemCommand.RaiseCanExecuteChanged();
            DeleteItemCommand.RaiseCanExecuteChanged();
            MoveItemToGroupCommand.RaiseCanExecuteChanged();
            MoveItemUpCommand.RaiseCanExecuteChanged();
            MoveItemDownCommand.RaiseCanExecuteChanged();
            CopyTargetCommand.RaiseCanExecuteChanged();
        }
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

    public string EmptyStateText => SelectedGroup is null
        ? string.Empty
        : $"No launchers in {SelectedGroup.Name} yet. Use “Add Item” to add a web page, file, folder or program.";

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
        CurrentLoad = LoadAsync(SelectedGroup!.Id, selectId, cts);
    }

    private async Task LoadAsync(long groupId, long? selectId, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            IsLoading = true;
            var items = await _owners.ListItemsAsync(groupId, token);
            token.ThrowIfCancellationRequested();
            Items = items;
            SelectedItem = items.FirstOrDefault(i => i.Id == selectId) ?? items.FirstOrDefault();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
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

    private Task ReloadItemsAsync(long? selectId)
    {
        if (SelectedGroup is null)
            return Task.CompletedTask;
        StartLoad(selectId);
        return CurrentLoad;
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
        if (!_dialogs.Confirm("Delete Launcher",
                $"Remove \"{item.Name}\" from WorkHub?\n\nOnly the WorkHub entry is removed. The {Describe(item.ActionType)} itself is NOT deleted.",
                "Delete"))
            return;

        await _owners.DeleteItemAsync(item.Id);
        await ReloadItemsAsync(null);
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

    private bool HasGroup() => SelectedGroup is not null;

    private bool HasItem() => SelectedItem is not null;

    private void RaiseGroupCommands()
    {
        RenameGroupCommand.RaiseCanExecuteChanged();
        DeleteGroupCommand.RaiseCanExecuteChanged();
        MoveGroupUpCommand.RaiseCanExecuteChanged();
        MoveGroupDownCommand.RaiseCanExecuteChanged();
        AddItemCommand.RaiseCanExecuteChanged();
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
