using Microsoft.Data.Sqlite;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>V003 (Workspace) on a fresh database and as an upgrade of an existing v2 (Phase 3/3.5) database.</summary>
public sealed class WorkspaceMigrationTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static List<string> Names(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static List<string> Columns(SqliteConnection connection, string table) =>
        Names(connection, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;");

    [Fact]
    public void FreshDatabase_HasTheReviewedWorkspaceSchema()
    {
        var result = _root.CreateInitializer().Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Database.Outcome);
        Assert.Equal(3, result.Database.SchemaVersion);
        using var connection = _root.Open();
        Assert.Equal(3, Db.ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Equal(["Id", "ParentId", "NodeType", "Name", "NameKey", "SortOrder", "CreatedAt", "UpdatedAt"], Columns(connection, "WorkspaceNodes"));
        Assert.Equal(["NodeId", "ProjectId", "Status", "Description", "Owner", "ProjectManager"], Columns(connection, "Projects"));
        Assert.Equal(
            ["IX_WorkspaceNodes_ParentId", "UX_WorkspaceNodes_ChildName", "UX_WorkspaceNodes_RootName"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'index' AND tbl_name = 'WorkspaceNodes' AND sql IS NOT NULL ORDER BY name;"));
        Assert.Equal(
            ["TR_Projects_Immutable", "TR_Projects_NodeIsProject", "TR_WorkspaceNodes_Immutable",
             "TR_WorkspaceNodes_ParentIsFolder_Insert", "TR_WorkspaceNodes_ParentIsFolder_Update"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'trigger' ORDER BY name;"));
        Assert.Contains("sqlite_sequence", Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'table';")); // AUTOINCREMENT
        Assert.Equal(0, Db.Count(connection, "WorkspaceNodes"));
        Assert.Equal(0, Db.Count(connection, "Projects"));
    }

    [Fact]
    public void PartialIndexes_AreUniqueAndScopedToRootOrChildren()
    {
        _root.CreateInitializer().Initialize();
        using var connection = _root.Open();

        var root = Db.ScalarString(connection, "SELECT sql FROM sqlite_schema WHERE name = 'UX_WorkspaceNodes_RootName';");
        var child = Db.ScalarString(connection, "SELECT sql FROM sqlite_schema WHERE name = 'UX_WorkspaceNodes_ChildName';");
        Assert.Contains("CREATE UNIQUE INDEX", root);
        Assert.Contains("WHERE ParentId IS NULL", root);
        Assert.Contains("CREATE UNIQUE INDEX", child);
        Assert.Contains("WHERE ParentId IS NOT NULL", child);
    }

    [Fact]
    public void ExistingV2Database_IsBackedUpAndUpgraded_PreservingAllData()
    {
        // A Phase 3.5 database: schema v2 with Systems, memberships (disabled + CustomTarget) and Owner data.
        _root.CreateDatabaseInitializer(new V001_InitialSchema(), new V002_OwnerManager()).Initialize();
        const string snapshotSql = """
            SELECT 'S|' || SystemCode || '|' || SystemName || '|' || ifnull(Description, '') || '|' || IsActive || '|' || CreatedAt FROM Systems
            UNION ALL
            SELECT 'M|' || SystemId || '|' || RepositoryType || '|' || IsEnabled || '|' || ifnull(CustomTarget, '<null>') FROM SystemRepositories
            UNION ALL
            SELECT 'G|' || Id || '|' || Name || '|' || NameKey || '|' || SortOrder FROM OwnerGroups
            UNION ALL
            SELECT 'I|' || Id || '|' || GroupId || '|' || Name || '|' || ActionType || '|' || Target || '|' || ifnull(Note, '') FROM OwnerItems
            ORDER BY 1;
            """;
        List<string> before;
        using (var connection = _root.Open())
        {
            var a = Db.InsertSystem(connection, "4000000037", "MST (Zaxis@net 機種グループ変更)");
            Db.InsertSystemRepository(connection, a, "SVN_SYSTEM");
            Db.Exec(connection, "INSERT INTO SystemRepositories (SystemId, RepositoryType, IsEnabled, CustomTarget) VALUES ($id, 'GIT_SYSTEM', 0, NULL);", ("$id", a));
            Db.Exec(connection, "INSERT INTO SystemRepositories (SystemId, RepositoryType, IsEnabled, CustomTarget) VALUES ($id, 'RELEASE', 1, 'https://svn.example.test/custom');", ("$id", a));
            Db.Exec(connection, "INSERT INTO OwnerGroups (Id, Name, NameKey, SortOrder, CreatedAt, UpdatedAt) VALUES (7, 'ツール', 'ツール', 0, $n, $n);", ("$n", Db.Now));
            Db.Exec(connection, "INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, Note, CreatedAt, UpdatedAt) VALUES (7, 'Wiki', 'Web', 'https://wiki.example.test', 'メモ', $n, $n);", ("$n", Db.Now));
            before = Names(connection, snapshotSql);
        }

        Assert.Equal(6, before.Count);

        var result = _root.CreateInitializer().Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Database.Outcome);
        Assert.Equal(2, result.Database.PreviousSchemaVersion);
        Assert.Equal(3, result.Database.SchemaVersion);
        using (var connection = _root.Open())
        {
            Assert.Equal(3, Db.ScalarLong(connection, "PRAGMA user_version;"));
            Assert.Equal(before, Names(connection, snapshotSql)); // every existing row unchanged
            Assert.Equal(0, Db.Count(connection, "WorkspaceNodes"));
            Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA quick_check(1);"));
        }

        // The validated pre-migration safety copy is the untouched v2 database.
        Assert.NotNull(result.Database.MigrationBackupFile);
        Assert.StartsWith(_root.Paths.MigrationBackupsDirectory, result.Database.MigrationBackupFile);
        Assert.Contains("_v2_before_v3_", Path.GetFileName(result.Database.MigrationBackupFile));
        using var backup = new SqliteConnection($"Data Source={result.Database.MigrationBackupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(2, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal(["OwnerGroups", "OwnerItems", "SystemRepositories", "Systems"], Db.UserTables(backup));
        Assert.Equal(before, Names(backup, snapshotSql));
    }

    [Fact]
    public void FailingV003_RollsBack_LeavesV2DataAndNoPartialWorkspaceSchema()
    {
        _root.CreateDatabaseInitializer(new V001_InitialSchema(), new V002_OwnerManager()).Initialize();
        using (var connection = _root.Open())
        {
            Db.InsertSystem(connection, "ABC001", "販売管理");
            // An object V003 needs to create already exists, so V003 fails after creating WorkspaceNodes.
            Db.Exec(connection, "CREATE TABLE Projects (Legacy INTEGER);");
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateInitializer().Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        using var after = _root.Open();
        Assert.Equal(2, Db.ScalarLong(after, "PRAGMA user_version;"));
        Assert.DoesNotContain("WorkspaceNodes", Db.UserTables(after));
        Assert.Equal(["Legacy"], Names(after, "SELECT name FROM pragma_table_info('Projects');"));
        Assert.Empty(Names(after, "SELECT name FROM sqlite_schema WHERE name LIKE '%Workspace%';"));
        Assert.Equal(1, Db.Count(after, "Systems"));
    }

    [Fact]
    public void V001AndV002_AreStillTheFrozenEarlierMigrations()
    {
        _root.CreateDatabaseInitializer(new V001_InitialSchema(), new V002_OwnerManager()).Initialize();
        using var connection = _root.Open();

        Assert.Equal(["OwnerGroups", "OwnerItems", "SystemRepositories", "Systems"], Db.UserTables(connection));
        Assert.Equal(2, Db.ScalarLong(connection, "PRAGMA user_version;"));
    }
}
