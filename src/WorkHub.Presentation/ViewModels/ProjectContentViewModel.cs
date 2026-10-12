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
    }

    // ---- Loading and navigation -------------------------------------------------------------------------------------

    internal Task NavigateAsync(long? folderId)
    {
        var version = ++_loadVersion;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        CancelChecks();

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
            ContainerMessage = result.Problem == ContentContainerProblem.ProjectNotFound
                ? "This project no longer exists."
                : "This folder no longer exists.";
            OnPropertyChanged(nameof(IsFolderMissing));
            IsLoading = false;
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

        _isLoaded = true;
        IsLoading = false;
        OnPropertyChanged(nameof(IsEmpty));
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
        BannerMessage = result.Succeeded ? null : result.Message ?? "The target could not be opened.";
    }

    private Task EditAsync()
    {
        var item = SelectedItem!;
        if (item.IsNote)
            return OpenNoteEditorAsync(item);

        var version = _loadVersion;
        if (item.Folder is { } folder)
        {
            var editor = ProjectFolderNameEditorViewModel.ForRename(_content, folder, LocationPath);
            if (_dialogs.ShowProjectFolderNameEditor(editor) && editor.SavedFolder is { } saved && version == _loadVersion)
            {
                if (saved.ParentFolderId != CurrentFolderId)
                    RemoveMovedAway(item);
                else
                {
                    item.ApplyFolder(saved);
                    Resort(item);
                }
            }
        }
        else
        {
            var editor = ProjectLinkEditorViewModel.ForEdit(_content, _dialogs, item.Link!, LocationPath);
            if (_dialogs.ShowProjectLinkEditor(editor) && editor.SavedLink is { } saved && version == _loadVersion)
            {
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
        }

        return Task.CompletedTask;
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
    }

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

        if (note is null)
        {
            RemoveMovedAway(item);
            BannerMessage = "This note no longer exists.";
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
            if (byId.TryGetValue(id, out var note) && note.ProjectNodeId == ProjectNodeId && note.FolderId == CurrentFolderId)
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
                RemoveMovedAway(row); // moved elsewhere or no longer exists; clears selection and preview if selected
            }
        }

        if (select is not null)
            SelectedItem = select;
        if (SelectedItem is { IsNote: true } selected && byId.TryGetValue(selected.Id, out var current) && Items.Contains(selected))
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
