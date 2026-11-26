using System.Collections.ObjectModel;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Settings;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Core.Workspace.Search;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Workspace tab: the logical tree of Folders and Projects plus the selected Project's details.
/// <list type="bullet">
/// <item>Nothing loads until the tab is first shown; then only root nodes load. A Folder's children load on its first
/// expand, and a load already in flight is reused rather than repeated. Re-expanding a loaded folder does not re-read it.</item>
/// <item>The tree changes after a successful write (only the affected collections, merged by Id), and during a search
/// reveal, which reconciles ONLY the branches on the revealed path to one fresh read snapshot (<see cref="RevealAsync"/>).</item>
/// <item>One instance per node Id, placed in exactly one collection (<see cref="Roots"/> or its Parent's Children):
/// every relocation removes the old placement first (<see cref="Place"/>).</item>
/// <item>Asynchronous results never overwrite newer state: details loads carry a version and are dropped (including
/// their failures) once the selection or the saved state moved on; mutations wait for a pending child load of the
/// folder they touch before merging.</item>
/// </list>
/// Workspace is metadata only: nothing here creates, moves or renames files or folders on disk.
/// </summary>
public sealed partial class WorkspaceAreaViewModel : ObservableObject
{
    private readonly WorkspaceService _workspace;
    private readonly IDialogService _dialogs;
    private readonly IAppLog _log;
    private readonly Dictionary<long, WorkspaceNodeViewModel> _nodes = [];

    private Task? _rootLoad;
    private bool _isTabSelected;
    private bool _isActivated;
    private bool _isLoadingRoots;
    private string? _rootLoadError;
    private WorkspaceNodeViewModel? _selectedNode;
    private ProjectDetails? _selectedProject;
    private bool _isLoadingDetails;
    private string? _detailsMessage;
    private int _detailsVersion;
    private CancellationTokenSource? _detailsCts;
    private string? _bannerMessage;
    private readonly ProjectContentService? _content;
    private readonly ILinkHealthChecker? _checker;
    private readonly LinkCheckCache _checkCache = new();
    private ProjectContentViewModel? _selectedContent;
    private readonly LatestOnlyRunner<SearchItemRef, RevealResolution>? _resolveRunner;
    private int _rootsStamp;
    private int _revealVersion;
    private int _revealSequence;
    /// <summary>
    /// Node Id → the sequence at which a reveal or a committed lifecycle result last placed or removed it. A lazy load that
    /// started earlier skips such rows unless the node already sits in its branch (Phase 8 proposal §8.3).
    /// </summary>
    private readonly Dictionary<long, int> _touchedByReveal = [];

    /// <summary>
    /// Phase 19 (D10, R2b–R2d): node Id → the sequence at which a READ-derived result (lazy load, details, Edit pre-read, folder
    /// check, reconcile, confirmation) last placed, moved or removed it. Keyed by Id, so it outlives a removed row and a
    /// forgotten instance. It never discards a result (rule C: it triggers one confirmation read). Pruned once no tracked
    /// read older than the stamp is in flight.
    /// </summary>
    private readonly Dictionary<long, int> _readUpdated = [];

    /// <summary>Read marks (sequence → count) of the tracked reads still in flight (pruning of <see cref="_readUpdated"/>).</summary>
    private readonly SortedDictionary<int, int> _readsInFlight = new();

    /// <summary>Per node: the confirmation read running (and whether one more is queued). At most one running + one queued.</summary>
    private readonly Dictionary<long, ConfirmationSlot> _confirmations = [];

    private sealed class ConfirmationSlot
    {
        public Task Task { get; set; } = Task.CompletedTask;

        public bool Queued { get; set; }

        /// <summary>
        /// Phase 20 (R2a-2): true while every request this run serves came from the bulk re-placement (<see cref="ReadNotice.Silent"/>);
        /// a <see cref="ReadNotice.Show"/> request that joins sets it to false for good, so a single flow keeps its banner.
        /// </summary>
        public bool Silent { get; set; }
    }

    /// <summary>
    /// Phase 20 (R2a-2): whether a read-derived tree update writes the Workspace banner. Every existing caller uses
    /// <see cref="Show"/>; only the Bulk Unarchive re-placement (and the confirmation it alone requested) uses <see cref="Silent"/>,
    /// which applies exactly the same tree effects and skips only the <see cref="BannerMessage"/> assignment.
    /// </summary>
    internal enum ReadNotice
    {
        Show,
        Silent,
    }

    private enum ConfirmationOutcome
    {
        /// <summary>The read was applied (or there was nothing to apply); a queued request still gets one more read.</summary>
        Done,

        /// <summary>Rule C: another read-derived update landed while it ran; one more read decides.</summary>
        Again,

        /// <summary>Rule L: a committed write of this window is newer; the result is discarded and the node's run ends, queued request included.</summary>
        Superseded,

        /// <summary>Read failed or its Node was missing/inconsistent; logged, nothing applied, no follow-up.</summary>
        Failed,
    }

    /// <summary>Tests: confirmation results discarded by rule L (a committed write of this window was newer).</summary>
    internal int ConfirmationsSuperseded { get; private set; }

    private bool _applyingReveal;
    private bool _quietSelection;
    private int _registrations;

    // ---- Workspace lifecycle (Phase 8) ----
    private bool _isLifecycleBusy;
    private int _visit;
    private int _lifecycleCompletions;
    private int _selectionVersion;
    private UndoOffer? _undo;
    private bool _isTrashOpen;
    private bool _isArchivedOpen;
    private bool _isInfoOpen;
    private WorkspaceNodeState? _selectedFolderInfo;
    private int _folderInfoVersion;

    /// <summary>
    /// The Undo of the latest Move to Trash or Archive, bound to the Workspace visit in which it started. An Archive's Undo
    /// carries its committed <see cref="ArchiveEvent"/>: it applies to exactly that archive event (Phase 12 A10). A Folder
    /// Archive's or Folder Trash's Undo carries its <see cref="FolderLifecycleEvent"/> (its LifecycleSerial, D13).
    /// </summary>
    private sealed record UndoOffer(long NodeId, string Name, int Visit, string Message, ArchiveEvent? Archive = null, FolderLifecycleEvent? Folder = null)
    {
        /// <summary>Phase 19 Bulk Undo: exactly the archive events the batch created, in batch order.</summary>
        public IReadOnlyList<ArchiveEvent>? Bulk { get; init; }

        /// <summary>Phase 19: the names read when the Bulk Archive dialog opened.</summary>
        public IReadOnlyDictionary<long, string>? BulkNames { get; init; }
    }

