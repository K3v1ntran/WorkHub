using System.Collections.ObjectModel;
using WorkHub.Core.Workspace;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>One archived Project as listed: its name, where it is and when it was last updated.</summary>
public sealed class ArchivedProjectEntryViewModel(ArchivedProjectEntry entry)
{
    public ArchivedProjectEntry Entry { get; } = entry;

    public long NodeId => Entry.NodeId;

    public string Name => Entry.Name;

    /// <summary>"in Clients / Tokyo" (the parent's CURRENT path) or the top level.</summary>
    public string LocationText => Entry.ParentPath is { } path ? $"in {path}" : "in Workspace (top level)";

    public string UpdatedText => $"updated {Entry.UpdatedAt.LocalDateTime:yyyy-MM-dd}";

    public override string ToString() => Name;
}

/// <summary>
/// The Archived projects panel, shown in the right pane instead of the selection's details (Phase 12 proposal revision 3
/// §3.2, §3.3, §8.4; A4). Built like the Workspace Trash panel:
/// <list type="bullet">
/// <item>Every list or count read records the area's lifecycle-completion count when it STARTS; its result is applied only if
/// no lifecycle result was applied meanwhile, otherwise it is discarded and one fresh read starts.</item>
/// <item>Unarchive shares the area's single lifecycle slot, asks for the status first, and never changes the tree selection.
/// Rows are keyed by node Id.</item>
/// <item>Search navigation (<see cref="WorkspaceAreaViewModel"/>) selects a row only from a read that started after its resolve,
/// by node Id, and only while the user has not selected a row, pressed Back or closed the panel meanwhile.</item>
/// </list>
/// </summary>
public sealed class ArchivedProjectsViewModel : ObservableObject
{
    private readonly WorkspaceAreaViewModel _area;
    private ArchivedProjectEntryViewModel? _selectedEntry;
    private bool _isOpen;
    private bool _isLoading;
    private string? _loadError;
    private string? _notice;
    private int _version;
    private int _appliedVersion;
    private int _userSelection;
    private int? _count;
    private bool _countRunning;
    private bool _countPending;

    internal ArchivedProjectsViewModel(WorkspaceAreaViewModel area)
    {
        _area = area;
        UnarchiveCommand = new AsyncRelayCommand(UnarchiveSelectedAsync, CanUnarchive, ex => Notice = UnarchiveFailed);
        ReloadCommand = new AsyncRelayCommand(() => Reload(), () => IsOpen && !IsLoading);
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public const string UnarchiveFailed = "Could not unarchive. The project is still archived.";

    public ObservableCollection<ArchivedProjectEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Bound to the list: a selection made here is the user's own. It supersedes a search navigation still resolving or
    /// loading (the reveal is invalidated, as a manual tree selection does), so the user's later choice stands.
    /// </summary>
    public ArchivedProjectEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (ReferenceEquals(_selectedEntry, value))
                return;
            _userSelection++;
            _area.InvalidateReveal();
            SetSelected(value);
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
            UnarchiveCommand.RaiseCanExecuteChanged();
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

    /// <summary>The last Unarchive's outcome ("Unarchived “X” to …" or why it was refused).</summary>
    public string? Notice { get => _notice; internal set => SetProperty(ref _notice, value); }

    /// <summary>Archived Projects (not in the Trash) as last read; null until the first count arrives.</summary>
    public int? Count
    {
        get => _count;
        private set
        {
            if (SetProperty(ref _count, value))
                OnPropertyChanged(nameof(ButtonText));
        }
    }

    public string ButtonText => Count is { } n ? $"Archived projects ({n})" : "Archived projects";

    public AsyncRelayCommand UnarchiveCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>The most recent list load (including a re-read after a discarded result); lets tests await it.</summary>
    internal Task CurrentLoad { get; private set; } = Task.CompletedTask;

    internal Task CurrentCount { get; private set; } = Task.CompletedTask;

    internal Task CurrentUnarchive { get; private set; } = Task.CompletedTask;

    /// <summary>Changes on every user selection, Back/close and Unarchive start (search navigation never overrides them).</summary>
    internal int UserSelectionVersion => _userSelection;

    /// <summary>The read whose rows are shown (0 = none yet).</summary>
    internal int AppliedVersion => _appliedVersion;

    internal Task Open()
    {
        IsOpen = true;
        Notice = null;
        return Reload();
    }

    internal void Close()
    {
        _version++;
        _userSelection++;
        IsOpen = false;
        IsLoading = false;
        SetSelected(null);
        Entries.Clear();
        _appliedVersion = 0;
        LoadError = null;
        Notice = null;
    }

    internal void RaiseCommands() => UnarchiveCommand.RaiseCanExecuteChanged();

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
        if (ReferenceEquals(_selectedEntry, entry))
            SetSelected(null);
        Entries.Remove(entry);
    }

