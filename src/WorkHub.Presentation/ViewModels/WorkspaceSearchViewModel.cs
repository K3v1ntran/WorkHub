using System.Collections.ObjectModel;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace.Search;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>What a reveal of a search result ended with.</summary>
public enum WorkspaceRevealOutcome
{
    Selected,
    NotFound,
    MovedMeanwhile,
    ContainerMissing,
    WorkspaceChanged,
    Busy,
    Failed,

    /// <summary>A newer reveal, search, selection or write took over; nothing to report.</summary>
    Superseded,
}

/// <summary>What a content-row reveal ended with (<see cref="ProjectContentViewModel.RevealAsync"/>).</summary>
public enum ContentRevealOutcome
{
    Selected,

    /// <summary>The container loaded but holds no row of that kind and Id.</summary>
    MovedMeanwhile,

    ContainerMissing,
    LoadFailed,
    Superseded,
}

/// <summary>One result row.</summary>
public sealed class WorkspaceSearchHitViewModel(WorkspaceSearchHit hit)
{
    public WorkspaceSearchHit Hit { get; } = hit;

    public SearchItemRef Item => Hit.Item;

    public string Name => Hit.Name;

    public string Location => Hit.Location;

    public string KindText => Hit.Item.Kind switch
    {
        SearchResultKind.WorkspaceFolder => "Folder",
        SearchResultKind.Project => "Project",
        SearchResultKind.ProjectFolder => "Project folder",
        SearchResultKind.Link => "Link",
        _ => "Note",
    };

    public string Glyph => Hit.Item.Kind switch
    {
        SearchResultKind.WorkspaceFolder => "",
        SearchResultKind.Project => "",
        SearchResultKind.ProjectFolder => "",
        SearchResultKind.Link => "",
        _ => "",
    };

    public string FieldText => Hit.MatchedField switch
    {
        SearchField.Name => Hit.Item.Kind == SearchResultKind.Note ? "title" : "name",
        SearchField.Owner => "owner",
        SearchField.ProjectManager => "project manager",
        SearchField.Description => "description",
        SearchField.Target => "target",
        _ => "content",
    };

    /// <summary>"title" or "content: “…agenda…”".</summary>
    public string MatchText => Hit.Excerpt is { } excerpt ? $"{FieldText}: “{excerpt}”" : FieldText;

    public string AccessibleName => $"{KindText} {Name}, {Location}, matched in {FieldText}";
}

/// <summary>
/// Workspace search box (Phase 6 proposal §3, §4.7). WPF-free.
/// <list type="bullet">
/// <item>Typing is debounced; every change invalidates a reveal in flight and supersedes the running search. Searches run
/// through a <see cref="LatestOnlyRunner{TRequest,TResult}"/>: at most one store call runs and only the newest query waits,
/// so rapid typing never accumulates work; superseded results and errors are dropped.</item>
/// <item>"Waiting for the database…" is shown only after the store reported that an attempt met a locked database (it
/// retries within its 30-second budget); "The database stayed busy…" when that budget ran out.</item>
/// <item>Activating a result only reveals it (tree + content row); it never opens a link or the note editor.</item>
/// <item>The query text is never logged.</item>
/// </list>
/// </summary>
public sealed class WorkspaceSearchViewModel : ObservableObject
{
    public const int DefaultDelayMs = 200;

    private readonly WorkspaceSearchService _service;
    private readonly WorkspaceAreaViewModel _area;
    private readonly IAppLog _log;
    private readonly int _delayMs;
    private readonly LatestOnlyRunner<(WorkspaceSearchQuery Query, int Version), WorkspaceSearchResult> _runner;
    private readonly SynchronizationContext? _context;
    private string _searchText = string.Empty;
    private int _version;
    private CancellationTokenSource? _debounce;
    private bool _isSearching;
    private bool _isWaitingForDatabase;
    private string? _statusText;
    private string? _errorText;
    private string? _noticeText;
    private WorkspaceSearchHitViewModel? _selectedResult;

