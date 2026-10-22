using Microsoft.Data.Sqlite;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>
/// V007 SQL invariants exercised with raw SQL on the latest schema (no application code in between) — Phase 8 proposal
/// revision 2 §6.2, §6.3, §10.1.
/// </summary>
public sealed class WorkspaceTrashSchemaTests : IDisposable
{
    private const string Trashed = "2026-02-01T00:00:00.000Z";

    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnection _connection;
    private readonly long _clients;
    private readonly long _alpha;
    private readonly long _beta;

    public WorkspaceTrashSchemaTests()
    {
        _root.CreateInitializer().Initialize();
        _connection = _root.Open();
        _clients = Db.InsertNode(_connection, null, "Folder", "Clients");
        _alpha = Db.InsertNode(_connection, _clients, "Project", "Alpha");
        Db.InsertProjectDetails(_connection, _alpha, "0123456789abcdef0123456789abcdef");
        _beta = Db.InsertNode(_connection, null, "Project", "Beta");
        Db.InsertProjectDetails(_connection, _beta, "fedcba9876543210fedcba9876543210");
        Exec("INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Specs', 'SPECS', $now, $now);"); // 1
        Exec("INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES ($p, 1, 'Web', 'Wiki', 'https://wiki.example.test/', $now, $now);"); // 1
        Exec("INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, 1, 'Kickoff', 'v1', $now, $now);"); // 1
        Exec("INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Web', 'Old', 'https://old.example.test/', $now, $now);"); // 2
        Exec("INSERT INTO ProjectTrashEntries (ProjectNodeId, RootKind, RootId, ItemCount, TrashedAt) VALUES ($p, 'Link', 2, 1, $now);");
        Exec("UPDATE ProjectLinks SET TrashEntryId = 1 WHERE Id = 2;");
    }

    public void Dispose()
    {
        _connection.Dispose();
        _root.Dispose();
    }

    private void Exec(string sql, long? project = null) =>
        Db.Exec(_connection, sql, ("$p", project ?? _alpha), ("$now", Db.Now), ("$t", Trashed));

    private string Fails(string sql, long? project = null) =>
        Assert.Throws<SqliteException>(() => Exec(sql, project)).Message;

    private void Trash(long node) => Exec("UPDATE WorkspaceNodes SET TrashedAt = $t WHERE Id = $p;", node);

    private void Restore(long node) => Exec("UPDATE WorkspaceNodes SET TrashedAt = NULL WHERE Id = $p;", node);

    private List<string> Rows(string sql) => ProjectContentMigrationTests.Names(_connection, sql);

    [Fact]
    public void OnlyProjects_CanBeTrashed_AndNodesAreInsertedActive()
    {
        Assert.Contains("CK_WorkspaceNodes_TrashedAt", Fails("UPDATE WorkspaceNodes SET TrashedAt = $t WHERE Id = $p;", _clients));
        Assert.Contains("CK_WorkspaceNodes_TrashedAt", Fails("UPDATE WorkspaceNodes SET TrashedAt = '' WHERE Id = $p;"));
        Assert.Contains("WH_NODE_INSERT_TRASHED", Fails(
            "INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt, TrashedAt) VALUES (NULL, 'Project', 'Gamma', 'GAMMA', $now, $now, $t);"));
    }

    [Fact]
    public void TrashAndRestore_ChangeOnlyTheMarker()
    {
        Assert.Contains("WH_TRASH_MARKER_ONLY", Fails("UPDATE WorkspaceNodes SET TrashedAt = $t, Name = 'Renamed', NameKey = 'RENAMED' WHERE Id = $p;"));
        Assert.Contains("WH_TRASH_MARKER_ONLY", Fails("UPDATE WorkspaceNodes SET TrashedAt = $t, ParentId = NULL WHERE Id = $p;"));
        Assert.Contains("WH_TRASH_MARKER_ONLY", Fails("UPDATE WorkspaceNodes SET TrashedAt = $t, UpdatedAt = $t WHERE Id = $p;"));
        Assert.Contains("WH_TRASH_MARKER_ONLY", Fails("UPDATE WorkspaceNodes SET TrashedAt = $t, SortOrder = 5 WHERE Id = $p;"));

        var before = Rows("SELECT Id || '|' || coalesce(ParentId, '-') || '|' || Name || '|' || NameKey || '|' || SortOrder || '|' || UpdatedAt FROM WorkspaceNodes ORDER BY Id;");
        Trash(_alpha);
        Assert.Equal(Trashed, Db.ScalarString(_connection, "SELECT TrashedAt FROM WorkspaceNodes WHERE Id = $p;", ("$p", _alpha)));
        Assert.Contains("WH_TRASH_MARKER_ONLY", Fails("UPDATE WorkspaceNodes SET TrashedAt = NULL, Name = 'Back' WHERE Id = $p;"));
        Restore(_alpha);
        Assert.Equal(before, Rows("SELECT Id || '|' || coalesce(ParentId, '-') || '|' || Name || '|' || NameKey || '|' || SortOrder || '|' || UpdatedAt FROM WorkspaceNodes ORDER BY Id;"));
    }

