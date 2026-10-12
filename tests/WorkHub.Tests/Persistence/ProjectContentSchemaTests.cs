using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>Database-enforced V004 invariants, exercised with raw SQL (no store), so the schema alone is proven.</summary>
public sealed class ProjectContentSchemaTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnection _connection;
    private readonly long _alpha;
    private readonly long _beta;

    public ProjectContentSchemaTests()
    {
        _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize(); // pinned: V007 adds delete guards (see WorkspaceTrashSchemaTests)
        _connection = _root.Open();
        _alpha = Db.InsertNode(_connection, null, "Project", "Alpha");
        Db.InsertProjectDetails(_connection, _alpha, "0123456789abcdef0123456789abcdef");
        _beta = Db.InsertNode(_connection, null, "Project", "Beta");
        Db.InsertProjectDetails(_connection, _beta, "fedcba9876543210fedcba9876543210");
    }

    public void Dispose()
    {
        _connection.Dispose();
        _root.Dispose();
    }

    private long Folder(long project, long? parent, string name, string? key = null)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt)
            VALUES ($p, $parent, $name, $key, $now, $now) RETURNING Id;
            """;
        command.Parameters.AddWithValue("$p", project);
        command.Parameters.AddWithValue("$parent", (object?)parent ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$key", key ?? WorkspaceName.Key(name));
        command.Parameters.AddWithValue("$now", Db.Now);
        return (long)command.ExecuteScalar()!;
    }

    private long Link(long project, long? folder, string name = "L", string type = "Web", string target = "https://example.test/")
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt)
            VALUES ($p, $folder, $type, $name, $target, $now, $now) RETURNING Id;
            """;
        command.Parameters.AddWithValue("$p", project);
        command.Parameters.AddWithValue("$folder", (object?)folder ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$now", Db.Now);
        return (long)command.ExecuteScalar()!;
    }

    private static int Code(Action action) => Assert.Throws<SqliteException>(action).SqliteExtendedErrorCode;

    /// <summary>
    /// ON DELETE RESTRICT refuses the delete. SQLite implements FK actions as internal triggers, so the refusal carries the
    /// trigger-level extended code (1811) with the foreign-key message rather than 787.
    /// </summary>
    private void AssertRestricted(string sql, long id)
    {
        var ex = Assert.Throws<SqliteException>(() => Db.Exec(_connection, sql, ("$id", id)));
        Assert.Contains("FOREIGN KEY constraint failed", ex.Message);
    }

    private const int ForeignKey = 787;
    private const int Unique = 2067;
    private const int Check = 275;
    private const int Trigger = 1811;

    [Fact]
    public void Owner_MustBeAnExistingProject_NotAFolderNodeOrMissing()
    {
        var workspaceFolder = Db.InsertNode(_connection, null, "Folder", "Clients");

        Assert.Equal(ForeignKey, Code(() => Folder(workspaceFolder, null, "X")));
        Assert.Equal(ForeignKey, Code(() => Folder(9999, null, "X")));
        Assert.Equal(ForeignKey, Code(() => Link(workspaceFolder, null)));
        Assert.Equal(ForeignKey, Code(() => Link(9999, null)));
    }

    [Fact]
    public void CompositeForeignKeys_KeepParentsAndLinkFoldersInTheSameProject()
    {
        var alphaDocs = Folder(_alpha, null, "Docs");

        Assert.Equal(ForeignKey, Code(() => Folder(_beta, alphaDocs, "Specs")));
        Assert.Equal(ForeignKey, Code(() => Link(_beta, alphaDocs)));
        Assert.Equal(ForeignKey, Code(() => Folder(_alpha, 9999, "Specs")));

        var specs = Folder(_alpha, alphaDocs, "Specs");
        var link = Link(_alpha, specs);
        Assert.True(specs > 0 && link > 0);

        // Moving an existing row into another Project's folder is rejected too.
        var betaDocs = Folder(_beta, null, "Docs");
        Assert.Equal(ForeignKey, Code(() => Db.Exec(_connection, "UPDATE ProjectLinks SET FolderId = $f WHERE Id = $id;", ("$f", betaDocs), ("$id", link))));
        Assert.Equal(ForeignKey, Code(() => Db.Exec(_connection, "UPDATE ProjectFolders SET ParentFolderId = $f WHERE Id = $id;", ("$f", betaDocs), ("$id", specs))));
    }

    [Fact]
    public void SelfParent_IsRejected()
    {
        var docs = Folder(_alpha, null, "Docs");
        Assert.Equal(Check, Code(() => Db.Exec(_connection, "UPDATE ProjectFolders SET ParentFolderId = Id WHERE Id = $id;", ("$id", docs))));
    }

    [Fact]
    public void FolderNames_AreUniqueAmongSiblingFolders_RootAndChildSeparately_PerProject()
    {
        var docs = Folder(_alpha, null, "Docs");
        Assert.Equal(Unique, Code(() => Folder(_alpha, null, "DOCS")));
        Assert.Equal(Unique, Code(() => Folder(_alpha, null, "ＤＯＣＳ"))); // full-width folds to the same key

        Folder(_beta, null, "Docs");               // another Project may reuse the name
        Folder(_alpha, docs, "Docs");              // a child may share its parent's name
        Assert.Equal(Unique, Code(() => Folder(_alpha, docs, "docs")));

        // Expanding NFKC key: "㍿" and "株式会社" collide; hiragana and katakana stay distinct.
        Folder(_alpha, null, "㍿");
        Assert.Equal(Unique, Code(() => Folder(_alpha, null, "株式会社")));
        Folder(_alpha, null, "ひらがな");
        Folder(_alpha, null, "ヒラガナ");
    }

    [Fact]
    public void LinkNames_MayRepeat_AndMayMatchFolderNames()
    {
        Folder(_alpha, null, "Docs");
        Link(_alpha, null, "Docs");
        Link(_alpha, null, "Docs");
        Assert.Equal(2, Db.ScalarLong(_connection, "SELECT count(*) FROM ProjectLinks WHERE Name = 'Docs';"));
    }

    [Fact]
    public void Identity_Owner_AndCreatedAt_AreImmutable()
    {
        var docs = Folder(_alpha, null, "Docs");
        var link = Link(_alpha, null);

        foreach (var sql in new[]
                 {
                     "UPDATE ProjectFolders SET ProjectNodeId = $beta WHERE Id = $id;",
                     "UPDATE ProjectFolders SET CreatedAt = '2030-01-01T00:00:00.000Z' WHERE Id = $id;",
                     "UPDATE ProjectFolders SET Id = 777 WHERE Id = $id;",
                 })
        {
            var ex = Assert.Throws<SqliteException>(() => Db.Exec(_connection, sql, ("$beta", _beta), ("$id", docs)));
            Assert.Equal(Trigger, ex.SqliteExtendedErrorCode);
            Assert.Contains("WH_CONTENT_IMMUTABLE", ex.Message);
        }

        foreach (var sql in new[]
                 {
                     "UPDATE ProjectLinks SET ProjectNodeId = $beta WHERE Id = $id;",
                     "UPDATE ProjectLinks SET CreatedAt = '2030-01-01T00:00:00.000Z' WHERE Id = $id;",
                     "UPDATE ProjectLinks SET Id = 777 WHERE Id = $id;",
                 })
        {
            var ex = Assert.Throws<SqliteException>(() => Db.Exec(_connection, sql, ("$beta", _beta), ("$id", link)));
            Assert.Contains("WH_CONTENT_IMMUTABLE", ex.Message);
        }

        // Name, type, target and container stay editable.
        Db.Exec(_connection, "UPDATE ProjectLinks SET LinkType = 'File', Target = 'C:\\x.txt', Name = 'X' WHERE Id = $id;", ("$id", link));
    }

    [Fact]
    public void Checks_RejectInvalidTypeNamesAndTargets()
    {
        Assert.Equal(Check, Code(() => Link(_alpha, null, type: "Executable")));
        Assert.Equal(Check, Code(() => Link(_alpha, null, name: "")));
        Assert.Equal(Check, Code(() => Link(_alpha, null, name: " padded ")));
        Assert.Equal(Check, Code(() => Link(_alpha, null, name: new string('n', 201))));
        Assert.Equal(Check, Code(() => Link(_alpha, null, target: "")));
        Assert.Equal(Check, Code(() => Link(_alpha, null, target: " https://example.test/")));
        Assert.Equal(Check, Code(() => Link(_alpha, null, target: "https://example.test/" + new string('a', 2048))));
        Assert.Equal(Check, Code(() => Folder(_alpha, null, " Docs")));
        Assert.Equal(Check, Code(() => Folder(_alpha, null, "Docs", key: "")));
    }

    [Fact]
    public void DeletingAProjectWithContent_IsRestricted_EvenThroughTheV003Cascade()
    {
        var docs = Folder(_alpha, null, "Docs");
        Link(_alpha, docs);

        AssertRestricted("DELETE FROM WorkspaceNodes WHERE Id = $id;", _alpha);
        AssertRestricted("DELETE FROM Projects WHERE NodeId = $id;", _alpha);
        AssertRestricted("DELETE FROM ProjectFolders WHERE Id = $id;", docs);
        Assert.Equal(1, Db.Count(_connection, "ProjectFolders"));
        Assert.Equal(1, Db.Count(_connection, "ProjectLinks"));
        Assert.Equal(2, Db.Count(_connection, "Projects"));

        // A Project WITHOUT content still cascades exactly as in V003.
        Db.Exec(_connection, "DELETE FROM WorkspaceNodes WHERE Id = $id;", ("$id", _beta));
        Assert.Equal(1, Db.Count(_connection, "Projects"));
    }
}