    internal WorkspaceSearchViewModel(WorkspaceSearchService service, WorkspaceAreaViewModel area, IAppLog log, int delayMs)
    {
        _service = service;
        _area = area;
        _log = log;
        _delayMs = delayMs;
        _context = SynchronizationContext.Current;
        _runner = new((request, token) => _service.SearchAsync(request.Query, token, () => OnBusy(request.Version)));
        RetryCommand = new RelayCommand(() => Start(delayMs: 0), () => ErrorText is not null);
        ClearCommand = new RelayCommand(() => SearchText = string.Empty, () => IsActive);
        ActivateSelectedCommand = new RelayCommand(() => Activate(SelectedResult), () => SelectedResult is not null);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
                return;
            OnPropertyChanged(nameof(IsActive));
            ClearCommand.RaiseCanExecuteChanged();
            Start(_delayMs);
        }
    }

    /// <summary>The query is non-empty: the results replace the tree.</summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(SearchText);

    public ObservableCollection<WorkspaceSearchHitViewModel> Results { get; } = [];

    public WorkspaceSearchHitViewModel? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (SetProperty(ref _selectedResult, value))
                ActivateSelectedCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsSearching { get => _isSearching; private set => SetProperty(ref _isSearching, value); }

    public bool IsWaitingForDatabase { get => _isWaitingForDatabase; private set => SetProperty(ref _isWaitingForDatabase, value); }

    /// <summary>"7 results", "No results for “…”.", "Showing the first 200 results…", "Searching…".</summary>
    public string? StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>A failed search (with Retry).</summary>
    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
                RetryCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Outcome of the last reveal when it did not simply select the item.</summary>
    public string? NoticeText { get => _noticeText; private set => SetProperty(ref _noticeText, value); }

    public RelayCommand RetryCommand { get; }

    public RelayCommand ClearCommand { get; }

    /// <summary>Enter / double-click on a result.</summary>
    public RelayCommand ActivateSelectedCommand { get; }

    /// <summary>The last search's work; lets tests await it.</summary>
    internal Task CurrentSearch { get; private set; } = Task.CompletedTask;

    /// <summary>The last activation's work; lets tests await it.</summary>
    internal Task CurrentActivation { get; private set; } = Task.CompletedTask;

    /// <summary>Instrumentation: store calls started (the runner's bound is visible here).</summary>
    internal int StoreCallsStarted => _runner.StartedCount;

    private void Start(int delayMs)
    {
        var version = ++_version;
        _area.InvalidateReveal();
        _debounce?.Cancel();
        _debounce = null;
        ErrorText = null;
        NoticeText = null;
        IsWaitingForDatabase = false;

        var query = WorkspaceSearchService.Parse(SearchText);
        if (query is null)
        {
            _runner.CancelAll();
            Results.Clear();
            SelectedResult = null;
            IsSearching = false;
            StatusText = null;
            CurrentSearch = Task.CompletedTask;
            return;
        }

        var debounce = _debounce = new CancellationTokenSource();
        CurrentSearch = RunAsync(query, version, delayMs, debounce.Token);
    }

    private async Task RunAsync(WorkspaceSearchQuery query, int version, int delayMs, CancellationToken debounce)
    {
        try
        {
            if (delayMs > 0)
                await Task.Delay(delayMs, debounce);
        }
        catch (OperationCanceledException)
        {
            return; // typed again during the pause
        }

        if (version != _version)
            return;
        IsSearching = true;
        StatusText = "Searching…";
        var outcome = await _runner.Submit((query, version));
        if (version != _version || outcome.Status == RunStatus.Superseded)
            return;

        IsSearching = false;
        IsWaitingForDatabase = false;
        if (outcome.Status == RunStatus.Failed)
        {
            Results.Clear();
            SelectedResult = null;
            StatusText = null;
            if (outcome.Error is WorkspaceSearchBusyException busy)
            {
                ErrorText = $"The database stayed busy for {busy.Budget.TotalSeconds:0} seconds (another program or window is writing). Try again.";
            }
            else
            {
                _log.Error("Workspace search failed.", outcome.Error!); // never the query text
                ErrorText = "Search failed. Try again.";
            }

            return;
        }

        var result = outcome.Value!;
        Results.Clear();
        foreach (var hit in result.Hits)
            Results.Add(new WorkspaceSearchHitViewModel(hit));
        SelectedResult = null;
        StatusText = Status(result, query);
    }

    private string Status(WorkspaceSearchResult result, WorkspaceSearchQuery query)
    {
        var text = result.Hits.Count switch
        {
            0 => $"No results for “{SearchText.Trim()}”.",
            1 => "1 result",
            _ => $"{result.Hits.Count} results",
        };
        if (result.Truncated)
            text = $"Showing the first {result.Hits.Count} results. Refine the search to see more.";
        if (query.WasShortened)
            text += $" Only the first {WorkspaceSearchText.MaxTerms} words and {WorkspaceSearchText.MaxQueryLength} characters are used.";
        return text;
    }

    /// <summary>The store met a locked database and is retrying (worker thread).</summary>
    private void OnBusy(int version)
    {
        void Show()
        {
            if (version == _version && IsSearching)
            {
                IsWaitingForDatabase = true;
                StatusText = "Waiting for the database… (another program or window is writing)";
            }
        }

        if (_context is null)
            Show();
        else
            _context.Post(_ => Show(), null);
    }

    private void Activate(WorkspaceSearchHitViewModel? hit)
    {
        if (hit is null)
            return;
        NoticeText = null;
        CurrentActivation = ActivateAsync(hit);
    }

    private async Task ActivateAsync(WorkspaceSearchHitViewModel hit)
    {
        var outcome = await _area.RevealAsync(hit.Item);
        switch (outcome)
        {
            case WorkspaceRevealOutcome.NotFound:
                // Remove exactly this (Kind, Id); a Folder, Link or Note with the same numeric Id stays.
                if (Results.FirstOrDefault(r => r.Item == hit.Item) is { } gone)
                    Results.Remove(gone);
                NoticeText = "This item no longer exists.";
                break;
            case WorkspaceRevealOutcome.MovedMeanwhile:
                NoticeText = "The item was moved meanwhile. Search again.";
                break;
            case WorkspaceRevealOutcome.ContainerMissing:
                NoticeText = "The item's folder no longer exists. Search again.";
                break;
            case WorkspaceRevealOutcome.WorkspaceChanged:
                NoticeText = "The workspace changed while revealing. Search again.";
                break;
            case WorkspaceRevealOutcome.Busy:
                NoticeText = "The database stayed busy (another program or window is writing). Try again.";
                break;
            case WorkspaceRevealOutcome.Failed:
                NoticeText = "The item could not be shown. Try again.";
                break;
        }
    }
}