    [Theory]
    [InlineData("UPDATE WorkspaceNodes SET Name = 'X', NameKey = 'X' WHERE Id = $p;")]
    [InlineData("UPDATE WorkspaceNodes SET ParentId = NULL WHERE Id = $p;")]
    [InlineData("UPDATE WorkspaceNodes SET UpdatedAt = '2027-01-01T00:00:00.000Z' WHERE Id = $p;")]
    [InlineData("UPDATE WorkspaceNodes SET TrashedAt = '2027-01-01T00:00:00.000Z' WHERE Id = $p;")]
    [InlineData("UPDATE Projects SET Description = 'changed' WHERE NodeId = $p;")]
    [InlineData("UPDATE Projects SET Status = 'Completed' WHERE NodeId = $p;")]
    public void TrashedProject_NodeAndDetails_AreReadOnly(string sql)
    {
        Trash(_alpha);

        Assert.Contains("WH_PROJECT_TRASHED", Fails(sql));
    }

    [Theory]
    [InlineData("INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'New', 'NEW', $now, $now);")]
    [InlineData("UPDATE ProjectFolders SET Name = 'Docs', NameKey = 'DOCS' WHERE Id = 1;")]
    [InlineData("DELETE FROM ProjectNotes WHERE Id = 1;")]
    [InlineData("INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Web', 'New', 'https://new.example.test/', $now, $now);")]
    [InlineData("UPDATE ProjectLinks SET Name = 'Wiki 2' WHERE Id = 1;")]
    [InlineData("DELETE FROM ProjectLinks WHERE Id = 1;")]
    [InlineData("INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'New', '', $now, $now);")]
    [InlineData("UPDATE ProjectNotes SET Content = 'v2', Revision = 2, UpdatedAt = '2027-01-01T00:00:00.000Z' WHERE Id = 1;")]
    [InlineData("DELETE FROM ProjectFolders WHERE Id = 1;")]
    [InlineData("INSERT INTO ProjectTrashEntries (ProjectNodeId, RootKind, RootId, ItemCount, TrashedAt) VALUES ($p, 'Note', 1, 1, $now);")]
    [InlineData("UPDATE ProjectTrashEntries SET ItemCount = 2 WHERE Id = 1;")]
    [InlineData("DELETE FROM ProjectTrashEntries WHERE Id = 1;")]
    [InlineData("UPDATE ProjectLinks SET TrashEntryId = NULL WHERE Id = 2;")] // a Phase 7 Restore inside a trashed Project
    public void TrashedProject_ContentAndPhase7Entries_AreFrozen(string sql)
    {
        Trash(_alpha);

        Assert.Contains("WH_PROJECT_TRASHED", Fails(sql));
    }

    [Fact]
    public void ContentOfOtherProjects_AndOfARestoredProject_StaysWritable()
    {
        Trash(_alpha);
        Exec("INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Specs', 'SPECS', $now, $now);", _beta);

        Restore(_alpha);
        Exec("UPDATE ProjectNotes SET Content = 'v2', Revision = 2, UpdatedAt = '2027-01-01T00:00:00.000Z' WHERE Id = 1;");
        Exec("UPDATE Projects SET Description = 'changed' WHERE NodeId = $p;");
        Assert.Equal(2, Db.ScalarLong(_connection, "SELECT Revision FROM ProjectNotes WHERE Id = 1;"));
    }

    [Fact]
    public void TrashThenRestore_LeavesEveryRowIdentical()
    {
        var snapshot = () => ProjectContentMigrationTests.DataSnapshot(_connection);
        var before = snapshot();

        Trash(_alpha);
        Restore(_alpha);

        Assert.Equal(before, snapshot());
    }

