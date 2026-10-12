using System.Collections.ObjectModel;
using WorkHub.Core.Workspace;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>One trashed Project as listed: its name, the folder it will be restored to and when it was trashed.</summary>
public sealed class WorkspaceTrashEntryViewModel(WorkspaceTrashEntry entry)
{
    public WorkspaceTrashEntry Entry { get; } = entry;

    public long NodeId => Entry.NodeId;

    public string Name => Entry.Name;

    /// <summary>"from Clients / Tokyo" (the original parent's CURRENT path) or the top level.</summary>
    public string FromText => Entry.ParentPath is { } path ? $"from {path}" : "from Workspace (top level)";

    public string TrashedAtText => $"{Entry.TrashedAt.LocalDateTime:yyyy-MM-dd HH:mm}";

    public override string ToString() => Name;
}

/// <summary>
/// The Workspace Trash (trashed Projects), shown in the right pane instead of the selection's details (Phase 8 proposal
/// revision 2 §4, §8.2, §8.3.4; D6).
/// <list type="bullet">
/// <item>Every list or count read records the area's lifecycle-completion count when it STARTS; its result is applied only if
/// no lifecycle result was applied meanwhile, otherwise it is discarded and one fresh read starts (after the completion).</item>
/// <item>Restore shares the area's single lifecycle slot. Rows are keyed by node Id; a Restore that succeeded or found the
/// Project no longer in the Trash removes that row. Restore never changes the tree selection.</item>
/// </list>
/// </summary>
public sealed class WorkspaceTrashViewModel : ObservableObject
{
    private readonly WorkspaceAreaViewModel _area;
    private WorkspaceTrashEntryViewModel? _selectedEntry;
    private bool _isOpen;
    private bool _isLoading;
    private string? _loadError;
    private string? _notice;
    private int _version;
    private int? _count;
    private bool _countRunning;
    private bool _countPending;

    internal WorkspaceTrashViewModel(WorkspaceAreaViewModel area)
    {
        _area = area;
        RestoreCommand = new AsyncRelayCommand(RestoreSelectedAsync, CanRestore, ex => Notice = RestoreFailed);
        ReloadCommand = new AsyncRelayCommand(() => Reload(), () => IsOpen && !IsLoading);
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public const string RestoreFailed = "Could not restore. The project is still in the Trash.";

    public ObservableCollection<WorkspaceTrashEntryViewModel> Entries { get; } = [];

    public WorkspaceTrashEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
                RestoreCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value))
                return;
            OnPropertyChanged(nameof(IsEmpty));
            RestoreCommand.RaiseCanExecuteChanged();
            ReloadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmpty => IsOpen && !IsLoading && LoadError is null && Entries.Count == 0;

    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value))
                OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>The last Restore's outcome ("Restored “X” to …" or why it was refused).</summary>
    public string? Notice { get => _notice; internal set => SetProperty(ref _notice, value); }

    /// <summary>Trashed Projects in the database as last read; null until the first count arrives.</summary>
    public int? Count
    {
        get => _count;
        private set
        {
            if (SetProperty(ref _count, value))
                OnPropertyChanged(nameof(ButtonText));
        }
    }

    /// <summary>"Trashed projects (n)" — distinct from the content pane's Phase 7 "Trash (n)".</summary>
    public string ButtonText => Count is { } n ? $"Trashed projects ({n})" : "Trashed projects";

    public AsyncRelayCommand RestoreCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>The most recent list load (including a re-read after a discarded result); lets tests await it.</summary>
    internal Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent count refresh; lets tests await it.</summary>
    internal Task CurrentCount { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent Restore; lets tests await it.</summary>
    internal Task CurrentRestore { get; private set; } = Task.CompletedTask;

    internal Task Open()
    {
        IsOpen = true;
        Notice = null;
        return Reload();
    }

    internal void Close()
    {
        _version++;
        IsOpen = false;
        IsLoading = false;
        SelectedEntry = null;
        Entries.Clear();
        LoadError = null;
        Notice = null;
    }

    internal void RaiseCommands() => RestoreCommand.RaiseCanExecuteChanged();

    /// <summary>Called by the area after it applied a lifecycle result: the list (if open) and the count are read again.</summary>
    internal void OnLifecycleCompleted()
    {
        if (IsOpen)
            _ = Reload();
        RefreshCount();
    }

    internal void RemoveEntry(long nodeId)
    {
        if (Entries.FirstOrDefault(e => e.NodeId == nodeId) is not { } entry)
            return;
        if (ReferenceEquals(SelectedEntry, entry))
            SelectedEntry = null;
        Entries.Remove(entry);
    }

    /// <summary>Latest-only count: one read runs; requests meanwhile collapse into one more read after it.</summary>
    internal Task RefreshCount()
    {
        if (_countRunning)
        {
            _countPending = true;
            return CurrentCount;
        }

        _countRunning = true;
        return CurrentCount = CountAsync();
    }

    private async Task CountAsync()
    {
        try
        {
            while (true)
            {
                _countPending = false;
                var completions = _area.LifecycleCompletions;
                int count;
                try
                {
                    count = await _area.Workspace.CountTrashAsync();
                }
                catch (Exception ex)
                {
                    _area.Log.Error("The Workspace Trash could not be counted.", ex);
                    return; // the button keeps its last count
                }

                if (completions == _area.LifecycleCompletions)
                    Count = count; // otherwise the read may predate a commit: discard it and read again
                else
                    _countPending = true;
                if (!_countPending)
                    return;
            }
        }
        finally
        {
            _countRunning = false;
        }
    }

    private Task Reload()
    {
        var version = ++_version;
        SelectedEntry = null;
        Entries.Clear();
        LoadError = null;
        IsLoading = true;
        return CurrentLoad = LoadAsync(version);
    }

    private async Task LoadAsync(int version)
    {
        var completions = _area.LifecycleCompletions;
        IReadOnlyList<WorkspaceTrashEntry> entries;
        try
        {
            entries = await _area.Workspace.ListTrashAsync();
        }
        catch (Exception ex)
        {
            if (version != _version)
                return;
            _area.Log.Error("The Workspace Trash could not be loaded.", ex);
            LoadError = "The Trash could not be loaded. Nothing was changed.";
            IsLoading = false;
            return;
        }

        if (version != _version)
            return; // closed or reloaded meanwhile
        if (completions != _area.LifecycleCompletions)
        {
            await Reload(); // a lifecycle result was applied after this read started: it may predate that commit
            return;
        }

        foreach (var entry in entries)
            Entries.Add(new WorkspaceTrashEntryViewModel(entry));
        Count = entries.Count;
        IsLoading = false;
    }

    private bool CanRestore() => IsOpen && !IsLoading && SelectedEntry is not null && !_area.IsLifecycleBusy;

    private Task RestoreSelectedAsync() => CurrentRestore = RestoreAsync();

    private async Task RestoreAsync()
    {
        if (!CanRestore() || SelectedEntry is not { } entry)
            return; // direct invocations are ignored while a lifecycle write is pending (one store call only)

        Notice = null;
        var notice = await _area.RestoreFromTrashAsync(entry.NodeId, entry.Name);
        if (IsOpen)
            Notice = notice;
    }
}