    /// <summary>Opens the panel if needed and starts a NEW read now (never the list already shown or in flight); returns its version.</summary>
    internal int ReloadForNavigation()
    {
        IsOpen = true;
        Notice = null;
        _ = Reload();
        return _version;
    }

    /// <summary>Selects the row of exactly this node Id, if listed. Never by name or path.</summary>
    internal bool SelectByNodeId(long nodeId)
    {
        if (Entries.FirstOrDefault(e => e.NodeId == nodeId) is not { } entry)
            return false;
        SetSelected(entry);
        return true;
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

    private void SetSelected(ArchivedProjectEntryViewModel? value)
    {
        if (SetProperty(ref _selectedEntry, value, nameof(SelectedEntry)))
            UnarchiveCommand.RaiseCanExecuteChanged();
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
                    count = await _area.Workspace.CountArchivedAsync();
                }
                catch (Exception ex)
                {
                    _area.Log.Error("Archived projects could not be counted.", ex);
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
        SetSelected(null);
        Entries.Clear();
        _appliedVersion = 0;
        LoadError = null;
        IsLoading = true;
        return CurrentLoad = LoadAsync(version);
    }

    private async Task LoadAsync(int version)
    {
        var completions = _area.LifecycleCompletions;
        IReadOnlyList<ArchivedProjectEntry> entries;
        try
        {
            entries = await _area.Workspace.ListArchivedAsync();
        }
        catch (Exception ex)
        {
            if (version != _version)
                return;
            _area.Log.Error("Archived projects could not be loaded.", ex);
            LoadError = "Archived projects could not be loaded. Nothing was changed.";
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
            Entries.Add(new ArchivedProjectEntryViewModel(entry));
        Count = entries.Count;
        _appliedVersion = version;
        IsLoading = false;
    }

    private bool CanUnarchive() => IsOpen && !IsLoading && SelectedEntry is not null && !_area.IsLifecycleBusy;

    private Task UnarchiveSelectedAsync() => CurrentUnarchive = UnarchiveAsync();

    private async Task UnarchiveAsync()
    {
        if (!CanUnarchive() || SelectedEntry is not { } entry)
            return; // direct invocations are ignored while a lifecycle write is pending (one store call only)

        _userSelection++;
        Notice = null;
        var notice = await _area.UnarchiveFromPanelAsync(entry.Entry);
        if (IsOpen && notice is not null)
            Notice = notice;
    }
}

/// <summary>
/// The Unarchive confirmation (A8): where the Project goes back to and the status it gets; Completed is preselected.
/// Nothing is written by the dialog itself: the Workspace area unarchives after it closes with Unarchive.
/// </summary>
public sealed class UnarchiveProjectViewModel : ObservableObject
{
    private ProjectStatusOption _status;

    public UnarchiveProjectViewModel(string name, string? parentPath)
    {
        Name = name;
        LocationText = parentPath is { } path ? $"Back to {path}" : "Back to Workspace (top level)";
        _status = Statuses.First(s => s.Status == ProjectStatus.Completed);
        ConfirmCommand = new RelayCommand(() => CloseRequested?.Invoke(this, true));
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public string Title => "Unarchive Project";

    public string Name { get; }

    public string Message => $"Unarchive project “{Name}”? It returns to the tree with the status you choose. Nothing on disk is touched.";

    public string LocationText { get; }

    /// <summary>The five editable statuses (never Archived).</summary>
    public IReadOnlyList<ProjectStatusOption> Statuses { get; } =
        ProjectStatuses.Editable.Select(s => new ProjectStatusOption(s, ProjectStatuses.DisplayName(s))).ToList();

    public ProjectStatusOption Status { get => _status; set => SetProperty(ref _status, value); }

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }
}
