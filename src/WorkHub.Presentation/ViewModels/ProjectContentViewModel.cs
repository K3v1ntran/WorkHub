using System.Collections.ObjectModel;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>Session-only memory of accepted link check results (revision 3 D6). Never persisted.</summary>
public sealed class LinkCheckCache
{
    private readonly Dictionary<long, (ProjectLinkType Type, string Target, LinkHealthStatus Status, DateTimeOffset CheckedAt)> _results = [];

    internal void Store(ProjectLink link, LinkHealthStatus status, DateTimeOffset checkedAt) =>
        _results[link.Id] = (link.Type, link.Target, status, checkedAt);

    /// <summary>A cached result, only while the link still has the checked type and target.</summary>
    internal (LinkHealthStatus Status, DateTimeOffset CheckedAt)? Get(ProjectLink link) =>
        _results.TryGetValue(link.Id, out var entry) && entry.Type == link.Type && string.Equals(entry.Target, link.Target, StringComparison.Ordinal)
            ? (entry.Status, entry.CheckedAt)
            : null;
}

/// <summary>One breadcrumb segment (null folder = Project root).</summary>
public sealed class ContentCrumbViewModel : ObservableObject
{
    private string _name;

    internal ContentCrumbViewModel(long? folderId, string name, Action<long?> navigate)
    {
        FolderId = folderId;
        _name = name;
        NavigateCommand = new RelayCommand(() => navigate(FolderId));
    }

    public long? FolderId { get; }

    public string Name { get => _name; internal set => SetProperty(ref _name, value); }

    public bool IsRoot => FolderId is null;

    public RelayCommand NavigateCommand { get; }
}

/// <summary>
/// Content of the selected Project in the right pane: one container (Project root or one ProjectFolder) at a time,
/// with a breadcrumb.
/// <list type="bullet">
/// <item>Listing loads carry a version; a newer navigation, retry or deactivation drops older results AND their errors.</item>
/// <item>Rows change only after a successful write, merged by Id.</item>
/// <item>Link checks are manual. Each result carries a token (Project, container, generation, Link Id, type, target) and is
/// applied only if all still match; navigation, reload, Stop and deactivation start a new generation and cancel queued
/// requests. Accepted results are kept for the session only.</item>
/// <item>Move to Trash, Restore and Undo share ONE pending-write slot (no duplicate store calls) while navigation stays
/// allowed. Each write remembers the visit it started in (Project, container, load version); its result changes rows,
/// selection, messages or the Undo banner only while that visit is still shown (proposal §7). Trashed rows are hidden;
/// acting on a row trashed elsewhere removes it with a message.</item>
/// </list>
/// Nothing here creates, moves, renames or deletes files or folders on disk.
/// </summary>
public sealed class ProjectContentViewModel : ObservableObject
{
    private readonly ProjectContentService _content;
    private readonly ILinkHealthChecker _checker;
    private readonly IDialogService _dialogs;
    private readonly IAppLog _log;
    private readonly LinkCheckCache _cache;
    private readonly TimeProvider _time;

    private string _projectName;
    private long? _currentFolderId;
    private ProjectContentItemViewModel? _selectedItem;
    private bool _isLoading;
    private bool _isLoaded;
    private string? _loadError;
    private string? _containerMessage;
    private string? _bannerMessage;
    private bool _isCheckingAll;
    private int _loadVersion;
    private int _generation;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource _checksCts = new();
    private bool _isDeactivated;
    private ProjectNote? _previewNote;
    private bool _isPreviewLoading;
    private string? _previewMessage;
    private int _previewVersion;
    private CancellationTokenSource? _previewCts;
    private bool _isWriteBusy;
    private int _operationSequence;
    private UndoOffer? _undo;
    private int _trashCount;

