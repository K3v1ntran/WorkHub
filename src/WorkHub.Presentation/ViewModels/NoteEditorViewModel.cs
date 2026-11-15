using WorkHub.Core.Workspace.Content;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>Note editor lifecycle states (Phase 5B proposal §4.4).</summary>
public enum NoteEditorState
{
    Editing,
    Saving,
    Prompting,
    Closing,
}

/// <summary>
/// Modal plain-text Note editor. Save / Ctrl+S (and the Save choice when closing) write; with the Phase 18 setting "Save notes
/// automatically" (off by default, read when the editor opens) an existing note is also saved automatically.
/// <list type="bullet">
/// <item>Save takes a snapshot of Title/Content and makes the inputs read-only until the write ends, so the stored values
/// and the new baseline cannot diverge from what was saved.</item>
/// <item>Every close path calls <see cref="RequestClose"/>; it acts only in <see cref="NoteEditorState.Editing"/>, so prompts
/// never stack and nothing closes during a pending write. A dirty close asks Save / Discard / Cancel; a close-triggered Save
/// closes only after it succeeded.</item>
/// <item>Every failure (validation, missing location, NotFound, Conflict, database error) keeps the draft and the window.
/// Conflict/NotFound offer Copy draft and Save as new note.</item>
/// <item><see cref="LocationText"/> comes from the persisted container: the opened row's at open, the saved row's after
/// every successful Save or Save as new note. The location is read only after the committed row has been accepted, so a
/// failed lookup shows a fallback and never turns a save into a failure.</item>
/// <item><see cref="TouchedNoteIds"/> lists the existing note opened and every note created in this session, so the caller
/// can reconcile from a fresh read after the editor closes, whatever was (or was not) saved.</item>
/// </list>
/// <para>
/// <b>Phase 18 autosave</b> (proposal Revision 3, D1–D10). Only for an existing note (a new note after its first successful
/// manual Save). A change starts a 3 s debounce and, for text no write covers yet, a 30 s max-wait mark; both are cleared
/// whenever the draft is clean (against the stored baseline, or against the in-flight snapshot while a write runs). The
/// automatic write uses the same revision-checked <see cref="ProjectContentService.SaveNoteAsync"/>, keeps the inputs
/// editable, never assigns <see cref="Title"/>/<see cref="Content"/>, and takes baseline and revision from the stored note it
/// returns. One write at a time: Save/Close requested during an automatic write wait in one pending slot (Close wins) and are
/// handled once after its result is applied. The timer callback only posts <see cref="Evaluate"/> to the editor's context;
/// <see cref="Evaluate"/> re-checks everything from the current state. A failed automatic write pauses autosave (no retry)
/// until a successful manual Save or Save as new note. The scheduler is disposed when the editor reaches
/// <see cref="NoteEditorState.Closing"/>.
/// </para>
/// </summary>
public sealed class NoteEditorViewModel : ObservableObject
{
    /// <summary>Phase 18 (D3): an automatic save is due this long after the last change.</summary>
    public static readonly TimeSpan AutoSaveDebounce = TimeSpan.FromSeconds(3);

    /// <summary>Phase 18 (D3): ... and at the latest this long after the first change no write covers yet.</summary>
    public static readonly TimeSpan AutoSaveMaxWait = TimeSpan.FromSeconds(30);

    public const string AutoSavedStatusPrefix = "Saved automatically ";
    public const string AutoSavePausedRetryStatus = "Automatic save paused. Press Save to try again.";
    public const string AutoSaveStoppedStatus = "Automatic save stopped. Your text is unchanged here.";
    public const string AutoSaveInvalidTitleStatus = "Not saved automatically: fix the title.";
    public const string AutoSaveInvalidContentStatus = "Not saved automatically: fix the content.";

    private enum PendingRequest
    {
        None,
        Save,
        Close,
    }

