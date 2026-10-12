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
/// Modal plain-text Note editor. No autosave: only Save / Ctrl+S (and the Save choice when closing) write.
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
/// </summary>
public sealed class NoteEditorViewModel : ObservableObject
{
    private readonly ProjectContentService _content;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly long _projectNodeId;
    private readonly List<long> _touched = [];
    private readonly HashSet<long> _created = [];
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

    private NoteEditorViewModel(
        ProjectContentService content, IDialogService dialogs, long projectNodeId, long? folderId, ProjectNote? existing,
        string? locationPath, TimeProvider? time)
    {
        _content = content;
        _dialogs = dialogs;
        _time = time ?? TimeProvider.System;
        _projectNodeId = projectNodeId;
        _folderId = folderId;
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
        SaveAsNewCommand = new AsyncRelayCommand(SaveAsNewAsync, () => State == NoteEditorState.Editing && CanRecover && _noteId is not null);
    }

    /// <summary>A new note in the given container (null = Project root). Nothing is written until the first Save.</summary>
    public static NoteEditorViewModel ForCreate(ProjectContentService content, IDialogService dialogs, long projectNodeId, long? folderId, string locationPath, TimeProvider? time = null) =>
        new(content, dialogs, projectNodeId, folderId, null, locationPath, time);

    /// <summary>
    /// An existing note, freshly read by the caller. <paramref name="locationPath"/> is the path of THAT note's container
    /// (see <see cref="TryReadLocationAsync"/>), not the listing the caller shows; null = it could not be determined.
    /// </summary>
    public static NoteEditorViewModel ForEdit(ProjectContentService content, IDialogService dialogs, ProjectNote note, string? locationPath, TimeProvider? time = null) =>
        new(content, dialogs, note.ProjectNodeId, note.FolderId, note, locationPath, time);

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
                OnDraftChanged();
        }
    }

    /// <summary>Note content, exactly as typed.</summary>
    public string Content
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
                OnDraftChanged();
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

    /// <summary>Inputs are editable only while Editing (read-only during a pending write and while prompting).</summary>
    public bool IsReadOnly => State != NoteEditorState.Editing;

    public bool IsSaving => State == NoteEditorState.Saving;

    /// <summary>The window may close (the view model reached <see cref="NoteEditorState.Closing"/>).</summary>
    public bool IsCloseApproved => State == NoteEditorState.Closing;

    /// <summary>No note has been created yet (the first Save inserts it).</summary>
    public bool IsNew => _noteId is null;

    public bool IsDirty => !string.Equals(Title, _baselineTitle, StringComparison.Ordinal) || !string.Equals(Content, _baselineText, StringComparison.Ordinal);

    public string WindowTitle => $"{(string.IsNullOrWhiteSpace(_baselineTitle) ? "New note" : _baselineTitle)} — Note{(IsDirty ? " •" : string.Empty)}";

    public string? TitleError { get => _titleError; private set => SetProperty(ref _titleError, value); }

    public string? ContentError { get => _contentError; private set => SetProperty(ref _contentError, value); }

    public string? GeneralError { get => _generalError; private set => SetProperty(ref _generalError, value); }

    /// <summary>"Saved 14:02", "Draft copied …" and similar confirmations.</summary>
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

    /// <summary>Something was persisted during this editor session.</summary>
    public bool SavedAny { get; private set; }

    /// <summary>The note currently edited (null until a new note is first saved).</summary>
    public long? NoteId => _noteId;

    /// <summary>The existing note opened and every note created in this session, in order, without duplicates.</summary>
    public IReadOnlyList<long> TouchedNoteIds => _touched;

    /// <summary>Notes created in this session (first save of a new note, Save as new note).</summary>
    public IReadOnlyCollection<long> CreatedNoteIds => _created;

    /// <summary>The last close request's work (prompt and close-triggered save); lets tests await it.</summary>
    internal Task CurrentClose { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Entry point for EVERY close path. Ignored unless Editing (so it never stacks prompts or closes during a write).
    /// Clean → closes. Dirty → Save / Discard / Cancel.
    /// </summary>
    public void RequestClose() => CurrentClose = RequestCloseAsync();

    private async Task RequestCloseAsync()
    {
        if (State != NoteEditorState.Editing)
            return;

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
                break;
        }
    }

    private string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "New note" : Title.Trim();

    private async Task SaveCoreAsync(bool fromClose)
    {
        if (State != NoteEditorState.Editing)
            return;

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
            return;
        }

        ShowFailure(result);
        State = NoteEditorState.Editing; // the draft is untouched; a failed close-triggered save never re-prompts
    }

    private async Task SaveAsNewAsync()
    {
        if (State != NoteEditorState.Editing || _noteId is not { } originalId)
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
        }

        State = NoteEditorState.Editing;
    }

    private void CopyDraft()
    {
        _dialogs.CopyToClipboard($"{Title}\r\n\r\n{Content}");
        StatusText = "Your text was copied to the clipboard.";
    }

    /// <summary>The persisted values become the new baseline (the inputs were read-only during the write).</summary>
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
        OnDraftChanged();
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

        if (result.OffersRecovery)
            CanRecover = true;
    }

    private void Approve()
    {
        State = NoteEditorState.Closing;
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
}