    [Fact]
    public void TrashedProjects_ReserveNoNames_AndRestoreIntoAConflictIsRejected()
    {
        Trash(_alpha);
        var child = Db.InsertNode(_connection, _clients, "Folder", "alpha", "ALPHA"); // child level, shared namespace
        Assert.Contains("UNIQUE", Fails("UPDATE WorkspaceNodes SET TrashedAt = NULL WHERE Id = $p;"));

        Trash(_beta);
        Db.InsertNode(_connection, null, "Project", "BETA", "BETA"); // root level
        Assert.Contains("UNIQUE", Fails("UPDATE WorkspaceNodes SET TrashedAt = NULL WHERE Id = $p;", _beta));

        Assert.Contains("UNIQUE", Assert.Throws<SqliteException>(() => Db.InsertNode(_connection, _clients, "Project", "ALPHA", "ALPHA")).Message); // active names still unique
        Exec("UPDATE WorkspaceNodes SET Name = 'Alpha (old)', NameKey = 'ALPHA (OLD)' WHERE Id = $p;", child);
        Restore(_alpha);
        Assert.Null(Db.ScalarString(_connection, "SELECT TrashedAt FROM WorkspaceNodes WHERE Id = $p;", ("$p", _alpha)));
    }

    [Fact]
    public void DeleteGuard_AllowsOnlyEmptyFolders_NeverProjects()
    {
        var empty = Db.InsertNode(_connection, null, "Folder", "Empty");
        var emptyProject = Db.InsertNode(_connection, null, "Project", "Bare");
        Db.InsertProjectDetails(_connection, emptyProject, "00000000000000000000000000000001");
        var holder = Db.InsertNode(_connection, null, "Folder", "Holder");
        var held = Db.InsertNode(_connection, holder, "Project", "Held");
        Db.InsertProjectDetails(_connection, held, "00000000000000000000000000000002");
        Trash(held);

        Assert.Contains("WH_NODE_DELETE_FORBIDDEN", Fails("DELETE FROM WorkspaceNodes WHERE Id = $p;", _clients)); // active child
        Assert.Contains("WH_NODE_DELETE_FORBIDDEN", Fails("DELETE FROM WorkspaceNodes WHERE Id = $p;", holder)); // only a trashed child
        Assert.Contains("WH_NODE_DELETE_FORBIDDEN", Fails("DELETE FROM WorkspaceNodes WHERE Id = $p;", emptyProject)); // V003 would cascade
        Assert.Contains("WH_NODE_DELETE_FORBIDDEN", Fails("DELETE FROM WorkspaceNodes WHERE Id = $p;", held)); // trashed Project
        Assert.Contains("WH_NODE_DELETE_FORBIDDEN", Fails("DELETE FROM Projects WHERE NodeId = $p;", emptyProject));

        Exec("DELETE FROM WorkspaceNodes WHERE Id = $p;", empty);
        Assert.Equal(0, Db.ScalarLongWith(_connection, "SELECT count(*) FROM WorkspaceNodes WHERE Id = $p;", ("$p", empty)));
    }

    [Fact]
    public void LeafContentOfAnActiveProject_IsNotProtectedBySql_OnlyByTheApplication()
    {
        // Documentation accuracy (§6.2): FK RESTRICT protects referenced parents, not leaf rows; the frozen-project triggers
        // apply only while the Project is trashed. WorkHub has no content-delete operation.
        Exec("DELETE FROM ProjectNotes WHERE Id = 1;");
        Assert.Contains("FOREIGN KEY", Fails("DELETE FROM ProjectFolders WHERE Id = 1;")); // still holds a link
    }

    [Fact]
    public void QueryPlans_UseTheNewIndexes()
    {
        Assert.Contains("IX_WorkspaceNodes_Trashed", Plan("SELECT Id FROM WorkspaceNodes WHERE TrashedAt IS NOT NULL ORDER BY TrashedAt DESC;"));
        // V009 replaced V007's active-name indexes by live-name indexes (a trashed Folder frees its name, like a trashed Project).
        Assert.Contains("UX_WorkspaceNodes_LiveChildName", Plan("SELECT Id FROM WorkspaceNodes WHERE ParentId = 1 AND NameKey = 'X' AND TrashedAt IS NULL AND FolderTrashedAt IS NULL;"));
        Assert.Contains("UX_WorkspaceNodes_LiveRootName", Plan("SELECT Id FROM WorkspaceNodes WHERE ParentId IS NULL AND NameKey = 'X' AND TrashedAt IS NULL AND FolderTrashedAt IS NULL;"));
    }

    private string Plan(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(reader.GetOrdinal("detail")));
        return string.Join(" | ", details);
    }
}
