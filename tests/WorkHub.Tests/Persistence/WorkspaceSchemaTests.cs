using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>V003 constraints, indexes and triggers exercised with raw SQL (no application code in between).</summary>
public sealed class WorkspaceSchemaTests : IDisposable
{
    private const int Unique = 2067;
    private const int Trigger = 1811;
    private const int Check = 275;
    private const int PrimaryKey = 1555;

    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnection _connection;

    public WorkspaceSchemaTests()
    {
        _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize(); // pinned: V007 adds delete guards (see WorkspaceTrashSchemaTests)
        _connection = _root.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _root.Dispose();
    }

    private SqliteException Fails(Action action) => Assert.Throws<SqliteException>(action);

    [Fact]
    public void RootNames_AreUnique_SharedByFoldersAndProjects()
    {
        Db.InsertNode(_connection, null, "Folder", "Alpha");

        Assert.Equal(Unique, Fails(() => Db.InsertNode(_connection, null, "Folder", "alpha", "ALPHA")).SqliteExtendedErrorCode);
        Assert.Equal(Unique, Fails(() => Db.InsertNode(_connection, null, "Project", "ALPHA", "ALPHA")).SqliteExtendedErrorCode);
    }

    [Fact]
    public void ChildNames_AreUniquePerParent_SharedByFoldersAndProjects_AndIndependentOfRoot()
    {
        var a = Db.InsertNode(_connection, null, "Folder", "A");
        var b = Db.InsertNode(_connection, null, "Folder", "B");
        Db.InsertNode(_connection, a, "Folder", "Docs");

        Assert.Equal(Unique, Fails(() => Db.InsertNode(_connection, a, "Project", "DOCS", "DOCS")).SqliteExtendedErrorCode);
        Db.InsertNode(_connection, b, "Project", "Docs"); // same name, other parent
        Db.InsertNode(_connection, null, "Project", "Docs"); // same name at the root

        Assert.Equal(5, Db.Count(_connection, "WorkspaceNodes"));
    }

    [Fact]
    public void Parent_MustBeAnExistingFolder()
    {
        var folder = Db.InsertNode(_connection, null, "Folder", "F");
        var project = Db.InsertNode(_connection, null, "Project", "P");
        var child = Db.InsertNode(_connection, folder, "Folder", "Child");

        var underProject = Fails(() => Db.InsertNode(_connection, project, "Folder", "X"));
        Assert.Equal(Trigger, underProject.SqliteExtendedErrorCode);
        Assert.Contains("WH_PARENT_NOT_FOLDER", underProject.Message);
        Assert.Contains("WH_PARENT_NOT_FOLDER", Fails(() => Db.InsertNode(_connection, 999, "Folder", "Y")).Message);

        var moveUnderProject = Fails(() => Db.Exec(_connection, "UPDATE WorkspaceNodes SET ParentId = $p WHERE Id = $id;", ("$p", project), ("$id", child)));
        Assert.Contains("WH_PARENT_NOT_FOLDER", moveUnderProject.Message);
        Assert.Contains("WH_PARENT_NOT_FOLDER", Fails(() => Db.Exec(_connection, "UPDATE WorkspaceNodes SET ParentId = 999 WHERE Id = $id;", ("$id", child))).Message);
        Assert.Equal(folder, Db.ScalarLong(_connection, $"SELECT ParentId FROM WorkspaceNodes WHERE Id = {child};"));
    }

    [Fact]
    public void SelfParenting_IsRejected()
    {
        var folder = Db.InsertNode(_connection, null, "Folder", "F");

        var ex = Fails(() => Db.Exec(_connection, "UPDATE WorkspaceNodes SET ParentId = Id WHERE Id = $id;", ("$id", folder)));

        Assert.True(ex.SqliteExtendedErrorCode is Check or Trigger, ex.Message);
        Assert.Equal(1, Db.ScalarLong(_connection, $"SELECT ParentId IS NULL FROM WorkspaceNodes WHERE Id = {folder};"));
    }

