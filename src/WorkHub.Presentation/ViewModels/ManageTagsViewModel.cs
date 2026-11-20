using System.Collections.ObjectModel;
using System.Globalization;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Phase 21 "Manage tags…" (proposal Revision 2 + R2a, §3.1): the tags in use, a filter, and Rename… / Merge into… on the selected
/// tag. A change is PREVIEWED first (the store's plan and token); Confirm is enabled only while the preview matches the current
/// request (same kind, source key and normalized new name) and would write something. Editing the input discards the preview, and a
/// preview that returns after the input changed is dropped. Confirm applies with the preview's token while the Workspace lifecycle
/// slot is held; a Stale result shows the fresh preview and needs a new Confirm click (never replayed). The dialog stays open after
/// a change and re-reads the list. Cancel / Close is the default; Enter never writes.
/// </summary>
public sealed class ManageTagsViewModel : ObservableObject
{
    private readonly WorkspaceService _workspace;
    private readonly Func<bool> _tryBeginWrite;
    private readonly Action _endWrite;
    private readonly IAppLog? _log;
    private IReadOnlyList<TagRowViewModel> _rows = [];
    private string _filter = string.Empty;
    private TagRowViewModel? _selected;
    private TagChangeKind? _mode;
    private string _newName = string.Empty;
    private TagRowViewModel? _mergeTarget;
    private TagChangePreview? _preview;
    private string? _message;
    private string? _nameError;
    private bool _isBusy;
    private bool _isLoading;
    private int _inputVersion;