    private readonly ProjectContentService _content;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly long _projectNodeId;
    private readonly List<long> _touched = [];
    private readonly HashSet<long> _created = [];
    private readonly bool _autoSave;
    private readonly SynchronizationContext? _ui;
    private long? _noteId;
    private long _revision;
    private long? _folderId;
    private string _title;
    private string _text;
    private string _baselineTitle;
    private string _baselineText;
    private NoteEditorState _state = NoteEditorState.Editing;
    private string? _titleError;
    private string? _contentError;
    private string? _generalError;
    private string? _statusText;
    private bool _canRecover;
    private string _locationText;

    // Phase 18 autosave state (touched only on the editor's context).
    private ITimer? _timer;
    private bool _schedulerClosed;
    private bool _autoSaveInFlight;
    private string? _inFlightTitle;
    private string? _inFlightContent;
    private PendingRequest _pending;
    private bool _autoSavePaused;
    private long? _debounceDueAt;     // monotonic timestamps (TimeProvider.GetTimestamp)
    private long? _firstUncoveredAt;

    private NoteEditorViewModel(
        ProjectContentService content, IDialogService dialogs, long projectNodeId, long? folderId, ProjectNote? existing,
        string? locationPath, TimeProvider? time, bool autoSave)
    {
        _content = content;
        _dialogs = dialogs;
        _time = time ?? TimeProvider.System;
        _projectNodeId = projectNodeId;
        _folderId = folderId;
        _autoSave = autoSave;
        _ui = SynchronizationContext.Current;
        _locationText = DescribeLocation(locationPath);
        if (existing is not null)
        {
            _noteId = existing.Id;
            _revision = existing.Revision;
            Track(existing.Id);
        }

        _title = _baselineTitle = existing?.Title ?? string.Empty;
        _text = _baselineText = existing?.Content ?? string.Empty;

        SaveCommand = new AsyncRelayCommand(() => SaveCoreAsync(fromClose: false), () => State == NoteEditorState.Editing && (IsDirty || IsNew));
        RequestCloseCommand = new RelayCommand(RequestClose, () => State == NoteEditorState.Editing);
        CopyDraftCommand = new RelayCommand(CopyDraft, () => State == NoteEditorState.Editing && CanRecover);
        SaveAsNewCommand = new AsyncRelayCommand(SaveAsNewAsync, () => State == NoteEditorState.Editing && CanRecover && !IsProjectTrashed && _noteId is not null);
    }

    /// <summary>A new note in the given container (null = Project root). Nothing is written until the first Save.</summary>
    /// <param name="autoSave">Phase 18: "Save notes automatically" as it is when the editor opens (applies after the first Save).</param>
    public static NoteEditorViewModel ForCreate(ProjectContentService content, IDialogService dialogs, long projectNodeId, long? folderId, string locationPath, TimeProvider? time = null, bool autoSave = false) =>
        new(content, dialogs, projectNodeId, folderId, null, locationPath, time, autoSave);

    /// <summary>
    /// An existing note, freshly read by the caller. <paramref name="locationPath"/> is the path of THAT note's container
    /// (see <see cref="TryReadLocationAsync"/>), not the listing the caller shows; null = it could not be determined.
    /// </summary>
    /// <param name="autoSave">Phase 18: "Save notes automatically" as it is when the editor opens.</param>
    public static NoteEditorViewModel ForEdit(ProjectContentService content, IDialogService dialogs, ProjectNote note, string? locationPath, TimeProvider? time = null, bool autoSave = false) =>
        new(content, dialogs, note.ProjectNodeId, note.FolderId, note, locationPath, time, autoSave);

    /// <summary>
    /// The logical path of a note's container ("Alpha Portal › Specs"), or null when it could not be read or no longer
    /// exists. Display only: a failure here never changes what was (or was not) written.
    /// </summary>
    public static async Task<string?> TryReadLocationAsync(ProjectContentService content, ProjectNote note)
    {
        try
        {
            return (await content.GetContainerPathAsync(note.ProjectNodeId, note.FolderId))?.Display;
        }
        catch (Exception)
        {
            return null; // the caller shows the "unavailable" fallback; nothing is retried or polled
        }
    }

