using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.Services;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 18 note autosave, view-model level (proposal Revision 3, approved A4b timeline). Every test is synchronous and
/// deterministic: a monotonic manual clock (<see cref="MonotonicTestClock"/>), a queued synchronization context run on the test
/// thread (<see cref="QueuedSynchronizationContext"/>) and an in-memory note save that can hold a write until the test releases
/// it (<see cref="ScriptedSaves"/>). No real sleep, no window.
/// </summary>
public sealed class NoteEditorAutoSaveTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly GatedProjectContentStore _store;
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _other;
    private readonly long _alpha;
    private readonly MonotonicTestClock _clock = new();
    private readonly QueuedSynchronizationContext _queue = new();
    private readonly IDisposable _installed;
    private readonly ScriptedSaves _saves;
    private int _closeRequests;

    public NoteEditorAutoSaveTests()
    {
        _root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var launcher = new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true);
        _store = new GatedProjectContentStore(new SqliteProjectContentStore(connections));
        _content = new ProjectContentService(_store, launcher);
        _other = new ProjectContentService(new SqliteProjectContentStore(connections), launcher);
        _alpha = new WorkspaceService(new SqliteWorkspaceStore(connections)).CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active)).Result.Value!.Id;
        _saves = new ScriptedSaves(_clock);
        _store.SaveNoteHandler = _saves.Handle;
        _store.ContainerPathHandler = (_, _) => Task.FromResult<ProjectContainerPath?>(new ProjectContainerPath("Alpha", []));
        _installed = _queue.Install(); // the editor captures this context when it is created
    }

    public void Dispose()
    {
        _installed.Dispose();
        _root.Dispose();
    }

    private NoteEditorViewModel Editing(string title = "Notes", string content = "agenda", long revision = 5, bool autoSave = true)
    {
        var note = new ProjectNote(101, _alpha, null, title, content, revision, _clock.GetUtcNow(), _clock.GetUtcNow());
        _saves.Reset(note);
        var editor = NoteEditorViewModel.ForEdit(_content, _dialogs, note, "Alpha", _clock, autoSave);
        editor.CloseRequested += (_, _) => _closeRequests++;
        return editor;
    }

    /// <summary>Moves the clock to <paramref name="seconds"/> and runs everything posted to the editor's context.</summary>
    private void At(double seconds)
    {
        _clock.AdvanceTo(seconds);
        _queue.RunAll();
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // ---- A2, A3, A3b, A15 -------------------------------------------------------------------------------------------------

    [Fact]
    public void A2_SettingOff_NeverWritesAutomatically()
    {
        var editor = Editing(autoSave: false);

        editor.Content = "changed";
        At(120);

        Assert.Empty(_saves.Calls);
        Assert.False(editor.IsAutoSaveEnabled);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void A3_OneChange_IsSavedOnceAfterTheThreeSecondDebounce_WithTheOpenedRevision()
    {
        var editor = Editing();

        editor.Content = "agenda v2";
        At(2.9);
        Assert.Empty(_saves.Calls);
        At(3.0);

        var call = Assert.Single(_saves.Calls);
        Assert.Equal((S(3), 5L, "agenda v2"), (call.At, call.ExpectedRevision, call.Content));
        Assert.StartsWith(NoteEditorViewModel.AutoSavedStatusPrefix, editor.StatusText);
        Assert.False(editor.IsDirty);
        Assert.False(editor.HasAutoSaveSchedule);
    }

    [Fact]
    public void A3b_TrimmedTitle_IsSavedOnce_TheBoxKeepsTheSpaces_NoRepeat_AndCloseNeedsNoPrompt()
    {
        var editor = Editing(title: "Notes");

        editor.Title = " Meeting ";
        At(3);
        At(123);

        var call = Assert.Single(_saves.Calls);                        // exactly one write, no repeat in 120 s
        Assert.Equal("Meeting", call.Title);                          // the service trims before the store
        Assert.Equal(" Meeting ", editor.Title);                      // the box keeps what the user typed
        Assert.Equal("Meeting — Note", editor.WindowTitle);           // baseline = the stored, trimmed title
        Assert.False(editor.IsDirty);
        Assert.Equal(6, _saves.Revision);

        editor.RequestClose();                                        // A15: nothing left unsaved
        _queue.RunAll();
        Assert.Equal(0, _dialogs.UnsavedChangesPrompts);
        Assert.Equal(1, _closeRequests);
        Assert.True(editor.SavedAny);
        Assert.Contains(101L, editor.TouchedNoteIds);
    }

    [Fact]
    public void A3b_OnlySpacesAroundTheStoredTitle_AreCleanFromTheStart()
    {
        var editor = Editing(title: "Meeting");

        editor.Title = " Meeting ";
        Assert.False(editor.IsDirty);
        Assert.False(editor.HasAutoSaveSchedule);
        At(120);

        Assert.Empty(_saves.Calls);
        editor.RequestClose();
        _queue.RunAll();
        Assert.Equal(0, _dialogs.UnsavedChangesPrompts);
        Assert.Equal(1, _closeRequests);
    }

    // ---- A4, A4b, A4c, A4d ------------------------------------------------------------------------------------------------

    [Fact]
    public void A4_ContinuousTyping_SavesAtTheThirtySecondMark_ThenAfterTheLastChange()
    {
        var editor = Editing();

        for (var s = 0; s <= 44; s++)
        {
            At(s);
            editor.Content = $"c{s}";
        }

        At(60);

        Assert.Equal([S(30), S(47)], _saves.Calls.Select(c => c.At));
        Assert.Equal(["c29", "c44"], _saves.Calls.Select(c => c.Content));
        Assert.Equal([5L, 6L], _saves.Calls.Select(c => c.ExpectedRevision));
    }

    /// <summary>
    /// The approved A4b timeline. A write held past the 30 s mark of text typed during it; the mark (35 s) is kept, so the
    /// second write starts at 50 s, when the first ends — not at the debounce (52 s) and not 30 s after the first write ended.
    /// </summary>
    [Fact]
    public void A4b_TheMaxWaitMarkOfTextTypedDuringAWrite_IsKept_AndTheNextWriteStartsTheMomentTheFirstEnds()
    {
        var editor = Editing(revision: 5);
        var r = 5L;

        editor.Content = "edit at 0";                            // t = 0
        _saves.HoldNext = true;
        At(3);                                                   // t = 3: W1 starts with revision r and is held
        var w1 = Assert.Single(_saves.Calls);
        Assert.Equal((S(3), r, "edit at 0"), (w1.At, w1.ExpectedRevision, w1.Content));
        Assert.True(editor.IsAutoSaving);
        Assert.False(editor.IsReadOnly);

        for (var s = 5; s <= 49; s++)                            // t = 5 first uncovered change (mark 35), ... last at 49
        {
            At(s);
            editor.Content = $"typed during W1 until {s}";
            _queue.RunAll();
            Assert.Single(_saves.Calls);                         // never a second (concurrent) write, also after t = 35
        }

        At(50);
        Assert.Single(_saves.Calls);
        Assert.Equal(S(50), _clock.Now);

        _saves.ReleaseHeld();                                    // W1 returns revision r+1
        _queue.RunAll();                                         // completion and queued callbacks, clock NOT advanced
        Assert.Equal(S(50), _clock.Now);

        Assert.Equal(2, _saves.Calls.Count);
        var w2 = _saves.Calls[1];
        Assert.Equal(S(50), w2.At);                              // immediately: the kept 35 s mark was due
        Assert.Equal("typed during W1 until 49", w2.Content);    // the whole t = 49 draft
        Assert.Equal(r + 1, w2.ExpectedRevision);

        At(53);                                                  // past the old debounce (52 s)
        At(120);
        Assert.Equal(2, _saves.Calls.Count);                     // no repeat from an old timer
        Assert.False(editor.IsDirty);
        Assert.Equal(r + 2, _saves.Revision);
    }

    [Fact]
    public void A4c_ReturningToTheBaseline_ClearsTheMarks_SoTheOldThirtySecondMarkNeverFires()
    {
        var editor = Editing(content: "agenda");

        editor.Content = "changed";                     // t = 0
        At(1);
        editor.Content = "agenda";                      // t = 1: back to the stored content
        Assert.False(editor.IsDirty);
        Assert.False(editor.HasAutoSaveSchedule);
        At(29);
        editor.Content = "changed again";               // t = 29: a new cycle (debounce 32, mark 59)

        At(30);
        Assert.Empty(_saves.Calls);                     // not at 30 from the old mark
        At(31.9);
        Assert.Empty(_saves.Calls);
        At(32);
        var call = Assert.Single(_saves.Calls);
        Assert.Equal((S(32), "changed again"), (call.At, call.Content));
        At(60);
        Assert.Single(_saves.Calls);
        Assert.False(editor.HasAutoSaveSchedule);
    }

    [Fact]
    public void A4d_AfterAWriteThatMatchesTheDraft_TheOldScheduleIsVoid_EvenWithAnOldCallbackQueued()
    {
        var editor = Editing();

        editor.Content = "s";                           // t = 0
        _saves.HoldNext = true;
        At(3);                                          // W1 starts with snapshot "s", held
        Assert.Single(_saves.Calls);
        At(5);
        editor.Content = "sX";                          // new part: mark 5, debounce 8
        _clock.AdvanceTo(S(8));                         // the debounce tick fires; its Evaluate is posted...
        var heldEvaluate = _queue.TakeAll();            // ...and held
        Assert.NotEmpty(heldEvaluate);
        At(9);
        editor.Content = "s";                           // back to the in-flight snapshot: marks cleared
        At(10);
        _saves.ReleaseHeld();                           // W1 returns the stored note "s"
        _queue.RunAll();

        Assert.Single(_saves.Calls);
        Assert.False(editor.IsDirty);
        Assert.False(editor.HasAutoSaveSchedule);       // old schedule cleared at completion
        foreach (var evaluate in heldEvaluate)
            evaluate();                                 // the old callback arrives late
        _queue.RunAll();
        Assert.Single(_saves.Calls);                    // no store call
        Assert.Equal(0, _clock.ArmedTimers);            // and nothing re-armed for 8 s or 35 s

        At(12);
        editor.Content = "sY";                          // a new cycle: debounce 15, mark 42
        At(14.9);
        Assert.Single(_saves.Calls);
        At(15);
        Assert.Equal(2, _saves.Calls.Count);
        Assert.Equal((S(15), "sY", 6L), (_saves.Calls[1].At, _saves.Calls[1].Content, _saves.Calls[1].ExpectedRevision));
        At(36);                                         // nothing at 35 from the old mark
        At(60);
        Assert.Equal(2, _saves.Calls.Count);
    }

    // ---- A5, A6 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A5_TextAddedDuringAWrite_StaysDirty_AndIsSavedNextWithTheReturnedRevision()
    {
        var editor = Editing();

        editor.Content = "A";
        _saves.HoldNext = true;
        At(3);
        editor.Content = "AB";                          // during W1 (t = 3)
        Assert.False(editor.IsReadOnly);
        _saves.ReleaseHeld();
        _queue.RunAll();

        Assert.Equal("AB", editor.Content);             // never replaced by the automatic write
        Assert.True(editor.IsDirty);                    // "B" is not stored yet
        At(6);
        Assert.Equal(2, _saves.Calls.Count);
        Assert.Equal(("AB", 6L), (_saves.Calls[1].Content, _saves.Calls[1].ExpectedRevision));
        Assert.Null(editor.GeneralError);
    }

    [Fact]
    public void A6_ManualSaveDuringAnAutomaticWrite_WaitsAndUsesTheNewRevision_WithoutASelfConflict()
    {
        var editor = Editing();

        editor.Content = "a";
        _saves.HoldNext = true;
        At(3);                                          // W1 held
        At(4);
        editor.Content = "ab";
        _ = editor.SaveCommand.ExecuteAsync();
        _ = editor.SaveCommand.ExecuteAsync();          // twice: one pending Save
        At(40);                                         // timers come due meanwhile
        Assert.Single(_saves.Calls);

        _saves.ReleaseHeld();
        _queue.RunAll();

        Assert.Equal(2, _saves.Calls.Count);
        Assert.Equal(("ab", 6L), (_saves.Calls[1].Content, _saves.Calls[1].ExpectedRevision));
        Assert.StartsWith("Saved ", editor.StatusText);
        Assert.DoesNotContain("automatically", editor.StatusText);
        Assert.Null(editor.GeneralError);
        Assert.Equal(NoteEditorState.Editing, editor.State);
        Assert.False(editor.IsDirty);
        At(80);
        Assert.Equal(2, _saves.Calls.Count);
    }

    // ---- A7, A8 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void A7_ANewNote_IsNotSavedAutomatically_UntilItsFirstManualSave()
    {
        var editor = NoteEditorViewModel.ForCreate(_content, _dialogs, _alpha, null, "Alpha", _clock, autoSave: true);
        editor.Title = "Fresh";
        editor.Content = "draft";
        At(60);
        Assert.Empty(_saves.Calls);
        Assert.True(editor.IsNew);

        var save = editor.SaveCommand.ExecuteAsync();   // CreateNoteAsync on the real store (other thread)
        _queue.RunUntil(() => save.IsCompleted);
        Assert.False(editor.IsNew);
        Assert.Contains(editor.NoteId!.Value, editor.CreatedNoteIds);
        var stored = new ProjectNote(editor.NoteId!.Value, _alpha, null, "Fresh", "draft", 1, _clock.GetUtcNow(), _clock.GetUtcNow()); // a new note starts at revision 1
        _saves.Reset(stored);

        At(61);
        editor.Content = "draft 2";
        At(64);
        var call = Assert.Single(_saves.Calls);
        Assert.Equal(("draft 2", stored.Revision), (call.Content, call.ExpectedRevision));
    }

    [Fact]
    public void A8_AnInvalidDraft_IsNeverWritten_IsNotAPause_AndResumesWhenValid()
    {
        var editor = Editing();

        editor.Title = "";
        At(10);
        Assert.Empty(_saves.Calls);
        Assert.NotNull(editor.TitleError);
        Assert.Equal(NoteEditorViewModel.AutoSaveInvalidTitleStatus, editor.StatusText);
        Assert.False(editor.IsAutoSavePaused);
        Assert.True(editor.IsDirty);

        editor.RequestClose();                          // invalid = unsaved: the prompt still appears (Cancel)
        _queue.RunAll();
        Assert.Equal(1, _dialogs.UnsavedChangesPrompts);
        Assert.Equal(0, _closeRequests);

        At(11);
        editor.Title = "Valid";                         // valid again: debounce 14 (the mark from t = 0 would be due at 30)
        At(14);
        Assert.Single(_saves.Calls);

        At(20);
        editor.Content = new string('x', ProjectNoteValidator.MaxContentLength + 1);
        At(23);
        Assert.Single(_saves.Calls);
        Assert.NotNull(editor.ContentError);
        Assert.Equal(NoteEditorViewModel.AutoSaveInvalidContentStatus, editor.StatusText);
        At(24);
        editor.Content = "ok";
        At(27);
        Assert.Equal(2, _saves.Calls.Count);
        Assert.Equal("ok", _saves.Calls[1].Content);
    }

    // ---- A11, A13, A13b (scripted outcomes) -------------------------------------------------------------------------------

    [Fact]
    public void A11_NotFound_PausesAutosave_KeepsTheDraft_AndOffersCopyDraftAndSaveAsNew()
    {
        var editor = Editing();
        _saves.NextOutcome = () => NoteStoreSaveResult.NotFound;

        editor.Content = "mine";
        At(3);
        At(130);

        Assert.Single(_saves.Calls);
        Assert.True(editor.IsAutoSavePaused);
        Assert.Equal("mine", editor.Content);
        Assert.Equal(NoteEditorViewModel.AutoSaveStoppedStatus, editor.StatusText);
        Assert.True(editor.CopyDraftCommand.CanExecute(null));
        Assert.True(editor.SaveAsNewCommand.CanExecute(null));
    }

    [Fact]
    public void A13_AWriteError_PausesWithoutRetry_OffersNoRecovery_AndAManualSaveResumes()
    {
        var editor = Editing();
        _saves.NextThrows = new IOException("database is locked");

        editor.Content = "mine";
        At(3);

        Assert.True(editor.IsAutoSavePaused);
        Assert.Equal(NoteEditorViewModel.AutoSavePausedRetryStatus, editor.StatusText);
        Assert.Contains("Your text is unchanged here.", editor.GeneralError);
        Assert.False(editor.CopyDraftCommand.CanExecute(null));
        Assert.False(editor.SaveAsNewCommand.CanExecute(null));
        At(123);
        Assert.Single(_saves.Calls);                    // no automatic retry

        var save = editor.SaveCommand.ExecuteAsync();   // the database is free again
        _queue.RunUntil(() => save.IsCompleted);
        Assert.False(editor.IsAutoSavePaused);
        Assert.Equal(2, _saves.Calls.Count);

        At(124);
        editor.Content = "mine 2";
        At(127);
        Assert.Equal(3, _saves.Calls.Count);            // autosave resumed
        Assert.Equal("mine 2", _saves.Calls[2].Content);
    }

    [Fact]
    public void A13b_AfterAFailedWrite_TypingNeverTriggersARetry()
    {
        var editor = Editing();
        _saves.NextThrows = new IOException("disk full");

        editor.Content = "a";
        At(3);
        for (var s = 4; s <= 64; s++)
        {
            At(s);
            editor.Content = $"a{s}";
        }

        At(120);
        Assert.Single(_saves.Calls);
        Assert.Equal("a64", editor.Content);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    // ---- A14, A14b --------------------------------------------------------------------------------------------------------

    [Fact]
    public void A14_CloseRepeatedDuringAHeldWrite_GivesOnePrompt_NoInterleavedWrite_AndCancelKeepsTheLatestDraft()
    {
        var editor = Editing();
        var callsDuringPrompt = -1;
        var stateDuringPrompt = NoteEditorState.Editing;
        _dialogs.OnUnsavedChanges = _ =>
        {
            stateDuringPrompt = editor.State;
            At(60);                                     // timers come due while the prompt is open
            callsDuringPrompt = _saves.Calls.Count;
            return UnsavedChangesChoice.Cancel;
        };

        editor.Content = "a";
        _saves.HoldNext = true;
        At(3);                                          // W1 held
        At(4);
        editor.Content = "ab";                          // not in W1's snapshot
        editor.RequestClose();
        editor.RequestClose();
        editor.RequestClose();
        At(44);                                         // past the debounce and the 30 s mark of "ab"
        Assert.Single(_saves.Calls);
        Assert.Equal(0, _dialogs.UnsavedChangesPrompts);

        _saves.ReleaseHeld();
        _queue.RunAll();

        Assert.Equal(1, _dialogs.UnsavedChangesPrompts);           // exactly one prompt
        Assert.Equal(NoteEditorState.Prompting, stateDuringPrompt);
        Assert.Equal(1, callsDuringPrompt);                        // no write between W1 and the prompt, none during it
        Assert.Equal(0, _closeRequests);
        Assert.Equal("ab", editor.Content);                        // Cancel keeps the latest draft
        Assert.Equal(NoteEditorState.Editing, editor.State);
        Assert.Equal(2, _saves.Calls.Count);                       // then the due autosave writes it once
        Assert.Equal(("ab", 6L), (_saves.Calls[1].Content, _saves.Calls[1].ExpectedRevision));
        At(120);
        Assert.Equal(2, _saves.Calls.Count);
    }

    [Fact]
    public void A14b_SaveThenCloseDuringAWrite_CloseWins_AndSaveFromThePromptUsesTheNewRevision()
    {
        var editor = Editing();
        _dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Save;

        editor.Content = "a";
        _saves.HoldNext = true;
        At(3);
        editor.Content = "ab";
        _ = editor.SaveCommand.ExecuteAsync();
        editor.RequestClose();
        Assert.Single(_saves.Calls);

        _saves.ReleaseHeld();
        _queue.RunAll();

        Assert.Equal(1, _dialogs.UnsavedChangesPrompts);
        Assert.Equal(2, _saves.Calls.Count);
        Assert.Equal(("ab", 6L), (_saves.Calls[1].Content, _saves.Calls[1].ExpectedRevision));
        Assert.Equal(1, _closeRequests);
        Assert.Equal(NoteEditorState.Closing, editor.State);
    }

    // ---- A19, A20 ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A19_AStaleQueuedCallback_MakesNoEarlyStoreCall_AndTheNewScheduleSavesTheLatestDraftOnce()
    {
        var editor = Editing();

        editor.Content = "a";                           // t = 0, debounce 3
        _clock.AdvanceTo(S(3));                         // the tick fires; its Evaluate is posted...
        var held = _queue.TakeAll();                    // ...and held
        Assert.NotEmpty(held);
        _clock.AdvanceTo(S(3.5));
        editor.Content = "ab";                          // debounce 6.5
        _clock.AdvanceTo(S(3.6));
        foreach (var evaluate in held)
            evaluate();                                 // the old callback, late
        _queue.RunAll();
        Assert.Empty(_saves.Calls);                     // not due any more: no early call

        At(6.5);
        var call = Assert.Single(_saves.Calls);
        Assert.Equal((S(6.5), "ab", 5L), (call.At, call.Content, call.ExpectedRevision));
        At(60);
        Assert.Single(_saves.Calls);
    }

    [Fact]
    public void A20_ACallbackArrivingAfterTheEditorClosed_DoesNothing()
    {
        var editor = Editing();
        _dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Discard;

        editor.Content = "a";
        _clock.AdvanceTo(S(3));
        var held = _queue.TakeAll();                    // the tick's Evaluate, held
        Assert.NotEmpty(held);
        editor.RequestClose();                          // prompt → Don't save → Closing
        _queue.RunAll();
        Assert.Equal(NoteEditorState.Closing, editor.State);
        Assert.Equal(1, _closeRequests);
        Assert.True(editor.IsAutoSaveSchedulerClosed);
        Assert.Equal(0, _clock.ArmedTimers);

        var status = editor.StatusText;
        var dirty = editor.IsDirty;
        var changes = 0;
        editor.PropertyChanged += (_, _) => changes++;
        foreach (var evaluate in held)
            evaluate();
        _queue.RunAll();
        At(120);

        Assert.Empty(_saves.Calls);
        Assert.Equal(0, changes);
        Assert.Equal((NoteEditorState.Closing, status, dirty), (editor.State, editor.StatusText, editor.IsDirty));
        Assert.Equal(1, _closeRequests);
        Assert.Equal(0, _clock.ArmedTimers);
    }

    /// <summary>
    /// An in-memory note save with the store's revision rule. Each call is recorded with the clock time; a held call completes
    /// only when the test releases it (on the test thread).
    /// </summary>
    private sealed class ScriptedSaves(MonotonicTestClock clock)
    {
        private ProjectNote _note = null!;
        private TaskCompletionSource<NoteStoreSaveResult>? _held;
        private NoteStoreSaveResult? _heldResult;

        public List<(TimeSpan At, long ExpectedRevision, string Title, string Content)> Calls { get; } = [];

        public long Revision => _note.Revision;

        public bool HoldNext { get; set; }

        public Func<NoteStoreSaveResult>? NextOutcome { get; set; }

        public Exception? NextThrows { get; set; }

        public void Reset(ProjectNote note)
        {
            _note = note;
            Calls.Clear();
        }

        public Task<NoteStoreSaveResult> Handle(long noteId, long expectedRevision, ProjectNoteDraft draft)
        {
            Calls.Add((clock.Now, expectedRevision, draft.Title ?? string.Empty, draft.Content ?? string.Empty));
            if (NextThrows is { } failure)
            {
                NextThrows = null;
                return Task.FromException<NoteStoreSaveResult>(failure);
            }

            NoteStoreSaveResult result;
            if (NextOutcome is { } outcome)
            {
                NextOutcome = null;
                result = outcome();
            }
            else if (expectedRevision != _note.Revision)
            {
                result = NoteStoreSaveResult.Conflict(_note.Revision);
            }
            else
            {
                _note = _note with { Title = draft.Title!, Content = draft.Content!, Revision = _note.Revision + 1, UpdatedAt = clock.GetUtcNow() };
                result = NoteStoreSaveResult.Saved(_note);
            }

            if (!HoldNext)
                return Task.FromResult(result);

            HoldNext = false;
            _held = new TaskCompletionSource<NoteStoreSaveResult>();
            _heldResult = result;
            return _held.Task;
        }

        public void ReleaseHeld()
        {
            var held = _held ?? throw new InvalidOperationException("No write is held.");
            _held = null;
            held.SetResult(_heldResult!);
        }
    }
}
