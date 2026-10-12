using Microsoft.Data.Sqlite;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;
using static WorkHub.Tests.Persistence.ProjectContentMigrationTests;

namespace WorkHub.Tests.Persistence;

/// <summary>V005 (ProjectNotes) on a fresh database, as an upgrade of a populated v4 database (and v3), and its rollback.</summary>
public sealed class NotesMigrationTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static readonly ISchemaMigration[] ThroughV004 =
        [new V001_InitialSchema(), new V002_OwnerManager(), new V003_Workspace(), new V004_ProjectContent()];

    /// <summary>Exactly V001–V005: V006 (Trash) extends ProjectNotes and is covered by its own migration tests.</summary>
    private static readonly ISchemaMigration[] ThroughV005 = [.. ThroughV004, new V005_ProjectNotes()];

    /// <summary>A v4 database with Repository edge cases, Owner data, Workspace nodes, Project details, folders and links.</summary>
    private void CreatePopulatedV4Database()
    {
        _root.CreateDatabaseInitializer(ThroughV004).Initialize();
        using var connection = _root.Open();
        var system = Db.InsertSystem(connection, "4000000002", "M-Find");
        Db.InsertSystemRepository(connection, system, "SVN_SYSTEM");
        Db.Exec(connection, "INSERT INTO SystemRepositories (SystemId, RepositoryType, IsEnabled, CustomTarget) VALUES ($id, 'GIT_SYSTEM', 0, NULL);", ("$id", system));
        Db.Exec(connection, "INSERT INTO SystemRepositories (SystemId, RepositoryType, IsEnabled, CustomTarget) VALUES ($id, 'RELEASE', 1, 'http://example.test/custom');", ("$id", system));
        Db.Exec(connection, "INSERT INTO OwnerGroups (Name, NameKey, SortOrder, CreatedAt, UpdatedAt) VALUES ('Tools', 'TOOLS', 1, $now, $now);", ("$now", Db.Now));
        Db.Exec(connection, "INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, SortOrder, IsFavorite, CreatedAt, UpdatedAt) VALUES (1, 'Portal', 'Web', 'https://portal.example.test/', 0, 1, $now, $now);", ("$now", Db.Now));
        var folder = Db.InsertNode(connection, null, "Folder", "Clients");
        var project = Db.InsertNode(connection, folder, "Project", "Alpha");
        Db.InsertProjectDetails(connection, project);
        Db.Exec(connection, "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Specs', 'SPECS', $now, $now);", ("$p", project), ("$now", Db.Now));
        Db.Exec(connection, "INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES ($p, 1, 'File', 'Spec', 'C:\\Docs\\spec.docx', $now, $now);", ("$p", project), ("$now", Db.Now));
    }

    [Fact]
    public void FreshDatabase_HasTheReviewedV005Schema()
    {
        var result = _root.CreateDatabaseInitializer(ThroughV005).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Outcome);
        Assert.Equal(5, result.SchemaVersion);
        using var connection = _root.Open();
        Assert.Equal(5, Db.ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Equal(
            ["Id", "ProjectNodeId", "FolderId", "Title", "Content", "Revision", "CreatedAt", "UpdatedAt"],
            Names(connection, "SELECT name FROM pragma_table_info('ProjectNotes') ORDER BY cid;"));
        Assert.Equal(["IX_ProjectNotes_Container"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'index' AND tbl_name = 'ProjectNotes' ORDER BY name;"));
        Assert.Equal(["TR_ProjectNotes_Immutable", "TR_ProjectNotes_RevisionForward", "TR_ProjectNotes_RevisionStartsAtOne"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'trigger' AND tbl_name = 'ProjectNotes' ORDER BY name;"));
        Assert.Equal(1, Db.ScalarLong(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'ProjectNotes' AND sql LIKE '%STRICT';"));
        var actions = Names(connection, """SELECT "table" || ':' || "from" || ':' || on_delete FROM pragma_foreign_key_list('ProjectNotes') ORDER BY 1;""");
        Assert.Equal(3, actions.Count);
        Assert.All(actions, a => Assert.EndsWith(":RESTRICT", a));
        Assert.Equal(0, Db.Count(connection, "ProjectNotes"));
    }

    [Fact]
    public void V005_IsRegistered_AndOnlyAddsNotes()
    {
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], SchemaMigrations.All.Select(m => m.Version));
        Assert.Contains("CREATE TABLE ProjectNotes", V005_ProjectNotes.Sql);
        Assert.DoesNotContain("CASCADE", V005_ProjectNotes.Sql);
        Assert.DoesNotContain("ALTER TABLE", V005_ProjectNotes.Sql);
        Assert.DoesNotContain("DROP ", V005_ProjectNotes.Sql);
    }

    [Fact]
    public void PopulatedV4Database_IsBackedUpAndUpgraded_PreservingEveryRow()
    {
        CreatePopulatedV4Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        Assert.Contains(beforeData, r => r.StartsWith("ProjectLinks|", StringComparison.Ordinal));
        Assert.Contains(beforeData, r => r.Contains("IsEnabled=0", StringComparison.Ordinal));

        var result = _root.CreateDatabaseInitializer(ThroughV005).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal(4, result.PreviousSchemaVersion);
        Assert.Equal(5, result.SchemaVersion);
        using (var connection = _root.Open())
        {
            Assert.Equal(beforeData, DataSnapshot(connection));
            var afterSchema = SchemaSnapshot(connection);
            Assert.All(beforeSchema, entry => Assert.Contains(entry, afterSchema));
            Assert.Equal(5, afterSchema.Count - beforeSchema.Count); // table, index, three triggers
            Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA quick_check(1);"));
        }

        var backupFile = result.MigrationBackupFile!;
        Assert.Contains("_v4_before_v5_", Path.GetFileName(backupFile));
        using var backup = new SqliteConnection($"Data Source={backupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(4, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal("ok", Db.ScalarString(backup, "PRAGMA quick_check(1);"));
        Assert.Equal(beforeData, DataSnapshot(backup));
        Assert.Equal(beforeSchema, SchemaSnapshot(backup));
    }

    [Fact]
    public void V3Database_GetsEveryLaterMigrationInOneStart_WithOneBackup()
    {
        _root.CreateDatabaseInitializer(new V001_InitialSchema(), new V002_OwnerManager(), new V003_Workspace()).Initialize();

        var result = _root.CreateInitializer().Initialize();

        Assert.Equal(3, result.Database.PreviousSchemaVersion);
        Assert.Equal(SchemaMigrations.LatestVersion, result.Database.SchemaVersion);
        Assert.Single(Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
        Assert.Contains($"_v3_before_v{SchemaMigrations.LatestVersion}_", Path.GetFileName(result.Database.MigrationBackupFile));
    }

    [Fact]
    public void FailureAtTheFirstStatement_KeepsTheConflictingTable_AndLeavesNoV005Objects_OnEveryRetry()
    {
        CreatePopulatedV4Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            Db.Exec(connection, "CREATE TABLE ProjectNotes (Legacy TEXT);");
            Db.Exec(connection, "INSERT INTO ProjectNotes (Legacy) VALUES ('kept');");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(ThroughV005).Initialize());
        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV4(beforeData, beforeSchema);
        var first = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);

        Thread.Sleep(5);
        Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(ThroughV005).Initialize());
        AssertUnchangedV4(beforeData, beforeSchema);
        var all = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 2);
        Assert.All(first, b => Assert.Contains(b, all));

        using var after = _root.Open();
        Assert.Equal(["Legacy"], Names(after, "SELECT name FROM pragma_table_info('ProjectNotes');"));
        Assert.Equal("kept", Db.ScalarString(after, "SELECT Legacy FROM ProjectNotes;"));
    }

    [Fact]
    public void FailureAfterPartialDdl_RollsBackTheCreatedTable_AndKeepsThePreExistingIndex()
    {
        CreatePopulatedV4Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // V005 creates ProjectNotes, then fails on this index name.
            Db.Exec(connection, "CREATE TABLE Unrelated (Value TEXT);");
            Db.Exec(connection, "CREATE INDEX IX_ProjectNotes_Container ON Unrelated (Value);");
            Db.Exec(connection, "INSERT INTO Unrelated (Value) VALUES ('kept');");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(ThroughV005).Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV4(beforeData, beforeSchema);
        using (var after = _root.Open())
        {
            Assert.DoesNotContain("ProjectNotes", Db.UserTables(after));
            Assert.Equal("Unrelated", Db.ScalarString(after, "SELECT tbl_name FROM sqlite_schema WHERE name = 'IX_ProjectNotes_Container';"));
        }

        AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);
    }

    private void AssertUnchangedV4(List<string> beforeData, List<string> beforeSchema)
    {
        using var after = _root.Open();
        Assert.Equal(4, Db.ScalarLong(after, "PRAGMA user_version;"));
        Assert.Equal(beforeSchema, SchemaSnapshot(after));
        Assert.Equal(beforeData, DataSnapshot(after));
        Assert.Empty(Names(after, "SELECT name FROM sqlite_schema WHERE name LIKE 'TR_ProjectNotes_%';"));
    }

    private List<string> AssertValidatedBackups(List<string> beforeData, List<string> beforeSchema, int expectedCount)
    {
        var backups = Directory.GetFiles(_root.Paths.MigrationBackupsDirectory, "workhub_v4_before_v5_*.db").Order().ToList();
        Assert.Equal(expectedCount, backups.Count);
        foreach (var file in backups)
        {
            using var backup = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            backup.Open();
            Assert.Equal("ok", Db.ScalarString(backup, "PRAGMA quick_check(1);"));
            Assert.Equal(4, Db.ScalarLong(backup, "PRAGMA user_version;"));
            Assert.Equal(beforeData, DataSnapshot(backup));
            Assert.Equal(beforeSchema, SchemaSnapshot(backup));
        }

        return backups;
    }
}
