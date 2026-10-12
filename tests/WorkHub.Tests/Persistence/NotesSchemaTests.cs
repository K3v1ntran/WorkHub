using Microsoft.Data.Sqlite;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>Database-enforced V005 invariants with raw SQL. The lost-update guard (expected revision) is the store's job, not SQL's.</summary>
public sealed class NotesSchemaTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnection _connection;
    private readonly long _alpha;
    private readonly long _beta;
    private readonly long _alphaFolder;
    private readonly long _betaFolder;

    public NotesSchemaTests()
    {
        _root.CreateInitializer().Initialize();
        _connection = _root.Open();
        _alpha = Db.InsertNode(_connection, null, "Project", "Alpha");
        Db.InsertProjectDetails(_connection, _alpha, "0123456789abcdef0123456789abcdef");
        _beta = Db.InsertNode(_connection, null, "Project", "Beta");
        Db.InsertProjectDetails(_connection, _beta, "fedcba9876543210fedcba9876543210");
        _alphaFolder = Folder(_alpha, "Specs");
        _betaFolder = Folder(_beta, "Other");
    }

    public void Dispose()
    {
        _connection.Dispose();
        _root.Dispose();
    }

    private long Folder(long project, string name)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, $n, upper($n), $now, $now) RETURNING Id;";
        command.Parameters.AddWithValue("$p", project);
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$now", Db.Now);
        return (long)command.ExecuteScalar()!;
    }

    private long Note(long project, long? folder, string title = "T", string content = "", long? revision = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = revision is null
            ? "INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, $f, $t, $c, $now, $now) RETURNING Id;"
            : "INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, Revision, CreatedAt, UpdatedAt) VALUES ($p, $f, $t, $c, $r, $now, $now) RETURNING Id;";
        command.Parameters.AddWithValue("$p", project);
        command.Parameters.AddWithValue("$f", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue("$t", title);
        command.Parameters.AddWithValue("$c", content);
        command.Parameters.AddWithValue("$r", (object?)revision ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Db.Now);
        return (long)command.ExecuteScalar()!;
    }

    private string Fails(string sql, long id)
    {
        var ex = Assert.Throws<SqliteException>(() => Db.Exec(_connection, sql, ("$id", id), ("$other", _betaFolder), ("$beta", _beta)));
        return ex.Message;
    }

    [Fact]
    public void Ownership_IsAnExistingProject_AndTheFolderBelongsToTheSameProject()
    {
        var workspaceFolder = Db.InsertNode(_connection, null, "Folder", "Clients");

        Assert.Throws<SqliteException>(() => Note(workspaceFolder, null));
        Assert.Throws<SqliteException>(() => Note(9999, null));
        Assert.Throws<SqliteException>(() => Note(_alpha, _betaFolder));
        Assert.Throws<SqliteException>(() => Note(_alpha, 9999));
        Assert.True(Note(_alpha, null) > 0);       // Project root
        Assert.True(Note(_alpha, _alphaFolder) > 0);
    }

    [Fact]
    public void Checks_TitleContentAndInitialRevision()
    {
        Assert.Contains("CK_ProjectNotes_Title", Assert.Throws<SqliteException>(() => Note(_alpha, null, title: "")).Message);
        Assert.Contains("CK_ProjectNotes_Title", Assert.Throws<SqliteException>(() => Note(_alpha, null, title: " padded")).Message);
        Assert.Contains("CK_ProjectNotes_Title", Assert.Throws<SqliteException>(() => Note(_alpha, null, title: new string('t', 201))).Message);
        Assert.Contains("CK_ProjectNotes_Content", Assert.Throws<SqliteException>(() => Note(_alpha, null, content: new string('x', 100001))).Message);
        Assert.True(Note(_alpha, null, content: new string('x', 100000)) > 0);
        Assert.Contains("WH_NOTE_REVISION", Assert.Throws<SqliteException>(() => Note(_alpha, null, revision: 2)).Message);
        Assert.Equal(1, Db.ScalarLong(_connection, "SELECT max(Revision) FROM ProjectNotes;"));
    }

    [Fact]
    public void ContentUpdates_MustBumpTheRevisionByExactlyOne_MovesNeedNot()
    {
        var id = Note(_alpha, null, content: "v1");

        Assert.Contains("WH_NOTE_REVISION", Fails("UPDATE ProjectNotes SET Title = 'x' WHERE Id = $id;", id));
        Assert.Contains("WH_NOTE_REVISION", Fails("UPDATE ProjectNotes SET Content = Content WHERE Id = $id;", id));
        Assert.Contains("WH_NOTE_REVISION", Fails("UPDATE ProjectNotes SET Revision = Revision + 2 WHERE Id = $id;", id));
        Assert.Contains("WH_NOTE_REVISION", Fails("UPDATE ProjectNotes SET Content = 'v2', Revision = Revision WHERE Id = $id;", id));

        Db.Exec(_connection, "UPDATE ProjectNotes SET Content = 'v2', Revision = Revision + 1, UpdatedAt = 'u' WHERE Id = $id;", ("$id", id));
        Db.Exec(_connection, "UPDATE ProjectNotes SET FolderId = $f, UpdatedAt = 'm' WHERE Id = $id;", ("$f", _alphaFolder), ("$id", id));
        Assert.Equal("2|v2", Db.ScalarString(_connection, "SELECT Revision || '|' || Content FROM ProjectNotes WHERE Id = $id;", ("$id", id)));
    }

    [Fact]
    public void Identity_Owner_AndCreatedAt_AreImmutable_AndMovesStayInTheProject()
    {
        var id = Note(_alpha, null);

        Assert.Contains("WH_CONTENT_IMMUTABLE", Fails("UPDATE ProjectNotes SET ProjectNodeId = $beta WHERE Id = $id;", id));
        Assert.Contains("WH_CONTENT_IMMUTABLE", Fails("UPDATE ProjectNotes SET CreatedAt = 'x' WHERE Id = $id;", id));
        Assert.Contains("WH_CONTENT_IMMUTABLE", Fails("UPDATE ProjectNotes SET Id = 777 WHERE Id = $id;", id));
        Assert.Contains("FOREIGN KEY", Fails("UPDATE ProjectNotes SET FolderId = $other WHERE Id = $id;", id));
    }

    [Fact]
    public void Deletes_ThatWouldRemoveNotes_AreRestricted()
    {
        Note(_alpha, _alphaFolder);

        Assert.Contains("FOREIGN KEY constraint failed", Fails("DELETE FROM ProjectFolders WHERE Id = $id;", _alphaFolder));
        Assert.Contains("FOREIGN KEY constraint failed", Fails("DELETE FROM WorkspaceNodes WHERE Id = $id;", _alpha));
        Assert.Contains("FOREIGN KEY constraint failed", Fails("DELETE FROM Projects WHERE NodeId = $id;", _alpha));
        Assert.Equal(1, Db.Count(_connection, "ProjectNotes"));

        // A Project without any content still cascades exactly as in V003.
        Db.Exec(_connection, "DELETE FROM ProjectFolders WHERE Id = $id;", ("$id", _betaFolder));
        Db.Exec(_connection, "DELETE FROM WorkspaceNodes WHERE Id = $id;", ("$id", _beta));
        Assert.Equal(1, Db.Count(_connection, "Projects"));
    }
}
