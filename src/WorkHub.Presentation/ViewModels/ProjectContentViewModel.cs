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
        }
    }

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

    /// <summary>Folder: enter it. Link: open its target.</summary>
    public AsyncRelayCommand OpenCommand { get; }

    /// <summary>Folder: rename. Link: edit.</summary>
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
        _loadVersion++;
        _loadCts?.Cancel();
        _loadCts = null;
        CancelChecks();
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

        var result = await _content.OpenLinkAsync(item.Id);
        BannerMessage = result.Succeeded ? null : result.Message ?? "The target could not be opened.";
    }

    private Task EditAsync()
    {
        var item = SelectedItem!;
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
        var editor = new ProjectContentMoveViewModel(_content, item.IsFolder, item.Id, item.Name, item.ContainerId, ProjectName, targets);
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

    // ---- Row ordering (folders first, then links; each by name) -----------------------------------------------------

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
        a.IsFolder != b.IsFolder ? a.IsFolder : WorkspaceNodeOrder.Instance.Compare(a.SortKey, b.SortKey) < 0;

    private void RaiseCommands()
    {
        NewFolderCommand.RaiseCanExecuteChanged();
        NewLinkCommand.RaiseCanExecuteChanged();
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