    [Theory]
    [InlineData("NodeType = 'Project'")]
    [InlineData("Id = 500")]
    [InlineData("CreatedAt = '2030-01-01T00:00:00.000Z'")]
    public void NodeIdentityTypeAndCreatedAt_AreImmutable(string change)
    {
        var folder = Db.InsertNode(_connection, null, "Folder", "F");

        var ex = Fails(() => Db.Exec(_connection, $"UPDATE WorkspaceNodes SET {change} WHERE Id = $id;", ("$id", folder)));

        Assert.Contains("WH_NODE_IMMUTABLE", ex.Message);
    }

    [Fact]
    public void NameAndUpdatedAt_CanChange()
    {
        var folder = Db.InsertNode(_connection, null, "Folder", "F");

        Db.Exec(_connection, "UPDATE WorkspaceNodes SET Name = 'G', NameKey = 'G', UpdatedAt = '2030-01-01T00:00:00.000Z' WHERE Id = $id;", ("$id", folder));

        Assert.Equal("G", Db.ScalarString(_connection, $"SELECT Name FROM WorkspaceNodes WHERE Id = {folder};"));
    }

    [Theory]
    [InlineData("'Thing'", "'X'", "'X'")]
    [InlineData("'Folder'", "''", "''")]
    [InlineData("'Folder'", "' X'", "' X'")]
    [InlineData("'Folder'", "'X'", "''")]
    [InlineData("'Folder'", "'X'", "NULL")]
    public void NodeConstraints_RejectInvalidValues(string type, string name, string key)
    {
        Fails(() => Db.Exec(_connection,
            $"INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt) VALUES (NULL, {type}, {name}, {key}, $n, $n);",
            ("$n", Db.Now)));
    }

    [Fact]
    public void DisplayName_IsLimitedTo200Characters_ButNameKeyHasNoUpperBound()
    {
        var longest = new string('㍿', 200);
        var key = WorkspaceName.Key(longest);

        var id = Db.InsertNode(_connection, null, "Project", longest, key);
        Fails(() => Db.InsertNode(_connection, null, "Project", new string('x', 201), "LONG"));

        Assert.Equal(800, Db.ScalarLong(_connection, $"SELECT length(NameKey) FROM WorkspaceNodes WHERE Id = {id};"));
        Assert.Equal(longest, Db.ScalarString(_connection, $"SELECT Name FROM WorkspaceNodes WHERE Id = {id};"));
    }

    [Fact]
    public void FolderWithChildren_CannotBeDeleted()
    {
        var folder = Db.InsertNode(_connection, null, "Folder", "F");
        Db.InsertNode(_connection, folder, "Folder", "Child");

        // SQLite reports ON DELETE RESTRICT as SQLITE_CONSTRAINT_TRIGGER with the foreign-key message.
        var restricted = Fails(() => Db.Exec(_connection, "DELETE FROM WorkspaceNodes WHERE Id = $id;", ("$id", folder)));
        Assert.Equal(19, restricted.SqliteErrorCode);
        Assert.Contains("FOREIGN KEY constraint failed", restricted.Message);
        Assert.Equal(2, Db.Count(_connection, "WorkspaceNodes"));
    }

    [Fact]
    public void ProjectDetails_DefaultToActive_AndAttachOnlyToProjectNodes()
    {
        var project = Db.InsertNode(_connection, null, "Project", "P");
        var folder = Db.InsertNode(_connection, null, "Folder", "F");

        Db.InsertProjectDetails(_connection, project);

        Assert.Equal("Active", Db.ScalarString(_connection, $"SELECT Status FROM Projects WHERE NodeId = {project};"));
        Assert.Contains("WH_NOT_A_PROJECT_NODE", Fails(() => Db.InsertProjectDetails(_connection, folder, "fedcba9876543210fedcba9876543210")).Message);
        Assert.Contains("WH_NOT_A_PROJECT_NODE", Fails(() => Db.InsertProjectDetails(_connection, 999, "fedcba9876543210fedcba9876543210")).Message);
        Assert.Equal(PrimaryKey, Fails(() => Db.InsertProjectDetails(_connection, project, "fedcba9876543210fedcba9876543210")).SqliteExtendedErrorCode); // one row per node
    }

