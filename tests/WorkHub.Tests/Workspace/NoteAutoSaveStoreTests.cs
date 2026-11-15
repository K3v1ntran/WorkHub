using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 18 note autosave against the real SQLite store (synthetic database in a temporary test root): conflicts with another
/// writer, notes and projects in the Trash, archived projects, byte-exact content (A9, A10, A12, A16). The editor runs under a
/// queued context on the test thread; the store's own continuations are awaited through <see cref="QueuedSynchronizationContext.RunUntil"/>.
/// </summary>
public sealed class NoteAutoSaveStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _other;
    private readonly WorkspaceService _workspace;
    private readonly long _alpha;
    private readonly MonotonicTestClock _clock = new();
    private readonly QueuedSynchronizationContext _queue = new();
    private IDisposable? _installed;

    public NoteAutoSaveStoreTests()
    {
        _root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        var launcher = new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true);
        _content = new ProjectContentService(new SqliteProjectContentStore(connections), launcher);
        _other = new ProjectContentService(new SqliteProjectContentStore(connections), launcher);
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(connections));
        _alpha = _workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active)).Result.Value!.Id;
    }

    public void Dispose()
    {
        _installed?.Dispose();
        _root.Dispose();
    }

    /// <summary>Runs a store call on the thread pool (no captured context) and waits for it.</summary>
    private static T Run<T>(Func<Task<T>> call) => Task.Run(call).GetAwaiter().GetResult();

    private ProjectNote Stored(long id) => Run(() => _other.GetNoteAsync(id))!;

    private (NoteEditorViewModel Editor, ProjectNote Note) Editing(string content = "agenda")
    {
        var note = Run(() => _other.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("Kickoff", content))).Note!;
        _installed = _queue.Install();
        return (NoteEditorViewModel.ForEdit(_content, _dialogs, note, "Alpha", _clock, autoSave: true), note);
    }

    private void AutoSaveAt(NoteEditorViewModel editor, double seconds)
    {
        _clock.AdvanceTo(seconds);
        _queue.RunAll();
        _queue.RunUntil(() => !editor.IsAutoSaving);
    }

    [Fact]
    public void A9_AnotherWriterSavedFirst_ConflictPauses_NothingIsOverwritten_AndSaveAsNewResumes()
    {
        var (editor, note) = Editing();
        Run(() => _other.SaveNoteAsync(note.Id, note.Revision, new ProjectNoteDraft("Kickoff", "theirs")));

        editor.Content = "mine";
        AutoSaveAt(editor, 3);

        Assert.True(editor.IsAutoSavePaused);
        Assert.Equal(NoteEditorViewModel.AutoSaveStoppedStatus, editor.StatusText);
        Assert.Equal(("theirs", 2L), (Stored(note.Id).Content, Stored(note.Id).Revision));
        Assert.Equal("mine", editor.Content);
        Assert.True(editor.CopyDraftCommand.CanExecute(null));
        Assert.True(editor.SaveAsNewCommand.CanExecute(null));

        AutoSaveAt(editor, 130);
        Assert.Equal(2, Stored(note.Id).Revision);           // no retry

        var save = editor.SaveCommand.ExecuteAsync();         // a manual Save meets the same conflict
        _queue.RunUntil(() => save.IsCompleted);
        Assert.True(editor.IsAutoSavePaused);
        Assert.Contains("changed elsewhere", editor.GeneralError);

        var asNew = editor.SaveAsNewCommand.ExecuteAsync();   // the way back
        _queue.RunUntil(() => asNew.IsCompleted);
        Assert.False(editor.IsAutoSavePaused);
        Assert.NotEqual(note.Id, editor.NoteId);
        var copy = editor.NoteId!.Value;

        editor.Content = "more";
        AutoSaveAt(editor, 133);
        Assert.Equal("more", Stored(copy).Content);
        Assert.StartsWith(NoteEditorViewModel.AutoSavedStatusPrefix, editor.StatusText);
        Assert.Equal("theirs", Stored(note.Id).Content);
    }

    [Fact]
    public void A10a_NoteMovedToTheTrash_Pauses_WithCopyDraftAndSaveAsNew()
    {
        var (editor, note) = Editing();
        Run(() => _other.TrashAsync(new ContentRef(ContentKind.Note, note.Id)));

        editor.Content = "mine";
        AutoSaveAt(editor, 3);

        Assert.True(editor.IsAutoSavePaused);
        Assert.True(editor.CopyDraftCommand.CanExecute(null));
        Assert.True(editor.SaveAsNewCommand.CanExecute(null));
        Assert.Equal("agenda", Stored(note.Id).Content);
    }

    [Fact]
    public void A10b_ProjectInTheWorkspaceTrash_PausesWithCopyDraftOnly_AndAManualSaveAfterRestoreResumes()
    {
        var (editor, note) = Editing();
        Run(() => _workspace.TrashProjectAsync(_alpha));

        editor.Content = "mine";
        AutoSaveAt(editor, 3);

        Assert.True(editor.IsAutoSavePaused);
        Assert.True(editor.IsProjectTrashed);
        Assert.True(editor.CopyDraftCommand.CanExecute(null));
        Assert.False(editor.SaveAsNewCommand.CanExecute(null));
        Assert.Equal("agenda", Stored(note.Id).Content);

        Run(() => _workspace.RestoreProjectAsync(_alpha));
        var save = editor.SaveCommand.ExecuteAsync();
        _queue.RunUntil(() => save.IsCompleted);
        Assert.False(editor.IsAutoSavePaused);
        Assert.Equal("mine", Stored(note.Id).Content);

        editor.Content = "mine 2";
        AutoSaveAt(editor, 10);
        Assert.Equal("mine 2", Stored(note.Id).Content);
    }

    [Fact]
    public void A12_AnArchivedProject_KeepsSavingAutomatically()
    {
        var (editor, note) = Editing();
        Run(() => _workspace.ArchiveProjectAsync(_alpha));

        editor.Content = "while archived";
        AutoSaveAt(editor, 3);

        Assert.False(editor.IsAutoSavePaused);
        Assert.Equal(("while archived", 2L), (Stored(note.Id).Content, Stored(note.Id).Revision));
    }

    [Fact]
    public void A16_ContentIsStoredByteForByte_AndTheEditorTextIsUnchanged()
    {
        var (editor, note) = Editing();
        var head = "line one\r\nline two\n\tindented  　全角スペース 日本語テキスト  \r\n";
        var text = head + new string('z', ProjectNoteValidator.MaxContentLength - head.Length);
        Assert.Equal(ProjectNoteValidator.MaxContentLength, text.Length);

        editor.Content = text;
        AutoSaveAt(editor, 3);

        Assert.Equal(text, Stored(note.Id).Content, StringComparer.Ordinal);
        Assert.Same(text, editor.Content);
        Assert.False(editor.IsDirty);
    }
}
