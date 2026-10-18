using Microsoft.Data.Sqlite;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;
using static WorkHub.Tests.Persistence.ProjectContentMigrationTests;

namespace WorkHub.Tests.Persistence;

/// <summary>
/// V006 (Project content Trash) — pinned to V001–V006 — on a fresh database, as an upgrade of empty and populated v5 databases, and its rollback
/// to an intact v5 database (Phase 7 proposal revision 2 §5, §10.1).
/// </summary>
public sealed class TrashMigrationTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static readonly ISchemaMigration[] ThroughV005 =
        [new V001_InitialSchema(), new V002_OwnerManager(), new V003_Workspace(), new V004_ProjectContent(), new V005_ProjectNotes()];

    private static readonly string[] V006Objects =
    [
        "ProjectTrashEntries", "IX_ProjectTrashEntries_Project", "TR_ProjectTrashEntries_ValidRoot", "TR_ProjectTrashEntries_Immutable",
        "IX_ProjectFolders_TrashEntry", "IX_ProjectLinks_TrashEntry", "IX_ProjectNotes_TrashEntry",
        "UX_ProjectFolders_ActiveRootName", "UX_ProjectFolders_ActiveChildName",
        "TR_ProjectFolders_InsertActive", "TR_ProjectLinks_InsertActive", "TR_ProjectNotes_InsertActive",
        "TR_ProjectFolders_MarkerTransition", "TR_ProjectLinks_MarkerTransition", "TR_ProjectNotes_MarkerTransition",
        "TR_ProjectFolders_TrashedReadOnly", "TR_ProjectLinks_TrashedReadOnly", "TR_ProjectNotes_TrashedReadOnly",
        "TR_ProjectFolders_NoActiveChildOfTrashedInsert", "TR_ProjectFolders_NoActiveChildOfTrashedMove",
        "TR_ProjectLinks_NoActiveInTrashedInsert", "TR_ProjectLinks_NoActiveInTrashedMove",
        "TR_ProjectNotes_NoActiveInTrashedInsert", "TR_ProjectNotes_NoActiveInTrashedMove",
    ];

    /// <summary>A v5 database with Repository, Owner and Workspace data and nested content in two Projects.</summary>
    private void CreatePopulatedV5Database()
    {
        _root.CreateDatabaseInitializer(ThroughV005).Initialize();
        using var connection = _root.Open();
        var system = Db.InsertSystem(connection, "4000000002", "M-Find");
        Db.InsertSystemRepository(connection, system, "SVN_SYSTEM");
        Db.Exec(connection, "INSERT INTO SystemRepositories (SystemId, RepositoryType, IsEnabled, CustomTarget) VALUES ($id, 'GIT_SYSTEM', 0, NULL);", ("$id", system));
        Db.Exec(connection, "INSERT INTO OwnerGroups (Name, NameKey, SortOrder, CreatedAt, UpdatedAt) VALUES ('Tools', 'TOOLS', 1, $now, $now);", ("$now", Db.Now));
        Db.Exec(connection, "INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, SortOrder, IsFavorite, CreatedAt, UpdatedAt) VALUES (1, 'Portal', 'Web', 'https://portal.example.test/', 0, 1, $now, $now);", ("$now", Db.Now));
        var clients = Db.InsertNode(connection, null, "Folder", "Clients");
        var alpha = Db.InsertNode(connection, clients, "Project", "Alpha");
        Db.InsertProjectDetails(connection, alpha, "0123456789abcdef0123456789abcdef");
        var beta = Db.InsertNode(connection, null, "Project", "Beta");
        Db.InsertProjectDetails(connection, beta, "fedcba9876543210fedcba9876543210");
        Db.Exec(connection, "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Specs', 'SPECS', $now, $now);", ("$p", alpha), ("$now", Db.Now));
        Db.Exec(connection, "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, 1, 'Drafts', 'DRAFTS', $now, $now);", ("$p", alpha), ("$now", Db.Now));
        Db.Exec(connection, "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Specs', 'SPECS', $now, $now);", ("$p", beta), ("$now", Db.Now));
        Db.Exec(connection, "INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES ($p, 2, 'File', 'Spec', 'C:\\Docs\\spec.docx', $now, $now);", ("$p", alpha), ("$now", Db.Now));
        Db.Exec(connection, "INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, 2, 'Kickoff', 'line1\nline2', $now, $now);", ("$p", alpha), ("$now", Db.Now));
        Db.Exec(connection, "UPDATE ProjectNotes SET Content = 'v2', Revision = Revision + 1, UpdatedAt = $now WHERE Id = 1;", ("$now", Db.Now));
    }

    /// <summary>The data snapshot of a v6 database without the (all-NULL) TrashEntryId columns, comparable with v5.</summary>
    private static List<string> DataWithoutMarkers(SqliteConnection connection) =>
        DataSnapshot(connection).Select(r => r.Replace("|TrashEntryId=NULL", string.Empty, StringComparison.Ordinal)).ToList();

    [Fact]
    public void FreshDatabase_HasTheReviewedV006Schema()
    {
        var result = _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Outcome);
        Assert.Equal(6, result.SchemaVersion);
        using var connection = _root.Open();
        Assert.Equal(6, Db.ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Equal(["Id", "ProjectNodeId", "RootKind", "RootId", "ItemCount", "TrashedAt"],
            Names(connection, "SELECT name FROM pragma_table_info('ProjectTrashEntries') ORDER BY cid;"));
        foreach (var table in new[] { "ProjectFolders", "ProjectLinks", "ProjectNotes" })
        {
            Assert.Equal("TrashEntryId", Names(connection, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;")[^1]);
            Assert.Contains($"ProjectTrashEntries:TrashEntryId:RESTRICT",
                Names(connection, $"""SELECT "table" || ':' || "from" || ':' || on_delete FROM pragma_foreign_key_list('{table}');"""));
        }

        Assert.All(V006Objects, name => Assert.Equal(1, Db.ScalarLongWith(connection, "SELECT count(*) FROM sqlite_schema WHERE name = $n;", ("$n", name))));
        Assert.Empty(Names(connection, "SELECT name FROM sqlite_schema WHERE name IN ('UX_ProjectFolders_RootName', 'UX_ProjectFolders_ChildName');"));
        Assert.Equal(1, Db.ScalarLong(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'ProjectTrashEntries' AND sql LIKE '%STRICT';"));
        Assert.All(Names(connection, """SELECT on_delete FROM pragma_foreign_key_list('ProjectTrashEntries');"""), a => Assert.Equal("RESTRICT", a));
        Assert.Equal(0, Db.Count(connection, "ProjectTrashEntries"));
    }

    [Fact]
    public void V006_IsTheApprovedSql_AndRegisteredAfterTheFrozenMigrations()
    {
        Assert.Equal(6, new V006_ProjectContentTrash().Version);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], SchemaMigrations.All.Select(m => m.Version));
        Assert.IsType<V006_ProjectContentTrash>(SchemaMigrations.All[5]);
        Assert.StartsWith("-- V006_ProjectContentTrash\n", V006_ProjectContentTrash.Sql.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("CASCADE", V006_ProjectContentTrash.Sql);
        Assert.DoesNotContain("DELETE FROM", V006_ProjectContentTrash.Sql); // nothing in the migration removes content
        Assert.DoesNotContain("UPDATE ", V006_ProjectContentTrash.Sql.Replace("BEFORE UPDATE", string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain("ProjectTrashEntries", V005_ProjectNotes.Sql);
        Assert.DoesNotContain("TrashEntryId", V004_ProjectContent.Sql);
    }

    [Fact]
    public void EmptyV5Database_IsBackedUpAndUpgraded()
    {
        _root.CreateDatabaseInitializer(ThroughV005).Initialize();
        List<string> beforeSchema;
        using (var connection = _root.Open())
            beforeSchema = SchemaSnapshot(connection);

        var result = _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal(5, result.PreviousSchemaVersion);
        Assert.Equal(6, result.SchemaVersion);
        Assert.Contains("_v5_before_v6_", Path.GetFileName(result.MigrationBackupFile));
        using var backup = new SqliteConnection($"Data Source={result.MigrationBackupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(5, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal(beforeSchema, SchemaSnapshot(backup));
        using var after = _root.Open();
        Assert.Equal("ok", Db.ScalarString(after, "PRAGMA quick_check(1);"));
    }

    [Fact]
    public void PopulatedV5Database_IsBackedUpAndUpgraded_EveryRowActiveAndUnchanged()
    {
        CreatePopulatedV5Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        Assert.Contains(beforeData, r => r.StartsWith("ProjectNotes|", StringComparison.Ordinal) && r.Contains("Revision=2", StringComparison.Ordinal));

        var result = _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal(5, result.PreviousSchemaVersion);
        Assert.Equal(6, result.SchemaVersion);
        using (var connection = _root.Open())
        {
            Assert.Equal(6, Db.ScalarLong(connection, "PRAGMA user_version;"));
            // Same rows with the same Ids, names, content, Revision and timestamps; every row active (NULL marker).
            Assert.Equal(beforeData, DataWithoutMarkers(connection));
            foreach (var table in new[] { "ProjectFolders", "ProjectLinks", "ProjectNotes" })
                Assert.Equal(0, Db.ScalarLong(connection, $"SELECT count(*) FROM {table} WHERE TrashEntryId IS NOT NULL;"));
            Assert.Equal(0, Db.Count(connection, "ProjectTrashEntries"));
            Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA quick_check(1);"));
            Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
        }

        var backupFile = result.MigrationBackupFile!;
        Assert.Contains("_v5_before_v6_", Path.GetFileName(backupFile));
        using var backup = new SqliteConnection($"Data Source={backupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(5, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal("ok", Db.ScalarString(backup, "PRAGMA quick_check(1);"));
        Assert.Equal(beforeData, DataSnapshot(backup));
        Assert.Equal(beforeSchema, SchemaSnapshot(backup));
    }

    [Fact]
    public void ActiveNameRules_AfterUpgrade_MatchTheV004Rules()
    {
        CreatePopulatedV5Database();
        _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize();
        using var connection = _root.Open();

        // Still unique among active siblings (root and child level), still independent across Projects.
        Assert.Contains("UNIQUE", Assert.Throws<SqliteException>(() => Db.Exec(connection,
            "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES (2, NULL, 'specs', 'SPECS', $now, $now);", ("$now", Db.Now))).Message);
        Assert.Contains("UNIQUE", Assert.Throws<SqliteException>(() => Db.Exec(connection,
            "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES (2, 1, 'drafts', 'DRAFTS', $now, $now);", ("$now", Db.Now))).Message);
    }

    [Fact]
    public void FailureAtTheFirstStatement_LeavesAnIntactV5Database_OnEveryRetry()
    {
        CreatePopulatedV5Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            Db.Exec(connection, "CREATE TABLE ProjectTrashEntries (Legacy TEXT);");
            Db.Exec(connection, "INSERT INTO ProjectTrashEntries (Legacy) VALUES ('kept');");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize());
        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV5(beforeData, beforeSchema);
        var first = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);

        Thread.Sleep(5); // distinct backup timestamp
        Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize());
        AssertUnchangedV5(beforeData, beforeSchema);
        var all = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 2);
        Assert.All(first, b => Assert.Contains(b, all));
        using var after = _root.Open();
        Assert.Equal("kept", Db.ScalarString(after, "SELECT Legacy FROM ProjectTrashEntries;"));
    }

    [Fact]
    public void FailureAfterTheAlterTablesAndIndexDrops_RollsEverythingBack_ToAnIntactV5Database()
    {
        CreatePopulatedV5Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // V006 creates its table, adds the three columns, then fails on this index name — after the ALTER TABLEs.
            Db.Exec(connection, "CREATE TABLE Unrelated (Value TEXT);");
            Db.Exec(connection, "CREATE INDEX IX_ProjectNotes_TrashEntry ON Unrelated (Value);");
            Db.Exec(connection, "INSERT INTO Unrelated (Value) VALUES ('kept');");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV5(beforeData, beforeSchema);
        using (var after = _root.Open())
        {
            Assert.DoesNotContain("ProjectTrashEntries", Db.UserTables(after));
            Assert.DoesNotContain("TrashEntryId", Names(after, "SELECT name FROM pragma_table_info('ProjectNotes');"));
            Assert.Equal(2, Db.ScalarLong(after, "SELECT count(*) FROM sqlite_schema WHERE name IN ('UX_ProjectFolders_RootName', 'UX_ProjectFolders_ChildName');"));
            Assert.Equal("Unrelated", Db.ScalarString(after, "SELECT tbl_name FROM sqlite_schema WHERE name = 'IX_ProjectNotes_TrashEntry';"));
        }

        AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);
    }

    [Fact]
    public void ExecuteScript_ThrowsForAnErrorAfterAStrictTableAlter_WhereExecuteNonQueryStaysSilent()
    {
        // The driver behavior V006 must not rely on (found while testing the rollback above).
        const string script = "ALTER TABLE S ADD COLUMN b INTEGER; CREATE INDEX dup ON S (b); CREATE TABLE After (x);";
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Db.Exec(connection, "CREATE TABLE S (a INTEGER) STRICT; CREATE TABLE U (v); CREATE INDEX dup ON U (v);");

        using (var transaction = connection.BeginTransaction())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = script;
            command.ExecuteNonQuery(); // no exception, although "CREATE INDEX dup" failed and "After" was never created
            Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'After';"));
            transaction.Rollback();
        }

        using (var transaction = connection.BeginTransaction())
        {
            var ex = Assert.Throws<SqliteException>(() => WorkHub.Infrastructure.Persistence.SqliteExtensions.ExecuteScript(connection, script, transaction));
            Assert.Contains("already exists", ex.Message);
        }

        using (var transaction = connection.BeginTransaction())
        {
            WorkHub.Infrastructure.Persistence.SqliteExtensions.ExecuteScript(connection, "ALTER TABLE S ADD COLUMN b INTEGER; CREATE TABLE After (x);", transaction);
            transaction.Commit();
        }

        Assert.Equal(1, Db.ScalarLong(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'After';")); // every statement ran
    }

    private void AssertUnchangedV5(List<string> beforeData, List<string> beforeSchema)
    {
        using var after = _root.Open();
        Assert.Equal(5, Db.ScalarLong(after, "PRAGMA user_version;"));
        Assert.Equal(beforeSchema, SchemaSnapshot(after)); // no V006 object of any kind; V004 indexes and table SQL intact
        Assert.Equal(beforeData, DataSnapshot(after));
        Assert.Empty(Names(after, "SELECT name FROM sqlite_schema WHERE name LIKE 'TR_%Trash%' OR name LIKE 'UX_ProjectFolders_Active%';"));
    }

    private List<string> AssertValidatedBackups(List<string> beforeData, List<string> beforeSchema, int expectedCount)
    {
        var backups = Directory.GetFiles(_root.Paths.MigrationBackupsDirectory, "workhub_v5_before_v6_*.db").Order().ToList();
        Assert.Equal(expectedCount, backups.Count);
        foreach (var file in backups)
        {
            using var backup = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            backup.Open();
            Assert.Equal("ok", Db.ScalarString(backup, "PRAGMA quick_check(1);"));
            Assert.Equal(5, Db.ScalarLong(backup, "PRAGMA user_version;"));
            Assert.Equal(beforeData, DataSnapshot(backup));
            Assert.Equal(beforeSchema, SchemaSnapshot(backup));
        }

        return backups;
    }
}