    [Theory]
    [InlineData("ProjectId = 'fedcba9876543210fedcba9876543210'")]
    [InlineData("NodeId = 77")]
    public void ProjectIdAndNodeLink_AreImmutable(string change)
    {
        var project = Db.InsertNode(_connection, null, "Project", "P");
        Db.InsertProjectDetails(_connection, project);

        Assert.Contains("WH_PROJECTID_IMMUTABLE", Fails(() => Db.Exec(_connection, $"UPDATE Projects SET {change} WHERE NodeId = $id;", ("$id", project))).Message);
    }

    [Fact]
    public void ProjectId_IsUnique()
    {
        var a = Db.InsertNode(_connection, null, "Project", "A");
        var b = Db.InsertNode(_connection, null, "Project", "B");
        Db.InsertProjectDetails(_connection, a);

        Assert.Equal(Unique, Fails(() => Db.InsertProjectDetails(_connection, b)).SqliteExtendedErrorCode);
    }

    [Theory]
    [InlineData("ProjectId", "'0123456789ABCDEF0123456789ABCDEF'")]
    [InlineData("ProjectId", "'0123'")]
    [InlineData("Status", "'On Hold'")]
    [InlineData("Status", "'Deleted'")]
    [InlineData("Description", "''")]
    [InlineData("Owner", "''")]
    [InlineData("Owner", "' Sato'")]
    [InlineData("ProjectManager", "printf('%.101c', 'x')")]
    public void ProjectConstraints_RejectInvalidValues(string column, string value)
    {
        var project = Db.InsertNode(_connection, null, "Project", "P");
        var values = new Dictionary<string, string>
        {
            ["ProjectId"] = "'0123456789abcdef0123456789abcdef'", ["Status"] = "'Active'", ["Description"] = "NULL",
            ["Owner"] = "NULL", ["ProjectManager"] = "NULL",
        };
        values[column] = value;

        Fails(() => Db.Exec(_connection,
            $"INSERT INTO Projects (NodeId, ProjectId, Status, Description, Owner, ProjectManager) " +
            $"VALUES ($id, {values["ProjectId"]}, {values["Status"]}, {values["Description"]}, {values["Owner"]}, {values["ProjectManager"]});",
            ("$id", project)));
    }

    [Fact]
    public void ArchivedAndAllEditableStatuses_AreStorable()
    {
        foreach (var status in Enum.GetValues<ProjectStatus>())
        {
            var node = Db.InsertNode(_connection, null, "Project", status.ToString());
            Db.Exec(_connection, "INSERT INTO Projects (NodeId, ProjectId, Status) VALUES ($id, $pid, $s);",
                ("$id", node), ("$pid", Guid.NewGuid().ToString("N")), ("$s", ProjectStatuses.StorageCode(status)));
        }

        Assert.Equal(6, Db.Count(_connection, "Projects"));
    }

    [Fact]
    public void DeletingAProjectNode_RemovesItsDetailsRow()
    {
        var project = Db.InsertNode(_connection, null, "Project", "P");
        Db.InsertProjectDetails(_connection, project);

        Db.Exec(_connection, "DELETE FROM WorkspaceNodes WHERE Id = $id;", ("$id", project));

        Assert.Equal(0, Db.Count(_connection, "Projects"));
    }

    [Fact]
    public void NodeIds_AreNeverReused()
    {
        var first = Db.InsertNode(_connection, null, "Folder", "A");
        Db.Exec(_connection, "DELETE FROM WorkspaceNodes WHERE Id = $id;", ("$id", first));

        var second = Db.InsertNode(_connection, null, "Folder", "B");

        Assert.True(second > first);
    }

    [Fact]
    public void Tables_AreStrict()
    {
        Fails(() => Db.Exec(_connection,
            "INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, SortOrder, CreatedAt, UpdatedAt) VALUES (NULL, 'Folder', 'X', 'X', 'first', $n, $n);",
            ("$n", Db.Now)));
    }
}
