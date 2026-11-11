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
/// <item>Every open, reload, close and deactivation starts a new Trash version; a load, Restore or purge result is applied only
/// while its version is still current (the Trash view token).</item>
/// <item>Restore and Phase 14's Delete permanently / Empty Trash share the content view's single pending-write slot, so Move to
/// Trash, Restore, Undo and purge never overlap and repeated invocations issue no second store call. Navigation stays allowed;
/// a submitted operation completes on its own.</item>
/// <item>A Restore changes only this list and the Project's "Trash (n)" count (by the Project view's count ordering rule). The
/// content listing is never modified by it: returning to the listing is a navigation and therefore a fresh load.</item>
/// <item>A purge previews (one snapshot), asks for confirmation of exactly that preview, then runs with a cancellation that is
/// honoured until "Deleting…". Its result is shown only while the same Trash view is open; a committed purge whose view is gone
/// re-reads the Project's Trash count instead. A stale or refused purge is never resubmitted or re-shown automatically.</item>
/// </list>
/// </summary>
public sealed class ProjectTrashViewModel : ObservableObject
{
    private readonly ProjectContentViewModel _owner;
    private TrashEntryViewModel? _selectedEntry;
    private IReadOnlyList<TrashEntryViewModel> _selectedEntries = [];
    private bool _isOpen;
    private bool _isLoading;
    private string? _loadError;
    private string? _notice;
    private int _version;
    private bool _isPurging;
    private string? _purgeStatus;
    private CancellationTokenSource? _purgeCts;
    private bool _canCancelPurge;

