using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// A REAL SQLite lock, not an injected exception: a separate connection holds a write transaction on the test database
/// while a Note save goes through the real store, service and editor. The service under test uses a short busy timeout
/// (test-only factory option); the app keeps Microsoft.Data.Sqlite's default.
/// </summary>
public sealed class NoteSqliteLockTests : IDisposable
{
    private const int BusyTimeoutSeconds = 2;

    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SqliteConnectionFactory _shortTimeout;
    private readonly ProjectContentService _content;
    private readonly ProjectContentService _other;
    private readonly long _alpha;

    public NoteSqliteLockTests()
    {
        _root.CreateInitializer().Initialize();
        var file = _root.Paths.DatabaseFile;
        var launcher = new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true);
        _shortTimeout = new SqliteConnectionFactory(file, BusyTimeoutSeconds);
        _content = new ProjectContentService(new SqliteProjectContentStore(_shortTimeout), launcher);
        var connections = new SqliteConnectionFactory(file);
        _other = new ProjectContentService(new SqliteProjectContentStore(connections), launcher);
        _alpha = new WorkspaceService(new SqliteWorkspaceStore(connections)).CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active)).Result.Value!.Id;
    }

    public void Dispose()
    {
        _shortTimeout.ClearPools(); // its own pool (different connection string) before the folder is deleted
        _root.Dispose();
    }

    /// <summary>Another writer: its own unpooled connection with BEGIN IMMEDIATE (RESERVED lock) held until disposed.</summary>
    private sealed class HeldWriteLock : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteTransaction _transaction;

        public HeldWriteLock(string databaseFile)
        {
            _connection = new SqliteConnectionFactory(databaseFile).Open(SqliteOpenMode.ReadWrite, pooling: false);
            _transaction = _connection.BeginTransaction(deferred: false);
        }

        public void Dispose()
        {
            _transaction.Rollback();
            _transaction.Dispose();
            _connection.Dispose();
        }
    }

    private (string Title, string Content, long Revision, string UpdatedAt) Row(long id)
    {
        using var connection = _root.Open(); // readers are allowed while another connection holds RESERVED
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Title, Content, Revision, UpdatedAt FROM ProjectNotes WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3));
    }

    [Fact]
    public async Task StoreSave_WhileAnotherConnectionHoldsAWriteTransaction_FailsWithSqliteBusy_AfterTheBusyTimeout()
    {
        var note = (await _other.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("Kickoff", "v1"))).Note!;
        var before = Row(note.Id);

        var watch = Stopwatch.StartNew();
        SqliteException busy;
        using (new HeldWriteLock(_root.Paths.DatabaseFile))
            busy = await Assert.ThrowsAsync<SqliteException>(() => _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Kickoff", "v2")));
        watch.Stop();

        Assert.Equal(5, busy.SqliteErrorCode); // SQLITE_BUSY
        Assert.Contains("database is locked", busy.Message);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(BusyTimeoutSeconds - 0.5), TimeSpan.FromSeconds(BusyTimeoutSeconds + 10));
        Assert.Equal(before, Row(note.Id)); // no partial update

        var retried = await _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Kickoff", "v2"));
        Assert.True(retried.Succeeded, retried.Error);
        var after = Row(note.Id);
        Assert.Equal(("v2", 2L), (after.Content, after.Revision));
    }

    [Fact]
    public async Task EditorSave_UnderARealLock_StaysPendingAndGuarded_KeepsTheDraft_ThenRetrySucceedsAfterRelease()
    {
        var note = (await _other.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("Kickoff", "v1"))).Note!;
        var before = Row(note.Id);
        var closes = 0;
        var editor = NoteEditorViewModel.ForEdit(_content, _dialogs, note, "Alpha");
        editor.CloseRequested += (_, _) => closes++;
        editor.Content = "my draft";

        Task save;
        using (new HeldWriteLock(_root.Paths.DatabaseFile))
        {
            save = editor.SaveCommand.ExecuteAsync();
            await Task.Delay(TimeSpan.FromSeconds(BusyTimeoutSeconds / 2.0)); // well inside the busy wait
            Assert.False(save.IsCompleted);                                    // really blocked by the other writer
            Assert.Equal(NoteEditorState.Saving, editor.State);
            Assert.True(editor.IsReadOnly);
            editor.RequestClose();                                             // close guard: ignored while pending
            Assert.False(editor.RequestCloseCommand.CanExecute(null));
            Assert.Equal(0, _dialogs.UnsavedChangesPrompts);
            await save;                                                        // the busy timeout expires
            Assert.Equal(before, Row(note.Id));                                // no partial update while locked
        }

        Assert.Equal(0, closes);
        Assert.Equal(NoteEditorState.Editing, editor.State);
        Assert.Contains("database is locked", editor.GeneralError);
        Assert.Equal("my draft", editor.Content);
        Assert.True(editor.IsDirty);
        Assert.False(editor.CanRecover);                                       // not a Conflict: plain retry
        Assert.Equal(before, Row(note.Id));

        await editor.SaveCommand.ExecuteAsync();                               // lock released: retry

        Assert.Null(editor.GeneralError);
        Assert.False(editor.IsDirty);
        var saved = Row(note.Id);
        Assert.Equal(("Kickoff", "my draft", 2L), (saved.Title, saved.Content, saved.Revision));
        Assert.Equal("Location: Alpha", editor.LocationText);
    }
}