    public static string DescribeLocation(string? locationPath) =>
        locationPath is null ? LocationUnavailable : $"Location: {locationPath}";

    public const string LocationUnavailable = "Location: unavailable (it could not be read)";

    /// <summary>Raised once, when the editor may close; the argument is <see cref="SavedAny"/>.</summary>
    public event EventHandler<bool>? CloseRequested;

    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>Close button and Esc. Window close paths call <see cref="RequestClose"/> directly.</summary>
    public RelayCommand RequestCloseCommand { get; }

    public RelayCommand CopyDraftCommand { get; }

    public AsyncRelayCommand SaveAsNewCommand { get; }

    /// <summary>"Location: Alpha Portal › Specs", from the persisted container (at open and after every successful write).</summary>
    public string LocationText { get => _locationText; private set => SetProperty(ref _locationText, value); }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                OnDraftChanged();
                OnUserEdit();
            }
        }
    }

    /// <summary>Note content, exactly as typed.</summary>
    public string Content
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnDraftChanged();
                OnUserEdit();
            }
        }
    }

    public NoteEditorState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
                return;
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(IsSaving));
            OnPropertyChanged(nameof(IsCloseApproved));
            RaiseCommands();
        }
    }

    /// <summary>Inputs are editable only while Editing (read-only during a pending manual write and while prompting).</summary>
    public bool IsReadOnly => State != NoteEditorState.Editing;

    public bool IsSaving => State == NoteEditorState.Saving;

    /// <summary>The window may close (the view model reached <see cref="NoteEditorState.Closing"/>).</summary>
    public bool IsCloseApproved => State == NoteEditorState.Closing;

    /// <summary>No note has been created yet (the first Save inserts it).</summary>
    public bool IsNew => _noteId is null;

    /// <summary>
    /// Unsaved changes. With autosave on for an existing note (Phase 18 §4.3): the title is compared after the validator's
    /// <c>Trim()</c> and the content ordinal and verbatim, against the stored note; an invalid draft always differs from the
    /// stored, valid baseline. Otherwise the raw ordinal comparison of Phase 5B.
    /// </summary>
    public bool IsDirty => UsesStoredComparison
        ? !DraftMatches(_baselineTitle, _baselineText)
        : !string.Equals(Title, _baselineTitle, StringComparison.Ordinal) || !string.Equals(Content, _baselineText, StringComparison.Ordinal);

    public string WindowTitle => $"{(string.IsNullOrWhiteSpace(_baselineTitle) ? "New note" : _baselineTitle)} — Note{(IsDirty ? " •" : string.Empty)}";

    public string? TitleError { get => _titleError; private set => SetProperty(ref _titleError, value); }

    public string? ContentError { get => _contentError; private set => SetProperty(ref _contentError, value); }

    public string? GeneralError { get => _generalError; private set => SetProperty(ref _generalError, value); }

    /// <summary>"Saved 14:02", "Saved automatically 14:02", "Draft copied …" and similar confirmations.</summary>
    public string? StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>After Conflict or NotFound: Copy draft and Save as new note are offered.</summary>
    public bool CanRecover
    {
        get => _canRecover;
        private set
        {
            if (SetProperty(ref _canRecover, value))
                RaiseCommands();
        }
    }

    /// <summary>
    /// The note's Project is in the Workspace Trash: Copy draft stays available, Save as new is not offered (it would write
    /// into the same read-only Project).
    /// </summary>
    public bool IsProjectTrashed
    {
        get => _isProjectTrashed;
        private set
        {
            if (SetProperty(ref _isProjectTrashed, value))
                RaiseCommands();
        }
    }

    private bool _isProjectTrashed;

    /// <summary>Something was persisted during this editor session.</summary>
    public bool SavedAny { get; private set; }

    /// <summary>The note currently edited (null until a new note is first saved).</summary>
    public long? NoteId => _noteId;

    /// <summary>The existing note opened and every note created in this session, in order, without duplicates.</summary>
    public IReadOnlyList<long> TouchedNoteIds => _touched;

    /// <summary>Notes created in this session (first save of a new note, Save as new note).</summary>
    public IReadOnlyCollection<long> CreatedNoteIds => _created;

    /// <summary>Phase 18: "Save notes automatically" was on when this editor opened.</summary>
    public bool IsAutoSaveEnabled => _autoSave;

    /// <summary>Phase 18: an automatic write is in flight (the inputs stay editable).</summary>
    public bool IsAutoSaving => _autoSaveInFlight;

    /// <summary>Phase 18: autosave stopped after a failed automatic write; a successful Save or Save as new note resumes it.</summary>
    public bool IsAutoSavePaused => _autoSavePaused;

    /// <summary>The last close request's work (prompt and close-triggered save); lets tests await it.</summary>
    internal Task CurrentClose { get; private set; } = Task.CompletedTask;

    /// <summary>The last automatic write (tests await it).</summary>
    internal Task CurrentAutoSave { get; private set; } = Task.CompletedTask;

    /// <summary>The last Save processed from the pending slot (tests await it).</summary>
    internal Task CurrentPendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>Tests: a debounce or max-wait mark is set.</summary>
    internal bool HasAutoSaveSchedule => _debounceDueAt is not null || _firstUncoveredAt is not null;

    /// <summary>Tests: the scheduler was disposed (the editor reached Closing).</summary>
    internal bool IsAutoSaveSchedulerClosed => _schedulerClosed;

    private bool AutoSaveApplies => _autoSave && _noteId is not null && !_schedulerClosed;

    private bool UsesStoredComparison => _autoSave && _noteId is not null;

    /// <summary>
    /// Entry point for EVERY close path. Ignored unless Editing (so it never stacks prompts or closes during a write).
    /// Clean → closes. Dirty → Save / Discard / Cancel. During an automatic write the request waits (pending slot).
    /// </summary>
    public void RequestClose() => CurrentClose = RequestCloseAsync();

    private async Task RequestCloseAsync()
    {
        if (State != NoteEditorState.Editing)
            return;

        if (_autoSaveInFlight)
        {
            _pending = PendingRequest.Close; // Close wins over a pending Save; repeats coalesce
            return;
        }

        if (!IsDirty)
        {
            Approve();
            return;
        }

        State = NoteEditorState.Prompting;
        var choice = _dialogs.AskUnsavedChanges("Unsaved changes", $"Save changes to “{DisplayTitle}”?");
        switch (choice)
        {
            case UnsavedChangesChoice.Discard:
                Approve();
                break;
            case UnsavedChangesChoice.Save:
                State = NoteEditorState.Editing; // SaveCoreAsync takes it from Editing to Saving
                await SaveCoreAsync(fromClose: true);
                break;
            default:
                State = NoteEditorState.Editing;
                Evaluate(); // a due autosave runs now that the prompt is gone
                break;
        }
    }

    private string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "New note" : Title.Trim();

    private async Task SaveCoreAsync(bool fromClose)
    {
        if (State != NoteEditorState.Editing)
            return;

        if (_autoSaveInFlight)
        {
            if (_pending == PendingRequest.None)
                _pending = PendingRequest.Save; // handled once after the automatic write
            return;
        }

        var draft = new ProjectNoteDraft(Title, Content); // snapshot; inputs are read-only until the write ends
        TitleError = ContentError = GeneralError = null;
        StatusText = null;
        State = NoteEditorState.Saving;

        NoteWriteResult result;
        try
        {
            result = _noteId is { } id
                ? await _content.SaveNoteAsync(id, _revision, draft)
                : await _content.CreateNoteAsync(_projectNodeId, _folderId, draft);
        }
        catch (Exception ex)
        {
            result = NoteWriteResult.Failed(NoteWriteStatus.Error, $"Could not save: {ex.Message} Your text is unchanged here.");
        }

        if (result.Succeeded)
        {
            // The committed row is accepted first (Id, Revision, baseline), so nothing after this can leave the editor
            // on an obsolete revision or report the write as failed.
            Accept(result.Note!, created: _noteId is null);
            var savedAt = $"Saved {_time.GetLocalNow():HH:mm}";
            StatusText = savedAt;
            CanRecover = false;
            if (fromClose)
            {
                Approve();
                return;
            }

            if (await RefreshLocationAsync(result.Note!) is null)
                StatusText = $"{savedAt}. The note's location could not be shown.";
            State = NoteEditorState.Editing;
            Evaluate();
            return;
        }

        ShowFailure(result);
        State = NoteEditorState.Editing; // the draft is untouched; a failed close-triggered save never re-prompts
        Evaluate();
    }

    private async Task SaveAsNewAsync()
    {
        if (State != NoteEditorState.Editing || _noteId is not { } originalId || _autoSaveInFlight)
            return;

        var draft = new ProjectNoteDraft(Title, Content);
        TitleError = ContentError = null;
        State = NoteEditorState.Saving;

        NoteWriteResult result;
        try
        {
            result = await _content.SaveNoteAsNewAsync(originalId, _projectNodeId, _folderId, draft);
        }
        catch (Exception ex)
        {
            result = NoteWriteResult.Failed(NoteWriteStatus.Error, $"Could not save as a new note: {ex.Message} Your text is unchanged here.");
        }

        if (result.Succeeded)
        {
            Accept(result.Note!, created: true); // the editor now edits the new note; the original stays tracked
            GeneralError = null;
            CanRecover = false;
            // The store resolved the destination inside the write (§6.2); show where the new row actually is.
            StatusText = await RefreshLocationAsync(result.Note!) is { } location
                ? $"Saved as a new note in {location}. The original note was not changed."
                : "Saved as a new note. Its location could not be shown. The original note was not changed.";
        }
        else
        {
            if (result.Status == NoteWriteStatus.Invalid)
                ShowFailure(result);
            else
                GeneralError = result.Error;
            if (result.Status == NoteWriteStatus.ProjectTrashed)
                IsProjectTrashed = true; // Copy draft remains; Save as new is withdrawn
        }

        State = NoteEditorState.Editing;
        Evaluate();
    }

    private void CopyDraft()
    {
        _dialogs.CopyToClipboard($"{Title}\r\n\r\n{Content}");
        StatusText = "Your text was copied to the clipboard.";
    }

    /// <summary>
    /// After a successful MANUAL write (Save, close-triggered Save, Save as new note): the persisted values become the new
    /// baseline AND the inputs (they were read-only during the write). Resumes a paused autosave (Phase 18 D7).
    /// </summary>
    private void Accept(ProjectNote note, bool created)
    {
        _noteId = note.Id;
        _revision = note.Revision;
        _folderId = note.FolderId;
        _baselineTitle = note.Title;
        _baselineText = note.Content;
        _title = note.Title;
        _text = note.Content;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Content));
        Track(note.Id);
        if (created)
            _created.Add(note.Id);
        SavedAny = true;
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(NoteId));
        if (_autoSavePaused)
        {
            _autoSavePaused = false;
            OnPropertyChanged(nameof(IsAutoSavePaused));
        }

        OnDraftChanged();
        ClearAutoSaveSchedule(); // the inputs now equal the stored note: nothing is uncovered
    }

    /// <summary>
    /// After a committed write: reads the logical path of the saved row's container (its persisted ProjectNodeId and
    /// FolderId) and shows it, or the "unavailable" fallback. Returns the path, or null when it could not be shown.
    /// </summary>
    private async Task<string?> RefreshLocationAsync(ProjectNote saved)
    {
        var location = await TryReadLocationAsync(_content, saved);
        LocationText = DescribeLocation(location);
        return location;
    }

    private void ShowFailure(NoteWriteResult result)
    {
        switch (result.Field)
        {
            case ProjectNoteValidator.Fields.Title: TitleError = result.Error; break;
            case ProjectNoteValidator.Fields.Content: ContentError = result.Error; break;
            default: GeneralError = result.Error; break;
        }

        if (result.Status == NoteWriteStatus.ProjectTrashed)
        {
            IsProjectTrashed = true; // the text stays here; Copy draft is the recovery
            CanRecover = true;
        }
        else if (result.OffersRecovery)
        {
            CanRecover = true;
        }
    }

    private void Approve()
    {
        State = NoteEditorState.Closing;
        CloseAutoSaveScheduler(); // before CloseRequested: a late callback finds the scheduler closed
        CloseRequested?.Invoke(this, SavedAny);
    }

    private void Track(long id)
    {
        if (!_touched.Contains(id))
            _touched.Add(id);
    }

    private void OnDraftChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommands()
    {
        SaveCommand.RaiseCanExecuteChanged();
        RequestCloseCommand.RaiseCanExecuteChanged();
        CopyDraftCommand.RaiseCanExecuteChanged();
        SaveAsNewCommand.RaiseCanExecuteChanged();
    }

    // ---- Phase 18: autosave scheduler ----------------------------------------------------------------------------------

    /// <summary>Title (after the validator's trim) and content (verbatim) equal the given stored/normalized values.</summary>
    private bool DraftMatches(string title, string content) =>
        string.Equals(Title.Trim(), title, StringComparison.Ordinal) && string.Equals(Content, content, StringComparison.Ordinal);

    /// <summary>
    /// "Clean" for scheduling (§4.5): against the in-flight write's normalized snapshot while it runs, otherwise against the
    /// stored baseline.
    /// </summary>
    private bool UncoveredClean => _autoSaveInFlight
        ? DraftMatches(_inFlightTitle!, _inFlightContent!)
        : DraftMatches(_baselineTitle, _baselineText);

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    private long? EarliestDue()
    {
        long? maxWait = _firstUncoveredAt is { } first ? first + Ticks(AutoSaveMaxWait) : null;
        return (_debounceDueAt, maxWait) switch
        {
            ({ } d, { } m) => Math.Min(d, m),
            ({ } d, null) => d,
            (null, { } m) => m,
            _ => null,
        };
    }

    /// <summary>A user change to Title or Content.</summary>
    private void OnUserEdit()
    {
        if (!AutoSaveApplies)
            return;

        if (UncoveredClean)
        {
            ClearAutoSaveSchedule(); // back to the reference: the next change starts a new cycle
            return;
        }

        var now = _time.GetTimestamp();
        _debounceDueAt = now + Ticks(AutoSaveDebounce);
        _firstUncoveredAt ??= now;
        if (!_autoSavePaused)
            Arm(now); // also during an automatic write: a due tick then only marks "due" (Evaluate returns at step 2)
    }

    private void ClearAutoSaveSchedule()
    {
        _debounceDueAt = null;
        _firstUncoveredAt = null;
        Disarm();
    }

    private void Arm(long now)
    {
        if (_schedulerClosed || EarliestDue() is not { } due)
        {
            Disarm();
            return;
        }

        var delay = due <= now ? TimeSpan.Zero : _time.GetElapsedTime(now, due);
        _timer ??= _time.CreateTimer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void Disarm() => _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void CloseAutoSaveScheduler()
    {
        _schedulerClosed = true;
        _pending = PendingRequest.None;
        _debounceDueAt = null;
        _firstUncoveredAt = null;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>The timer callback (any thread): only posts an evaluation to the editor's context.</summary>
    private void OnTimerTick(object? state)
    {
        if (_ui is { } ui)
            ui.Post(_ => Evaluate(), null);
        else
            Evaluate(); // no context captured (tests without one)
    }

    /// <summary>
    /// Phase 18 §4.5.1: decides from the CURRENT state whether an automatic write starts now. Safe to call any number of
    /// times; a stale or late call does nothing.
    /// </summary>
    internal void Evaluate()
    {
        if (!AutoSaveApplies)
            return; // closed, off, or a note not saved yet: no store call, no state change, no re-arm
        if (_autoSavePaused || _autoSaveInFlight || _pending != PendingRequest.None || State != NoteEditorState.Editing)
            return; // the end of the write / request / prompt / pause evaluates again

        if (UncoveredClean)
        {
            ClearAutoSaveSchedule();
            return;
        }

        var now = _time.GetTimestamp();
        if (EarliestDue() is not { } due)
        {
            // Dirty without marks (defensive): start a cycle now.
            _debounceDueAt = now + Ticks(AutoSaveDebounce);
            _firstUncoveredAt = now;
            Arm(now);
            return;
        }

        if (now < due)
        {
            Arm(now); // the schedule moved since this callback was armed: no write yet
            return;
        }

        CurrentAutoSave = AutoSaveAsync();
    }

    private async Task AutoSaveAsync()
    {
        var draft = new ProjectNoteDraft(Title, Content);
        var (normalized, field, error) = ProjectNoteValidator.Validate(draft);
        if (normalized is null)
        {
            // Not a pause: nothing is written; the next change that makes the draft valid makes it eligible again.
            if (field == ProjectNoteValidator.Fields.Content)
            {
                ContentError = error;
                StatusText = AutoSaveInvalidContentStatus;
            }
            else
            {
                TitleError = error;
                StatusText = AutoSaveInvalidTitleStatus;
            }

            Disarm();
            return;
        }

        _autoSaveInFlight = true;
        _inFlightTitle = normalized.Title ?? string.Empty;
        _inFlightContent = normalized.Content ?? string.Empty;
        _debounceDueAt = null; // the snapshot covers everything up to now
        _firstUncoveredAt = null;
        Disarm();
        OnPropertyChanged(nameof(IsAutoSaving));

        NoteWriteResult result;
        try
        {
            result = await _content.SaveNoteAsync(_noteId!.Value, _revision, draft);
        }
        catch (Exception ex)
        {
            result = NoteWriteResult.Failed(NoteWriteStatus.Error, $"Could not save automatically: {ex.Message} Your text is unchanged here.");
        }

        CompleteAutoSave(result);
    }

    private void CompleteAutoSave(NoteWriteResult result)
    {
        _autoSaveInFlight = false;
        _inFlightTitle = _inFlightContent = null;
        OnPropertyChanged(nameof(IsAutoSaving));
        if (_schedulerClosed)
            return; // defensive: Close waits for the write, so this does not happen

        // (1) Apply the result.
        if (result.Succeeded)
        {
            var note = result.Note!;
            _revision = note.Revision;
            _folderId = note.FolderId;
            _baselineTitle = note.Title;   // the stored (trimmed) title; the box keeps what the user typed
            _baselineText = note.Content;
            Track(note.Id);
            SavedAny = true;
            TitleError = ContentError = GeneralError = null;
            StatusText = $"{AutoSavedStatusPrefix}{_time.GetLocalNow():HH:mm}";
            OnDraftChanged();
            if (UncoveredClean)
            {
                ClearAutoSaveSchedule(); // the draft equals what the store returned
            }
            else if (EarliestDue() is null)
            {
                var now = _time.GetTimestamp(); // defensive: dirty but no change was recorded during the write
                _debounceDueAt = now + Ticks(AutoSaveDebounce);
                _firstUncoveredAt = now;
            }
            // otherwise the uncovered part keeps its own marks (max-wait from its first change after the snapshot)
        }
        else if (result.Status == NoteWriteStatus.Invalid)
        {
            ShowFailure(result); // validated before the call, so not expected; not a pause
        }
        else
        {
            _autoSavePaused = true;
            OnPropertyChanged(nameof(IsAutoSavePaused));
            ShowFailure(result); // recovery offers exactly as for a manual save (none for Error)
            StatusText = result.Status == NoteWriteStatus.Error ? AutoSavePausedRetryStatus : AutoSaveStoppedStatus;
            Disarm();
        }

        // (2)-(4) The pending request, once.
        var pending = _pending;
        _pending = PendingRequest.None;
        switch (pending)
        {
            case PendingRequest.Save:
                CurrentPendingSave = SaveCoreAsync(fromClose: false);
                return;
            case PendingRequest.Close:
                RequestClose();
                return;
        }

        // (5) Then the timers.
        Evaluate();
    }
}
