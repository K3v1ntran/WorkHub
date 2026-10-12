using System.Collections.ObjectModel;
using WorkHub.Core.Workspace.Content;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>One Trash entry as listed: its top item, where it came from, how many items it holds and when it was trashed.</summary>
public sealed class TrashEntryViewModel(TrashEntry entry)
{
    public TrashEntry Entry { get; } = entry;

    public long Id => Entry.Id;

    public string Name => Entry.RootName;

    public string KindText => Entry.Root.Kind switch
    {
        ContentKind.Folder => "Folder",
        ContentKind.Link => "Link",
        _ => "Note",
    };

    public bool IsFolder => Entry.Root.Kind == ContentKind.Folder;

    public bool IsLink => Entry.Root.Kind == ContentKind.Link;

    public bool IsNote => Entry.Root.Kind == ContentKind.Note;

    /// <summary>"from Alpha Portal › Specs" (the container's CURRENT path), with a hint when Restore would be refused.</summary>
    public string FromText => Entry.ContainerPath is not { } path
        ? "from a folder that no longer exists"
        : Entry.ContainerTrashed ? $"from {path.Display} (in the Trash)" : $"from {path.Display}";

    public string ItemsText => Entry.ItemCount == 1 ? "1 item" : $"{Entry.ItemCount} items";

    public string TrashedAtText => $"{Entry.TrashedAt.LocalDateTime:yyyy-MM-dd HH:mm}";

    public override string ToString() => Name;
}

/// <summary>
/// The selected Project's Trash, shown in the content pane instead of the listing (proposal §3, §7.5).
/// <list type="bullet">
/// <item>Every open, reload, close and deactivation starts a new Trash version; a load or Restore result is applied only
/// while its version is still current (the Trash view token).</item>
/// <item>Restore shares the content view's single pending-write slot, so Move to Trash, Restore and Undo never overlap and
/// repeated invocations issue no second store call. Navigation stays allowed; a submitted Restore completes on its own.</item>
/// <item>A Restore changes only this list. The content listing is never modified by it: returning to the listing is a
/// navigation and therefore a fresh load.</item>
/// </list>
/// </summary>
public sealed class ProjectTrashViewModel : ObservableObject
{
    private readonly ProjectContentViewModel _owner;
    private TrashEntryViewModel? _selectedEntry;
    private bool _isOpen;
    private bool _isLoading;
    private string? _loadError;
    private string? _notice;
    private int _version;

    internal ProjectTrashViewModel(ProjectContentViewModel owner)
    {
        _owner = owner;
        RestoreCommand = new AsyncRelayCommand(RestoreSelectedAsync, CanRestore, ex => Notice = ProjectContentService.RestoreFailed);
        ReloadCommand = new AsyncRelayCommand(() => Reload(), () => IsOpen && !IsLoading);
        Entries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Title));
        };
    }

    public ObservableCollection<TrashEntryViewModel> Entries { get; } = [];

    public TrashEntryViewModel? SelectedEntry
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
    public string? Notice { get => _notice; private set => SetProperty(ref _notice, value); }

    public string Title => $"Trash — {_owner.ProjectName}" + (IsOpen && !IsLoading && LoadError is null ? $" ({Entries.Count})" : string.Empty);

    public AsyncRelayCommand RestoreCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>The most recent list load; lets tests await it.</summary>
    internal Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent Restore; lets tests await it.</summary>
    internal Task CurrentRestore { get; private set; } = Task.CompletedTask;

    internal int Version => _version;

    internal Task Open()
    {
        IsOpen = true;
        Notice = null;
        return Reload();
    }

    /// <summary>Closing (Back, navigation, deactivation) makes every pending load or Restore result inapplicable here.</summary>
    internal void Close()
    {
        _version++;
        IsOpen = false;
        IsLoading = false;
        SelectedEntry = null;
        Entries.Clear();
        LoadError = null;
        Notice = null;
        OnPropertyChanged(nameof(Title));
    }

    internal void RaiseCommands() => RestoreCommand.RaiseCanExecuteChanged();

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
        IReadOnlyList<TrashEntry> entries;
        try
        {
            entries = await _owner.Content.ListTrashAsync(_owner.ProjectNodeId);
        }
        catch (Exception ex)
        {
            if (version != _version)
                return;
            _owner.Log.Error("The Trash could not be loaded.", ex);
            LoadError = "The Trash could not be loaded. Nothing was changed.";
            IsLoading = false;
            OnPropertyChanged(nameof(Title));
            return;
        }

        if (version != _version)
            return; // closed, reloaded or deactivated meanwhile

        foreach (var entry in entries)
            Entries.Add(new TrashEntryViewModel(entry));
        IsLoading = false;
        OnPropertyChanged(nameof(Title));
    }

    private bool CanRestore() => IsOpen && !IsLoading && SelectedEntry is not null && !_owner.IsWriteBusy;

    private Task RestoreSelectedAsync() => CurrentRestore = RestoreAsync();

    private async Task RestoreAsync()
    {
        // Direct invocations are ignored while any write of this Project view is pending (one store call only).
        if (!CanRestore() || SelectedEntry is not { } entry)
            return;

        var version = _version;
        _owner.BeginWrite();
        try
        {
            RestoreResult result;
            try
            {
                result = await _owner.Content.RestoreAsync(entry.Id);
            }
            catch (Exception ex)
            {
                _owner.Log.Error("Restore from the Trash failed.", ex); // operation only; no names or content
                if (IsEligible(version))
                    Notice = ProjectContentService.RestoreFailed;
                return;
            }

            if (!IsEligible(version))
                return; // the database result stands; this view no longer shows that list

            switch (result.Outcome)
            {
                case RestoreOutcome.Restored:
                    RemoveEntry(entry);
                    Notice = $"Restored “{result.Restored!.Name}” to {result.Restored.ContainerPath}.";
                    break;
                case RestoreOutcome.EntryNotFound:
                    RemoveEntry(entry); // restored elsewhere: the stale row leaves the list
                    Notice = result.Error;
                    break;
                default:
                    Notice = result.Error; // the entry stays, unchanged
                    if (result.Outcome == RestoreOutcome.ProjectTrashed)
                        _owner.ObserveProjectTrashed();
                    break;
            }
        }
        finally
        {
            _owner.EndWrite();
        }
    }

    private bool IsEligible(int version) => IsOpen && version == _version && !_owner.IsDeactivated;

    private void RemoveEntry(TrashEntryViewModel entry)
    {
        if (ReferenceEquals(SelectedEntry, entry))
            SelectedEntry = null;
        Entries.Remove(entry);
        OnPropertyChanged(nameof(Title));
    }
}