    public ProjectContentViewModel(
        ProjectContentService content,
        ILinkHealthChecker checker,
        IDialogService dialogs,
        IAppLog log,
        LinkCheckCache cache,
        long projectNodeId,
        string projectName,
        TimeProvider? time = null)
    {
        _content = content;
        _checker = checker;
        _dialogs = dialogs;
        _log = log;
        _cache = cache;
        _time = time ?? TimeProvider.System;
        ProjectNodeId = projectNodeId;
        _projectName = projectName;

        Action<Exception> onError = ex => ReportError("The operation failed. Nothing was changed.", ex);
        NewFolderCommand = new AsyncRelayCommand(NewFolderAsync, () => IsReady, onError);
        NewLinkCommand = new AsyncRelayCommand(NewLinkAsync, () => IsReady, onError);
        NewNoteCommand = new AsyncRelayCommand(NewNoteAsync, () => IsReady, onError);
        OpenCommand = new AsyncRelayCommand(OpenAsync, HasSelection, onError);
        EditCommand = new AsyncRelayCommand(EditAsync, HasSelection, onError);
        MoveCommand = new AsyncRelayCommand(MoveAsync, HasSelection, onError);
        CheckCommand = new AsyncRelayCommand(CheckSelectedAsync, () => IsReady && SelectedItem is { IsLink: true, IsChecking: false }, onError);
        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, () => IsReady && !IsCheckingAll && Items.Any(i => i.IsLink), onError);
        StopCheckingCommand = new RelayCommand(StopChecking, () => IsCheckingAll || Items.Any(i => i.IsChecking));
        GoUpCommand = new AsyncRelayCommand(GoUpAsync, () => CurrentFolderId is not null, onError);
        RetryCommand = new AsyncRelayCommand(() => NavigateAsync(CurrentFolderId), () => LoadError is not null && !IsLoading, onError);
        DismissBannerCommand = new RelayCommand(() => BannerMessage = null);
        Trash = new ProjectTrashViewModel(this);
        MoveToTrashCommand = new AsyncRelayCommand(MoveToTrashAsync, () => HasSelection() && !IsWriteBusy, onError);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => _undo is not null && !IsWriteBusy, onError);
        DismissUndoCommand = new RelayCommand(ClearUndo);
        OpenTrashCommand = new AsyncRelayCommand(OpenTrashAsync, () => !_isDeactivated && !Trash.IsOpen, onError);
        CloseTrashCommand = new AsyncRelayCommand(() => NavigateAsync(CurrentFolderId), () => Trash.IsOpen, onError);
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            CheckAllCommand.RaiseCanExecuteChanged();
            StopCheckingCommand.RaiseCanExecuteChanged();
        };
    }

    public long ProjectNodeId { get; }

    public string ProjectName
    {
        get => _projectName;
        internal set
        {
            if (!SetProperty(ref _projectName, value))
                return;
            if (Breadcrumb.FirstOrDefault() is { IsRoot: true } root)
                root.Name = value;
        }
    }

    public ObservableCollection<ProjectContentItemViewModel> Items { get; } = [];

    public ObservableCollection<ContentCrumbViewModel> Breadcrumb { get; } = [];

    public long? CurrentFolderId
    {
        get => _currentFolderId;
        private set
        {
            if (SetProperty(ref _currentFolderId, value))
                GoUpCommand.RaiseCanExecuteChanged();
        }
    }

    public ProjectContentItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;
            OpenCommand.RaiseCanExecuteChanged();
            EditCommand.RaiseCanExecuteChanged();
            MoveCommand.RaiseCanExecuteChanged();
            MoveToTrashCommand.RaiseCanExecuteChanged();
            CheckCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsPreviewVisible));
            StartPreviewLoad();
        }
    }

    /// <summary>The preview area is shown only while a Note row is selected.</summary>
    public bool IsPreviewVisible => SelectedItem is { IsNote: true };

    /// <summary>Read-only preview of the selected Note (null while loading, on error, or for other rows).</summary>
    public ProjectNote? PreviewNote
    {
        get => _previewNote;
        private set
        {
            if (SetProperty(ref _previewNote, value))
                OnPropertyChanged(nameof(PreviewUpdatedText));
        }
    }

    public string? PreviewUpdatedText => PreviewNote is { } note ? $"Updated {note.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}" : null;

    public bool IsPreviewLoading { get => _isPreviewLoading; private set => SetProperty(ref _isPreviewLoading, value); }

    /// <summary>"This note no longer exists." / load failure, for the CURRENT selection only.</summary>
    public string? PreviewMessage { get => _previewMessage; private set => SetProperty(ref _previewMessage, value); }

    /// <summary>The most recent preview load; lets tests await it.</summary>
    internal Task CurrentPreviewLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent post-editor reconciliation; lets tests await it.</summary>
    internal Task CurrentReconciliation { get; private set; } = Task.CompletedTask;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value))
                return;
            OnPropertyChanged(nameof(IsEmpty));
            RaiseCommands();
        }
    }

    /// <summary>The current container is listed (not loading, no error, still exists).</summary>
    public bool IsReady => _isLoaded && !IsLoading;

    public bool IsEmpty => IsReady && Items.Count == 0;

    /// <summary>Listing failure for the CURRENT container only.</summary>
    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value))
                RetryCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>"This project/folder no longer exists." for the current container.</summary>
    public string? ContainerMessage { get => _containerMessage; private set => SetProperty(ref _containerMessage, value); }

    public bool IsFolderMissing => ContainerMessage is not null && CurrentFolderId is not null;

    /// <summary>Errors that changed nothing (open failures, write failures outside a dialog).</summary>
    public string? BannerMessage { get => _bannerMessage; private set => SetProperty(ref _bannerMessage, value); }

    public bool IsCheckingAll
    {
        get => _isCheckingAll;
        private set
        {
            if (!SetProperty(ref _isCheckingAll, value))
                return;
            CheckAllCommand.RaiseCanExecuteChanged();
            StopCheckingCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Display path of the current container, e.g. "Alpha › Specs › 2026".</summary>
    public string LocationPath => string.Join(" › ", Breadcrumb.Select(c => c.Name));

    public AsyncRelayCommand NewFolderCommand { get; }

    public AsyncRelayCommand NewLinkCommand { get; }

    public AsyncRelayCommand NewNoteCommand { get; }

    /// <summary>Folder: enter it. Link: open its target. Note: open the editor.</summary>
    public AsyncRelayCommand OpenCommand { get; }

    /// <summary>Folder: rename. Link: edit. Note: open the editor.</summary>
    public AsyncRelayCommand EditCommand { get; }

    public AsyncRelayCommand MoveCommand { get; }

    public AsyncRelayCommand CheckCommand { get; }

    public AsyncRelayCommand CheckAllCommand { get; }

    public RelayCommand StopCheckingCommand { get; }

    public AsyncRelayCommand GoUpCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    public RelayCommand DismissBannerCommand { get; }

    // ---- Trash (proposal §3, §7) --------------------------------------------------------------------------------------

    /// <summary>The Project's Trash; while it is open the content pane shows it instead of the listing.</summary>
    public ProjectTrashViewModel Trash { get; }

    public bool IsTrashOpen => Trash.IsOpen;

    /// <summary>"Trash (3)": entries in the Project's Trash, from the latest listing (adjusted by this view's own writes).</summary>
    public string TrashButtonText => _trashCount > 0 ? $"Trash ({_trashCount})" : "Trash";

    internal int TrashCount
    {
        get => _trashCount;
        private set
        {
            if (_trashCount == value)
                return;
            _trashCount = value;
            OnPropertyChanged(nameof(TrashButtonText));
        }
    }

    /// <summary>
    /// The single pending-write slot of this Project view: while a Move to Trash (including its count read and
    /// confirmation), Restore or Undo is pending, none of them can start. Navigation stays allowed.
    /// </summary>
    public bool IsWriteBusy
    {
        get => _isWriteBusy;
        private set
        {
            if (_isWriteBusy == value)
                return;
            _isWriteBusy = value;
            OnPropertyChanged();
            MoveToTrashCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
            Trash.RaiseCommands();
        }
    }

    /// <summary>"Moved “Drafts” and 4 items inside it to the Trash." with Undo; null when no Undo is offered.</summary>
    public string? UndoMessage => _undo?.Message;

    public bool HasUndo => _undo is not null;

    /// <summary>Move the selected row (a folder with its contents) to the Trash, after a confirmation (toolbar, menu, Del).</summary>
    public AsyncRelayCommand MoveToTrashCommand { get; }

    /// <summary>Restores the entry of the Undo banner with the normal Restore rules.</summary>
    public AsyncRelayCommand UndoCommand { get; }

    public RelayCommand DismissUndoCommand { get; }

    public AsyncRelayCommand OpenTrashCommand { get; }

    /// <summary>Back from the Trash to the container that was shown (a navigation: fresh load).</summary>
    public AsyncRelayCommand CloseTrashCommand { get; }

    internal bool IsDeactivated => _isDeactivated;

    /// <summary>
    /// Set by the Workspace area: called when this view learns from a fresh read or a refused write that its Project is in
    /// the Workspace Trash (trashed by another window). Never called after deactivation.
    /// </summary>
    internal Action? ProjectTrashedObserved { get; set; }

    internal void ObserveProjectTrashed()
    {
        if (!_isDeactivated)
            ProjectTrashedObserved?.Invoke();
    }

    /// <summary>
    /// Set by the Workspace area: called when a fresh listing says this view's Project is archived (archived by another
    /// window). The content stays listed and writable (A3); the area decides by a fresh typed read. Never called after
    /// deactivation.
    /// </summary>
    internal Action? ProjectArchivedObserved { get; set; }

    internal ProjectContentService Content => _content;

    internal IAppLog Log => _log;

    /// <summary>The most recent Move to Trash or Undo; lets tests await it.</summary>
    internal Task CurrentTrashOperation { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent listing load; lets tests await it.</summary>
    internal Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent Check all run (completed when none).</summary>
    internal Task CurrentCheckAll { get; private set; } = Task.CompletedTask;

    internal int Generation => _generation;

    /// <summary>Shows the Project root.</summary>
    public Task ActivateAsync() => NavigateAsync(null);

    /// <summary>Called when another Project (or nothing) is selected: drops pending loads and cancels checks.</summary>
    public void Deactivate()
    {
        _isDeactivated = true;
        _loadVersion++;
        _loadCts?.Cancel();
        _loadCts = null;
        CancelChecks();
        InvalidatePreview();
        ClearUndo();
        CloseTrash();
        OpenTrashCommand.RaiseCanExecuteChanged();
    }

    // ---- Loading and navigation -------------------------------------------------------------------------------------

    internal Task NavigateAsync(long? folderId)
    {
        var version = ++_loadVersion;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        CancelChecks();
        ClearUndo(); // an Undo is offered only in the visit that produced it
        CloseTrash();

        CurrentFolderId = folderId;
        SelectedItem = null;
        Items.Clear();
        _isLoaded = false;
        LoadError = null;
        ContainerMessage = null;
        OnPropertyChanged(nameof(IsFolderMissing));
        IsLoading = true;
        return CurrentLoad = LoadAsync(folderId, version, cts.Token);
    }

    private async Task LoadAsync(long? folderId, int version, CancellationToken token)
    {
        ProjectContentListResult result;
        try
        {
            result = await _content.ListAsync(ProjectNodeId, folderId, token);
        }
        catch (Exception ex)
        {
            if (version != _loadVersion)
                return; // stale failures never show against a newer container
            if (ex is not OperationCanceledException)
                _log.Error("Project content could not be loaded.", ex);
            LoadError = "Content could not be loaded. Nothing was changed.";
            IsLoading = false;
            return;
        }

        if (version != _loadVersion)
            return; // a newer navigation owns the view now

        if (result.Listing is not { } listing)
        {
            ContainerMessage = result.Problem switch
            {
                ContentContainerProblem.ProjectNotFound => "This project no longer exists.",
                ContentContainerProblem.ProjectTrashed => WorkspaceService.ProjectInTrash,
                ContentContainerProblem.FolderTrashed => "This folder is in the Trash.",
                _ => "This folder no longer exists.",
            };
            OnPropertyChanged(nameof(IsFolderMissing));
            IsLoading = false;
            if (result.Problem == ContentContainerProblem.ProjectTrashed)
                ObserveProjectTrashed();
            return;
        }

        Breadcrumb.Clear();
        Breadcrumb.Add(new ContentCrumbViewModel(null, ProjectName, id => _ = NavigateAsync(id)));
        foreach (var crumb in listing.Breadcrumb)
            Breadcrumb.Add(new ContentCrumbViewModel(crumb.Id, crumb.Name, id => _ = NavigateAsync(id)));
        OnPropertyChanged(nameof(LocationPath));

        foreach (var folder in listing.Folders)
            Items.Add(new ProjectContentItemViewModel(folder));
        foreach (var link in listing.Links)
            Items.Add(CreateLinkRow(link));
        foreach (var note in listing.Notes)
            Items.Add(new ProjectContentItemViewModel(note));

        TrashCount = listing.TrashCount;
        _isLoaded = true;
        IsLoading = false;
        OnPropertyChanged(nameof(IsEmpty));
        if (listing.ProjectArchived && !_isDeactivated)
            ProjectArchivedObserved?.Invoke();
    }

    /// <summary>
    /// Search reveal (Phase 6 proposal §4.6): opens <paramref name="containerFolderId"/> with the normal navigation (which
    /// supersedes any earlier load and follows the usual check-cancellation rules), then selects the row of EXACTLY this
    /// kind and Id — Folder, Link and Note Ids are independent, so the Id alone never identifies a row. Selecting a Note
    /// starts its read-only preview; nothing opens the editor or a link.
    /// </summary>
    internal async Task<ContentRevealOutcome> RevealAsync(long? containerFolderId, ProjectContentItemKind kind, long id)
    {
        if (_isDeactivated)
            return ContentRevealOutcome.Superseded;

        var load = NavigateAsync(containerFolderId);
        var version = _loadVersion;
        await load;
        if (version != _loadVersion || _isDeactivated)
            return ContentRevealOutcome.Superseded;
        if (ContainerMessage is not null)
            return ContentRevealOutcome.ContainerMissing;
        if (LoadError is not null)
            return ContentRevealOutcome.LoadFailed;

        var row = Items.SingleOrDefault(i => i.Kind == kind && i.Id == id);
        if (row is null)
            return ContentRevealOutcome.MovedMeanwhile; // the container is still shown
        SelectedItem = row;
        return ContentRevealOutcome.Selected;
    }

    private Task GoUpAsync() =>
        NavigateAsync(!IsFolderMissing && Breadcrumb.Count >= 2 ? Breadcrumb[^2].FolderId : null);

    // ---- Commands -----------------------------------------------------------------------------------------------------

    private bool HasSelection() => IsReady && SelectedItem is not null;

    private Task NewFolderAsync()
    {
        var version = _loadVersion;
        var editor = ProjectFolderNameEditorViewModel.ForCreate(_content, ProjectNodeId, CurrentFolderId, LocationPath);
        if (_dialogs.ShowProjectFolderNameEditor(editor) && editor.SavedFolder is { } folder && version == _loadVersion && folder.ParentFolderId == CurrentFolderId)
            SelectedItem = InsertSorted(new ProjectContentItemViewModel(folder));
        return Task.CompletedTask;
    }

    private Task NewLinkAsync()
    {
        var version = _loadVersion;
        var editor = ProjectLinkEditorViewModel.ForCreate(_content, _dialogs, ProjectNodeId, CurrentFolderId, LocationPath);
        if (_dialogs.ShowProjectLinkEditor(editor) && editor.SavedLink is { } link && version == _loadVersion && link.FolderId == CurrentFolderId)
            SelectedItem = InsertSorted(CreateLinkRow(link));
        return Task.CompletedTask;
    }

    private async Task OpenAsync()
    {
        var item = SelectedItem!;
        if (item.IsFolder)
        {
            await NavigateAsync(item.Id);
            return;
        }

        if (item.IsNote)
        {
            await OpenNoteEditorAsync(item);
            return;
        }

        var result = await _content.OpenLinkAsync(item.Id);
        if (result.Problem == ProjectLinkOpenProblem.LinkTrashed)
            RemoveRow(item.Ref); // a stale row (trashed in another window) leaves this listing
        BannerMessage = result.Succeeded ? null : result.Message ?? "The target could not be opened.";
        if (result.Problem == ProjectLinkOpenProblem.ProjectTrashed)
            ObserveProjectTrashed();
    }

    private Task EditAsync()
    {
        var item = SelectedItem!;
        if (item.IsNote)
            return OpenNoteEditorAsync(item);

        var token = CaptureToken();
        var version = _loadVersion;
        if (item.Folder is { } folder)
        {
            var editor = ProjectFolderNameEditorViewModel.ForRename(_content, folder, LocationPath);
            if (_dialogs.ShowProjectFolderNameEditor(editor) && editor.SavedFolder is { } saved)
            {
                if (version != _loadVersion)
                    return Task.CompletedTask;
                if (saved.ParentFolderId != CurrentFolderId)
                    RemoveMovedAway(item);
                else
                {
                    item.ApplyFolder(saved);
                    Resort(item);
                }
            }
            else if (editor.SaveRefused)
                return ReconcileSourceAsync(item.Ref, token);
        }
        else
        {
            var editor = ProjectLinkEditorViewModel.ForEdit(_content, _dialogs, item.Link!, LocationPath);
            if (_dialogs.ShowProjectLinkEditor(editor) && editor.SavedLink is { } saved)
            {
                if (version != _loadVersion)
                    return Task.CompletedTask;
                if (saved.FolderId != CurrentFolderId)
                    RemoveMovedAway(item);
                else
                {
                    item.ApplyLink(saved); // a Type/Target change resets the status; any in-flight result no longer matches
                    Resort(item);
                }

                StopCheckingCommand.RaiseCanExecuteChanged();
                CheckCommand.RaiseCanExecuteChanged();
            }
            else if (editor.SaveRefused)
                return ReconcileSourceAsync(item.Ref, token);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// After a Rename / Edit / Move dialog closed with a refused write (proposal revision 2 §8): the row may be stale (for
    /// example trashed by another window). Re-reads the item by (Kind, Id) and applies its CURRENT state, never the refusal:
    /// in the Trash or gone → the row leaves with a message; active elsewhere → the row leaves; active here → nothing changes.
    /// Applied only while the originating visit is still shown; other rows and their link checks are untouched.
    /// </summary>
    private Task ReconcileSourceAsync(ContentRef target, ContentViewToken token) =>
        CurrentReconciliation = ReconcileSourceCoreAsync(target, token);

    private async Task ReconcileSourceCoreAsync(ContentRef target, ContentViewToken token)
    {
        if (!IsEligible(token))
            return;

        ContentState? state;
        try
        {
            state = await _content.GetContentStateAsync(target);
        }
        catch (Exception ex)
        {
            _log.Error("The item could not be re-read after a refused change.", ex); // the dialog already showed the refusal
            return;
        }

        if (!IsEligible(token) || Items.FirstOrDefault(i => i.Ref == target) is not { } row)
            return;
        if (state is { ProjectTrashed: true })
        {
            BannerMessage = WorkspaceService.ProjectInTrash;
            ObserveProjectTrashed(); // the Workspace area reconciles the Project by Id (and deactivates this view if current)
            return;
        }

        if (state is { Trashed: false } && state.ProjectNodeId == ProjectNodeId && state.ContainerFolderId == CurrentFolderId)
            return; // still active here (for example restored meanwhile): the row is valid

        RemoveMovedAway(row);
        BannerMessage = state is null ? "This item no longer exists."
            : state.Trashed ? "This item is in the Trash."
            : BannerMessage; // active in another container: the row leaves this listing, as after a move elsewhere
    }

    /// <summary>
    /// The saved item now lives in another container (moved by another window or process while its editor was open).
    /// The edit never writes the container, so the item stays where the other move put it; this view only drops the row.
    /// A check still in flight for it finds no row and is discarded (<see cref="AcceptingRow"/>).
    /// </summary>
    private void RemoveMovedAway(ProjectContentItemViewModel item)
    {
        if (ReferenceEquals(SelectedItem, item))
            SelectedItem = null;
        Items.Remove(item);
    }

    private async Task MoveAsync()
    {
        var item = SelectedItem!;
        var token = CaptureToken();
        var version = _loadVersion;
        var targets = await _content.ListFolderMoveTargetsAsync(ProjectNodeId, item.IsFolder ? item.Id : null);
        if (!ReferenceEquals(SelectedItem, item) || version != _loadVersion)
            return; // the view changed while destinations were loading

        // The observed source container is the one this row was listed in; the dialog sends it unchanged.
        var editor = new ProjectContentMoveViewModel(_content, item.Kind, item.Id, item.Name, item.ContainerId, ProjectName, targets);
        var confirmed = _dialogs.ShowProjectContentMove(editor);
        if (version != _loadVersion)
            return;

        if (editor.IsStale)
        {
            await NavigateAsync(CurrentFolderId); // show where the item is now
            return;
        }

        if (confirmed && editor.Moved && editor.MovedTo != CurrentFolderId)
        {
            Items.Remove(item);
            SelectedItem = null;
        }
        else if (!confirmed && editor.MoveRefused)
            await ReconcileSourceAsync(item.Ref, token);
    }

    // ---- Move to Trash, Undo and the Trash view (proposal §7) ----------------------------------------------------------

    /// <summary>
    /// The visit an operation started in. Eligible ⇔ this view is still active and still shows that load (the load version
    /// changes on every navigation — including revisiting the same container —, on opening the Trash and on deactivation).
    /// </summary>
    private ContentViewToken CaptureToken() => new(ProjectNodeId, CurrentFolderId, _loadVersion);

    private bool IsEligible(ContentViewToken token) =>
        !_isDeactivated && !Trash.IsOpen && token.LoadVersion == _loadVersion && token.ContainerFolderId == CurrentFolderId;

    /// <summary>Takes the pending-write slot. Starting a write also withdraws the current Undo (it is never offered after a newer write began).</summary>
    internal int BeginWrite()
    {
        ClearUndo();
        IsWriteBusy = true;
        return ++_operationSequence;
    }

    internal void EndWrite() => IsWriteBusy = false;

    private Task MoveToTrashAsync() => CurrentTrashOperation = MoveToTrashCoreAsync();

    /// <summary>
    /// Count snapshot → confirmation → Trash (§7.3–7.4). The slot is held from the count read to the write's completion, so a
    /// repeated click, Del or Enter issues no second store call. A result is applied only while the originating visit is
    /// still shown; otherwise the database change stands and the next fresh load shows it.
    /// </summary>
    private async Task MoveToTrashCoreAsync()
    {
        if (IsWriteBusy || !HasSelection() || SelectedItem is not { } item)
            return; // direct invocations while busy are ignored

        var token = CaptureToken();
        var target = item.Ref;
        var name = item.Name;
        IsWriteBusy = true; // the current Undo stays visible (disabled) until the write itself starts
        try
        {
            TrashCount count;
            try
            {
                count = await _content.CountTrashSetAsync(target);
            }
            catch (Exception ex)
            {
                _log.Error("The Trash count could not be read.", ex);
                if (IsEligible(token))
                    BannerMessage = ProjectContentService.TrashFailed;
                return;
            }

            // No dialog for a visit that is no longer shown or a selection that changed meanwhile.
            if (!IsEligible(token) || SelectedItem?.Ref != target)
                return;
            if (!count.Exists || count.Trashed)
            {
                RemoveRow(target);
                BannerMessage = count.Exists ? "This item is in the Trash." : "This item no longer exists.";
                return;
            }

            if (!_dialogs.Confirm("Move to Trash", ConfirmationText(target.Kind, name, count.ActiveItems), "Move to Trash"))
                return;

            ClearUndo(); // the write starts now: an older Undo is never offered after it
            var sequence = ++_operationSequence;
            TrashResult result;
            try
            {
                result = await _content.TrashAsync(target);
            }
            catch (Exception ex)
            {
                _log.Error("Move to Trash failed.", ex); // operation only; no names or content
                if (IsEligible(token))
                    BannerMessage = ProjectContentService.TrashFailed;
                return;
            }

            if (!IsEligible(token))
                return; // committed or not, nothing changes in another container, Project or visit

            switch (result.Outcome)
            {
                case TrashOutcome.Trashed:
                    RemoveRow(target); // only this (Kind, Id); other rows and their link checks are untouched
                    TrashCount++;
                    BannerMessage = null;
                    if (sequence == _operationSequence)
                        OfferUndo(new UndoOffer(result.Entry!.Id, sequence, token, TrashedText(target.Kind, result.Entry.RootName, result.Entry.ItemCount)));
                    break;
                case TrashOutcome.AlreadyTrashed or TrashOutcome.NotFound:
                    RemoveRow(target);
                    BannerMessage = result.Error;
                    break;
                default:
                    BannerMessage = result.Error; // nothing written; the row stays
                    if (result.Outcome == TrashOutcome.ProjectTrashed)
                        ObserveProjectTrashed();
                    break;
            }
        }
        finally
        {
            IsWriteBusy = false;
        }
    }

    private Task UndoAsync() => CurrentTrashOperation = UndoCoreAsync();

    /// <summary>
    /// Restore of the banner's entry (§7.6). The restored row is inserted only when it belongs in the container still shown
    /// by the visit that produced the banner and is not already listed; it is built from the row the Restore committed.
    /// </summary>
    private async Task UndoCoreAsync()
    {
        if (IsWriteBusy || _undo is not { } undo || !IsEligible(undo.Token))
            return;

        BeginWrite();
        try
        {
            RestoreResult result;
            try
            {
                result = await _content.RestoreAsync(undo.EntryId);
            }
            catch (Exception ex)
            {
                _log.Error("Undo (restore from the Trash) failed.", ex);
                if (IsEligible(undo.Token))
                    BannerMessage = ProjectContentService.RestoreFailed;
                return;
            }

            if (!IsEligible(undo.Token))
                return;

            if (result.Outcome != RestoreOutcome.Restored)
            {
                BannerMessage = result.Error; // no Undo is offered again
                if (result.Outcome == RestoreOutcome.ProjectTrashed)
                    ObserveProjectTrashed();
                return;
            }

            TrashCount = Math.Max(0, TrashCount - 1);
            var restored = result.Restored!;
            if (restored.ProjectNodeId != ProjectNodeId || restored.ContainerFolderId != CurrentFolderId)
            {
                BannerMessage = $"Restored to {restored.ContainerPath}.";
                return;
            }

            BannerMessage = null;
            if (!Items.Any(i => i.Ref == restored.Root) && RowFor(restored) is { } row)
                SelectedItem = InsertSorted(row);
        }
        finally
        {
            EndWrite();
        }
    }

    private Task OpenTrashAsync()
    {
        if (_isDeactivated || Trash.IsOpen)
            return Task.CompletedTask;

        // Opening the Trash is a navigation: the listing's load, checks, preview and Undo belong to the visit that ends here.
        _loadVersion++;
        _loadCts?.Cancel();
        _loadCts = null;
        CancelChecks();
        ClearUndo();
        SelectedItem = null;
        Items.Clear();
        _isLoaded = false;
        LoadError = null;
        IsLoading = false;
        RaiseCommands();
        var load = Trash.Open();
        OnTrashOpenChanged();
        return load;
    }

    private void CloseTrash()
    {
        if (!Trash.IsOpen)
            return;
        Trash.Close();
        OnTrashOpenChanged();
    }

    private void OnTrashOpenChanged()
    {
        OnPropertyChanged(nameof(IsTrashOpen));
        OpenTrashCommand.RaiseCanExecuteChanged();
        CloseTrashCommand.RaiseCanExecuteChanged();
    }

    private void OfferUndo(UndoOffer offer)
    {
        _undo = offer;
        OnUndoChanged();
    }

    private void ClearUndo()
    {
        if (_undo is null)
            return;
        _undo = null;
        OnUndoChanged();
    }

    private void OnUndoChanged()
    {
        OnPropertyChanged(nameof(UndoMessage));
        OnPropertyChanged(nameof(HasUndo));
        UndoCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Removes the row of exactly this (Kind, Id), if listed; a selected row also clears the selection and preview.</summary>
    private void RemoveRow(ContentRef target)
    {
        if (Items.FirstOrDefault(i => i.Ref == target) is { } row)
            RemoveMovedAway(row);
    }

    private ProjectContentItemViewModel? RowFor(RestoredRoot restored) =>
        restored.Folder is { } folder ? new ProjectContentItemViewModel(folder)
        : restored.Link is { } link ? CreateLinkRow(link)
        : restored.Note is { } note ? new ProjectContentItemViewModel(note)
        : null;

    /// <summary>The confirmation names the item; the count is a snapshot and is not promised (T7, T11).</summary>
    internal static string ConfirmationText(ContentKind kind, string name, int activeItems)
    {
        switch (kind)
        {
            case ContentKind.Folder:
                var inside = activeItems - 1;
                var contains = inside switch
                {
                    0 => "It is currently empty",
                    1 => "It currently contains 1 item",
                    _ => $"It currently contains {inside} items",
                };
                return $"Move folder “{name}” and its contents to the Trash? {contains}; whatever it contains when the move happens goes with it. Files on disk are not touched.";
            case ContentKind.Link:
                return $"Move link “{name}” to the Trash? Its target is not touched.";
            default:
                return $"Move note “{name}” to the Trash?";
        }
    }

    /// <summary>The banner states the ACTUAL number moved, from the transaction.</summary>
    internal static string TrashedText(ContentKind kind, string name, int itemCount)
    {
        var inside = itemCount - 1;
        if (kind != ContentKind.Folder || inside == 0)
            return $"Moved “{name}” to the Trash.";
        return inside == 1
            ? $"Moved “{name}” and 1 item inside it to the Trash."
            : $"Moved “{name}” and {inside} items inside it to the Trash.";
    }

    private readonly record struct ContentViewToken(long ProjectNodeId, long? ContainerFolderId, int LoadVersion);

    private sealed record UndoOffer(long EntryId, int Sequence, ContentViewToken Token, string Message);

    // ---- Notes: editor, preview, reconciliation ---------------------------------------------------------------------

    private async Task NewNoteAsync()
    {
        var editor = NoteEditorViewModel.ForCreate(_content, _dialogs, ProjectNodeId, CurrentFolderId, LocationPath, _time);
        _dialogs.ShowNoteEditor(editor);
        await ReconcileAfterEditorAsync(editor);
    }

    /// <summary>Reads the note fresh (not the preview copy) and opens the modal editor on it.</summary>
    private async Task OpenNoteEditorAsync(ProjectContentItemViewModel item)
    {
        var version = _loadVersion;
        var note = await _content.GetNoteAsync(item.Id);
        if (!ReferenceEquals(SelectedItem, item) || version != _loadVersion || _isDeactivated)
            return; // the view changed while the note was loading

        if (note is null || note.IsTrashed)
        {
            RemoveMovedAway(item);
            BannerMessage = note is null ? "This note no longer exists." : "This note is in the Trash.";
            return;
        }

        // The location comes from the fresh row's container: the note may have been moved since this listing was loaded.
        var location = await NoteEditorViewModel.TryReadLocationAsync(_content, note);
        if (!ReferenceEquals(SelectedItem, item) || version != _loadVersion || _isDeactivated)
            return;

        var editor = NoteEditorViewModel.ForEdit(_content, _dialogs, note, location, _time);
        _dialogs.ShowNoteEditor(editor); // the result says only whether something was saved; it never gates reconciliation
        await ReconcileAfterEditorAsync(editor);
    }

    /// <summary>
    /// Fresh-read reconciliation after EVERY note editor close (Phase 5B proposal §5.3): the editor's saved rows are never
    /// treated as current. Reads the latest state of every note the editor opened or created, then applies it in place if
    /// the same Project/container/load version is still shown and the view is active. No reload, no write, link checks
    /// untouched. A read failure changes nothing and shows a refresh banner.
    /// </summary>
    private Task ReconcileAfterEditorAsync(NoteEditorViewModel editor) =>
        CurrentReconciliation = ReconcileNotesAsync(editor.TouchedNoteIds.ToList(), editor.CreatedNoteIds.ToHashSet());

    private async Task ReconcileNotesAsync(IReadOnlyList<long> ids, IReadOnlySet<long> created)
    {
        if (ids.Count == 0)
            return; // a new-note editor closed without a successful save

        var folder = CurrentFolderId;
        var version = _loadVersion;
        bool StillCurrent() => !_isDeactivated && version == _loadVersion && folder == CurrentFolderId;

        IReadOnlyList<ProjectNote> fresh;
        try
        {
            fresh = await _content.GetNotesAsync(ids);
        }
        catch (Exception ex)
        {
            if (!StillCurrent())
                return; // stale failures never show against a newer view
            _log.Error("Notes could not be refreshed after editing.", ex); // no note content is logged
            BannerMessage = "The list could not be refreshed after editing. It may be out of date; open the folder again to refresh.";
            return; // rows, selection and preview stay as they are; nothing is treated as missing
        }

        if (!StillCurrent())
            return;

        var byId = fresh.ToDictionary(n => n.Id);
        ProjectContentItemViewModel? select = null;
        foreach (var id in ids)
        {
            var row = Items.FirstOrDefault(i => i.IsNote && i.Id == id);
            if (byId.TryGetValue(id, out var note) && !note.IsTrashed && note.ProjectNodeId == ProjectNodeId && note.FolderId == CurrentFolderId)
            {
                if (row is null)
                    row = InsertSorted(new ProjectContentItemViewModel(note.Summary));
                else
                {
                    row.ApplyNote(note.Summary);
                    Resort(row);
                }

                if (created.Contains(id))
                    select = row; // the last created note that landed here
            }
            else if (row is not null)
            {
                RemoveMovedAway(row); // moved elsewhere, in the Trash or no longer exists; clears selection and preview if selected
            }
        }

        if (select is not null)
            SelectedItem = select;
        if (SelectedItem is { IsNote: true } selected && byId.TryGetValue(selected.Id, out var current) && !current.IsTrashed && Items.Contains(selected))
            ShowPreview(current);
    }

    /// <summary>Starts a versioned preview load for the selected Note; anything else clears the preview.</summary>
    private void StartPreviewLoad()
    {
        InvalidatePreview();
        if (SelectedItem is not { IsNote: true } item)
        {
            CurrentPreviewLoad = Task.CompletedTask;
            return;
        }

        IsPreviewLoading = true;
        var cts = _previewCts = new CancellationTokenSource();
        CurrentPreviewLoad = LoadPreviewAsync(item, _previewVersion, cts.Token);
    }

    private async Task LoadPreviewAsync(ProjectContentItemViewModel item, int version, CancellationToken token)
    {
        try
        {
            var note = await _content.GetNoteAsync(item.Id, token);
            if (version != _previewVersion || !ReferenceEquals(SelectedItem, item))
                return; // a newer selection or a refreshed preview owns the pane
            if (note is null)
                PreviewMessage = "This note no longer exists.";
            else if (note.IsTrashed)
                PreviewMessage = "This note is in the Trash.";
            else
                PreviewNote = note;
        }
        catch (Exception ex)
        {
            if (version != _previewVersion || !ReferenceEquals(SelectedItem, item))
                return; // stale failures are dropped too
            if (ex is not OperationCanceledException)
                _log.Error("A note preview could not be loaded.", ex);
            PreviewMessage = "The note could not be loaded.";
        }
        finally
        {
            if (version == _previewVersion)
                IsPreviewLoading = false;
        }
    }

    /// <summary>Shows a freshly read note; any older preview request is invalidated FIRST so it cannot overwrite this.</summary>
    private void ShowPreview(ProjectNote note)
    {
        InvalidatePreview();
        PreviewNote = note;
        CurrentPreviewLoad = Task.CompletedTask;
    }

    private void InvalidatePreview()
    {
        _previewVersion++;
        _previewCts?.Cancel();
        _previewCts = null;
        PreviewNote = null;
        PreviewMessage = null;
        IsPreviewLoading = false;
    }

    // ---- Link checks -------------------------------------------------------------------------------------------------

    private Task CheckSelectedAsync() => RunCheckAsync(SelectedItem!, _checksCts.Token);

    private async Task CheckAllAsync()
    {
        var generation = _generation;
        var token = _checksCts.Token;
        IsCheckingAll = true;
        var run = Task.WhenAll(Items.Where(i => i.IsLink && !i.IsChecking).ToList().Select(i => RunCheckAsync(i, token)));
        CurrentCheckAll = run;
        try
        {
            await run;
        }
        finally
        {
            if (generation == _generation)
                IsCheckingAll = false;
        }
    }

    private async Task RunCheckAsync(ProjectContentItemViewModel row, CancellationToken token)
    {
        var link = row.Link!;
        var check = new CheckToken(ProjectNodeId, CurrentFolderId, _generation, link.Id, link.Type, link.Target);
        row.BeginCheck();
        RaiseCheckCommands();

        // The row is a snapshot: a link trashed or removed in another window is not checked, and its row leaves this
        // listing (proposal §4.5). A failed re-read never blocks the check.
        ProjectLink? stored;
        try
        {
            stored = await _content.GetLinkAsync(link.Id, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Error("A link could not be re-read before its check.", ex);
            stored = link;
        }

        if (token.IsCancellationRequested)
            return; // Stop, navigation or deactivation while re-reading
        if (stored is null || stored.IsTrashed)
        {
            if (AcceptingRow(check) is { } stale)
            {
                stale.RevertCheck();
                RemoveMovedAway(stale);
                BannerMessage = stored is null ? "This link no longer exists." : "This link is in the Trash.";
                RaiseCheckCommands();
            }

            return;
        }

        LinkHealthStatus status;
        try
        {
            status = await _checker.CheckAsync(link.Type, link.Target, token);
        }
        catch (OperationCanceledException)
        {
            return; // Stop already reverted the row; navigation and deactivation dropped it
        }
        catch (Exception ex)
        {
            _log.Error("Link check failed.", ex);
            status = LinkHealthStatus.Unknown;
        }

        if (AcceptingRow(check) is not { } current)
            return; // late or stale result: discarded silently

        var checkedAt = _time.GetUtcNow();
        current.SetStatus(status, checkedAt);
        _cache.Store(current.Link!, status, checkedAt);
        RaiseCheckCommands();
    }

    /// <summary>The row a result may be applied to: same Project, container and generation, and the same Link Id, type and target.</summary>
    private ProjectContentItemViewModel? AcceptingRow(CheckToken check)
    {
        if (check.ProjectNodeId != ProjectNodeId || check.ContainerFolderId != CurrentFolderId || check.Generation != _generation)
            return null;
        return Items.FirstOrDefault(i => i.Link is { } l && l.Id == check.LinkId && l.Type == check.Type
                                         && string.Equals(l.Target, check.Target, StringComparison.Ordinal) && i.IsChecking);
    }

    private void StopChecking() => CancelChecks();

    /// <summary>New generation: queued requests are cancelled, late results are discarded, checking rows revert.</summary>
    private void CancelChecks()
    {
        _generation++;
        _checksCts.Cancel();
        _checksCts = new CancellationTokenSource();
        foreach (var item in Items)
            item.RevertCheck();
        IsCheckingAll = false;
        RaiseCheckCommands();
    }

    private ProjectContentItemViewModel CreateLinkRow(ProjectLink link)
    {
        var row = new ProjectContentItemViewModel(link);
        if (_cache.Get(link) is { } cached)
            row.SetStatus(cached.Status, cached.CheckedAt);
        return row;
    }

    // ---- Row ordering (folders, then links, then notes; each by name) -----------------------------------------------

    private ProjectContentItemViewModel InsertSorted(ProjectContentItemViewModel item)
    {
        var index = 0;
        while (index < Items.Count && Precedes(Items[index], item))
            index++;
        Items.Insert(index, item);
        return item;
    }

    private void Resort(ProjectContentItemViewModel item)
    {
        var oldIndex = Items.IndexOf(item);
        if (oldIndex < 0)
            return;
        var newIndex = Items.Count(other => !ReferenceEquals(other, item) && Precedes(other, item));
        if (newIndex != oldIndex)
            Items.Move(oldIndex, newIndex);
    }

    private static bool Precedes(ProjectContentItemViewModel a, ProjectContentItemViewModel b) =>
        a.KindRank != b.KindRank ? a.KindRank < b.KindRank : WorkspaceNodeOrder.Instance.Compare(a.SortKey, b.SortKey) < 0;

    private void RaiseCommands()
    {
        NewFolderCommand.RaiseCanExecuteChanged();
        NewLinkCommand.RaiseCanExecuteChanged();
        NewNoteCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        EditCommand.RaiseCanExecuteChanged();
        MoveCommand.RaiseCanExecuteChanged();
        MoveToTrashCommand.RaiseCanExecuteChanged();
        RetryCommand.RaiseCanExecuteChanged();
        RaiseCheckCommands();
    }

    private void RaiseCheckCommands()
    {
        CheckCommand.RaiseCanExecuteChanged();
        CheckAllCommand.RaiseCanExecuteChanged();
        StopCheckingCommand.RaiseCanExecuteChanged();
    }

    private void ReportError(string message, Exception ex)
    {
        _log.Error(message, ex);
        BannerMessage = message;
    }

    private readonly record struct CheckToken(long ProjectNodeId, long? ContainerFolderId, int Generation, long LinkId, ProjectLinkType Type, string Target);
}