    /// <param name="content">Project content (ProjectFolders and Links); null leaves the content view out.</param>
    /// <param name="checker">The application-wide manual link checker.</param>
    /// <param name="search">Workspace search; null leaves search out.</param>
    /// <param name="searchDelayMs">Search debounce (tests shorten it).</param>
    /// <param name="settings">Phase 13A Workspace preferences (the selection toolbar); null leaves them out (toolbar shown).</param>
    public WorkspaceAreaViewModel(
        WorkspaceService workspace, IDialogService dialogs, IAppLog log,
        ProjectContentService? content = null, ILinkHealthChecker? checker = null,
        WorkspaceSearchService? search = null, int searchDelayMs = WorkspaceSearchViewModel.DefaultDelayMs,
        SettingsService? settings = null)
    {
        _workspace = workspace;
        _dialogs = dialogs;
        _log = log;
        _content = content;
        _checker = checker;
        _noteAutoSave = settings is null ? null : () => settings.Current.Ui.Notes.AutoSave; // Phase 18: read when an editor opens
        if (settings is not null)
        {
            Preferences = new WorkspacePreferencesViewModel(settings, message => BannerMessage = message);
            Preferences.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(WorkspacePreferencesViewModel.ShowTreeToolbar)
                    or nameof(WorkspacePreferencesViewModel.ShowSelectionToolbar)
                    or nameof(WorkspacePreferencesViewModel.ShowContentToolbar))
                    OnPropertyChanged(e.PropertyName);
            };
        }

        ToggleInfoCommand = new RelayCommand(() => IsInfoOpen = !IsInfoOpen, () => IsInfoOpen || CanOpenInfo);
        CloseInfoCommand = new RelayCommand(() => IsInfoOpen = false, () => IsInfoOpen);
        if (search is not null)
        {
            _resolveRunner = new LatestOnlyRunner<SearchItemRef, RevealResolution>((item, token) => search.ResolveAsync(item, token));
            Search = new WorkspaceSearchViewModel(search, this, log, searchDelayMs);
            // Phase 25 (§2.1 step 7): search replacing the tree cancels a pending hover-expand.
            Search.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WorkspaceSearchViewModel.IsActive) && Search.IsActive)
                    CancelDragHover();
            };
        }

        Roots.CollectionChanged += (_, _) => _rootsStamp++;

        Action<Exception> onError = ex => ReportError("The Workspace operation failed. Nothing was changed.", ex);
        // D15: every Workspace write command is disabled while a lifecycle write (Trash, Restore, Undo, Remove) is pending.
        NewFolderCommand = new AsyncRelayCommand(NewFolderAsync, CanWrite, onError);
        NewProjectCommand = new AsyncRelayCommand(NewProjectAsync, CanWrite, onError);
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => CanWrite() && HasSelection(), onError);
        EditProjectCommand = new AsyncRelayCommand(EditProjectAsync, () => CanWrite() && SelectedNode?.IsProject == true, onError);
        MoveCommand = new AsyncRelayCommand(MoveAsync, () => CanWrite() && HasSelection(), onError);
        TrashProjectCommand = new AsyncRelayCommand(TrashSelectedProjectAsync, () => CanWrite() && SelectedNode?.IsProject == true, onError);
        RemoveFolderCommand = new AsyncRelayCommand(RemoveSelectedFolderAsync, () => CanWrite() && SelectedNode?.IsFolder == true, onError);
        DeleteCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => CanWrite() && HasSelection(), onError);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => _undo is not null && !IsLifecycleBusy, onError);
        OpenTrashCommand = new AsyncRelayCommand(OpenTrashAsync, () => IsActivated && !IsTrashOpen, onError);
        CloseTrashCommand = new RelayCommand(CloseTrash, () => IsTrashOpen);
        Trash = new WorkspaceTrashViewModel(this);
        ArchiveProjectCommand = new AsyncRelayCommand(ArchiveSelectedProjectAsync, () => CanWrite() && SelectedNode?.IsProject == true, onError);
        ArchiveProjectsInFolderCommand = new AsyncRelayCommand(ArchiveProjectsInFolderAsync, () => CanWrite() && SelectedNode?.IsFolder == true, onError);
        ArchiveTopLevelProjectsCommand = new AsyncRelayCommand(ArchiveTopLevelProjectsAsync, CanWrite, onError);
        ManageTagsCommand = new AsyncRelayCommand(ManageTagsAsync, CanWrite, onError);
        ShowBulkArchiveDetailsCommand = new RelayCommand(ShowBulkArchiveDetails, () => LastBulkArchiveDetails is not null);
        OpenArchivedCommand = new AsyncRelayCommand(OpenArchivedAsync, () => IsActivated && !IsArchivedOpen, onError);
        CloseArchivedCommand = new RelayCommand(() =>
        {
            InvalidateReveal(); // Back is the user's own action: a navigation still resolving never reopens the panel
            CloseArchived();
        }, () => IsArchivedOpen);
        Archived = new ArchivedProjectsViewModel(this);
        ArchiveFolderCommand = new AsyncRelayCommand(ArchiveSelectedFolderAsync, () => CanWrite() && SelectedNode?.IsFolder == true, onError);
        TrashFolderCommand = new AsyncRelayCommand(TrashSelectedFolderAsync, () => CanWrite() && SelectedNode?.IsFolder == true, onError);
        RetryLoadCommand = new AsyncRelayCommand(RetryLoadAsync, () => RootLoadError is not null && !IsLoadingRoots, onError);
        DismissBannerCommand = new RelayCommand(() => BannerMessage = null);
        Roots.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<WorkspaceNodeViewModel> Roots { get; } = [];

    /// <summary>The Workspace search box and its results; null when search is not wired.</summary>
    public WorkspaceSearchViewModel? Search { get; }

    public AsyncRelayCommand NewFolderCommand { get; }

    public AsyncRelayCommand NewProjectCommand { get; }

    public AsyncRelayCommand RenameCommand { get; }

    public AsyncRelayCommand EditProjectCommand { get; }

    public AsyncRelayCommand MoveCommand { get; }

    /// <summary>Move the selected Project to the Workspace Trash (confirmed).</summary>
    public AsyncRelayCommand TrashProjectCommand { get; }

    /// <summary>Permanently remove the selected Workspace Folder if it is truly empty (confirmed; D2).</summary>
    public AsyncRelayCommand RemoveFolderCommand { get; }

    /// <summary>Del in the tree: Project → Move to Trash; Folder → Remove folder (D10).</summary>
    public AsyncRelayCommand DeleteCommand { get; }

    /// <summary>Restores the Project the latest Move to Trash of this visit put in the Trash (D9).</summary>
    public AsyncRelayCommand UndoCommand { get; }

    /// <summary>Opens the Workspace Trash ("Trashed projects (n)") in the right pane (D6).</summary>
    public AsyncRelayCommand OpenTrashCommand { get; }

    /// <summary>Back: closes the Workspace Trash and shows the unchanged selection again.</summary>
    public RelayCommand CloseTrashCommand { get; }

    public WorkspaceTrashViewModel Trash { get; }

    /// <summary>Archive the selected Project (confirmed; Phase 12). Toolbar and context menu only: no keyboard shortcut (A13).</summary>
    public AsyncRelayCommand ArchiveProjectCommand { get; }

    /// <summary>Phase 19: "Archive projects…" on the selected Workspace Folder (its direct live Projects; checklist).</summary>
    public AsyncRelayCommand ArchiveProjectsInFolderCommand { get; }

    /// <summary>Phase 19: "Archive projects at the top level…" (the top-level live Projects; checklist).</summary>
    public AsyncRelayCommand ArchiveTopLevelProjectsCommand { get; }

    /// <summary>Phase 21: "Manage tags…" (rename and merge a tag across Projects). Disabled while a lifecycle write runs.</summary>
    public AsyncRelayCommand ManageTagsCommand { get; }

    /// <summary>Phase 19: the banner's Details for the last Bulk Archive or Bulk Undo (one line per Project).</summary>
    public RelayCommand ShowBulkArchiveDetailsCommand { get; }

    /// <summary>Phase 19: the per-Project lines of the last Bulk Archive or Bulk Undo; null before the first one.</summary>
    public IReadOnlyList<string>? LastBulkArchiveDetails
    {
        get => _lastBulkArchiveDetails;
        private set
        {
            if (SetProperty(ref _lastBulkArchiveDetails, value))
                ShowBulkArchiveDetailsCommand.RaiseCanExecuteChanged();
        }
    }

    private IReadOnlyList<string>? _lastBulkArchiveDetails;

    /// <summary>Opens the Archived projects panel ("Archived projects (n)") in the right pane (A4).</summary>
    public AsyncRelayCommand OpenArchivedCommand { get; }

    /// <summary>Back: closes the Archived projects panel and shows the unchanged selection again.</summary>
    public RelayCommand CloseArchivedCommand { get; }

    public ArchivedProjectsViewModel Archived { get; }

    /// <summary>Archive the selected Folder and its whole subtree (Phase 13; confirmed with a token-checked preview, §7.4).</summary>
    public AsyncRelayCommand ArchiveFolderCommand { get; }

    /// <summary>Move the selected Folder and its whole subtree to the Trash (confirmed; §7.4). Del on a non-empty Folder (D12).</summary>
    public AsyncRelayCommand TrashFolderCommand { get; }

    public bool IsArchivedOpen
    {
        get => _isArchivedOpen;
        private set
        {
            if (!SetProperty(ref _isArchivedOpen, value))
                return;
            OpenArchivedCommand.RaiseCanExecuteChanged();
            CloseArchivedCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsPanelOpen));
            OnInfoAvailabilityChanged();
        }
    }

    /// <summary>Phase 13A Workspace preferences; null when settings are not wired (every toolbar is then shown).</summary>
    public WorkspacePreferencesViewModel? Preferences { get; }

    /// <summary>The tree toolbar is shown; when hidden, a More (⋯) button beside search, the tree menu and keys remain.</summary>
    public bool ShowTreeToolbar => Preferences?.ShowTreeToolbar ?? true;

    /// <summary>The selection toolbar (Edit / Move / Archive / Trash) is shown; More, Info, menus and keys always remain.</summary>
    public bool ShowSelectionToolbar => Preferences?.ShowSelectionToolbar ?? true;

    /// <summary>The content toolbar is shown; when hidden, the breadcrumb, a More (⋯) button, the list menu and keys remain.</summary>
    public bool ShowContentToolbar => Preferences?.ShowContentToolbar ?? true;

    /// <summary>Info can be opened: something is selected and no panel replaces the selection view.</summary>
    public bool CanOpenInfo => SelectedNode is not null && !IsPanelOpen;

    /// <summary>The UI context the view model was created on (null in tests without one).</summary>
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    /// <summary>Phase 18: "Save notes automatically" for the note editors opened from this area; null = off (no settings wired).</summary>
    private readonly Func<bool>? _noteAutoSave;

    /// <summary>
    /// The Info drawer (Phase 13A §5.4). It follows the selection: it shows whatever is selected now, and closes when nothing
    /// is selected or the Trash / Archived panel opens. Session-only.
    /// </summary>
    public bool IsInfoOpen
    {
        get => _isInfoOpen;
        set
        {
            if (!SetProperty(ref _isInfoOpen, value && CanOpenInfo))
                return;
            ToggleInfoCommand.RaiseCanExecuteChanged();
            CloseInfoCommand.RaiseCanExecuteChanged();
            StartFolderInfoLoad();
        }
    }

    /// <summary>Ctrl+I, the Info button and the menus.</summary>
    public RelayCommand ToggleInfoCommand { get; }

    /// <summary>✕ and Esc in the drawer.</summary>
    public RelayCommand CloseInfoCommand { get; }

    /// <summary>Counts for the selected Folder while Info is open (null otherwise or while loading).</summary>
    public WorkspaceNodeState? SelectedFolderInfo
    {
        get => _selectedFolderInfo;
        private set => SetProperty(ref _selectedFolderInfo, value);
    }

    /// <summary>The most recent folder-info load; lets tests await it.</summary>
    internal Task CurrentFolderInfoLoad { get; private set; } = Task.CompletedTask;

    private void OnInfoAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanOpenInfo));
        if (IsPanelOpen)
        {
            IsInfoOpen = false; // a panel replaces the selection view: close now
        }
        else if (SelectedNode is null)
        {
            // A tree selection change can deselect the old node before the new one is selected (a transient "nothing
            // selected"). Info closes only if the selection is still empty once the current UI work item has finished.
            if (_ui is null)
                IsInfoOpen = false;
            else
                _ui.Post(_ => { if (SelectedNode is null) IsInfoOpen = false; }, null);
        }
        else
        {
            StartFolderInfoLoad();
        }

        ToggleInfoCommand.RaiseCanExecuteChanged();
    }

    private void StartFolderInfoLoad()
    {
        var version = ++_folderInfoVersion;
        SelectedFolderInfo = null;
        if (!IsInfoOpen || SelectedNode is not { IsFolder: true } folder)
        {
            CurrentFolderInfoLoad = Task.CompletedTask;
            return;
        }

        CurrentFolderInfoLoad = LoadFolderInfoAsync(folder.Id, version);
    }

    private async Task LoadFolderInfoAsync(long folderId, int version)
    {
        try
        {
            var state = await _workspace.GetNodeStateAsync(folderId);
            if (version == _folderInfoVersion)
                SelectedFolderInfo = state;
        }
        catch (Exception ex)
        {
            _log.Error("Loading folder info failed.", ex); // read only; the drawer shows the location without counts
        }
    }

    /// <summary>The Workspace Trash or the Archived projects panel replaces the details and content (one at a time).</summary>
    public bool IsPanelOpen => IsTrashOpen || IsArchivedOpen;

    public bool IsTrashOpen
    {
        get => _isTrashOpen;
        private set
        {
            if (!SetProperty(ref _isTrashOpen, value))
                return;
            OpenTrashCommand.RaiseCanExecuteChanged();
            CloseTrashCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsPanelOpen));
            OnInfoAvailabilityChanged();
        }
    }

    /// <summary>A lifecycle write is pending (the slot is taken).</summary>
    public bool IsLifecycleBusy
    {
        get => _isLifecycleBusy;
        private set
        {
            if (!SetProperty(ref _isLifecycleBusy, value))
                return;
            RaiseWriteCommands();
            Trash.RaiseCommands();
            Archived.RaiseCommands();
        }
    }

    /// <summary>The banner offers Undo (the latest Move to Trash of the current visit).</summary>
    public bool IsUndoAvailable => _undo is not null;

    internal WorkspaceService Workspace => _workspace;

    internal IAppLog Log => _log;

    /// <summary>Phase 20: the Archived panel shows its Bulk Unarchive Details through the same dialog service.</summary>
    internal IDialogService Dialogs => _dialogs;

    /// <summary>Lifecycle results applied so far (Trash, Restore, Undo, Remove, stale-node reconciliation).</summary>
    internal int LifecycleCompletions => _lifecycleCompletions;

    /// <summary>The most recent lifecycle operation; lets tests await it.</summary>
    internal Task CurrentLifecycleOperation { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent stale-node reconciliation; lets tests await it.</summary>
    internal Task CurrentReconciliation { get; private set; } = Task.CompletedTask;

    public AsyncRelayCommand RetryLoadCommand { get; }

    public RelayCommand DismissBannerCommand { get; }

    /// <summary>Bound to the Workspace tab. The first selection starts the one root load.</summary>
    public bool IsTabSelected
    {
        get => _isTabSelected;
        set
        {
            if (!SetProperty(ref _isTabSelected, value))
                return;
            if (value)
            {
                _ = ActivateAsync();
            }
            else
            {
                InvalidateReveal(); // leaving the tab cancels a reveal in flight
                EndVisit(); // committed lifecycle results still reconcile the tree; only Undo and its selection are visit-bound
            }
        }
    }

    /// <summary>True once the root nodes have loaded.</summary>
    public bool IsActivated
    {
        get => _isActivated;
        private set
        {
            if (!SetProperty(ref _isActivated, value))
                return;
            OnPropertyChanged(nameof(IsEmpty));
            RaiseWriteCommands();
            OpenTrashCommand.RaiseCanExecuteChanged();
            OpenArchivedCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoadingRoots
    {
        get => _isLoadingRoots;
        private set
        {
            if (SetProperty(ref _isLoadingRoots, value))
                RetryLoadCommand.RaiseCanExecuteChanged();
        }
    }

    public string? RootLoadError
    {
        get => _rootLoadError;
        private set
        {
            if (SetProperty(ref _rootLoadError, value))
                RetryLoadCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmpty => IsActivated && Roots.Count == 0;

    public WorkspaceNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (value?.IsPlaceholder == true || ReferenceEquals(_selectedNode, value))
                return;
            if (!_applyingReveal && !_quietSelection)
                InvalidateReveal(); // a manual selection wins over a reveal still resolving
            if (value is not null && IsTrashOpen)
                CloseTrash(); // selecting a tree node shows that node instead of the Workspace Trash
            if (value is not null && IsArchivedOpen)
                CloseArchived(); // ... or instead of the Archived projects panel

            _selectionVersion++;
            var previous = _selectedNode;
            _selectedNode = value;
            if (previous is not null)
            {
                previous.IsSelected = false;
                previous.PropertyChanged -= OnSelectedNodeChanged;
            }

            if (value is not null)
            {
                value.IsSelected = true;
                value.PropertyChanged += OnSelectedNodeChanged;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFolderSelected));
            OnPropertyChanged(nameof(SelectedLocation));
            RaiseWriteCommands();
            StartDetailsLoad();
            UpdateContent();
            OnInfoAvailabilityChanged(); // Info follows the selection, or closes when nothing is selected
        }
    }

    public bool IsFolderSelected => SelectedNode?.IsFolder == true;

    /// <summary>Where the selected node is: its parent folder's path, or "Workspace" at the top level (Phase 13A title line).</summary>
    public string? SelectedLocation => SelectedNode is null ? null : SelectedNode.Parent?.Path ?? "Workspace";

    private void OnSelectedNodeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceNodeViewModel.Parent) or nameof(WorkspaceNodeViewModel.Path) or nameof(WorkspaceNodeViewModel.Name) or null)
            OnPropertyChanged(nameof(SelectedLocation));
    }

    /// <summary>Content (ProjectFolders and Links) of the selected Project; null for Folders, no selection, or when content is not wired.</summary>
    public ProjectContentViewModel? SelectedContent
    {
        get => _selectedContent;
        private set => SetProperty(ref _selectedContent, value);
    }

    /// <summary>Keeps the content view while the same Project stays selected (rename, edit, move); replaces it otherwise.</summary>
    private void UpdateContent()
    {
        var project = SelectedNode is { IsProject: true } node ? node : null;
        if (project is not null && SelectedContent?.ProjectNodeId == project.Id)
            return;

        SelectedContent?.Deactivate();
        if (project is null || _content is null || _checker is null)
        {
            SelectedContent = null;
            return;
        }

        var content = new ProjectContentViewModel(_content, _checker, _dialogs, _log, _checkCache, project.Id, project.Name, noteAutoSave: _noteAutoSave);
        var projectId = project.Id;
        content.ProjectTrashedObserved = () => _ = ReconcileNodeAsync(projectId); // trashed by another window
        content.ProjectArchivedObserved = () => _ = ReconcileNodeAsync(projectId); // archived by another window (its listing says so)
        content.DestinationRequested = item => CurrentDestinationReveal = RevealDestinationAsync(item); // Phase 15: Open destination
        SelectedContent = content;
        _ = content.ActivateAsync();
    }

    /// <summary>Details of the selected Project, once loaded (null for Folders and while loading).</summary>
    public ProjectDetails? SelectedProject
    {
        get => _selectedProject;
        private set
        {
            if (!SetProperty(ref _selectedProject, value))
                return;
            OnPropertyChanged(nameof(SelectedProjectStatus));
            OnPropertyChanged(nameof(SelectedProjectTags));
            OnPropertyChanged(nameof(HasSelectedProjectTags));
        }
    }

    public string? SelectedProjectStatus => SelectedProject is null ? null : ProjectStatuses.DisplayName(SelectedProject.Status);

    /// <summary>The selected Project's tags as Info-drawer chips (Phase 17); a chip searches the Workspace for its display name.</summary>
    public IReadOnlyList<TagChipViewModel> SelectedProjectTags =>
        SelectedProject?.Tags.Select(t => new TagChipViewModel(t.Name, new RelayCommand(() => SearchByTag(t.Name)))).ToList() ?? [];

    public bool HasSelectedProjectTags => SelectedProject is { Tags.Count: > 0 };

    /// <summary>
    /// A chip click in Info (Phase 17 §4.4): the exact display name replaces the search text and the normal search runs with the
    /// normal word splitting (no exact-match or <c>tag:</c> syntax).
    /// </summary>
    public void SearchByTag(string name)
    {
        if (Search is not null)
            Search.SearchText = name;
    }

    public bool IsLoadingDetails { get => _isLoadingDetails; private set => SetProperty(ref _isLoadingDetails, value); }

    /// <summary>"No longer exists" / load failure for the CURRENT selection only.</summary>
    public string? DetailsMessage { get => _detailsMessage; private set => SetProperty(ref _detailsMessage, value); }

    /// <summary>The banner text. The Undo button sits in this banner: dismissing or replacing its message withdraws the Undo.</summary>
    public string? BannerMessage
    {
        get => _bannerMessage;
        private set
        {
            if (!SetProperty(ref _bannerMessage, value))
                return;
            if (_undo is not null && value != _undo.Message)
                ClearUndo();
            OnPropertyChanged(nameof(IsBulkDetailsAvailable));
        }
    }

    /// <summary>Phase 19: the banner shows a Bulk Archive or Bulk Undo summary, so its Details button is offered.</summary>
    public bool IsBulkDetailsAvailable => LastBulkArchiveDetails is not null && _bulkSummaryMessage is not null && BannerMessage == _bulkSummaryMessage;

    private string? _bulkSummaryMessage;

    /// <summary>The most recent details load; lets tests await it.</summary>
    internal Task CurrentDetailsLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The root load (completed when none was started).</summary>
    internal Task RootLoad => _rootLoad ?? Task.CompletedTask;

    /// <summary>Starts the root load once. Later calls (tab switches) reuse it and never reload.</summary>
    public Task ActivateAsync() => _rootLoad ??= LoadRootsAsync();

    // ---- Loading ----------------------------------------------------------------------------------------------------

    private Task RetryLoadAsync()
    {
        InvalidateReveal();
        EndVisit();
        return _rootLoad = LoadRootsAsync();
    }

    private async Task LoadRootsAsync()
    {
        IsLoadingRoots = true;
        RootLoadError = null;
        var mark = BeginTrackedRead();
        try
        {
            var roots = await _workspace.ListChildrenAsync(null);
            var held = ReplaceChildren(Roots, null, roots.Nodes, _revealSequence, mark);
            IsActivated = true;
            RequestConfirmations(held); // D10 rule C: rows held back because a newer read-derived update moved them
            _ = Trash.RefreshCount();
            _ = Archived.RefreshCount();
        }
        catch (Exception ex)
        {
            _log.Error("Workspace root load failed.", ex);
            RootLoadError = "Workspace could not be loaded. Nothing was changed.";
        }
        finally
        {
            IsLoadingRoots = false;
            EndTrackedRead(mark);
        }
    }

    internal void OnExpanded(WorkspaceNodeViewModel node) => _ = EnsureChildrenLoadedAsync(node);

    /// <summary>Loads a Folder's children once; concurrent callers share the load in flight. False if it failed.</summary>
    internal Task<bool> EnsureChildrenLoadedAsync(WorkspaceNodeViewModel node)
    {
        if (!node.IsFolder || node.IsChildrenLoaded)
            return Task.FromResult(node.IsFolder);
        if (node.ChildrenLoad is { } inFlight)
            return inFlight;

        var load = LoadChildrenAsync(node);
        if (!load.IsCompleted)
            node.ChildrenLoad = load;
        return load;
    }

    private async Task<bool> LoadChildrenAsync(WorkspaceNodeViewModel node)
    {
        var reconcileStamp = node.ReconcileStamp;
        var revealSequence = _revealSequence;
        var mark = BeginTrackedRead();
        try
        {
            var children = await _workspace.ListChildrenAsync(node.Id);
            if (!IsRegistered(node))
                return false; // §7.9 C2: its subtree left the tree meanwhile (or a newer instance took its Id): discard, register nothing
            switch (children.Status)
            {
                case ChildrenReadStatus.ParentNotLive:
                    // §7.9 C3: archived or trashed (itself or above) — the rows are never applied; the branch leaves the tree.
                    if (!IsTouchedSince(node.Id, revealSequence))
                        ApplyNotLive(node.Id, children.ChainFromRoot, children.ParentState!, mark);
                    return false;
                case ChildrenReadStatus.ParentMissing:
                    if (!IsTouchedSince(node.Id, revealSequence) && RemoveMissingByRead(node.Id, mark) is null)
                        BannerMessage = "This folder no longer exists.";
                    return false;
            }

            if (node.ReconcileStamp != reconcileStamp)
                return node.IsChildrenLoaded; // a reveal reconciled this branch to a newer snapshot meanwhile: never undo it
            var held = ReplaceChildren(node.Children, node, children.Nodes, revealSequence, mark);
            node.IsChildrenLoaded = true;
            RequestConfirmations(held); // D10 rule C: rows held back because a newer read-derived update changed them
            return true;
        }
        catch (Exception ex)
        {
            if (!IsRegistered(node))
                return false; // a forgotten instance's failure is nobody's concern (C2)
            ReportError($"The contents of “{node.Name}” could not be loaded.", ex);
            node.CollapseAfterFailedLoad();
            return false;
        }
        finally
        {
            node.ChildrenLoad = null;
            EndTrackedRead(mark);
        }
    }

    /// <summary>The instance is still the one registered for its Id (§7.9 C2): only such an instance may apply a load result.</summary>
    private bool IsRegistered(WorkspaceNodeViewModel node) => _nodes.TryGetValue(node.Id, out var current) && ReferenceEquals(current, node);

    /// <summary>The instance registered for the Id, if any (tests: C1 forgets a subtree, C2 registers nothing late).</summary>
    internal WorkspaceNodeViewModel? CachedNode(long id) => _nodes.GetValueOrDefault(id);

    /// <param name="revealSequence">The reveal sequence when the load STARTED: rows of nodes that a later reveal placed or
    /// removed are skipped unless the node already sits in this branch, so an older lazy load can neither pull a revealed
    /// node back to its former parent nor resurrect one the reveal found gone or elsewhere.</param>
    /// <param name="readMark">Phase 19 (D10): the load's read mark. A row whose node a read-derived result changed after that mark,
    /// and that this load would show differently (not in this branch now), is held back (rule C) and returned, so the caller
    /// requests one confirmation read for it once the branch is marked loaded. A cached instance this load moves out of another
    /// branch gets a read-update stamp (never a committed stamp: a lazy load is not a committed write).</param>
    /// <returns>The held-back node Ids.</returns>
    private IReadOnlyList<long> ReplaceChildren(ObservableCollection<WorkspaceNodeViewModel> target, WorkspaceNodeViewModel? parent, IReadOnlyList<WorkspaceNode> loaded, int revealSequence, ReadMark readMark)
    {
        var present = target.Where(c => !c.IsPlaceholder).Select(c => c.Id).ToHashSet();
        loaded = loaded.Where(n => !_touchedByReveal.TryGetValue(n.Id, out var seq) || seq <= revealSequence || present.Contains(n.Id)).ToList();
        var held = loaded.Where(n => !present.Contains(n.Id) && ReadUpdatedSince(n.Id, readMark)).Select(n => n.Id).ToList();
        if (held.Count > 0)
            loaded = loaded.Where(n => !held.Contains(n.Id)).ToList(); // rule C: the row's data is discarded (R2d)
        // Keep anything merged while this load was pending (e.g. a node created meanwhile), then the loaded rows; by Id.
        var merged = target.Where(c => !c.IsPlaceholder && loaded.All(n => n.Id != c.Id)).ToList();
        var moved = new List<long>();
        target.Clear();
        foreach (var node in loaded)
        {
            var wasCached = _nodes.ContainsKey(node.Id);
            var vm = GetOrCreate(node, parent);
            if (wasCached && !ReferenceEquals(CollectionOf(vm), target))
                moved.Add(node.Id);
            Detach(vm, except: target); // a cached instance found under another parent moves here; it never appears twice
            vm.Parent = parent;
            target.Add(vm);
        }

        foreach (var extra in merged)
            InsertSorted(target, extra);
        if (moved.Count > 0)
            StampRead(moved, countCompletion: false);
        return held;
    }

    /// <summary>The cached instance for the node (name and child flag refreshed), or a new one under <paramref name="parent"/>.
    /// Never changes the placement of an existing instance; callers place it.</summary>
    private WorkspaceNodeViewModel GetOrCreate(WorkspaceNode node, WorkspaceNodeViewModel? parent)
    {
        if (_nodes.TryGetValue(node.Id, out var existing))
        {
            existing.Name = node.Name;
            existing.SetHasChildren(node.HasChildren);
            return existing;
        }

        var created = new WorkspaceNodeViewModel(this, node, parent) { RegisteredAt = ++_registrations };
        _nodes[node.Id] = created;
        return created;
    }

    /// <summary>The collection an instance is placed in according to its Parent (Roots for top-level nodes).</summary>
    private ObservableCollection<WorkspaceNodeViewModel> CollectionOf(WorkspaceNodeViewModel node) => node.Parent?.Children ?? Roots;

    /// <summary>Removes the instance from its current collection unless that is <paramref name="except"/>.</summary>
    private void Detach(WorkspaceNodeViewModel node, ObservableCollection<WorkspaceNodeViewModel>? except = null)
    {
        var current = CollectionOf(node);
        if (!ReferenceEquals(current, except))
            current.Remove(node);
    }

    /// <summary>
    /// One-placement invariant: puts the instance under <paramref name="parent"/> at its sorted position, removing it from
    /// its former collection first. An instance already in place is moved only if its sort position changed (e.g. renamed
    /// elsewhere), so its identity, expansion and loaded children are kept.
    /// </summary>
    private void Place(WorkspaceNodeViewModel node, WorkspaceNodeViewModel? parent)
    {
        var target = parent?.Children ?? Roots;
        Detach(node, except: target);
        node.Parent = parent;
        var index = target.IndexOf(node);
        if (index < 0)
        {
            InsertSorted(target, node);
            return;
        }

        var sorted = target.Count(s => !ReferenceEquals(s, node) && !s.IsPlaceholder && WorkspaceNodeOrder.Instance.Compare(s.SortKey, node.SortKey) < 0);
        if (sorted != index)
            target.Move(index, sorted);
    }

    // ---- Selection and details --------------------------------------------------------------------------------------

    internal void OnSelectionChanged(WorkspaceNodeViewModel node, bool isSelected)
    {
        if (isSelected)
            SelectedNode = node;
        else if (ReferenceEquals(SelectedNode, node))
            SelectedNode = null;
    }

    private void StartDetailsLoad()
    {
        var version = ++_detailsVersion;
        _detailsCts?.Cancel();
        _detailsCts = null;
        SelectedProject = null;
        DetailsMessage = null;

        if (SelectedNode is not { IsProject: true } node)
        {
            IsLoadingDetails = false;
            CurrentDetailsLoad = Task.CompletedTask;
            return;
        }

        IsLoadingDetails = true;
        var cts = _detailsCts = new CancellationTokenSource();
        CurrentDetailsLoad = LoadDetailsAsync(node.Id, version, BeginTrackedRead(), cts.Token);
    }

    private async Task LoadDetailsAsync(long nodeId, int version, ReadMark mark, CancellationToken token)
    {
        try
        {
            var details = await _workspace.GetProjectAsync(nodeId, token);
            if (version != _detailsVersion)
                return; // a newer selection or saved state owns the pane now
            if (details is null)
            {
                DetailsMessage = "This project no longer exists.";
            }
            else if (!details.Effective.IsLive)
            {
                // Trashed or archived elsewhere, itself or through a Folder: the EFFECTIVE root decides the removed branch and the
                // notice (Trash dominates; a Project-only state is its own root). A result stale for any node of that chain is not
                // applied; the details are read again.
                if (!ApplyNotLive(nodeId, details.ChainFromRoot, details.Effective, mark) && SelectedNode?.Id == nodeId)
                    StartDetailsLoad();
            }
            else
            {
                SelectedProject = details;
            }
        }
        catch (Exception ex)
        {
            if (version != _detailsVersion)
                return; // stale failures never show against a different selection
            if (ex is not OperationCanceledException)
                _log.Error("Project details could not be loaded.", ex);
            DetailsMessage = "Project details could not be loaded.";
        }
        finally
        {
            if (version == _detailsVersion)
                IsLoadingDetails = false;
            EndTrackedRead(mark);
        }
    }

    /// <summary>Shows freshly saved details and invalidates any older load still in flight.</summary>
    private void ShowSavedDetails(ProjectDetails saved)
    {
        if (SelectedNode?.Id != saved.NodeId)
            return;
        _detailsVersion++;
        _detailsCts?.Cancel();
        _detailsCts = null;
        DetailsMessage = null;
        IsLoadingDetails = false;
        SelectedProject = saved;
        CurrentDetailsLoad = Task.CompletedTask;
    }

    // ---- Commands -----------------------------------------------------------------------------------------------------

    private bool HasSelection() => SelectedNode is not null;

    /// <summary>New items go into the selected Folder, beside the selected Project, or at the root.</summary>
    private WorkspaceNodeViewModel? CreationParent() => SelectedNode switch
    {
        { IsFolder: true } folder => folder,
        { IsProject: true } project => project.Parent,
        _ => null,
    };

    private bool CanWrite() => IsActivated && !IsLifecycleBusy;

    private void RaiseWriteCommands()
    {
        NewFolderCommand.RaiseCanExecuteChanged();
        NewProjectCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        EditProjectCommand.RaiseCanExecuteChanged();
        MoveCommand.RaiseCanExecuteChanged();
        TrashProjectCommand.RaiseCanExecuteChanged();
        ArchiveProjectCommand.RaiseCanExecuteChanged();
        ArchiveProjectsInFolderCommand.RaiseCanExecuteChanged();
        ArchiveTopLevelProjectsCommand.RaiseCanExecuteChanged();
        ManageTagsCommand.RaiseCanExecuteChanged();
        RemoveFolderCommand.RaiseCanExecuteChanged();
        ArchiveFolderCommand.RaiseCanExecuteChanged();
        TrashFolderCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Every Workspace write starts here: a reveal in flight is cancelled and an offered Undo is withdrawn.</summary>
    private void BeginWorkspaceWrite()
    {
        InvalidateReveal();
        ClearUndo();
    }

    // ---- Phase 21: Manage tags (proposal Revision 2 + R2a §3.1) ---------------------------------------------------------------
    // A tag change writes only ProjectTags: the tree, the selection, the banner and an offered Undo are left as they are (TR7).
    // The dialog applies each confirmed change while it holds the lifecycle slot; the selected Project's details are re-read after
    // a committed change.

    private async Task ManageTagsAsync()
    {
        if (!CanWrite())
            return;
        var dialog = new ManageTagsViewModel(_workspace, TryBeginTagWrite, () => IsLifecycleBusy = false, _log);
        _dialogs.ShowManageTags(dialog);
        if (dialog.ChangedAny && SelectedNode is { IsProject: true })
        {
            StartDetailsLoad();
            await CurrentDetailsLoad; // Phase 24 (D7): completes BEFORE a Show in Workspace reveal starts
        }

        if (dialog.RevealTarget is { } projectNodeId)
            await RevealTaggedProjectAsync(projectNodeId);
    }

    /// <summary>
    /// Phase 24 Show in Workspace (proposal §3.5): the Project is found by its id and revealed at its CURRENT place through the same
    /// fresh resolve as a search result (moved → its new place; archived → Archived; changed twice → WorkspaceChanged). Never writes.
    /// </summary>
    private async Task RevealTaggedProjectAsync(long projectNodeId)
    {
        var outcome = await RevealAsync(new SearchItemRef(SearchResultKind.Project, projectNodeId));
        BannerMessage = outcome switch
        {
            WorkspaceRevealOutcome.Selected or WorkspaceRevealOutcome.Superseded => BannerMessage,
            WorkspaceRevealOutcome.Archived => "The project is archived — it is selected under Archived.",
            WorkspaceRevealOutcome.InArchivedFolder => "The project is in an archived folder — that folder is selected under Archived.",
            WorkspaceRevealOutcome.NotFound => "The project no longer exists.",
            WorkspaceRevealOutcome.Trashed or WorkspaceRevealOutcome.ProjectTrashed => "The project is in the Trash.",
            WorkspaceRevealOutcome.FolderTrashed => "The project's folder is in the Trash.",
            WorkspaceRevealOutcome.WorkspaceChanged or WorkspaceRevealOutcome.NoLongerArchived or WorkspaceRevealOutcome.MovedMeanwhile
                or WorkspaceRevealOutcome.ContainerMissing => "The project was moved or changed meanwhile. Try Show in Workspace again.",
            WorkspaceRevealOutcome.Busy => "The database stayed busy (another program or window is writing). Try again.",
            _ => "The project could not be shown. Try again.",
        };
    }

    /// <summary>Takes the lifecycle slot for one tag change (false while another lifecycle write holds it: nothing is written).</summary>
    private bool TryBeginTagWrite()
    {
        if (!CanWrite())
            return false;
        IsLifecycleBusy = true;
        return true;
    }

    /// <summary>Tests (TR14): takes the lifecycle slot as another lifecycle write would, and returns its release.</summary>
    internal Action HoldLifecycleSlotForTests()
    {
        IsLifecycleBusy = true;
        return () => IsLifecycleBusy = false;
    }

    private async Task NewFolderAsync()
    {
        BeginWorkspaceWrite();
        var parent = CreationParent();
        var editor = WorkspaceNameEditorViewModel.ForNewFolder(_workspace, parent?.Id, parent?.Path);
        if (_dialogs.ShowWorkspaceNameEditor(editor) && editor.CreatedNode is { } created)
            await ApplyCreatedAsync(parent, created);
        else if (editor.Refusal is WorkspaceFailure.ParentNotFound or WorkspaceFailure.AncestorNotLive && parent is not null)
            await ReconcileNodeAsync(parent.Id, "The destination folder no longer exists. Nothing was created.");
    }

    private async Task NewProjectAsync()
    {
        BeginWorkspaceWrite();
        var parent = CreationParent();
        var editor = ProjectEditorViewModel.ForCreate(_workspace, parent?.Id, parent?.Path);
        if (_dialogs.ShowProjectEditor(editor) && editor.CreatedNode is { } created)
            await ApplyCreatedAsync(parent, created);
        else if (editor.Refusal is WorkspaceFailure.ParentNotFound or WorkspaceFailure.AncestorNotLive && parent is not null)
            await ReconcileNodeAsync(parent.Id, "The destination folder no longer exists. Nothing was created.");
    }

    private async Task RenameAsync()
    {
        BeginWorkspaceWrite();
        var node = SelectedNode!;
        var editor = WorkspaceNameEditorViewModel.ForRename(_workspace, node.Id, node.Type, node.Name, node.Parent?.Path);
        if (_dialogs.ShowWorkspaceNameEditor(editor) && editor.SavedName is { } name)
        {
            ApplyName(node, name);
            if (ReferenceEquals(SelectedNode, node) && node.IsProject)
                StartDetailsLoad(); // name and UpdatedAt changed
        }
        else if (editor.Refusal is WorkspaceFailure.ProjectTrashed or WorkspaceFailure.ProjectArchived or WorkspaceFailure.NodeNotFound or WorkspaceFailure.AncestorNotLive)
        {
            await ReconcileNodeAsync(node.Id); // the dialog kept its values; now a fresh typed read decides
        }
    }

    private async Task EditProjectAsync()
    {
        BeginWorkspaceWrite();
        var node = SelectedNode!;
        // Edit is available as soon as a Project is selected; it reads the current state itself if the pane is still loading.
        var mark = BeginTrackedRead();
        ProjectDetails? current;
        try
        {
            current = SelectedProject is { } shown && shown.NodeId == node.Id ? shown : await _workspace.GetProjectAsync(node.Id);
            if (!ReferenceEquals(SelectedNode, node))
                return; // the selection changed while the project was loading
            if (current is null)
            {
                BannerMessage = "This project no longer exists.";
                return;
            }

            if (!current.Effective.IsLive)
            {
                // Never open the editor on a Project that is not live. The EFFECTIVE root decides the removed branch and the notice
                // (Trash dominates; a Project-only state is its own root); a result stale for that chain (or held back for a
                // confirmation read, D10 rule C) changes nothing here.
                if (!ApplyNotLive(node.Id, current.ChainFromRoot, current.Effective, mark))
                    BannerMessage = "The workspace changed while this project was being read. Nothing was changed; try again.";
                return;
            }
        }
        finally
        {
            EndTrackedRead(mark);
        }

        var editor = ProjectEditorViewModel.ForEdit(_workspace, current, node.Parent?.Path);
        if (_dialogs.ShowProjectEditor(editor) && editor.SavedDetails is { } saved)
        {
            ApplyName(node, saved.Name);
            ShowSavedDetails(saved);
        }
        else if (editor.Refusal is WorkspaceFailure.ProjectTrashed or WorkspaceFailure.ProjectArchived or WorkspaceFailure.NodeNotFound or WorkspaceFailure.AncestorNotLive)
        {
            await ReconcileNodeAsync(node.Id);
        }
    }

    private async Task MoveAsync()
    {
        BeginWorkspaceWrite();
        var node = SelectedNode!;
        var targets = await _workspace.ListMoveTargetsAsync(node.Id);
        if (!ReferenceEquals(SelectedNode, node))
            return; // the selection changed while destinations were loading

        var editor = new WorkspaceMoveViewModel(_workspace, node.Id, node.Name, node.Parent?.Id, targets);
        if (_dialogs.ShowWorkspaceMove(editor) && editor.Moved)
            await ApplyMovedAsync(node, editor.MovedTo);
        else if (editor.Refusal is WorkspaceFailure.ProjectTrashed or WorkspaceFailure.ProjectArchived or WorkspaceFailure.NodeNotFound or WorkspaceFailure.AncestorNotLive)
            await ReconcileNodeAsync(node.Id);
        else if (editor.Refusal is WorkspaceFailure.ParentNotFound or WorkspaceFailure.DestinationNotLive && editor.RefusedDestinationId is { } destination)
            await ReconcileNodeAsync(destination, "The destination folder no longer exists. Nothing was moved.");
    }

    // ---- Workspace lifecycle: Project Trash, Undo, Restore, empty-folder removal (Phase 8 proposal revision 2 §8) --------
    //
    // Timing: nothing in the UI changes before the store commits. Results are applied on the UI thread, by node Id, whenever
    // they arrive (also after the tab was left): the tree is a projection of committed state. Applying one first touches the
    // node (lazy loads that started earlier skip it) and counts a lifecycle completion (reveals and Trash reads that started
    // earlier are re-read). Only Undo and its automatic selection are bound to the Workspace visit.

    /// <summary>Del: Project → Move to Trash; empty live Folder → Remove folder; non-empty Folder → Move folder to Trash (D12).</summary>
    private Task DeleteSelectedAsync() => SelectedNode switch
    {
        { IsProject: true } => TrashSelectedProjectAsync(),
        { IsFolder: true } => CurrentLifecycleOperation = RemoveFolderCoreAsync(trashWhenNotEmpty: true),
        _ => Task.CompletedTask,
    };

    private Task TrashSelectedProjectAsync() => CurrentLifecycleOperation = TrashProjectCoreAsync();

    private async Task TrashProjectCoreAsync()
    {
        if (!CanWrite() || SelectedNode is not { IsProject: true } node)
            return; // direct invocations while a lifecycle write is pending issue no second store call

        var (id, name, visit) = (node.Id, node.Name, _visit); // the visit is captured when the command starts
        BeginWorkspaceWrite();
        if (!_dialogs.Confirm("Move to Trash",
                $"Move project “{name}” to the Trash? Its folders, links and notes stay with it and come back when you restore it. Nothing on disk is touched.",
                "Move to Trash"))
            return;

        IsLifecycleBusy = true;
        BannerMessage = $"Moving “{name}” to the Trash…";
        try
        {
            ProjectTrashResult result;
            try
            {
                result = await _workspace.TrashProjectAsync(id);
            }
            catch (Exception ex)
            {
                _log.Error("Move project to Trash failed.", ex); // operation only; no names
                BannerMessage = $"Could not move “{name}” to the Trash. Nothing was changed.";
                return; // nothing was suspended, so nothing needs restoring
            }

            switch (result.Outcome)
            {
                case ProjectTrashOutcome.Trashed:
                    ApplyRemoved(id);
                    Search?.RemoveResult(new SearchItemRef(SearchResultKind.Project, id));
                    BannerMessage = $"Moved “{name}” to the Trash.";
                    if (visit == _visit)
                        OfferUndo(new UndoOffer(id, name, visit, BannerMessage!)); // never for a Trash started in an ended visit
                    break;
                case ProjectTrashOutcome.AlreadyTrashed:
                    ApplyRemoved(id);
                    BannerMessage = WorkspaceService.ProjectInTrash;
                    break;
                case ProjectTrashOutcome.NotFound:
                    ApplyRemoved(id);
                    BannerMessage = "This project no longer exists.";
                    break;
                case ProjectTrashOutcome.Archived:
                    ApplyRemoved(id); // archived by another window: it leaves this tree as well
                    BannerMessage = "This project is archived. Unarchive it first.";
                    break;
                case ProjectTrashOutcome.AncestorNotLive:
                    await ReconcileNodeAsync(id); // C11/C4: a Folder above it was archived or trashed elsewhere; a fresh read decides
                    BannerMessage = "A folder above this project is archived or in the Trash. Nothing was changed.";
                    break;
                default:
                    BannerMessage = "Only projects can be moved to the Trash.";
                    break;
            }

            Trash.OnLifecycleCompleted();
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    private Task UndoAsync() => CurrentLifecycleOperation = _undo switch
    {
        { Bulk: not null } => UndoBulkArchiveCoreAsync(),
        { Archive: not null } => UndoArchiveCoreAsync(),
        { Folder: not null } => UndoFolderCoreAsync(),
        _ => UndoCoreAsync(),
    };

    private async Task UndoCoreAsync()
    {
        if (IsLifecycleBusy || _undo is not { Archive: null, Folder: null, Bulk: null } undo || undo.Visit != _visit)
            return;

        var (visit, selection) = (_visit, _selectionVersion); // captured at the Undo click
        BeginWorkspaceWrite(); // also withdraws this Undo: it is offered once
        IsLifecycleBusy = true;
        BannerMessage = $"Restoring “{undo.Name}”…";
        try
        {
            var (notice, placed) = await RestoreCoreAsync(undo.NodeId, undo.Name);
            BannerMessage = notice;
            // Selected only while the Undo's visit is current and the user has not selected anything since the click.
            if (placed is not null && visit == _visit && selection == _selectionVersion && _nodes.TryGetValue(undo.NodeId, out var restored) && ReferenceEquals(restored, placed))
                SelectedNode = placed;
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    /// <summary>Restore from the Workspace Trash panel (a Project, or a Folder with its subtree). Never changes the selection.</summary>
    /// <returns>The notice for the panel.</returns>
    internal async Task<string> RestoreFromTrashAsync(WorkspaceTrashEntry entry)
    {
        if (IsLifecycleBusy)
            return string.Empty;

        BeginWorkspaceWrite();
        IsLifecycleBusy = true;
        try
        {
            if (entry.Type == WorkspaceNodeType.Folder)
            {
                var folder = ClearFolderCoreAsync(entry.NodeId, entry.Name, FolderLifecycleKind.Trash, lifecycleEvent: null);
                CurrentLifecycleOperation = folder;
                return (await folder).Notice;
            }

            var operation = RestoreCoreAsync(entry.NodeId, entry.Name);
            CurrentLifecycleOperation = operation;
            return (await operation).Notice;
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    /// <summary>The Restore store call and its by-Id application; the caller holds the lifecycle slot.</summary>
    private async Task<(string Notice, WorkspaceNodeViewModel? Placed)> RestoreCoreAsync(long nodeId, string name)
    {
        ProjectRestoreResult result;
        try
        {
            result = await _workspace.RestoreProjectAsync(nodeId);
        }
        catch (Exception ex)
        {
            _log.Error("Restore project from the Trash failed.", ex); // operation only; no names
            return ($"Could not restore “{name}”. It is still in the Trash.", null);
        }

        WorkspaceNodeViewModel? placed = null;
        string notice;
        switch (result.Outcome)
        {
            case ProjectRestoreOutcome.Restored when result.Restored!.IsArchived:
                // Archived and trashed by an earlier build: it comes back archived, so the tree does not show it (§10.6).
                Touch(nodeId);
                notice = $"Restored “{result.Restored.Node.Name}”. It is archived: find it under Archived.";
                Trash.RemoveEntry(nodeId);
                _ = Archived.RefreshCount();
                break;
            case ProjectRestoreOutcome.Restored:
                placed = await ApplyRestoredAsync(result.Restored!.Node);
                notice = $"Restored “{result.Restored.Node.Name}” to {result.Restored.ParentPath ?? "Workspace (top level)"}.";
                Trash.RemoveEntry(nodeId);
                break;
            case ProjectRestoreOutcome.NameConflict:
                notice = $"The folder it came from already contains “{result.BlockingName ?? name}”. Rename or move that item first; nothing was restored.";
                break;
            case ProjectRestoreOutcome.ParentArchived:
                notice = $"“{name}” is in an archived folder. Unarchive that folder first; nothing was restored.";
                break;
            case ProjectRestoreOutcome.ParentTrashed:
                notice = $"The folder “{name}” came from is in the Trash. Restore that folder first; nothing was restored.";
                break;
            case ProjectRestoreOutcome.NotTrashed:
                notice = $"“{name}” is no longer in the Trash (it may have been restored in another window).";
                Trash.RemoveEntry(nodeId);
                Touch(nodeId); // reads that started earlier are re-read; the tree shows it with the next load of its parent
                break;
            default:
                notice = "This project no longer exists.";
                Trash.RemoveEntry(nodeId);
                ApplyRemoved(nodeId);
                break;
        }

        Trash.OnLifecycleCompleted();
        return (notice, placed);
    }

    // ---- Project Archive (Phase 12 proposal revision 3 §3, §6, §8) ---------------------------------------------------------
    //
    // Same timing as the Trash: nothing in the UI changes before the store commits, and the committed result is applied by Id.
    // Archive takes the node out of the tree; the content view current at that moment is deactivated only if it belongs to the
    // Project. Note editors are never closed and the Project's content stays writable (A3). Undo applies to exactly the
    // archive event it was offered for (its ArchiveSerial); Unarchive from the panel is a separate, confirmed, state-based write.

    public const string ProjectArchivedNotice = "This project is archived.";

    private Task ArchiveSelectedProjectAsync() => CurrentLifecycleOperation = ArchiveProjectCoreAsync();

    private async Task ArchiveProjectCoreAsync()
    {
        if (!CanWrite() || SelectedNode is not { IsProject: true } node)
            return; // direct invocations while a lifecycle write is pending issue no second store call

        var (id, name, visit) = (node.Id, node.Name, _visit); // the visit is captured when the command starts
        BeginWorkspaceWrite();
        if (!_dialogs.Confirm("Archive project",
                $"Archive project “{name}”? It leaves the tree and is listed under Archived. Its details, folders, links and notes are kept exactly as they are; open note editors stay open. Nothing on disk is touched.",
                "Archive"))
            return;

        IsLifecycleBusy = true;
        BannerMessage = $"Archiving “{name}”…";
        try
        {
            ProjectArchiveResult result;
            try
            {
                result = await _workspace.ArchiveProjectAsync(id);
            }
            catch (Exception ex)
            {
                _log.Error("Archive project failed.", ex); // operation only; no names
                BannerMessage = $"Could not archive “{name}”. Nothing was changed.";
                return; // nothing was suspended, so nothing needs restoring
            }

            switch (result.Outcome)
            {
                case ProjectArchiveOutcome.Archived:
                    ApplyRemoved(id);
                    Search?.MarkArchived(id, archived: true);
                    BannerMessage = $"Archived “{name}”.";
                    if (visit == _visit)
                        OfferUndo(new UndoOffer(id, name, visit, BannerMessage!, result.Event)); // never for an Archive started in an ended visit
                    break;
                case ProjectArchiveOutcome.AlreadyArchived:
                    ApplyRemoved(id);
                    BannerMessage = "This project is already archived.";
                    break;
                case ProjectArchiveOutcome.ProjectTrashed:
                    ApplyRemoved(id);
                    BannerMessage = WorkspaceService.ProjectInTrash;
                    break;
                case ProjectArchiveOutcome.NotFound:
                    ApplyRemoved(id);
                    BannerMessage = "This project no longer exists.";
                    break;
                case ProjectArchiveOutcome.AncestorNotLive:
                    await ReconcileNodeAsync(id); // C11/C4: a Folder above it was archived or trashed elsewhere; a fresh read decides
                    BannerMessage = "A folder above this project is archived or in the Trash. Nothing was changed.";
                    break;
                default:
                    BannerMessage = "Only projects can be archived.";
                    break;
            }

            Archived.OnLifecycleCompleted();
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    /// <summary>Undo of an Archive: restores the exact previous status only while it is still that archive event (§6.4).</summary>
    private async Task UndoArchiveCoreAsync()
    {
        if (IsLifecycleBusy || _undo is not { Archive: { } archiveEvent } undo || undo.Visit != _visit)
            return;

        var (visit, selection) = (_visit, _selectionVersion); // captured at the Undo click
        BeginWorkspaceWrite(); // also withdraws this Undo: it is offered once
        IsLifecycleBusy = true;
        BannerMessage = $"Restoring “{undo.Name}”…";
        try
        {
            ArchiveUndoResult result;
            try
            {
                result = await _workspace.UndoArchiveAsync(archiveEvent);
            }
            catch (Exception ex)
            {
                _log.Error("Undo archive failed.", ex);
                BannerMessage = $"Could not restore “{undo.Name}”. It is still archived.";
                return;
            }

            switch (result.Outcome)
            {
                case ArchiveUndoOutcome.Undone:
                    var placed = await ApplyRestoredAsync(result.Restored!.Node);
                    Archived.RemoveEntry(undo.NodeId);
                    Search?.MarkArchived(undo.NodeId, archived: false);
                    BannerMessage = $"Restored “{result.Restored.Node.Name}” to {result.Restored.ParentPath ?? "Workspace (top level)"}.";
                    // Selected only while the Undo's visit is current and the user has not selected anything since the click.
                    if (placed is not null && visit == _visit && selection == _selectionVersion && _nodes.TryGetValue(undo.NodeId, out var restored) && ReferenceEquals(restored, placed))
                        SelectedNode = placed;
                    break;
                case ArchiveUndoOutcome.NotArchived:
                    Touch(undo.NodeId); // reads that started earlier are re-read; the tree shows it with the next load of its parent
                    BannerMessage = $"“{undo.Name}” is no longer archived.";
                    break;
                case ArchiveUndoOutcome.ArchivedAgain:
                    BannerMessage = $"“{undo.Name}” was archived again elsewhere. Undo is no longer available; use Archived to unarchive it.";
                    break;
                case ArchiveUndoOutcome.ProjectTrashed:
                    _ = Trash.RefreshCount();
                    BannerMessage = WorkspaceService.ProjectInTrash;
                    break;
                case ArchiveUndoOutcome.ParentArchived or ArchiveUndoOutcome.ParentTrashed:
                    BannerMessage = $"A folder above “{undo.Name}” was archived or moved to the Trash elsewhere. Undo is no longer available.";
                    break;
                default:
                    BannerMessage = "This project no longer exists.";
                    break;
            }

            Archived.OnLifecycleCompleted();
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    // ---- Phase 19: Bulk Archive of Projects (proposal Revision 2 + R2a–R2d, §4) ----------------------------------------------
    // One checklist per place (a Folder's direct Projects, or the top level). Each ticked Project is archived by its OWN write
    // transaction, bound to the confirmed place (MovedElsewhere writes nothing). Sequential; no retry. Only LocationNotLive (the
    // confirmed place itself archived or trashed) and an exception stop the batch. Bulk Undo undoes exactly the batch's events.

    /// <summary>At most this many names are written in a summary banner (the rest are counted; Details lists all).</summary>
    internal const int BulkSummaryNames = 3;

    /// <summary>Tests: the archive events the offered Bulk Undo holds (0 when none is offered).</summary>
    internal IReadOnlyList<ArchiveEvent> BulkUndoEvents => _undo?.Bulk ?? [];

    private Task ArchiveProjectsInFolderAsync() =>
        CurrentLifecycleOperation = SelectedNode is { IsFolder: true } folder ? BulkArchiveCoreAsync(folder.Id, folder.Path) : Task.CompletedTask;

    private Task ArchiveTopLevelProjectsAsync() => CurrentLifecycleOperation = BulkArchiveCoreAsync(null, null);

    private async Task BulkArchiveCoreAsync(long? placeId, string? placePath)
    {
        if (!CanWrite())
            return; // B15: no dialog and no store call while another lifecycle write is pending

        var visit = _visit;
        BeginWorkspaceWrite();
        BulkArchiveCandidates candidates;
        try
        {
            candidates = await _workspace.ListBulkArchiveCandidatesAsync(placeId);
        }
        catch (Exception ex)
        {
            ReportError("The projects could not be listed. Nothing was changed.", ex);
            return;
        }

        if (candidates.Status != ChildrenReadStatus.Listed)
        {
            // The folder itself was removed, archived or trashed elsewhere: a fresh read of it decides the tree.
            if (placeId is { } id)
                await ReconcileNodeAsync(id, "This folder no longer exists.");
            BannerMessage = candidates.Status == ChildrenReadStatus.ParentMissing
                ? "This folder no longer exists."
                : "This folder is archived or in the Trash. Nothing was changed.";
            return;
        }

        if (candidates.Projects.Count == 0)
        {
            BannerMessage = "No projects to archive here."; // B8: no dialog
            return;
        }

        if (!CanWrite())
            return; // another lifecycle write started while the list was read
        var dialog = new BulkArchiveViewModel(placeId, placePath, candidates.Projects, candidates.Truncated);
        if (!_dialogs.ShowBulkArchive(dialog))
            return; // B7: Cancel, Esc and Enter on the default never archive
        var targets = dialog.ConfirmedTargets;
        if (targets.Count == 0 || !CanWrite())
            return;
        await RunBulkArchiveAsync(placeId, placePath, targets, dialog.NamesById, visit);
    }

    private async Task RunBulkArchiveAsync(long? placeId, string? placePath, IReadOnlyList<BulkArchiveTarget> targets, IReadOnlyDictionary<long, string> names, int visit)
    {
        var rows = new List<(long Id, string Name, string Outcome, bool IsArchived)>();
        var events = new List<ArchiveEvent>();
        var refusalPlaces = new Dictionary<long, string>();
        string? stopText = null;
        var notAttempted = 0;
        IsLifecycleBusy = true;
        BannerMessage = $"Archiving {ProjectCount(targets.Count)}…";
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var name = names.GetValueOrDefault(target.ProjectId, "");
                BulkArchiveResult result;
                try
                {
                    result = await _workspace.ArchiveProjectInPlaceAsync(target);
                }
                catch (Exception ex)
                {
                    _log.Error("Bulk archive failed.", ex); // operation only; no names
                    rows.Add((target.ProjectId, name, "could not be archived", false));
                    notAttempted = targets.Count - i - 1;
                    stopText = $"Could not archive “{name}”. {NotAttemptedText(notAttempted)}Nothing else was changed.";
                    break;
                }

                var id = target.ProjectId;
                switch (result.Outcome)
                {
                    case BulkArchiveOutcome.Archived:
                        ApplyRemoved(id); // a committed write of this window: committed stamp (rule L)
                        Search?.MarkArchived(id, archived: true);
                        events.Add(result.Event!.Value);
                        var renamed = result.Entry is { } entry && entry.Name != name ? " (renamed elsewhere)" : "";
                        rows.Add((id, name, "archived" + renamed, true));
                        break;
                    case BulkArchiveOutcome.MovedElsewhere:
                        rows.Add((id, name, "moved elsewhere", false));
                        // Details records where the REFUSAL saw it (store's CurrentParentId), labelled before the reconcile runs and
                        // without any store call. The tree is decided only by the reconcile below, never by CurrentParentId.
                        refusalPlaces[id] = RefusalPlaceText(result.CurrentParentId);
                        await ReconcileNodeAsync(id); // R2a: re-read the Project itself (and its confirmation, R2b); never the origin
                        break;
                    case BulkArchiveOutcome.AlreadyArchived:
                        ApplyRemoved(id);
                        rows.Add((id, name, "already archived", false));
                        break;
                    case BulkArchiveOutcome.ProjectTrashed:
                        ApplyRemoved(id);
                        rows.Add((id, name, "in the Trash", false));
                        break;
                    case BulkArchiveOutcome.NotFound:
                        ApplyRemoved(id);
                        rows.Add((id, name, "no longer exists", false));
                        break;
                    case BulkArchiveOutcome.NotAProject:
                        rows.Add((id, name, "not a project", false));
                        break;
                    case BulkArchiveOutcome.LocationNotLive:
                        rows.Add((id, name, "not archived: its folder is archived or in the Trash", false));
                        notAttempted = targets.Count - i - 1;
                        stopText = (LocationNotLiveText(placeId, placePath, result) + " " + NotAttemptedText(notAttempted)).TrimEnd();
                        if (placeId is { } place)
                            await ReconcileNodeAsync(place); // the confirmed cause: the origin is re-read exactly once
                        break;
                }

                if (stopText is not null)
                    break;
            }

            Archived.OnLifecycleCompleted();
        }
        finally
        {
            IsLifecycleBusy = false;
        }

        foreach (var skipped in targets.Skip(rows.Count))
            rows.Add((skipped.ProjectId, names.GetValueOrDefault(skipped.ProjectId, ""), "not attempted", false));
        LastBulkArchiveDetails = rows
            .Select(r => refusalPlaces.TryGetValue(r.Id, out var seen) ? $"“{r.Name}”: {r.Outcome} (when refused: {seen})" : $"“{r.Name}”: {r.Outcome}")
            .ToList();
        _bulkSummaryMessage = BulkArchiveSummary(rows, targets.Count, stopText);
        BannerMessage = _bulkSummaryMessage;
        if (events.Count > 0 && visit == _visit)
            OfferUndo(new UndoOffer(placeId ?? 0, placePath ?? "", visit, BannerMessage!) { Bulk = events, BulkNames = names });
    }

    private static string BulkArchiveSummary(IReadOnlyList<(long Id, string Name, string Outcome, bool IsArchived)> rows, int total, string? stopText)
    {
        var archived = rows.Count(r => r.IsArchived);
        var others = rows.Where(r => !r.IsArchived && r.Outcome != "not attempted").ToList();
        var text = archived == total ? $"Archived {ProjectCount(archived)}." : $"Archived {archived} of {ProjectCount(total)}.";
        if (others.Count > 0)
        {
            var parts = others.GroupBy(r => r.Outcome).Select(g => $"{g.Count()} {g.Key}");
            var named = string.Join(", ", others.Take(BulkSummaryNames).Select(r => $"“{r.Name}”"));
            var more = others.Count > BulkSummaryNames ? $" and {others.Count - BulkSummaryNames} more" : "";
            text += $" Not archived: {string.Join(", ", parts)} ({named}{more}).";
        }

        return stopText is null ? text : $"{text} {stopText}";
    }

    private static string LocationNotLiveText(long? placeId, string? placePath, BulkArchiveResult result)
    {
        var place = placePath is null ? "The folder" : $"“{placePath}”";
        var what = result.LocationState == LifecycleState.Trashed ? "moved to the Trash" : "archived";
        return result.MarkedNodeId == placeId
            ? $"{place} was {what} elsewhere."
            : $"A folder above {place} was {what} elsewhere.";
    }

    /// <summary>
    /// The place a MovedElsewhere refusal observed: the store's <see cref="BulkArchiveResult.CurrentParentId"/> (null = top level).
    /// The folder's path is added only when that folder is already cached; no store call is made for it.
    /// </summary>
    private string RefusalPlaceText(long? currentParentId) => currentParentId switch
    {
        null => "at the top level",
        { } id when _nodes.TryGetValue(id, out var folder) => $"in folder #{id} “{folder.Path}”",
        { } id => $"in folder #{id}",
    };

    private static string NotAttemptedText(int count) =>
        count == 0 ? "" : count == 1 ? "1 project was not attempted. " : $"{count} projects were not attempted. ";

    private static string ProjectCount(int count) => count == 1 ? "1 project" : $"{count} projects";

    private void ShowBulkArchiveDetails()
    {
        if (LastBulkArchiveDetails is { } lines)
            _dialogs.ShowMessage("Archive projects: details", string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// Bulk Undo (§4.4.2): <see cref="IWorkspaceStore.UndoArchiveAsync"/> once per event the batch created, in batch order. It never
    /// selects a restored Project (a selection made meanwhile is kept), continues after per-Project refusals, and stops at an
    /// exception, reporting restored, refused, failed and not-attempted items separately.
    /// </summary>
    private async Task UndoBulkArchiveCoreAsync()
    {
        if (IsLifecycleBusy || _undo is not { Bulk: { } events } undo || undo.Visit != _visit)
            return;

        var names = undo.BulkNames ?? new Dictionary<long, string>();
        BeginWorkspaceWrite(); // also withdraws this Undo: it is offered once
        IsLifecycleBusy = true;
        BannerMessage = $"Restoring {ProjectCount(events.Count)}…";
        var rows = new List<(string Name, string Outcome)>();
        var restored = 0;
        string? failed = null;
        var notAttempted = 0;
        try
        {
            for (var i = 0; i < events.Count; i++)
            {
                var archiveEvent = events[i];
                var name = names.GetValueOrDefault(archiveEvent.NodeId, "");
                ArchiveUndoResult result;
                try
                {
                    result = await _workspace.UndoArchiveAsync(archiveEvent);
                }
                catch (Exception ex)
                {
                    _log.Error("Bulk undo archive failed.", ex); // operation only; no names
                    failed = name;
                    notAttempted = events.Count - i - 1;
                    rows.Add((name, "could not be restored; it is still archived"));
                    break;
                }

                switch (result.Outcome)
                {
                    case ArchiveUndoOutcome.Undone:
                        await ApplyRestoredAsync(result.Restored!.Node); // placed, never selected (G5)
                        Archived.RemoveEntry(archiveEvent.NodeId);
                        Search?.MarkArchived(archiveEvent.NodeId, archived: false);
                        restored++;
                        rows.Add((name, "restored"));
                        break;
                    case ArchiveUndoOutcome.NotArchived:
                        Touch(archiveEvent.NodeId); // as the single Undo: reads that started earlier are re-read
                        rows.Add((name, "no longer archived"));
                        break;
                    case ArchiveUndoOutcome.ArchivedAgain:
                        rows.Add((name, "archived again elsewhere"));
                        break;
                    case ArchiveUndoOutcome.ProjectTrashed:
                        _ = Trash.RefreshCount();
                        rows.Add((name, "in the Trash"));
                        break;
                    case ArchiveUndoOutcome.ParentArchived or ArchiveUndoOutcome.ParentTrashed:
                        rows.Add((name, "a folder above it was archived or moved to the Trash"));
                        break;
                    default:
                        rows.Add((name, "no longer exists"));
                        break;
                }
            }

            Archived.OnLifecycleCompleted();
        }
        finally
        {
            IsLifecycleBusy = false;
        }

        foreach (var skipped in events.Skip(rows.Count))
            rows.Add((names.GetValueOrDefault(skipped.NodeId, ""), "not attempted; still archived"));
        LastBulkArchiveDetails = rows.Select(r => $"“{r.Name}”: {r.Outcome}").ToList();
        var refused = rows.Where(r => r.Outcome is not ("restored" or "could not be restored; it is still archived" or "not attempted; still archived")).ToList();
        var text = restored == events.Count ? $"Restored {ProjectCount(restored)}." : $"Restored {restored} of {ProjectCount(events.Count)}.";
        if (refused.Count > 0)
            text += $" Not restored: {string.Join(", ", refused.GroupBy(r => r.Outcome).Select(g => $"{g.Count()} {g.Key}"))}.";
        if (failed is not null)
            text += $" Could not restore “{failed}”. {NotAttemptedText(notAttempted)}They are still archived.";
        _bulkSummaryMessage = text;
        BannerMessage = text;
    }

    /// <summary>
    /// Unarchive from the Archived projects panel: asks for the status (A8), then a state-based Unarchive in the lifecycle
    /// slot. Never changes the selection.
    /// </summary>
    /// <returns>The notice for the panel; null when the dialog was cancelled or the slot was taken.</returns>
    internal async Task<string?> UnarchiveFromPanelAsync(ArchivedProjectEntry entry)
    {
        if (IsLifecycleBusy)
            return null;

        BeginWorkspaceWrite();
        if (entry.Type == WorkspaceNodeType.Folder)
        {
            // A Folder has no status to choose (§7.2): Unarchive is the panel action itself, like Restore in the Trash panel.
            IsLifecycleBusy = true;
            try
            {
                var folder = ClearFolderCoreAsync(entry.NodeId, entry.Name, FolderLifecycleKind.Archive, lifecycleEvent: null);
                CurrentLifecycleOperation = folder;
                return (await folder).Notice;
            }
            finally
            {
                IsLifecycleBusy = false;
            }
        }

        var dialog = new UnarchiveProjectViewModel(entry.Name, entry.ParentPath);
        if (!_dialogs.ShowUnarchive(dialog) || IsLifecycleBusy)
            return null;

        IsLifecycleBusy = true;
        try
        {
            var operation = UnarchiveCoreAsync(entry.NodeId, entry.Name, dialog.Status.Status);
            CurrentLifecycleOperation = operation;
            return await operation;
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    private async Task<string> UnarchiveCoreAsync(long nodeId, string name, ProjectStatus status)
    {
        ProjectUnarchiveResult result;
        try
        {
            result = await _workspace.UnarchiveProjectAsync(nodeId, status);
        }
        catch (Exception ex)
        {
            _log.Error("Unarchive project failed.", ex); // operation only; no names
            return $"Could not unarchive “{name}”. It is still archived.";
        }

        string notice;
        switch (result.Outcome)
        {
            case ProjectUnarchiveOutcome.Unarchived:
                await ApplyRestoredAsync(result.Unarchived!.Node);
                Search?.MarkArchived(nodeId, archived: false);
                notice = $"Unarchived “{result.Unarchived.Node.Name}” to {result.Unarchived.ParentPath ?? "Workspace (top level)"}.";
                break;
            case ProjectUnarchiveOutcome.NotArchived:
                Touch(nodeId); // unarchived elsewhere: the tree shows it with the next load of its parent
                notice = $"“{name}” is no longer archived.";
                break;
            case ProjectUnarchiveOutcome.ProjectTrashed:
                Touch(nodeId);
                _ = Trash.RefreshCount();
                notice = "This project is in the Trash. Restore it first.";
                break;
            case ProjectUnarchiveOutcome.ParentArchived:
                notice = $"“{name}” is inside an archived folder. Unarchive that folder instead.";
                break;
            case ProjectUnarchiveOutcome.ParentTrashed:
                notice = $"“{name}” is inside a folder that is in the Trash. Restore that folder first.";
                break;
            default:
                ApplyRemoved(nodeId);
                notice = "This project no longer exists.";
                break;
        }

        Archived.RemoveEntry(nodeId);
        Archived.OnLifecycleCompleted();
        return notice;
    }

    // ---- Phase 20: Bulk Unarchive of Projects (proposal Revision 2 + R2a, §4) ------------------------------------------------
    // The checklist lists the archived Project roots with the ArchiveSerial read with each. Each ticked Project is unarchived by its
    // OWN write transaction, bound to that serial (NotArchived and ArchivedAgain write nothing). Sequential; no retry; only an
    // exception stops the batch; no Undo. The area returns the summary and never writes panel state or the Workspace banner.

    /// <summary>
    /// Bulk Unarchive from the Archived panel. <paramref name="isPanelVisitCurrent"/> answers whether the panel is still open in the
    /// visit that started the request (R2-3); it is checked when the checklist read returns and again after the dialog confirms,
    /// together with the lifecycle slot (R2a-1). Only when both pass is the slot taken, in the same synchronous step, BEFORE
    /// <see cref="BeginWorkspaceWrite"/> and before <paramref name="onStarted"/> (the panel progress).
    /// </summary>
    /// <returns>The summary for the panel (written there only if the visit is still current), or null when nothing is to be shown:
    /// the slot was taken, the dialog was cancelled, or the request ended because the visit changed or the slot was taken.</returns>
    internal async Task<BulkUnarchiveReport?> BulkUnarchiveFromPanelAsync(Func<bool> isPanelVisitCurrent, Action<int>? onStarted = null)
    {
        if (!CanWrite())
            return null; // BU12: no dialog and no store call while another lifecycle write is pending

        BulkUnarchiveCandidates candidates;
        try
        {
            candidates = await _workspace.ListBulkUnarchiveCandidatesAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Archived projects could not be listed for bulk unarchive.", ex); // no names
            return isPanelVisitCurrent() ? new BulkUnarchiveReport(BulkUnarchiveListFailed, null) : null;
        }

        if (!isPanelVisitCurrent())
            return null; // R2-3: the read returned into another visit: no dialog, no write, no Notice
        if (candidates.Projects.Count == 0)
            return new BulkUnarchiveReport(NoArchivedProjectsToUnarchive, null); // BU11: no dialog
        if (!CanWrite())
            return null; // another lifecycle write started while the list was read

        var dialog = new BulkUnarchiveViewModel(candidates.Projects, candidates.Truncated);
        if (!_dialogs.ShowBulkUnarchive(dialog))
            return null; // BU10: Cancel, Esc and Enter on the default never unarchive
        var targets = dialog.ConfirmedTargets;
        if (targets.Count == 0)
            return null;

        // R2a-1 post-confirmation guard, then the slot FIRST, with no await in between (approval order, proposal §0.3).
        if (!isPanelVisitCurrent() || !CanWrite())
            return null; // no BeginWorkspaceWrite (an Undo offer stays), no Notice or Details, no bulk write
        IsLifecycleBusy = true;
        try
        {
            BeginWorkspaceWrite(); // ends an earlier banner Undo offer and an in-flight reveal; the banner text stays (R2a-2)
            onStarted?.Invoke(targets.Count);
            var operation = RunBulkUnarchiveAsync(targets, dialog.Status.Status, dialog.NamesById);
            CurrentLifecycleOperation = operation;
            return LastBulkUnarchiveReport = await operation;
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    /// <summary>Tests (BU18): the summary the last started batch returned, whether or not its panel visit could show it.</summary>
    internal BulkUnarchiveReport? LastBulkUnarchiveReport { get; private set; }

    internal const string BulkUnarchiveListFailed = "The archived projects could not be listed. Nothing was changed.";

    internal const string NoArchivedProjectsToUnarchive = "No archived projects to unarchive.";

    private async Task<BulkUnarchiveReport> RunBulkUnarchiveAsync(IReadOnlyList<BulkUnarchiveTarget> targets, ProjectStatus status, IReadOnlyDictionary<long, string> names)
    {
        var rows = new List<(long Id, string Name, BulkUnarchiveRow Kind, string Detail)>();
        string? failedName = null;
        var notAttempted = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var id = target.ProjectId;
            var name = names.GetValueOrDefault(id, "");
            BulkUnarchiveResult result;
            try
            {
                result = await _workspace.UnarchiveProjectBoundAsync(target, status);
            }
            catch (Exception ex)
            {
                _log.Error("Bulk unarchive failed.", ex); // operation only; no names
                // Round 2: only what the batch knows. An exception says nothing about the Project's current state (another window
                // may have changed it), so the line claims no status, and no store read is made just to word it.
                rows.Add((id, name, BulkUnarchiveRow.Failed, "could not be unarchived"));
                failedName = name;
                notAttempted = targets.Count - i - 1;
                break;
            }

            switch (result.Outcome)
            {
                case BulkUnarchiveOutcome.Unarchived:
                    await ApplyRestoredAsync(result.Unarchived!.Node); // committed write: Touch (rule L); placed once, never selected
                    Search?.MarkArchived(id, archived: false);
                    rows.Add((id, name, BulkUnarchiveRow.Unarchived, $"unarchived to {result.Unarchived.ParentPath ?? "Workspace (top level)"}"));
                    break;
                case BulkUnarchiveOutcome.ArchivedAgain:
                    rows.Add((id, name, BulkUnarchiveRow.ArchivedAgain, "archived again elsewhere; not unarchived"));
                    break;
                case BulkUnarchiveOutcome.NotArchived:
                    rows.Add((id, name, BulkUnarchiveRow.NotArchived, "no longer archived (unarchived elsewhere)"));
                    await ReplaceByReadAsync(id); // R2-2: one silent re-read of the Project itself; no Touch
                    break;
                case BulkUnarchiveOutcome.ParentArchived:
                    rows.Add((id, name, BulkUnarchiveRow.ParentArchived, "inside an archived folder"));
                    break;
                case BulkUnarchiveOutcome.ParentTrashed:
                    rows.Add((id, name, BulkUnarchiveRow.ParentTrashed, "inside a folder in the Trash"));
                    break;
                case BulkUnarchiveOutcome.ProjectTrashed:
                    rows.Add((id, name, BulkUnarchiveRow.ProjectTrashed, "in the Trash"));
                    _ = Trash.RefreshCount();
                    await ReplaceByReadAsync(id);
                    break;
                case BulkUnarchiveOutcome.NotFound:
                    rows.Add((id, name, BulkUnarchiveRow.NotFound, "no longer exists"));
                    break;
                case BulkUnarchiveOutcome.NotAProject:
                    rows.Add((id, name, BulkUnarchiveRow.NotAProject, "not a project"));
                    break;
            }
        }

        Archived.OnLifecycleCompleted(); // committed facts: the list (if open) and the count are read again, whatever the visit

        foreach (var skipped in targets.Skip(rows.Count))
            rows.Add((skipped.ProjectId, names.GetValueOrDefault(skipped.ProjectId, ""), BulkUnarchiveRow.NotAttempted, "not attempted"));
        var details = rows.Select(r => $"“{r.Name}”: {r.Detail}").ToList();
        return new BulkUnarchiveReport(BulkUnarchiveSummary(rows.Select(r => (r.Name, r.Kind)).ToList(), targets.Count, failedName, notAttempted), details);
    }

    /// <summary>The kinds of Details line, in the Notice's fixed group order (§4.4).</summary>
    private enum BulkUnarchiveRow
    {
        Unarchived,
        ArchivedAgain,
        NotArchived,
        ParentArchived,
        ParentTrashed,
        ProjectTrashed,
        NotFound,
        NotAProject,
        Failed,
        NotAttempted,
    }

    private static string BulkUnarchiveGroup(BulkUnarchiveRow kind) => kind switch
    {
        BulkUnarchiveRow.ArchivedAgain => "archived again elsewhere",
        BulkUnarchiveRow.NotArchived => "no longer archived",
        BulkUnarchiveRow.ParentArchived => "inside an archived folder",
        BulkUnarchiveRow.ParentTrashed => "inside a folder in the Trash",
        BulkUnarchiveRow.ProjectTrashed => "in the Trash",
        BulkUnarchiveRow.NotFound => "no longer exists",
        _ => "not a project",
    };

    /// <summary>
    /// "Unarchived 4 of 6 projects. Not unarchived: 1 archived again elsewhere (“Beta”), 1 inside an archived folder (“Alpha”)."
    /// Groups in a fixed order; at most <see cref="BulkSummaryNames"/> names in all, then "and n more"; then the failure and the
    /// not-attempted count.
    /// </summary>
    private static string BulkUnarchiveSummary(IReadOnlyList<(string Name, BulkUnarchiveRow Kind)> rows, int total, string? failedName, int notAttempted)
    {
        var unarchived = rows.Count(r => r.Kind == BulkUnarchiveRow.Unarchived);
        var text = unarchived == total ? $"Unarchived {ProjectCount(unarchived)}." : $"Unarchived {unarchived} of {ProjectCount(total)}.";
        var budget = BulkSummaryNames;
        var parts = new List<string>();
        foreach (var group in rows.Where(r => r.Kind is not (BulkUnarchiveRow.Unarchived or BulkUnarchiveRow.Failed or BulkUnarchiveRow.NotAttempted))
                     .GroupBy(r => r.Kind).OrderBy(g => g.Key))
        {
            var names = group.Take(budget).Select(r => $"“{r.Name}”").ToList();
            budget -= names.Count;
            var more = group.Count() - names.Count;
            var named = names.Count == 0 ? $"{more} more" : string.Join(", ", names) + (more > 0 ? $" and {more} more" : "");
            parts.Add($"{group.Count()} {BulkUnarchiveGroup(group.Key)} ({named})");
        }

        if (parts.Count > 0)
            text += $" Not unarchived: {string.Join(", ", parts)}.";
        if (failedName is not null)
            text += $" Could not unarchive “{failedName}”. {NotAttemptedText(notAttempted)}".TrimEnd(); // no claim about their status
        return text;
    }

    private Task RemoveSelectedFolderAsync() => CurrentLifecycleOperation = RemoveFolderCoreAsync(trashWhenNotEmpty: false);

    /// <param name="trashWhenNotEmpty">Del (D12): a Folder with any child row is offered the confirmed subtree Trash instead.</param>
    private async Task RemoveFolderCoreAsync(bool trashWhenNotEmpty)
    {
        if (!CanWrite() || SelectedNode is not { IsFolder: true } node)
            return;

        var (id, name, visit) = (node.Id, node.Name, _visit);
        BeginWorkspaceWrite();

        // A fresh read decides whether a confirmation is shown at all (§4): only a folder with no child row qualifies.
        var sequence = _revealSequence;
        var mark = BeginTrackedRead();
        WorkspaceNodeState? state;
        try
        {
            try
            {
                state = await _workspace.GetNodeStateAsync(id);
            }
            catch (Exception ex)
            {
                ReportError("The folder could not be checked. Nothing was changed.", ex);
                return;
            }

            if (!ReferenceEquals(SelectedNode, node) || IsLifecycleBusy)
                return; // the selection changed (or another lifecycle write started) while the folder was being checked
            if (state is null)
            {
                if (!IsTouchedSince(id, sequence))
                    _ = RemoveMissingByRead(id, mark); // D10: a read-derived removal (rule C if a newer read changed it)
                BannerMessage = "This folder no longer exists.";
                return;
            }

            if (!state.Effective.IsLive)
            {
                if (!IsTouchedSince(id, sequence))
                    ApplyNotLive(id, state.ChainFromRoot, state.Effective, mark); // archived or trashed elsewhere (C4)
                return;
            }
        }
        finally
        {
            EndTrackedRead(mark);
        }

        if (state.ActiveChildren + state.ArchivedChildren + state.TrashedChildren > 0)
        {
            if (trashWhenNotEmpty)
            {
                await FolderLifecycleCoreAsync(FolderLifecycleKind.Trash, visit, begun: true);
                return;
            }

            BannerMessage = NotEmptyText(name, state.ActiveChildren, state.ArchivedChildren, state.TrashedChildren);
            return;
        }

        if (!_dialogs.Confirm("Remove folder",
                $"Remove the empty folder “{name}”? This removes only the WorkHub folder and cannot be undone. Nothing on disk is touched.",
                "Remove"))
            return;

        IsLifecycleBusy = true;
        try
        {
            FolderRemoveResult result;
            try
            {
                result = await _workspace.RemoveEmptyFolderAsync(id);
            }
            catch (Exception ex)
            {
                _log.Error("Remove empty folder failed.", ex);
                BannerMessage = $"Could not remove “{name}”. Nothing was changed.";
                return;
            }

            switch (result.Outcome)
            {
                case FolderRemoveOutcome.Removed:
                    ApplyRemoved(id);
                    Search?.RemoveResult(new SearchItemRef(SearchResultKind.WorkspaceFolder, id));
                    BannerMessage = $"Removed the empty folder “{name}”.";
                    break;
                case FolderRemoveOutcome.NotEmpty:
                    if (_nodes.TryGetValue(id, out var folder))
                        folder.SetHasChildren(true);
                    BannerMessage = NotEmptyText(name, result.ActiveChildren, result.ArchivedProjects, result.TrashedProjects);
                    break;
                case FolderRemoveOutcome.NotFound:
                    ApplyRemoved(id);
                    BannerMessage = "This folder no longer exists.";
                    break;
                case FolderRemoveOutcome.NotLive:
                    await ReconcileNodeAsync(id); // archived or trashed (itself or above) meanwhile: a fresh read decides (C4)
                    break;
                default:
                    BannerMessage = "Only folders can be removed.";
                    break;
            }
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    private static string NotEmptyText(string name, int active, int archived, int trashed)
    {
        if (active > 0)
            return $"“{name}” is not empty ({(active == 1 ? "1 item" : $"{active} items")}). Move or remove them first, or move the folder to the Trash.";
        if (archived > 0) // hidden from the tree, so the message says what holds the folder (A11)
            return $"“{name}” still holds {(archived == 1 ? "1 archived item" : $"{archived} archived items")}. Unarchive {(archived == 1 ? "it" : "them")} and move {(archived == 1 ? "it" : "them")} elsewhere first, or move the folder to the Trash.";
        return $"“{name}” still holds {(trashed == 1 ? "1 item" : $"{trashed} items")} in the Trash. Restore {(trashed == 1 ? "it" : "them")} and move {(trashed == 1 ? "it" : "them")} elsewhere first, or move the folder to the Trash.";
    }

    // ---- Folder Archive and Folder Trash (Phase 13 proposal revision 4 §7.2-§7.4, §7.9) ----------------------------------------
    //
    // Same timing as the Project operations: nothing in the UI changes before the store commits, and the committed result is
    // applied by Id (C1: the Folder and its whole cached subtree leave the tree in one synchronous step; a selection inside it is
    // cleared, any other stays). The confirmation is one computed preview with a token; a stale commit writes nothing and shows
    // a NEW confirmation that needs a new explicit click (it is never resubmitted). Undo is event-specific (LifecycleSerial).

    private Task ArchiveSelectedFolderAsync() => CurrentLifecycleOperation = FolderLifecycleCoreAsync(FolderLifecycleKind.Archive, _visit, begun: false);

    private Task TrashSelectedFolderAsync() => CurrentLifecycleOperation = FolderLifecycleCoreAsync(FolderLifecycleKind.Trash, _visit, begun: false);

    /// <param name="visit">The Workspace visit captured when the command started (Undo is offered only while it is current).</param>
    /// <param name="begun">The caller (Del) already started the Workspace write.</param>
    private async Task FolderLifecycleCoreAsync(FolderLifecycleKind kind, int visit, bool begun)
    {
        if (!CanWrite() || SelectedNode is not { IsFolder: true } node)
            return; // direct invocations while a lifecycle write is pending issue no store call

        var (id, name) = (node.Id, node.Name);
        if (!begun)
            BeginWorkspaceWrite();

        FolderLifecyclePreview preview;
        try
        {
            preview = await _workspace.PrepareFolderLifecycleAsync(id, kind);
        }
        catch (Exception ex)
        {
            ReportError("The folder could not be checked. Nothing was changed.", ex);
            return;
        }

        if (preview.Refusal is { } refused)
        {
            await ApplyFolderRefusalAsync(id, refused);
            return;
        }

        if (!ReferenceEquals(SelectedNode, node) || IsLifecycleBusy)
            return; // the selection changed (or another lifecycle write started) while the folder was being checked

        var confirmation = preview.Confirmation!;
        var stale = false;
        while (true)
        {
            if (!_dialogs.ShowFolderLifecycleConfirmation(new FolderLifecycleConfirmationViewModel(confirmation, stale)) || IsLifecycleBusy)
            {
                if (stale)
                    BannerMessage = $"Nothing was changed: “{name}” changed after you confirmed, and you cancelled the new confirmation.";
                return; // Cancel discards it: nothing is written and nothing is retried
            }

            IsLifecycleBusy = true;
            BannerMessage = kind == FolderLifecycleKind.Archive ? $"Archiving folder “{name}”…" : $"Moving folder “{name}” to the Trash…";
            try
            {
                FolderLifecycleResult result;
                try
                {
                    result = await _workspace.MarkFolderAsync(id, kind, confirmation.Token);
                }
                catch (Exception ex)
                {
                    _log.Error(kind == FolderLifecycleKind.Archive ? "Archive folder failed." : "Move folder to Trash failed.", ex); // operation only; no names
                    BannerMessage = kind == FolderLifecycleKind.Archive
                        ? $"Could not archive “{name}”. Nothing was changed."
                        : $"Could not move “{name}” to the Trash. Nothing was changed.";
                    return; // C11: no subtree removal, no selection change, no Undo
                }

                if (result.Outcome == FolderLifecycleOutcome.ConfirmationStale)
                {
                    // §7.4: nothing was written. The fresh preview is shown as a NEW confirmation; only a new click commits it.
                    confirmation = result.Fresh!;
                    stale = true;
                    BannerMessage = $"“{name}” changed since you confirmed. Review it again.";
                    continue;
                }

                if (result.Outcome == FolderLifecycleOutcome.Committed)
                {
                    ApplyRemoved(id); // C1, checked at application time (C8)
                    if (kind == FolderLifecycleKind.Trash)
                        Search?.RemoveResult(new SearchItemRef(SearchResultKind.WorkspaceFolder, id));
                    else
                        Search?.MarkFolderArchived(id, archived: true);
                    BannerMessage = kind == FolderLifecycleKind.Archive ? $"Archived folder “{name}”." : $"Moved folder “{name}” to the Trash.";
                    if (visit == _visit)
                        OfferUndo(new UndoOffer(id, name, visit, BannerMessage!, Folder: result.Event)); // never for a command whose visit ended (C7)
                }
                else
                {
                    await ApplyFolderRefusalAsync(id, result.Outcome);
                }

                Trash.OnLifecycleCompleted();
                Archived.OnLifecycleCompleted();
                return;
            }
            finally
            {
                IsLifecycleBusy = false;
            }
        }
    }

    /// <summary>
    /// C11: a refused Folder Archive or Trash wrote nothing. No subtree is removed and no selection changes, except the C4
    /// reconciliation of the Folder the outcome reports as gone or not live.
    /// </summary>
    private async Task ApplyFolderRefusalAsync(long id, FolderLifecycleOutcome outcome)
    {
        switch (outcome)
        {
            case FolderLifecycleOutcome.NotFound:
                ApplyRemoved(id);
                BannerMessage = "This folder no longer exists.";
                return;
            case FolderLifecycleOutcome.NotAFolder:
                BannerMessage = "Only folders can be archived or moved to the Trash this way.";
                return;
        }

        await ReconcileNodeAsync(id);
        BannerMessage = outcome switch
        {
            FolderLifecycleOutcome.AlreadyArchived => "This folder is already archived.",
            FolderLifecycleOutcome.FolderTrashed => "This folder is in the Trash.",
            FolderLifecycleOutcome.FolderArchived => "This folder is archived. Unarchive it first.",
            _ => "A folder above this folder is archived or in the Trash. Nothing was changed.",
        };
    }

    /// <summary>Undo of a Folder Archive or Trash: clears the marker only while it is still exactly that event (D13).</summary>
    private async Task UndoFolderCoreAsync()
    {
        if (IsLifecycleBusy || _undo is not { Folder: { } lifecycleEvent } undo || undo.Visit != _visit)
            return;

        var (visit, selection) = (_visit, _selectionVersion); // captured at the Undo click
        BeginWorkspaceWrite(); // also withdraws this Undo: it is offered once
        IsLifecycleBusy = true;
        BannerMessage = $"Restoring “{undo.Name}”…";
        try
        {
            var (notice, placed) = await ClearFolderCoreAsync(undo.NodeId, undo.Name, lifecycleEvent.Kind, lifecycleEvent);
            BannerMessage = notice;
            // Selected only while the Undo's visit is current and the user has not selected anything since the click (C7).
            if (placed is not null && visit == _visit && selection == _selectionVersion && _nodes.TryGetValue(undo.NodeId, out var restored) && ReferenceEquals(restored, placed))
                SelectedNode = placed;
        }
        finally
        {
            IsLifecycleBusy = false;
        }
    }

    /// <summary>
    /// Unarchive or Restore of a Folder (from a panel), or the Undo of one event; the caller holds the lifecycle slot. Never changes
    /// the selection. The committed result is placed by C9.
    /// </summary>
    private async Task<(string Notice, WorkspaceNodeViewModel? Placed)> ClearFolderCoreAsync(long nodeId, string name, FolderLifecycleKind kind, FolderLifecycleEvent? lifecycleEvent)
    {
        FolderClearResult result;
        try
        {
            result = lifecycleEvent is { } undo
                ? await _workspace.UndoFolderLifecycleAsync(undo)
                : await _workspace.ClearFolderAsync(nodeId, kind);
        }
        catch (Exception ex)
        {
            _log.Error(kind == FolderLifecycleKind.Archive ? "Unarchive folder failed." : "Restore folder from the Trash failed.", ex); // no names
            return (kind == FolderLifecycleKind.Archive ? $"Could not unarchive “{name}”. It is still archived." : $"Could not restore “{name}”. It is still in the Trash.", null);
        }

        WorkspaceNodeViewModel? placed = null;
        string notice;
        switch (result.Outcome)
        {
            case FolderClearOutcome.Cleared:
                placed = await ApplyRestoredAsync(result.Folder!.Node);
                notice = $"{(kind == FolderLifecycleKind.Archive ? "Unarchived" : "Restored")} folder “{result.Folder.Node.Name}” to {result.Folder.ParentPath ?? "Workspace (top level)"}.";
                if (kind == FolderLifecycleKind.Archive)
                    Search?.MarkFolderArchived(nodeId, archived: false);
                RemovePanelEntry(nodeId, kind);
                break;
            case FolderClearOutcome.NotArchived or FolderClearOutcome.NotTrashed:
                Touch(nodeId); // cleared elsewhere: reads that started earlier are re-read; the tree shows it with the next load of its parent
                notice = kind == FolderLifecycleKind.Archive ? $"“{name}” is no longer archived." : $"“{name}” is no longer in the Trash (it may have been restored in another window).";
                RemovePanelEntry(nodeId, kind);
                break;
            case FolderClearOutcome.MarkedAgain:
                notice = kind == FolderLifecycleKind.Archive
                    ? $"“{name}” was archived again elsewhere. Undo is no longer available; use Archived to unarchive it."
                    : $"“{name}” was moved to the Trash again elsewhere. Undo is no longer available; use the Trash to restore it.";
                break;
            case FolderClearOutcome.ParentArchived:
                notice = $"“{name}” is inside an archived folder. Unarchive that folder first; nothing was changed.";
                break;
            case FolderClearOutcome.ParentTrashed:
                notice = $"The folder that holds “{name}” is in the Trash. Restore that folder first; nothing was changed.";
                break;
            case FolderClearOutcome.NameConflict:
                notice = $"The folder it came from already contains “{result.BlockingName ?? name}”. Rename or move that item first; nothing was restored.";
                break;
            default:
                notice = "This folder no longer exists.";
                RemovePanelEntry(nodeId, kind);
                ApplyRemoved(nodeId);
                break;
        }

        Trash.OnLifecycleCompleted();
        Archived.OnLifecycleCompleted();
        return (notice, placed);
    }

    private void RemovePanelEntry(long nodeId, FolderLifecycleKind kind)
    {
        if (kind == FolderLifecycleKind.Archive)
            Archived.RemoveEntry(nodeId);
        else
            Trash.RemoveEntry(nodeId);
    }

    /// <summary>
    /// The completion boundary (§8.3.1): marks the node so lazy loads that started earlier skip it, and counts a lifecycle
    /// completion so reveal snapshots and Trash reads that started earlier are read again.
    /// </summary>
    private void Touch(long nodeId)
    {
        _touchedByReveal[nodeId] = ++_revealSequence;
        _lifecycleCompletions++;
    }

    private bool IsTouchedSince(long nodeId, int sequence) => _touchedByReveal.TryGetValue(nodeId, out var touched) && touched > sequence;

    /// <summary>Where the tree stood when a read started: the touch sequence and the instance registrations so far.</summary>
    private readonly record struct ReadMark(int Sequence, int Registrations);

    private ReadMark MarkRead() => new(_revealSequence, _registrations);

    /// <summary>
    /// A read that started at <paramref name="mark"/> may still change these nodes: none was touched by a newer committed result
    /// or reveal, and no cached instance of them was registered after the read started (a newer load or placement).
    /// </summary>
    private bool IsFreshFor(IEnumerable<long> nodeIds, ReadMark mark) =>
        nodeIds.All(id => !IsTouchedSince(id, mark.Sequence) && (!_nodes.TryGetValue(id, out var node) || node.RegisteredAt <= mark.Registrations));

    /// <summary>
    /// A committed removal from the tree (trashed or archived Project or Folder subtree, removed folder, node found not live or
    /// gone): by Id, wherever its instance is. §7.9 C1 — ONE synchronous step with no await between its parts: touch the root
    /// once; clear the selection quietly iff the selected instance is the root or has it on its Parent chain, checked NOW, at
    /// application time (C8) — which deactivates exactly the content view current at that moment; remove the root row; forget the
    /// root and every cached descendant (their in-flight loads are discarded by C2). Other selections, views, visits and drafts
    /// are untouched; counts refresh through the completion path.
    /// </summary>
    private void ApplyRemoved(long nodeId)
    {
        Touch(nodeId);
        _ = Trash.RefreshCount(); // a Project or Folder may have entered the Trash (here or in another window)
        _ = Archived.RefreshCount(); // ... or may have been archived
        if (!_nodes.TryGetValue(nodeId, out var node))
            return;
        if (SelectedNode is { } selected && IsSelfOrBelow(selected, node))
            ClearSelectionQuietly(); // BEFORE the row leaves: a tree whose selected row is removed selects that row's parent itself
        CollectionOf(node).Remove(node);
        ForgetSubtree(node);
    }

    private static bool IsSelfOrBelow(WorkspaceNodeViewModel candidate, WorkspaceNodeViewModel root)
    {
        for (var current = candidate; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, root))
                return true;
        }

        return false;
    }

    /// <summary>
    /// §7.9 C3/C4: a fresh read says the node is not live (itself, or through an ancestor): C1 on the OUTERMOST cached node of the
    /// chain from the effective root down to the node, with a notice. Nothing else is read or reloaded.
    /// </summary>
    /// <returns>False when the result is stale for any node of the chain (a newer result or instance stands; nothing is applied).</returns>
    private bool ApplyNotLive(long nodeId, IReadOnlyList<long> chainFromRoot, EffectiveLifecycle state, ReadMark mark) =>
        ApplyNotLive(nodeId, chainFromRoot, state, mark, confirmed: false, ReadNotice.Show, out _);

    /// <summary>
    /// Phase 19 (D10, R2c): every caller is a PURE READ, so the removal is read-derived: it writes read-update stamps (for the
    /// removed root and every cached descendant), never <see cref="Touch"/>. Rule L (a committed write, or a newer
    /// instance, after the read started) still discards. Rule C: if a read-derived update changed a node of the chain after
    /// the read started and the branch is still shown, nothing is removed and one confirmation read is requested instead
    /// (<paramref name="confirmed"/> = this IS the confirmation: no rule C).
    /// Phase 20 (R2a-2): <paramref name="notice"/> = <see cref="ReadNotice.Silent"/> applies the same tree effects without the banner,
    /// and a confirmation it requests is silent too.
    /// </summary>
    /// <returns>True when the removal was applied; false when stale (rule L) or held back for a confirmation (rule C).</returns>
    private bool ApplyNotLive(long nodeId, IReadOnlyList<long> chainFromRoot, EffectiveLifecycle state, ReadMark mark, bool confirmed, ReadNotice notice, out Task? confirmation)
    {
        confirmation = null;
        var chain = chainFromRoot.Append(nodeId).ToList();
        if (!IsFreshFor(chain, mark))
            return false;
        var outermost = chainFromRoot.Where(_nodes.ContainsKey).Select(id => (long?)id).FirstOrDefault() ?? nodeId;
        if (!confirmed && _nodes.ContainsKey(outermost) && chain.Any(id => ReadUpdatedSince(id, mark)))
        {
            confirmation = RequestConfirmation(nodeId, notice);
            return false;
        }

        ApplyRemovedByRead(outermost);
        if (notice == ReadNotice.Silent)
            return true; // R2a-2: the Bulk Unarchive keeps the Workspace banner (and the Phase 19 Details it guards)
        BannerMessage = (state.RootType, state.State, state.RootId == nodeId) switch
        {
            (WorkspaceNodeType.Project, LifecycleState.Trashed, _) => WorkspaceService.ProjectInTrash,
            (WorkspaceNodeType.Project, _, _) => ProjectArchivedNotice,
            (_, LifecycleState.Trashed, true) => "This folder is in the Trash.",
            (_, LifecycleState.Trashed, false) => "This item's folder is in the Trash.",
            (_, _, true) => "This folder is archived.",
            _ => "This item's folder is archived.",
        };
        return true;
    }

    // ---- Phase 19: conflicting reads (D10; proposal §4.4.4, R2b–R2d) ------------------------------------------------------

    /// <summary>A read mark registered as in flight until <see cref="EndTrackedRead"/> (lazy loads, details, Edit, folder check,
    /// reconcile, confirmation).</summary>
    private ReadMark BeginTrackedRead()
    {
        var mark = MarkRead();
        _readsInFlight[mark.Sequence] = _readsInFlight.GetValueOrDefault(mark.Sequence) + 1;
        return mark;
    }

    private void EndTrackedRead(ReadMark mark)
    {
        if (_readsInFlight.TryGetValue(mark.Sequence, out var count))
        {
            if (count <= 1)
                _readsInFlight.Remove(mark.Sequence);
            else
                _readsInFlight[mark.Sequence] = count - 1;
        }

        PruneReadUpdates();
    }

    /// <summary>A stamp no newer than every in-flight read mark can never trigger rule C again (future marks are newer too).</summary>
    private void PruneReadUpdates()
    {
        if (_readUpdated.Count == 0)
            return;
        if (_readsInFlight.Count == 0)
        {
            _readUpdated.Clear();
            return;
        }

        var oldest = _readsInFlight.Keys.First();
        foreach (var id in _readUpdated.Where(e => e.Value <= oldest).Select(e => e.Key).ToList())
            _readUpdated.Remove(id);
    }

    /// <summary>Rule C trigger: a read-derived update changed this node after the read that <paramref name="mark"/> started.</summary>
    private bool ReadUpdatedSince(long nodeId, ReadMark mark) => _readUpdated.TryGetValue(nodeId, out var stamp) && stamp > mark.Sequence;

    /// <summary>A read-derived tree update: one new sequence for every Id given (never a committed stamp).</summary>
    /// <param name="countCompletion">True where the update replaces a former <see cref="Touch"/> (reconcile, read-derived removal,
    /// confirmation): reveal snapshots and Trash reads that started earlier are read again, as before. A lazy-load move never
    /// counted and still does not.</param>
    private void StampRead(IReadOnlyCollection<long> nodeIds, bool countCompletion = true)
    {
        var sequence = ++_revealSequence;
        foreach (var id in nodeIds)
            _readUpdated[id] = sequence;
        if (countCompletion)
            _lifecycleCompletions++;
    }

    private static List<long> SubtreeIds(WorkspaceNodeViewModel node)
    {
        var ids = new List<long> { node.Id };
        foreach (var child in node.Children.Where(c => !c.IsPlaceholder))
            ids.AddRange(SubtreeIds(child));
        return ids;
    }

    /// <summary>
    /// A READ-derived removal (R2c): as <see cref="ApplyRemoved"/>, but the removed root and every cached descendant it forgets get
    /// a read-update stamp instead of a committed one, so a later read that saw the node active can still be confirmed.
    /// </summary>
    private void ApplyRemovedByRead(long nodeId)
    {
        var cached = _nodes.GetValueOrDefault(nodeId);
        StampRead(cached is null ? new List<long> { nodeId } : SubtreeIds(cached));
        _ = Trash.RefreshCount();
        _ = Archived.RefreshCount();
        if (cached is null)
            return;
        if (SelectedNode is { } selected && IsSelfOrBelow(selected, cached))
            ClearSelectionQuietly(); // BEFORE the row leaves (see ApplyRemoved)
        CollectionOf(cached).Remove(cached);
        ForgetSubtree(cached);
    }

    /// <summary>A read found the node missing: removed (read class), or — rule C — a confirmation is requested and returned.</summary>
    private Task? RemoveMissingByRead(long nodeId, ReadMark mark, ReadNotice notice = ReadNotice.Show)
    {
        if (_nodes.ContainsKey(nodeId) && ReadUpdatedSince(nodeId, mark))
            return RequestConfirmation(nodeId, notice);
        ApplyRemovedByRead(nodeId);
        return null;
    }

    /// <summary>
    /// A read found a cached node active under another parent (reconcile or confirmation): placed there when that parent is the
    /// top level or a loaded folder, otherwise removed so it appears with that folder's load. Read class (no committed stamp).
    /// </summary>
    private void ApplyActiveElsewhere(WorkspaceNodeViewModel node, long? parentId)
    {
        if (parentId is null || (_nodes.TryGetValue(parentId.Value, out var loaded) && loaded.IsChildrenLoaded))
        {
            StampRead([node.Id]);
            Place(node, parentId is null ? null : _nodes[parentId.Value]);
            return;
        }

        StampRead(SubtreeIds(node));
        if (ReferenceEquals(SelectedNode, node))
            ClearSelectionQuietly(); // before the row leaves (see ApplyRemoved)
        CollectionOf(node).Remove(node); // its new parent is not loaded here: it appears when that folder loads
        ForgetSubtree(node);
    }

    /// <summary>The latest confirmation read requested (tests await it).</summary>
    internal Task CurrentConfirmation { get; private set; } = Task.CompletedTask;

    /// <summary>Confirmation reads started so far (tests).</summary>
    internal int ConfirmationsStarted { get; private set; }

    /// <summary>Tests: the node has a read-update stamp that has not been pruned.</summary>
    internal bool HasReadUpdate(long nodeId) => _readUpdated.ContainsKey(nodeId);

    /// <summary>Tests: the node has a committed stamp newer than <paramref name="sequence"/>.</summary>
    internal bool IsCommittedSince(long nodeId, int sequence) => IsTouchedSince(nodeId, sequence);

    /// <summary>Tests: the current sequence (read marks are taken from it).</summary>
    internal int CurrentSequence => _revealSequence;

    /// <summary>Tests: a confirmation of <paramref name="nodeId"/> is running and one more request is queued behind it.</summary>
    internal bool IsConfirmationQueued(long nodeId) => _confirmations.TryGetValue(nodeId, out var slot) && slot.Queued;

    private void RequestConfirmations(IReadOnlyList<long> nodeIds)
    {
        foreach (var id in nodeIds)
            _ = RequestConfirmation(id);
    }

    /// <summary>
    /// Rule C: one confirmation read of <paramref name="nodeId"/>, started now (after the conflicting result returned, so its
    /// snapshot is not older than either conflicting read). Per node at most one running and one queued: a request while one
    /// runs is queued (coalesced) and served by the same task.
    /// </summary>
    /// <param name="notice">Phase 20 (R2a-2): a run is silent only while every request it serves is silent; a
    /// <see cref="ReadNotice.Show"/> request that joins a running slot makes it show its banner, and a silent request never makes a
    /// showing run silent.</param>
    private Task RequestConfirmation(long nodeId, ReadNotice notice = ReadNotice.Show)
    {
        if (_confirmations.TryGetValue(nodeId, out var running))
        {
            running.Queued = true;
            if (notice == ReadNotice.Show)
                running.Silent = false;
            return running.Task;
        }

        var slot = new ConfirmationSlot { Silent = notice == ReadNotice.Silent };
        _confirmations[nodeId] = slot;
        slot.Task = RunConfirmationsAsync(nodeId, slot);
        return CurrentConfirmation = slot.Task;
    }

    private async Task RunConfirmationsAsync(long nodeId, ConfirmationSlot slot)
    {
        try
        {
            while (true)
            {
                slot.Queued = false;
                var outcome = await ConfirmOnceAsync(nodeId, slot);
                if (outcome == ConfirmationOutcome.Failed)
                    return; // R2b: logged, nothing applied, no retry and no follow-up
                if (outcome == ConfirmationOutcome.Superseded)
                {
                    // Rule L: the committed write already decided the node; a queued request in this run is dropped too,
                    // because any read it would make cannot be newer than the commit's own placement.
                    slot.Queued = false;
                    return;
                }
                if (outcome == ConfirmationOutcome.Done && !slot.Queued)
                    return;
                // Again (another read-derived update landed while it ran) or a queued request: exactly one more read.
            }
        }
        finally
        {
            if (_confirmations.TryGetValue(nodeId, out var current) && ReferenceEquals(current, slot))
                _confirmations.Remove(nodeId);
        }
    }

    private async Task<ConfirmationOutcome> ConfirmOnceAsync(long nodeId, ConfirmationSlot slot)
    {
        ConfirmationsStarted++;
        var sequence = _revealSequence;
        var mark = BeginTrackedRead();
        try
        {
            WorkspaceNodeState? state;
            try
            {
                state = await _workspace.GetNodeStateAsync(nodeId);
            }
            catch (Exception ex)
            {
                _log.Error("A Workspace node could not be confirmed after two overlapping reads.", ex); // no names
                return ConfirmationOutcome.Failed;
            }

            if (IsTouchedSince(nodeId, sequence))
            {
                ConfirmationsSuperseded++;
                return ConfirmationOutcome.Superseded; // rule L: a committed result of this window is newer; discard, end the run
            }
            if (ReadUpdatedSince(nodeId, mark))
                return ConfirmationOutcome.Again; // another read-derived update landed after this read started
            // The mode is read when the result is applied, so a Show request that joined while this read ran still gets its banner.
            var notice = slot.Silent ? ReadNotice.Silent : ReadNotice.Show;
            return ApplyConfirmed(nodeId, state, mark, notice) ? ConfirmationOutcome.Done : ConfirmationOutcome.Failed;
        }
        finally
        {
            EndTrackedRead(mark);
        }
    }

    /// <summary>
    /// Applies a confirmation read with the read-class rules. Not found / not live keep their own branches. An active node that is
    /// cached is placed or removed (<see cref="ApplyActiveElsewhere"/>). An active node that left the cache is re-created under a
    /// loaded parent (or at the top level) ONLY from this snapshot's <see cref="WorkspaceNodeState.Node"/> (R2d); a missing or
    /// inconsistent Node creates nothing and counts as a failed confirmation.
    /// </summary>
    /// <returns>False only for a missing or inconsistent <see cref="WorkspaceNodeState.Node"/>.</returns>
    private bool ApplyConfirmed(long nodeId, WorkspaceNodeState? state, ReadMark mark, ReadNotice notice)
    {
        if (state is null)
        {
            if (_nodes.ContainsKey(nodeId))
                ApplyRemovedByRead(nodeId);
            return true;
        }

        if (!state.Effective.IsLive)
        {
            ApplyNotLive(nodeId, state.ChainFromRoot, state.Effective, mark, confirmed: true, notice, out _);
            return true;
        }

        var parentId = state.ParentId;
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            if (node.Parent?.Id != parentId || !CollectionOf(node).Contains(node))
                ApplyActiveElsewhere(node, parentId);
            return true;
        }

        return PlaceUncachedFromState(nodeId, state);
    }

    /// <summary>
    /// A live node that is not cached: re-created under a loaded parent (or at the top level) ONLY from this snapshot's
    /// <see cref="WorkspaceNodeState.Node"/> (R2d) and placed once; under a parent that is not loaded the parent only gets
    /// <c>HasChildren</c>. Shared by the confirmation (Phase 19) and the Bulk Unarchive re-placement (Phase 20, §4.5.2).
    /// </summary>
    /// <returns>False only for a missing or inconsistent <see cref="WorkspaceNodeState.Node"/> (nothing created).</returns>
    private bool PlaceUncachedFromState(long nodeId, WorkspaceNodeState state)
    {
        var parentId = state.ParentId;
        WorkspaceNodeViewModel? parent = null;
        var parentLoaded = parentId is null ? IsActivated : _nodes.TryGetValue(parentId.Value, out parent) && parent.IsChildrenLoaded;
        if (!parentLoaded)
        {
            parent?.SetHasChildren(true); // it appears with that folder's load (as ApplyRestoredAsync)
            return true;
        }

        if (state.Node is not { } row || row.Id != nodeId || row.ParentId != parentId || row.Type != state.Type || state.Archived || state.Trashed)
        {
            _log.Error("A Workspace read returned node data that does not match its state; no row was created.");
            return false;
        }

        var created = GetOrCreate(row, parent);
        StampRead([nodeId]);
        Place(created, parent);
        return true;
    }

    /// <summary>Clears the selection without cancelling a reveal in flight (it is not a user selection).</summary>
    private void ClearSelectionQuietly()
    {
        _quietSelection = true;
        try
        {
            SelectedNode = null;
        }
        finally
        {
            _quietSelection = false;
        }
    }

    /// <summary>
    /// A committed Restore: if the parent's child load is in flight it is awaited FIRST, then the node is touched and merged
    /// by Id, so it appears exactly once whether that load's snapshot predates the commit or not (§8.3.2).
    /// </summary>
    /// <returns>The placed instance, or null when its parent is not loaded in this tree.</returns>
    private async Task<WorkspaceNodeViewModel?> ApplyRestoredAsync(WorkspaceNode restored)
    {
        WorkspaceNodeViewModel? parent = null;
        if (restored.ParentId is { } parentId && _nodes.TryGetValue(parentId, out parent) && parent.ChildrenLoad is { } pending)
            await pending;

        Touch(restored.Id);
        if (restored.ParentId is { } id && (!_nodes.TryGetValue(id, out parent) || !parent.IsChildrenLoaded))
        {
            parent?.SetHasChildren(true); // appears with the folder's next load
            return null;
        }

        var node = GetOrCreate(restored, parent);
        if ((parent?.Children ?? Roots).All(c => c.Id != node.Id))
            Place(node, parent);
        return node;
    }

    /// <summary>
    /// Stale-node reconciliation (§8.4): a fresh typed read of the node, applied by Id unless a newer completion or reveal
    /// already reconciled it. Trashed or missing → removed; active under another parent → placed there if loaded, else
    /// forgotten; active in place → nothing.
    /// </summary>
    internal Task ReconcileNodeAsync(long nodeId, string? missingMessage = null) =>
        CurrentReconciliation = ReconcileNodeCoreAsync(nodeId, missingMessage);

    private async Task ReconcileNodeCoreAsync(long nodeId, string? missingMessage)
    {
        var sequence = _revealSequence;
        var mark = BeginTrackedRead();
        Task? confirmation;
        try
        {
            WorkspaceNodeState? state;
            try
            {
                state = await _workspace.GetNodeStateAsync(nodeId);
            }
            catch (Exception ex)
            {
                _log.Error("A Workspace node could not be re-read after a refused change.", ex); // the dialog already showed the refusal
                return;
            }

            confirmation = ApplyReconciled(nodeId, state, sequence, mark, missingMessage);
        }
        finally
        {
            EndTrackedRead(mark);
        }

        // R2b: the reconcile's task completes after its own confirmation read (the Bulk Archive batch awaits it).
        if (confirmation is not null)
            await confirmation;
    }

    /// <param name="notice">Phase 20 (R2a-2): <see cref="ReadNotice.Silent"/> for the Bulk Unarchive re-placement (no banner).</param>
    /// <param name="replace">Phase 20 (§4.5.2): the re-placement mode. An uncached live node is placed (re-created only from this
    /// read's <see cref="WorkspaceNodeState.Node"/>) instead of "not shown here → nothing"; an instance registered after the read
    /// started counts as a newer read-derived update (rule C) like a read-update stamp.</param>
    /// <returns>The confirmation read requested under rule C, or null.</returns>
    private Task? ApplyReconciled(long nodeId, WorkspaceNodeState? state, int sequence, ReadMark mark, string? missingMessage,
        ReadNotice notice = ReadNotice.Show, bool replace = false)
    {
        if (IsTouchedSince(nodeId, sequence))
            return null; // rule L: a newer committed result or reveal already applied a fresher state
        if (state is null)
        {
            if (RemoveMissingByRead(nodeId, mark, notice) is { } held)
                return held;
            if (notice == ReadNotice.Show)
                BannerMessage = missingMessage ?? "This item no longer exists.";
            return null;
        }

        if (!state.Effective.IsLive)
        {
            // §7.9 C4: by effective state; never applied when stale for any node of that chain (rule L) or held back (rule C).
            ApplyNotLive(nodeId, state.ChainFromRoot, state.Effective, mark, confirmed: false, notice, out var confirmation);
            return confirmation;
        }

        if (!_nodes.TryGetValue(nodeId, out var node))
        {
            if (!replace)
                return null; // not shown here
            if (ReadUpdatedSince(nodeId, mark))
                return RequestConfirmation(nodeId, notice); // rule C: a newer read-derived update removed it meanwhile
            PlaceUncachedFromState(nodeId, state); // a missing or inconsistent Node is logged and creates nothing (no retry)
            return null;
        }

        if (node.Parent?.Id == state.ParentId && (!replace || CollectionOf(node).Contains(node)))
            return null; // active in place
        if (ReadUpdatedSince(nodeId, mark) || (replace && node.RegisteredAt > mark.Registrations))
            return RequestConfirmation(nodeId, notice); // rule C: a newer read-derived update moved it; a fresh read decides
        ApplyActiveElsewhere(node, state.ParentId); // read class: a read-update stamp, never Touch (R2b)
        if (replace && state.ParentId is { } parentId && _nodes.TryGetValue(parentId, out var parent) && !parent.IsChildrenLoaded)
            parent.SetHasChildren(true); // it appears with that folder's load
        return null;
    }

    /// <summary>
    /// Phase 20 (§4.5.2, R2-2): after a <c>NotArchived</c> or <c>ProjectTrashed</c> refusal of the Bulk Unarchive, ONE fresh read of
    /// that Project (a D10 tracked read) applied by the re-placement mode of the reconcile path, silently (R2a-2: no Workspace
    /// banner). Rules L and C as in Phase 19; a failed read is logged and applies nothing (no retry). It never selects a node or
    /// expands a folder, and writes nothing to the database.
    /// </summary>
    internal Task ReplaceByReadAsync(long nodeId) => CurrentReconciliation = ReplaceByReadCoreAsync(nodeId);

    private async Task ReplaceByReadCoreAsync(long nodeId)
    {
        var sequence = _revealSequence;
        var mark = BeginTrackedRead();
        Task? confirmation;
        try
        {
            WorkspaceNodeState? state;
            try
            {
                state = await _workspace.GetNodeStateAsync(nodeId);
            }
            catch (Exception ex)
            {
                _log.Error("A Workspace project could not be re-read after a refused bulk unarchive.", ex); // no names
                return;
            }

            confirmation = ApplyReconciled(nodeId, state, sequence, mark, missingMessage: null, ReadNotice.Silent, replace: true);
        }
        finally
        {
            EndTrackedRead(mark);
        }

        if (confirmation is not null)
            await confirmation;
    }

    /// <summary>Tests (BU6j): a rule-C confirmation request for <paramref name="nodeId"/> with the given notice mode, as any flow would make it.</summary>
    internal Task RequestConfirmationForTests(long nodeId, ReadNotice notice) => RequestConfirmation(nodeId, notice);

    private void OfferUndo(UndoOffer offer)
    {
        _undo = offer;
        OnPropertyChanged(nameof(IsUndoAvailable));
        UndoCommand.RaiseCanExecuteChanged();
    }

    private void ClearUndo()
    {
        if (_undo is null)
            return;
        _undo = null;
        OnPropertyChanged(nameof(IsUndoAvailable));
        UndoCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Leaving the tab (or Retry) ends the Workspace visit: its Undo is withdrawn and can never return.</summary>
    private void EndVisit()
    {
        _visit++;
        ClearUndo();
    }

    private Task OpenTrashAsync()
    {
        if (!IsActivated || IsTrashOpen)
            return Task.CompletedTask;
        InvalidateReveal(); // opening a panel is the user's own action: a reveal still in flight never overrides it
        CloseArchived(); // one panel at a time
        IsTrashOpen = true; // the selection and its content view stay as they are, hidden behind the panel
        return Trash.Open();
    }

    private Task OpenArchivedAsync()
    {
        if (!IsActivated || IsArchivedOpen)
            return Task.CompletedTask;
        InvalidateReveal(); // opening a panel is the user's own action: a reveal still in flight never overrides it
        CloseTrash(); // one panel at a time
        IsArchivedOpen = true; // the selection and its content view stay as they are, hidden behind the panel
        return Archived.Open();
    }

    private void CloseArchived()
    {
        if (!IsArchivedOpen)
            return;
        Archived.Close();
        IsArchivedOpen = false;
    }

    private void CloseTrash()
    {
        if (!IsTrashOpen)
            return;
        Trash.Close();
        IsTrashOpen = false;
    }

    // ---- Search reveal (Phase 6 proposal §4.5) -------------------------------------------------------------------------

    /// <summary>Cancels a reveal in flight at its next step (new search, manual selection, write command, retry, leaving the tab).</summary>
    internal void InvalidateReveal() => _revealVersion++;

    /// <summary>The last reveal's work; lets tests await it.</summary>
    internal Task<WorkspaceRevealOutcome> CurrentReveal { get; private set; } = Task.FromResult(WorkspaceRevealOutcome.Superseded);

    /// <summary>
    /// Reveals a search result in its CURRENT location, identified by (Kind, Id):
    /// <list type="number">
    /// <item>takes a reveal version and the branch stamps of every cached collection;</item>
    /// <item>resolves through the bounded runner (one running + newest pending) — one read snapshot of the ancestor chain
    /// and every branch on it;</item>
    /// <item>gives up silently if the version moved on; NotFound and Trashed are reported;</item>
    /// <item>if a branch on the path changed locally during the read, or a lazy load of one is still in flight, retries
    /// once; a second time reports <see cref="WorkspaceRevealOutcome.WorkspaceChanged"/> and changes nothing;</item>
    /// <item>applies the snapshot synchronously (no await) to the path branches only, expands the ancestors and selects the
    /// Folder/Project;</item>
    /// <item>for content, opens the item's container in the Project's content view and selects the row of that kind and Id.</item>
    /// </list>
    /// Never writes, never reloads the whole tree, never opens a link or the note editor.
    /// </summary>
    internal Task<WorkspaceRevealOutcome> RevealAsync(SearchItemRef item) => CurrentReveal = RevealCoreAsync(item);

    /// <summary>The most recent Open destination (Phase 15); lets tests await it.</summary>
    internal Task CurrentDestinationReveal { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Phase 15 Open destination: the copy is found by its NEW identity (Kind, Id) and revealed at its CURRENT location, through
    /// the same fresh resolve as a search result. A copy that was trashed, purged or moved meanwhile is reported, never guessed.
    /// </summary>
    private async Task RevealDestinationAsync(ContentRef item)
    {
        var kind = item.Kind switch
        {
            ContentKind.Folder => SearchResultKind.ProjectFolder,
            ContentKind.Link => SearchResultKind.Link,
            _ => SearchResultKind.Note,
        };
        var outcome = await RevealAsync(new SearchItemRef(kind, item.Id));
        BannerMessage = outcome switch
        {
            WorkspaceRevealOutcome.Selected or WorkspaceRevealOutcome.Superseded => BannerMessage,
            WorkspaceRevealOutcome.NotFound => "The copy no longer exists.",
            WorkspaceRevealOutcome.Trashed => "The copy is in the Trash.",
            WorkspaceRevealOutcome.FolderTrashed => "The copy's folder is in the Trash.",
            WorkspaceRevealOutcome.ProjectTrashed => "The copy's project is in the Trash.",
            WorkspaceRevealOutcome.Archived => "The copy's project is archived — it is selected under Archived.",
            WorkspaceRevealOutcome.InArchivedFolder => "The copy's project is in an archived folder — that folder is selected under Archived.",
            WorkspaceRevealOutcome.MovedMeanwhile or WorkspaceRevealOutcome.ContainerMissing or WorkspaceRevealOutcome.WorkspaceChanged or WorkspaceRevealOutcome.NoLongerArchived
                => "The copy was moved or changed meanwhile. Try Open destination again.",
            WorkspaceRevealOutcome.Busy => "The database stayed busy (another program or window is writing). Try again.",
            _ => "The copy could not be shown. Try again.",
        };
    }

    private async Task<WorkspaceRevealOutcome> RevealCoreAsync(SearchItemRef item)
    {
        if (_resolveRunner is null)
            return WorkspaceRevealOutcome.Failed;
        var version = ++_revealVersion;
        await ActivateAsync();
        if (!IsActivated || version != _revealVersion)
            return IsActivated ? WorkspaceRevealOutcome.Superseded : WorkspaceRevealOutcome.Failed;

        SearchRevealTarget? target = null;
        for (var attempt = 1; attempt <= 2 && target is null; attempt++)
        {
            var stamps = CaptureStamps();
            var outcome = await _resolveRunner.Submit(item);
            if (version != _revealVersion || outcome.Status == RunStatus.Superseded)
                return WorkspaceRevealOutcome.Superseded;
            if (outcome.Status == RunStatus.Failed)
            {
                if (outcome.Error is not WorkspaceSearchBusyException)
                    _log.Error("A search result could not be resolved.", outcome.Error!); // no query text or content
                return outcome.Error is WorkspaceSearchBusyException ? WorkspaceRevealOutcome.Busy : WorkspaceRevealOutcome.Failed;
            }

            if (outcome.Value!.Status != RevealResolveStatus.Found)
            {
                // A negative answer is as stale as a snapshot when a committed lifecycle result was applied after this
                // resolve started (e.g. a Restore): it counts as the detected change and uses the same bounded retry.
                if (_lifecycleCompletions != stamps.Completions)
                    continue;
                if (outcome.Value.Status is RevealResolveStatus.Archived or RevealResolveStatus.ProjectArchived)
                    return await RevealInArchivedAsync(outcome.Value.ArchivedRootId!.Value, version);
                if (outcome.Value.Status == RevealResolveStatus.InArchivedFolder)
                {
                    // §7.1: the Archived panel lists the OUTERMOST archived Folder; its row is selected, the notice names the item.
                    var archived = await RevealInArchivedAsync(outcome.Value.ArchivedRootId!.Value, version);
                    return archived == WorkspaceRevealOutcome.Archived ? WorkspaceRevealOutcome.InArchivedFolder : archived;
                }

                return outcome.Value.Status switch
                {
                    RevealResolveStatus.Trashed => WorkspaceRevealOutcome.Trashed,
                    RevealResolveStatus.ProjectTrashed => WorkspaceRevealOutcome.ProjectTrashed,
                    RevealResolveStatus.FolderTrashed => WorkspaceRevealOutcome.FolderTrashed,
                    _ => WorkspaceRevealOutcome.NotFound,
                };
            }

            var candidate = outcome.Value.Target!;
            var pendingLoads = PathNodes(candidate).Select(n => n.ChildrenLoad).OfType<Task<bool>>().ToList();
            if (pendingLoads.Count > 0)
            {
                await Task.WhenAll(pendingLoads); // an older lazy load must land (or be discarded) before we apply
                if (version != _revealVersion)
                    return WorkspaceRevealOutcome.Superseded;
                continue;
            }

            if (PathChanged(candidate, stamps))
                continue;
            target = candidate;
        }

        if (target is null)
            return WorkspaceRevealOutcome.WorkspaceChanged;

        ApplyReveal(target);
        if (target.Content is not { } content)
            return WorkspaceRevealOutcome.Selected;

        if (SelectedContent is not { } view || view.ProjectNodeId != content.ProjectNodeId)
            return WorkspaceRevealOutcome.Failed; // content view not wired
        var kind = content.Kind switch
        {
            SearchResultKind.ProjectFolder => ProjectContentItemKind.Folder,
            SearchResultKind.Link => ProjectContentItemKind.Link,
            SearchResultKind.Note => ProjectContentItemKind.Note,
            _ => throw new InvalidOperationException($"{content.Kind} is not a content kind."),
        };
        var revealed = await view.RevealAsync(content.ContainerFolderId, kind, content.Id);
        if (version != _revealVersion)
            return WorkspaceRevealOutcome.Superseded;
        return revealed switch
        {
            ContentRevealOutcome.Selected => WorkspaceRevealOutcome.Selected,
            ContentRevealOutcome.MovedMeanwhile => WorkspaceRevealOutcome.MovedMeanwhile,
            ContentRevealOutcome.ContainerMissing => WorkspaceRevealOutcome.ContainerMissing,
            ContentRevealOutcome.LoadFailed => WorkspaceRevealOutcome.Failed,
            _ => WorkspaceRevealOutcome.Superseded,
        };
    }

    /// <summary>
    /// Typed archived-search navigation (§8.4; Phase 13 §7.1: the archive ROOT, a Project or the outermost archived Folder): opens
    /// the Archived panel and selects the root's row, by node Id, from a panel read that started AFTER the resolve (never a list already shown or in flight; a lifecycle
    /// completion during the read means one re-read). Gives up (Superseded) when the reveal version moved on (new reveal,
    /// manual selection, tab leave, Retry, Workspace write) or the user selected a row, pressed Back or closed the panel.
    /// </summary>
    private async Task<WorkspaceRevealOutcome> RevealInArchivedAsync(long projectNodeId, int version)
    {
        CloseTrash();
        IsArchivedOpen = true; // the tree selection and its content view stay as they are, hidden behind the panel
        var userSelection = Archived.UserSelectionVersion;
        var read = Archived.ReloadForNavigation();
        do
        {
            await Archived.CurrentLoad;
            if (version != _revealVersion || !IsArchivedOpen || Archived.UserSelectionVersion != userSelection)
                return WorkspaceRevealOutcome.Superseded; // the user's later action stands
        }
        while (Archived.IsLoading); // a completion during the read started one re-read: wait for that one

        if (Archived.LoadError is not null)
            return WorkspaceRevealOutcome.Failed;
        if (Archived.AppliedVersion < read)
            return WorkspaceRevealOutcome.Superseded;
        return Archived.SelectByNodeId(projectNodeId) ? WorkspaceRevealOutcome.Archived : WorkspaceRevealOutcome.NoLongerArchived;
    }

    /// <summary>The roots stamp, every cached branch stamp and the lifecycle-completion count, taken BEFORE a resolve.</summary>
    private (int Roots, int Completions, Dictionary<long, int> Nodes) CaptureStamps() =>
        (_rootsStamp, _lifecycleCompletions, _nodes.Values.ToDictionary(n => n.Id, n => n.BranchStamp));

    /// <summary>Cached instances whose Children the snapshot reconciles (the ancestors), plus the target's current parent.</summary>
    private IEnumerable<WorkspaceNodeViewModel> PathNodes(SearchRevealTarget target)
    {
        foreach (var id in target.WorkspaceAncestorIds)
        {
            if (_nodes.TryGetValue(id, out var node))
                yield return node;
        }

        if (_nodes.TryGetValue(target.WorkspaceNodeId, out var self) && self.Parent is { } parent)
            yield return parent;
    }

    /// <summary>
    /// A path branch, the roots, or ANY committed lifecycle result applied since the stamps were taken: the snapshot may predate
    /// that commit (even for an unloaded branch whose stamps cannot show it), so it is never applied as is (§8.3.3).
    /// </summary>
    private bool PathChanged(SearchRevealTarget target, (int Roots, int Completions, Dictionary<long, int> Nodes) before) =>
        _rootsStamp != before.Roots
        || _lifecycleCompletions != before.Completions
        || PathNodes(target).Any(n => !before.Nodes.TryGetValue(n.Id, out var stamp) || stamp != n.BranchStamp);

    /// <summary>
    /// Synchronous, root level first. Loaded collections on the path become exactly the snapshot (a Complete level removes
    /// what is no longer there; an incomplete level only adds/relocates); unloaded ones are filled from the snapshot.
    /// Removed instances that the snapshot does not place elsewhere on the path are forgotten (their new location is in a
    /// branch this reveal does not read). Other branches are untouched, except that a relocated instance leaves its former
    /// collection.
    /// </summary>
    private void ApplyReveal(SearchRevealTarget target)
    {
        _applyingReveal = true;
        try
        {
            var sequence = ++_revealSequence;
            var placedOnPath = target.Branches.SelectMany(b => b.Children).Select(c => c.Id).ToHashSet();
            var removed = new List<WorkspaceNodeViewModel>(); // forgotten only after EVERY level is placed (see below)
            foreach (var branch in target.Branches)
            {
                var parent = branch.ParentId is { } parentId ? _nodes[parentId] : null; // placed by the previous level
                var collection = parent?.Children ?? Roots;
                var loaded = parent is null || parent.IsChildrenLoaded;
                if (loaded && branch.Complete)
                {
                    var fresh = branch.Children.Select(c => c.Id).ToHashSet();
                    foreach (var stale in collection.Where(c => !c.IsPlaceholder && !fresh.Contains(c.Id)).ToList())
                    {
                        collection.Remove(stale);
                        _touchedByReveal[stale.Id] = sequence;
                        if (!placedOnPath.Contains(stale.Id))
                            removed.Add(stale);
                    }
                }

                foreach (var row in branch.Children)
                {
                    Place(GetOrCreate(row, parent), parent);
                    _touchedByReveal[row.Id] = sequence;
                }

                if (parent is not null && !parent.IsChildrenLoaded && branch.Complete)
                {
                    foreach (var placeholder in parent.Children.Where(c => c.IsPlaceholder).ToList())
                        parent.Children.Remove(placeholder);
                    parent.IsChildrenLoaded = true;
                }

                if (parent is not null)
                    parent.ReconcileStamp++; // roots never lazy-reload after activation, so only folders need the guard
            }

            // An obsolete cached ancestor may still list descendants that this snapshot keeps elsewhere. Forgetting it only now
            // (they have been relocated out of its Children by then) and never forgetting a retained node keeps their instances,
            // loaded subtrees and expansion, whatever the order of the snapshot rows.
            foreach (var node in removed)
                ForgetSubtree(node, keep: placedOnPath);

            foreach (var id in target.WorkspaceAncestorIds)
                _nodes[id].IsExpanded = true;
            SelectedNode = _nodes[target.WorkspaceNodeId];
        }
        finally
        {
            _applyingReveal = false;
        }
    }

    // ---- Targeted tree updates (only after a successful write) ----------------------------------------------------

    private async Task ApplyCreatedAsync(WorkspaceNodeViewModel? parent, WorkspaceNode created)
    {
        WorkspaceNodeViewModel vm;
        if (parent is null)
        {
            vm = GetOrCreate(created, null);
            MergeInto(Roots, vm);
        }
        else
        {
            parent.SetHasChildren(true);
            if (!await EnsureChildrenLoadedAsync(parent))
                return; // the node exists; it appears when the folder loads
            vm = GetOrCreate(created, parent);
            MergeInto(parent.Children, vm);
            parent.IsExpanded = true;
        }

        SelectedNode = vm;
    }

    private void ApplyName(WorkspaceNodeViewModel node, string name)
    {
        node.Name = name;
        if (SelectedContent?.ProjectNodeId == node.Id)
            SelectedContent.ProjectName = name;
        var siblings = node.Parent?.Children ?? Roots;
        var oldIndex = siblings.IndexOf(node);
        if (oldIndex < 0)
            return;

        var newIndex = siblings.Count(s => !ReferenceEquals(s, node) && WorkspaceNodeOrder.Instance.Compare(s.SortKey, node.SortKey) < 0);
        if (newIndex != oldIndex)
            siblings.Move(oldIndex, newIndex);
        if (ReferenceEquals(SelectedNode, node))
            node.IsSelected = true; // keep the selection across the re-sort
    }

    /// <summary>
    /// Applies a committed move to the tree. Phase 25: returns false when the tree could not be updated (the destination's children
    /// could not be loaded; the node then leaves its stale place); true otherwise, also when the destination is simply not loaded.
    /// </summary>
    private async Task<bool> ApplyMovedAsync(WorkspaceNodeViewModel node, long? targetId)
    {
        var oldParent = node.Parent;
        if (oldParent?.ChildrenLoad is { } pendingSource)
            await pendingSource;
        (oldParent?.Children ?? Roots).Remove(node);

        if (targetId is null)
        {
            node.Parent = null;
            MergeInto(Roots, node);
        }
        else if (_nodes.TryGetValue(targetId.Value, out var target))
        {
            node.Parent = target;
            target.SetHasChildren(true);
            if (!await EnsureChildrenLoadedAsync(target))
            {
                ForgetSubtree(node);
                SelectedNode = null;
                return false;
            }

            MergeInto(target.Children, node);
            target.IsExpanded = true;
        }
        else
        {
            // Destination folder not loaded anywhere in the tree: the node now lives below a collapsed, unloaded folder.
            ForgetSubtree(node);
            SelectedNode = null;
            return true;
        }

        if (ReferenceEquals(SelectedNode, node))
        {
            node.IsSelected = true;
            StartDetailsLoad();
        }
        else
        {
            SelectedNode = node;
        }

        return true;
    }

    /// <param name="keep">Nodes (with their subtrees) that must stay registered because they are placed elsewhere.</param>
    private void ForgetSubtree(WorkspaceNodeViewModel node, IReadOnlySet<long>? keep = null)
    {
        if (keep?.Contains(node.Id) == true)
            return;
        _nodes.Remove(node.Id);
        foreach (var child in node.Children.Where(c => !c.IsPlaceholder))
            ForgetSubtree(child, keep);
    }

    private static void MergeInto(ObservableCollection<WorkspaceNodeViewModel> target, WorkspaceNodeViewModel node)
    {
        if (target.Any(c => c.Id == node.Id && !c.IsPlaceholder))
            return;
        InsertSorted(target, node);
    }

    private static void InsertSorted(ObservableCollection<WorkspaceNodeViewModel> target, WorkspaceNodeViewModel node)
    {
        if (target.Count == 1 && target[0].IsPlaceholder)
            target.Clear();
        var index = 0;
        while (index < target.Count && WorkspaceNodeOrder.Instance.Compare(target[index].SortKey, node.SortKey) < 0)
            index++;
        target.Insert(index, node);
    }

    private void ReportError(string message, Exception ex)
    {
        _log.Error(message, ex);
        BannerMessage = message;
    }
}