    /// <param name="tryBeginWrite">Takes the Workspace lifecycle slot; false when another lifecycle write holds it (nothing is written).</param>
    /// <param name="endWrite">Releases the slot taken by <paramref name="tryBeginWrite"/>.</param>
    public ManageTagsViewModel(WorkspaceService workspace, Func<bool> tryBeginWrite, Action endWrite, IAppLog? log = null)
    {
        _workspace = workspace;
        _tryBeginWrite = tryBeginWrite;
        _endWrite = endWrite;
        _log = log;
        StartRenameCommand = new RelayCommand(() => StartMode(TagChangeKind.Rename), () => CanStart);
        StartMergeCommand = new RelayCommand(() => StartMode(TagChangeKind.MergeInto), () => CanStart && MergeTargets.Count > 0);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, () => CurrentRequest is not null && !IsBusy, ex => ReportError("Could not preview the change. Nothing was changed.", ex));
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm, ex => ReportError(CouldNotRename, ex));
        CancelChangeCommand = new RelayCommand(() => Mode = null, () => Mode is not null && !IsBusy);
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, ChangedAny), () => !IsBusy);
    }

    public event EventHandler<bool>? CloseRequested;

    public string Title => "Manage tags";

    /// <summary>True once a change committed while the dialog was open (the caller reloads the selected Project's details).</summary>
    public bool ChangedAny { get; private set; }

    public IReadOnlyList<TagRowViewModel> Rows { get => _rows; private set { if (SetProperty(ref _rows, value)) RefreshRows(); } }

    /// <summary>The rows whose spelling or key contains the filter text (case-insensitive).</summary>
    public IReadOnlyList<TagRowViewModel> FilteredRows =>
        _filter.Trim().Length == 0 ? _rows : _rows.Where(r => r.Spelling.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase)
                                                             || r.NameKey.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value ?? string.Empty)) OnPropertyChanged(nameof(FilteredRows)); } }

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    public TagRowViewModel? SelectedRow
    {
        get => _selected;
        set
        {
            if (IsBusy || !SetProperty(ref _selected, value))
                return;
            Mode = null;
            OnPropertyChanged(nameof(MergeTargets));
            RaiseCommands();
        }
    }

    /// <summary>The change being prepared: Rename (typed name), MergeInto (picked target), or none.</summary>
    public TagChangeKind? Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value))
                return;
            OnPropertyChanged(nameof(IsRenameMode));
            OnPropertyChanged(nameof(IsMergeMode));
            InputChanged();
        }
    }

    public bool IsRenameMode => Mode == TagChangeKind.Rename;

    public bool IsMergeMode => Mode == TagChangeKind.MergeInto;

    /// <summary>Rename…: the new name (prefilled with the selected tag's spelling).</summary>
    public string NewName { get => _newName; set { if (SetProperty(ref _newName, value ?? string.Empty)) InputChanged(); } }

    /// <summary>Merge into…: the other listed tags (any key but the selected one).</summary>
    public IReadOnlyList<TagRowViewModel> MergeTargets => _selected is null ? [] : _rows.Where(r => !string.Equals(r.NameKey, _selected.NameKey, StringComparison.Ordinal)).ToList();

    public TagRowViewModel? MergeTarget { get => _mergeTarget; set { if (SetProperty(ref _mergeTarget, value)) InputChanged(); } }

    /// <summary>The preview of <see cref="CurrentRequest"/>, or null (none yet, or discarded because the input changed).</summary>
    public TagChangePreview? Preview
    {
        get => _preview;
        private set
        {
            if (!SetProperty(ref _preview, value))
                return;
            OnPropertyChanged(nameof(PreviewText));
            RaiseCommands();
        }
    }

    /// <summary>The confirmation line built from the preview only.</summary>
    public string? PreviewText => Preview is { } p ? Describe(p, _selected?.Spelling ?? p.FromKey) : null;

    /// <summary>The result of the last action (outcome, Stale, error), or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>Inline validation of the new name (the editor's rules).</summary>
    public string? NameError { get => _nameError; private set => SetProperty(ref _nameError, value); }

    /// <summary>True while a preview read or an apply runs.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RaiseCommands();
        }
    }

    public RelayCommand StartRenameCommand { get; }

    public RelayCommand StartMergeCommand { get; }

    public AsyncRelayCommand PreviewCommand { get; }

    public AsyncRelayCommand ConfirmCommand { get; }

    public RelayCommand CancelChangeCommand { get; }

    public RelayCommand CloseCommand { get; }

    private bool CanStart => _selected is { ChangeableProjects: > 0 } && !IsBusy;

    /// <summary>The request the current input describes, or null when it is incomplete.</summary>
    public TagChangeRequest? CurrentRequest => (_selected, Mode) switch
    {
        ({ } row, TagChangeKind.Rename) when NewName.Trim().Length > 0 => new TagChangeRequest(TagChangeKind.Rename, row.NameKey, NewName),
        ({ } row, TagChangeKind.MergeInto) when MergeTarget is { } target => new TagChangeRequest(TagChangeKind.MergeInto, row.NameKey, target.Spelling),
        _ => null,
    };

    /// <summary>Confirm: a confirmable preview that matches the current request exactly, and nothing running.</summary>
    public bool CanConfirm => !IsBusy && Preview is { IsConfirmable: true } p && CurrentRequest is { } r && p.Matches(r);

    /// <summary>Reads the list (when the dialog opens, and after a committed change). Keeps the selection by key when it still exists.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var tags = await _workspace.ListTagsInUseAsync();
            var key = _selected?.NameKey;
            Rows = tags.Select(t => new TagRowViewModel(t)).ToList();
            _selected = key is null ? null : _rows.FirstOrDefault(r => string.Equals(r.NameKey, key, StringComparison.Ordinal));
            OnPropertyChanged(nameof(SelectedRow));
            OnPropertyChanged(nameof(MergeTargets));
        }
        catch (Exception ex)
        {
            ReportError("The tags could not be listed.", ex);
        }
        finally
        {
            IsLoading = false;
            RaiseCommands();
        }
    }

    private void StartMode(TagChangeKind kind)
    {
        Message = null;
        Mode = kind;
        if (kind == TagChangeKind.Rename)
            NewName = _selected?.Spelling ?? string.Empty;
        else
            MergeTarget = null;
    }

    /// <summary>Any change of the input discards the preview; an older preview still in flight is dropped when it returns.</summary>
    private void InputChanged()
    {
        _inputVersion++;
        NameError = null;
        Preview = null;
        OnPropertyChanged(nameof(CurrentRequest));
        RaiseCommands();
    }

    private async Task PreviewAsync()
    {
        if (CurrentRequest is not { } request)
            return;
        Message = null;
        var version = _inputVersion;
        if (request.Kind == TagChangeKind.Rename && !ProjectTagName.TryNormalize(request.NewName, out _, out var invalid))
        {
            NameError = invalid; // refused before any store call
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _workspace.PrepareTagChangeAsync(request);
            if (version != _inputVersion || CurrentRequest is not { } now || result.Value is not { } preview || !preview.Matches(now))
            {
                if (version == _inputVersion && result.Error is { } error)
                    NameError = error;
                return; // a late preview of an older input: dropped
            }

            Preview = preview;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConfirmAsync()
    {
        if (CurrentRequest is not { } request || Preview is not { IsConfirmable: true } preview || !preview.Matches(request))
            return;
        if (!_tryBeginWrite())
        {
            Message = AnotherChangeRunning; // TR14: the slot is taken; nothing is written
            return;
        }

        TagChangeResult result;
        IsBusy = true;
        try
        {
            result = await _workspace.ApplyTagChangeAsync(request, preview.Token);
        }
        finally
        {
            _endWrite();
            IsBusy = false;
        }

        var from = _selected?.Spelling ?? request.FromKey;
        switch (result.Outcome)
        {
            case TagChangeOutcome.Stale:
                Preview = result.Preview; // the fresh plan; Confirm needs a new click (enabled only if confirmable)
                Message = StaleMessage;
                break;
            case TagChangeOutcome.NothingToRename:
                Preview = result.Preview;
                Message = NothingToRenameMessage;
                break;
            case TagChangeOutcome.Unchanged:
                Preview = result.Preview;
                Message = AlreadySpelledMessage(result.Preview?.NewName ?? request.NewName);
                break;
            case TagChangeOutcome.InvalidName:
                NameError = result.Error;
                break;
            default:
                ChangedAny = true;
                Message = Done(result, from);
                Mode = null;
                await LoadAsync();
                break;
        }
    }

    private void ReportError(string text, Exception ex)
    {
        _log?.Error(text, ex); // no tag or project names in the log
        Message = text;
    }

    private void RefreshRows()
    {
        OnPropertyChanged(nameof(FilteredRows));
        OnPropertyChanged(nameof(MergeTargets));
    }

    private void RaiseCommands()
    {
        StartRenameCommand.RaiseCanExecuteChanged();
        StartMergeCommand.RaiseCanExecuteChanged();
        PreviewCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
        CancelChangeCommand.RaiseCanExecuteChanged();
        CloseCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanConfirm));
    }

    // ---- texts (proposal §3.4) -------------------------------------------------------------------------------------------------

    internal const string StaleMessage = "The tag changed in another window. Review the new preview.";
    internal const string NothingToRenameMessage = "No project outside the Trash has this tag any more.";
    internal const string CouldNotRename = "Could not rename the tag. Nothing was changed.";
    internal const string AnotherChangeRunning = "Another Workspace change is running. Nothing was changed; try again.";
    internal const string CannotUndo = " This cannot be undone automatically.";

    internal static string AlreadySpelledMessage(string name) => $"Every project already uses “{name}”.";

    private static string Projects(int n) => n == 1 ? "1 project" : string.Create(CultureInfo.InvariantCulture, $"{n} projects");

    private static string TrashNote(int trashed, string from) =>
        trashed == 0 ? string.Empty : $" {Projects(trashed)} in the Trash keep{(trashed == 1 ? "s" : string.Empty)} “{from}”.";

    /// <summary>The confirmation line of a preview (§3.1 step 4).</summary>
    internal static string Describe(TagChangePreview p, string from)
    {
        if (p.Replaced + p.Merged + p.AlreadySpelled == 0)
            return NothingToRenameMessage;
        if (p.Operation == TagChangeOperation.SpellingChange && p.Replaced == 0)
            return AlreadySpelledMessage(p.NewName);
        return p.Operation switch
        {
            TagChangeOperation.SpellingChange =>
                $"Change “{from}” to “{p.NewName}” in {Projects(p.Replaced)}." + (p.AlreadySpelled > 0 ? $" {Projects(p.AlreadySpelled)} already use{(p.AlreadySpelled == 1 ? "s" : string.Empty)} it." : string.Empty)
                + TrashNote(p.Trashed, from) + CannotUndo,
            TagChangeOperation.Merge =>
                $"This will merge into “{p.NewName}”. Merge “{from}” into “{p.NewName}” in {Projects(p.Replaced + p.Merged)}" + (p.Merged > 0 ? $" ({p.Merged} already {(p.Merged == 1 ? "has" : "have")} “{p.NewName}”)." : ".")
                + TrashNote(p.Trashed, from) + CannotUndo,
            _ => $"Rename “{from}” to “{p.NewName}” in {Projects(p.Replaced)}." + TrashNote(p.Trashed, from) + CannotUndo,
        };
    }

    /// <summary>The result line of a committed change (§3.4).</summary>
    internal static string Done(TagChangeResult result, string from)
    {
        var p = result.Preview!;
        return result.Outcome switch
        {
            TagChangeOutcome.SpellingChanged => $"Changed “{from}” to “{p.NewName}” in {Projects(p.Replaced)}." + (p.AlreadySpelled > 0 ? $" {p.AlreadySpelled} already used it." : string.Empty) + TrashNote(p.Trashed, from),
            TagChangeOutcome.Merged => $"Merged “{from}” into “{p.NewName}” in {Projects(p.Replaced + p.Merged)} ({p.Merged} already had “{p.NewName}”)." + TrashNote(p.Trashed, from),
            _ => $"Renamed “{from}” to “{p.NewName}” in {Projects(p.Replaced)}." + TrashNote(p.Trashed, from),
        };
    }
}

/// <summary>One row of the Manage tags list.</summary>
public sealed class TagRowViewModel(TagInUse tag)
{
    public string NameKey { get; } = tag.NameKey;

    public string Spelling { get; } = tag.Spelling;

    public int ChangeableProjects { get; } = tag.ChangeableProjects;

    public int TrashedProjects { get; } = tag.TrashedProjects;

    public string CountText => ChangeableProjects == 1 ? "1 project" : $"{ChangeableProjects} projects";

    public string? TrashText => TrashedProjects == 0 ? null : $"+ {TrashedProjects} in the Trash";

    public override string ToString() => Spelling;
}