    internal ProjectTrashViewModel(ProjectContentViewModel owner)
    {
        _owner = owner;
        RestoreCommand = new AsyncRelayCommand(RestoreSelectedAsync, CanRestore, ex => Notice = ProjectContentService.RestoreFailed);
        ReloadCommand = new AsyncRelayCommand(() => Reload(), () => IsOpen && !IsLoading);
        DeletePermanentlyCommand = new AsyncRelayCommand(() => StartPurge(PurgeMode.SelectedEntries), CanDeleteSelected, ex => Notice = ProjectContentService.PurgeFailed);
        EmptyTrashCommand = new AsyncRelayCommand(() => StartPurge(PurgeMode.EntireTrash), CanEmptyTrash, ex => Notice = ProjectContentService.PurgeFailed);
        CancelPurgeCommand = new RelayCommand(CancelPurge, () => CanCancelPurge);
        Entries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Title));
            EmptyTrashCommand.RaiseCanExecuteChanged();
        };
    }

    public ObservableCollection<TrashEntryViewModel> Entries { get; } = [];

    public TrashEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
                RaiseCommands();
        }
    }

    /// <summary>Every selected row (the list allows extended selection). Restore needs exactly one; Delete permanently takes all.</summary>
    public IReadOnlyList<TrashEntryViewModel> SelectedEntries => _selectedEntries;

    /// <summary>Called by the view whenever the list's selection changes.</summary>
    public void UpdateSelection(IEnumerable<TrashEntryViewModel> selected)
    {
        _selectedEntries = selected.Where(Entries.Contains).ToList();
        OnPropertyChanged(nameof(SelectedEntries));
        RaiseCommands();
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
            RaiseCommands();
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

    /// <summary>The last Restore's or purge's outcome ("Restored “X” to …", "Removed from the database: …" or why it was refused).</summary>
    public string? Notice { get => _notice; private set => SetProperty(ref _notice, value); }

    public string Title => $"Trash — {_owner.ProjectName}" + (IsOpen && !IsLoading && LoadError is null ? $" ({Entries.Count})" : string.Empty);

    public AsyncRelayCommand RestoreCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    /// <summary>Phase 14: permanently delete the selected entries (and the entries trashed separately inside them).</summary>
    public AsyncRelayCommand DeletePermanentlyCommand { get; }

    /// <summary>Phase 14: permanently delete every entry of this Project's Trash.</summary>
    public AsyncRelayCommand EmptyTrashCommand { get; }

    /// <summary>Cancels a running purge; enabled only until "Deleting…" begins.</summary>
    public RelayCommand CancelPurgeCommand { get; }

    /// <summary>A purge (preview, confirmation, backup or deletion) of this view is in progress.</summary>
    public bool IsPurging { get => _isPurging; private set => SetProperty(ref _isPurging, value); }

    /// <summary>"Making a backup…" / "Deleting…" while a purge runs; null otherwise.</summary>
    public string? PurgeStatus { get => _purgeStatus; private set => SetProperty(ref _purgeStatus, value); }

    public bool CanCancelPurge
    {
        get => _canCancelPurge;
        private set
        {
            if (SetProperty(ref _canCancelPurge, value))
                CancelPurgeCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The most recent list load; lets tests await it.</summary>
    internal Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent Restore; lets tests await it.</summary>
    internal Task CurrentRestore { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent purge; lets tests await it.</summary>
    internal Task CurrentPurge { get; private set; } = Task.CompletedTask;

    /// <summary>The Project view's most recent count reconciliation (for example after a committed purge whose view was gone); lets tests await it.</summary>
    internal Task CurrentCountReconciliation => _owner.CurrentCountReconciliation;

    internal int Version => _version;

    internal Task Open()
    {
        IsOpen = true;
        Notice = null;
        return Reload();
    }

    /// <summary>Closing (Back, navigation, deactivation) makes every pending load, Restore or purge result inapplicable here.</summary>
    internal void Close()
    {
        _version++;
        IsOpen = false;
        IsLoading = false;
        SelectedEntry = null;
        UpdateSelection([]);
        Entries.Clear();
        LoadError = null;
        Notice = null;
        OnPropertyChanged(nameof(Title));
    }

    internal void RaiseCommands()
    {
        RestoreCommand.RaiseCanExecuteChanged();
        DeletePermanentlyCommand.RaiseCanExecuteChanged();
        EmptyTrashCommand.RaiseCanExecuteChanged();
    }

    private Task Reload()
    {
        var version = ++_version;
        SelectedEntry = null;
        UpdateSelection([]);
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

    private bool CanWrite() => IsOpen && !IsLoading && !_owner.IsWriteBusy;

    private bool CanRestore() => CanWrite() && SelectedEntry is not null && _selectedEntries.Count <= 1;

    private bool CanDeleteSelected() => CanWrite() && (_selectedEntries.Count > 0 || SelectedEntry is not null);

    private bool CanEmptyTrash() => CanWrite() && Entries.Count > 0;

    private Task RestoreSelectedAsync() => CurrentRestore = RestoreAsync();

    private async Task RestoreAsync()
    {
        // Direct invocations are ignored while any write of this Project view is pending (one store call only).
        if (!CanRestore() || SelectedEntry is not { } entry)
            return;

        var version = _version;
        _owner.BeginWrite();
        var countBasis = _owner.CountBasis;
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

            if (result.Outcome == RestoreOutcome.Restored)
                _owner.CountChangeCommitted(countBasis, -1, exact: IsEligible(version)); // a view that is gone re-reads the count

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

    // ---- Phase 14: Delete permanently / Empty Trash -------------------------------------------------------------------

    private Task StartPurge(PurgeMode mode) => CurrentPurge = PurgeAsync(mode);

    private async Task PurgeAsync(PurgeMode mode)
    {
        if (mode == PurgeMode.SelectedEntries ? !CanDeleteSelected() : !CanEmptyTrash())
            return; // direct invocations while busy are ignored

        var version = _version;
        var request = mode == PurgeMode.EntireTrash
            ? PurgeRequest.EntireTrash(_owner.ProjectNodeId)
            : PurgeRequest.Selected(_owner.ProjectNodeId, (_selectedEntries.Count > 0 ? _selectedEntries : [SelectedEntry!]).Select(e => e.Id));

        _owner.BeginWrite();
        var countBasis = _owner.CountBasis;
        IsPurging = true;
        try
        {
            // 1. One snapshot: the preview, its displayed values and its token.
            PurgePreviewResult preview;
            try
            {
                preview = await _owner.Content.PreparePurgeAsync(request);
            }
            catch (Exception ex)
            {
                _owner.Log.Error("The permanent deletion could not be prepared.", ex);
                if (IsEligible(version))
                    Notice = ProjectContentService.PurgeFailed;
                return;
            }

            if (!IsEligible(version))
                return;
            if (!preview.IsReady)
            {
                Notice = ProjectContentService.PurgeRefusalText(preview.Status);
                if (preview.Status == PurgePreviewStatus.ProjectTrashed)
                    _owner.ObserveProjectTrashed();
                else if (preview.Status is PurgePreviewStatus.EntryNotFound or PurgePreviewStatus.TrashEmpty)
                    _ = Reload(); // the list was stale; the notice stays
                return;
            }

            // 2. The confirmation of exactly that preview (Cancel is the default).
            if (!_owner.Dialogs.ShowPurgeConfirmation(new PurgeConfirmationViewModel(preview.Preview!)) || !IsEligible(version))
                return;

            // 3. Backup, then the revalidated deletion; cancellable until "Deleting…".
            using var cts = _purgeCts = new CancellationTokenSource();
            CanCancelPurge = true;
            PurgeStatus = "Making a backup…";
            var progress = new Progress<PurgeStage>(stage => OnPurgeStage(stage, cts));
            PurgeResult result;
            try
            {
                result = await _owner.Content.PurgeAsync(request, preview.Preview!.Token, progress, cts.Token);
            }
            catch (Exception ex)
            {
                _owner.Log.Error("Permanent deletion failed.", ex);
                if (IsEligible(version))
                    Notice = ProjectContentService.PurgeFailed;
                return;
            }

            // The count follows the Project view's ordering rule; a view that is gone (Back) re-reads it, and nothing else changes.
            if (result.Committed)
                _owner.CountChangeCommitted(countBasis, -(result.Deleted?.Entries ?? 0), exact: IsEligible(version) && result.Deleted is not null);

            if (IsEligible(version))
            {
                Notice = ProjectContentService.PurgeResultText(result);
                if (result.Refusal == PurgePreviewStatus.ProjectTrashed)
                    _owner.ObserveProjectTrashed();
                else if (result.Committed || result.Outcome is PurgeOutcome.ConfirmationStale or PurgeOutcome.Refused)
                    _ = Reload(); // a fresh list; never a new confirmation
            }
        }
        finally
        {
            _purgeCts = null;
            CanCancelPurge = false;
            PurgeStatus = null;
            IsPurging = false;
            _owner.EndWrite();
        }
    }

    private void OnPurgeStage(PurgeStage stage, CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_purgeCts, cts))
            return; // a late report of a finished purge
        switch (stage)
        {
            case PurgeStage.MakingBackup:
                PurgeStatus = cts.IsCancellationRequested ? "Cancelling…" : "Making a backup…";
                break;
            case PurgeStage.BackupPublished:
                PurgeStatus = cts.IsCancellationRequested ? "Cancelling…" : "Backup made. Deleting…";
                break;
            case PurgeStage.Deleting:
                CanCancelPurge = false; // the non-cancellable stage
                PurgeStatus = "Deleting…";
                break;
        }
    }

    private void CancelPurge()
    {
        if (_purgeCts is not { } cts || !CanCancelPurge)
            return;
        cts.Cancel();
        CanCancelPurge = false;
        PurgeStatus = "Cancelling…";
    }

    private bool IsEligible(int version) => IsOpen && version == _version && !_owner.IsDeactivated;

    private void RemoveEntry(TrashEntryViewModel entry)
    {
        if (ReferenceEquals(SelectedEntry, entry))
            SelectedEntry = null;
        Entries.Remove(entry);
        UpdateSelection(_selectedEntries.Where(e => !ReferenceEquals(e, entry)));
        OnPropertyChanged(nameof(Title));
    }
}
