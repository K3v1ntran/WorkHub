using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;
using static WorkHub.Tests.Persistence.ProjectContentMigrationTests;

namespace WorkHub.Tests.Persistence;

/// <summary>
/// V007 (Workspace Project Trash) on a fresh database, as an upgrade of empty and populated v6 databases (with nested
/// Phase 7 Trash entries), and its rollback to an intact v6 database (Phase 8 proposal revision 2 §6, §7, §10.1).
/// </summary>
public sealed class WorkspaceTrashMigrationTests : IDisposable
{
    /// <summary>SHA-256 of the approved proposal §6.1 SQL block (LF line endings, final newline).</summary>
    private const string ApprovedSqlSha256 = "e8c087999857df29e5d9390d25af3444a478d7e37b4e0c6b323a0d98a2cf16ce";

    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static readonly string[] V007Objects =
    [
        "UX_WorkspaceNodes_ActiveRootName", "UX_WorkspaceNodes_ActiveChildName", "IX_WorkspaceNodes_Trashed",
        "TR_WorkspaceNodes_InsertActive", "TR_WorkspaceNodes_MarkerTransition", "TR_WorkspaceNodes_TrashedReadOnly",
        "TR_WorkspaceNodes_DeleteOnlyEmptyFolder", "TR_Projects_NoDelete", "TR_Projects_TrashedReadOnly",
        "TR_ProjectFolders_ProjectActive_Insert", "TR_ProjectFolders_ProjectActive_Update", "TR_ProjectFolders_ProjectActive_Delete",
        "TR_ProjectLinks_ProjectActive_Insert", "TR_ProjectLinks_ProjectActive_Update", "TR_ProjectLinks_ProjectActive_Delete",
        "TR_ProjectNotes_ProjectActive_Insert", "TR_ProjectNotes_ProjectActive_Update", "TR_ProjectNotes_ProjectActive_Delete",
        "TR_ProjectTrashEntries_ProjectActive_Insert", "TR_ProjectTrashEntries_ProjectActive_Update", "TR_ProjectTrashEntries_ProjectActive_Delete",
    ];

    /// <summary>
    /// A v6 database with Repository, Owner and nested Workspace data, content in two Projects, a revised note and Phase 7
    /// Trash entries: an independent entry nested inside a later one, and an independently trashed root link.
    /// </summary>
    private void CreatePopulatedV6Database()
    {
        _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize();
        using var c = _root.Open();
        var system = Db.InsertSystem(c, "4000000002", "M-Find");
        Db.InsertSystemRepository(c, system, "SVN_SYSTEM");
        Db.Exec(c, "INSERT INTO OwnerGroups (Name, NameKey, SortOrder, CreatedAt, UpdatedAt) VALUES ('Tools', 'TOOLS', 1, $now, $now);", ("$now", Db.Now));
        Db.Exec(c, "INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, SortOrder, IsFavorite, CreatedAt, UpdatedAt) VALUES (1, 'Portal', 'Web', 'https://portal.example.test/', 0, 1, $now, $now);", ("$now", Db.Now));
        var clients = Db.InsertNode(c, null, "Folder", "Clients");
        var tokyo = Db.InsertNode(c, clients, "Folder", "東京支社");
        var alpha = Db.InsertNode(c, tokyo, "Project", "Alpha");
        Db.InsertProjectDetails(c, alpha, "0123456789abcdef0123456789abcdef");
        var beta = Db.InsertNode(c, null, "Project", "Beta");
        Db.InsertProjectDetails(c, beta, "fedcba9876543210fedcba9876543210");
        Db.InsertNode(c, null, "Folder", "Empty");

        void Content(string sql) => Db.Exec(c, sql, ("$p", alpha), ("$now", Db.Now));
        Content("INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Specs', 'SPECS', $now, $now);"); // 1
        Content("INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, 1, 'Drafts', 'DRAFTS', $now, $now);"); // 2
        Content("INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES ($p, 2, 'File', 'Spec', 'C:\\Docs\\spec.docx', $now, $now);"); // 1
        Content("INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, 2, 'Kickoff', 'line1', $now, $now);"); // 1
        Content("UPDATE ProjectNotes SET Content = 'v2', Revision = Revision + 1, UpdatedAt = $now WHERE Id = 1;");
        Content("INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Web', 'Wiki', 'https://wiki.example.test/', $now, $now);"); // 2
        Db.Exec(c, "INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Specs', 'SPECS', $now, $now);", ("$p", beta), ("$now", Db.Now));

        // Entry 1: Drafts with its link and note. Entry 2 (later): Specs, whose subtree is already in entry 1. Entry 3: Wiki.
        Content("INSERT INTO ProjectTrashEntries (ProjectNodeId, RootKind, RootId, ItemCount, TrashedAt) VALUES ($p, 'Folder', 2, 3, '2026-01-02T00:00:00.000Z');");
        Content("UPDATE ProjectFolders SET TrashEntryId = 1 WHERE Id = 2; UPDATE ProjectLinks SET TrashEntryId = 1 WHERE Id = 1; UPDATE ProjectNotes SET TrashEntryId = 1 WHERE Id = 1;");
        Content("INSERT INTO ProjectTrashEntries (ProjectNodeId, RootKind, RootId, ItemCount, TrashedAt) VALUES ($p, 'Folder', 1, 1, '2026-01-03T00:00:00.000Z');");
        Content("UPDATE ProjectFolders SET TrashEntryId = 2 WHERE Id = 1;");
        Content("INSERT INTO ProjectTrashEntries (ProjectNodeId, RootKind, RootId, ItemCount, TrashedAt) VALUES ($p, 'Link', 2, 1, '2026-01-04T00:00:00.000Z');");
        Content("UPDATE ProjectLinks SET TrashEntryId = 3 WHERE Id = 2;");
    }

    /// <summary>The data snapshot of a v7 database without the (all-NULL) node markers, comparable with v6.</summary>
    private static List<string> DataWithoutNodeMarkers(SqliteConnection connection) =>
        DataSnapshot(connection)
            .Select(r => r.StartsWith("WorkspaceNodes|", StringComparison.Ordinal) ? r.Replace("|TrashedAt=NULL", string.Empty, StringComparison.Ordinal) : r)
            .ToList();

    [Fact]
    public void V007_IsTheApprovedSql_AndRegisteredAfterTheFrozenMigrations()
    {
        Assert.Equal(7, new V007_WorkspaceProjectTrash().Version);
        Assert.Equal("Workspace project trash", new V007_WorkspaceProjectTrash().Description);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], SchemaMigrations.All.Select(m => m.Version));
        Assert.IsType<V007_WorkspaceProjectTrash>(SchemaMigrations.All[6]);
        var sql = V007_WorkspaceProjectTrash.Sql.ReplaceLineEndings("\n") + "\n";
        Assert.Equal(ApprovedSqlSha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        Assert.DoesNotContain("DELETE FROM", V007_WorkspaceProjectTrash.Sql); // no row is removed by the migration
        Assert.DoesNotContain("INSERT INTO", V007_WorkspaceProjectTrash.Sql);
        Assert.DoesNotContain("TrashedAt", V003_Workspace.Sql);
    }

    [Fact]
    public void FreshDatabase_HasTheReviewedV007Schema()
    {
        var result = _root.CreateDatabaseInitializer(MigrationSets.ThroughV007).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Outcome);
        Assert.Equal(7, result.SchemaVersion);
        using var connection = _root.Open();
        Assert.Equal(7, Db.ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Equal(["Id", "ParentId", "NodeType", "Name", "NameKey", "SortOrder", "CreatedAt", "UpdatedAt", "TrashedAt"],
            Names(connection, "SELECT name FROM pragma_table_info('WorkspaceNodes') ORDER BY cid;"));
        Assert.Equal(1, Db.ScalarLong(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'WorkspaceNodes' AND sql LIKE '%STRICT';"));
        Assert.All(V007Objects, name => Assert.Equal(1, Db.ScalarLongWith(connection, "SELECT count(*) FROM sqlite_schema WHERE name = $n;", ("$n", name))));
        Assert.Equal(
            ["IX_WorkspaceNodes_ParentId", "IX_WorkspaceNodes_Trashed", "UX_WorkspaceNodes_ActiveChildName", "UX_WorkspaceNodes_ActiveRootName"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'index' AND tbl_name = 'WorkspaceNodes' AND sql IS NOT NULL ORDER BY name;"));
        Assert.Equal(
            ["TR_Projects_Immutable", "TR_Projects_NoDelete", "TR_Projects_NodeIsProject", "TR_Projects_TrashedReadOnly",
             "TR_WorkspaceNodes_DeleteOnlyEmptyFolder", "TR_WorkspaceNodes_Immutable", "TR_WorkspaceNodes_InsertActive",
             "TR_WorkspaceNodes_MarkerTransition", "TR_WorkspaceNodes_ParentIsFolder_Insert", "TR_WorkspaceNodes_ParentIsFolder_Update",
             "TR_WorkspaceNodes_TrashedReadOnly"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'trigger' AND tbl_name IN ('WorkspaceNodes', 'Projects') ORDER BY name;"));
        Assert.Equal(0, Db.Count(connection, "WorkspaceNodes"));
    }

    [Fact]
    public void EmptyV6Database_IsBackedUpAndUpgraded()
    {
        _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize();
        List<string> beforeSchema;
        using (var connection = _root.Open())
            beforeSchema = SchemaSnapshot(connection);

        var result = _root.CreateDatabaseInitializer(MigrationSets.ThroughV007).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal(6, result.PreviousSchemaVersion);
        Assert.Equal(7, result.SchemaVersion);
        Assert.Contains("_v6_before_v7_", Path.GetFileName(result.MigrationBackupFile));
        using var backup = new SqliteConnection($"Data Source={result.MigrationBackupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(6, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal(beforeSchema, SchemaSnapshot(backup));
        using var after = _root.Open();
        Assert.Equal("ok", Db.ScalarString(after, "PRAGMA quick_check(1);"));
    }

    [Fact]
    public void PopulatedV6Database_IsBackedUpAndUpgraded_EveryRowAndPhase7EntryUnchanged()
    {
        CreatePopulatedV6Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        Assert.Equal(3, beforeData.Count(r => r.StartsWith("ProjectTrashEntries|", StringComparison.Ordinal)));
        Assert.Contains(beforeData, r => r.StartsWith("ProjectNotes|", StringComparison.Ordinal) && r.Contains("Revision=2", StringComparison.Ordinal) && r.Contains("TrashEntryId=1", StringComparison.Ordinal));

        var result = _root.CreateDatabaseInitializer(MigrationSets.ThroughV007).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal(6, result.PreviousSchemaVersion);
        Assert.Equal(7, result.SchemaVersion);
        using (var connection = _root.Open())
        {
            Assert.Equal(7, Db.ScalarLong(connection, "PRAGMA user_version;"));
            // Same rows, Ids, names, content, Revision, timestamps, markers, entries and ItemCounts; every node active.
            Assert.Equal(beforeData, DataWithoutNodeMarkers(connection));
            Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM WorkspaceNodes WHERE TrashedAt IS NOT NULL;"));
            Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA quick_check(1);"));
            Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA integrity_check;"));
            Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
            // Every v6 object is unchanged except the two replaced V003 name indexes and the altered node table.
            var afterSchema = SchemaSnapshot(connection);
            var replaced = new[] { "index|UX_WorkspaceNodes_RootName|", "index|UX_WorkspaceNodes_ChildName|", "table|WorkspaceNodes|" };
            Assert.All(beforeSchema.Where(e => !replaced.Any(r => e.StartsWith(r, StringComparison.Ordinal))), e => Assert.Contains(e, afterSchema));
            Assert.DoesNotContain(afterSchema, e => e.StartsWith("index|UX_WorkspaceNodes_RootName|", StringComparison.Ordinal));
        }

        var backupFile = result.MigrationBackupFile!;
        Assert.Contains("_v6_before_v7_", Path.GetFileName(backupFile));
        using var backup = new SqliteConnection($"Data Source={backupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(6, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal("ok", Db.ScalarString(backup, "PRAGMA quick_check(1);"));
        Assert.Equal(beforeData, DataSnapshot(backup));
        Assert.Equal(beforeSchema, SchemaSnapshot(backup));
    }

    [Fact]
    public void UpgradedDatabase_KeepsSiblingNamesUniqueAmongActiveNodes_AtRootAndChildLevel()
    {
        CreatePopulatedV6Database();
        _root.CreateDatabaseInitializer(MigrationSets.ThroughV007).Initialize();
        using var connection = _root.Open();

        Assert.Contains("UNIQUE", Assert.Throws<SqliteException>(() => Db.InsertNode(connection, null, "Folder", "beta", "BETA")).Message);
        Assert.Contains("UNIQUE", Assert.Throws<SqliteException>(() => Db.InsertNode(connection, 1, "Project", "東京支社", "東京支社")).Message);
    }

    [Fact]
    public void FailureAtTheFirstStatement_LeavesAnIntactV6Database_OnEveryRetry()
    {
        CreatePopulatedV6Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // The first statement (ADD COLUMN TrashedAt) fails because the column already exists.
            Db.Exec(connection, "ALTER TABLE WorkspaceNodes ADD COLUMN TrashedAt TEXT NULL;");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(MigrationSets.ThroughV007).Initialize());
        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV6(beforeData, beforeSchema);
        var first = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);

        Thread.Sleep(5); // distinct backup timestamp
        Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(MigrationSets.ThroughV007).Initialize());
        AssertUnchangedV6(beforeData, beforeSchema);
        var all = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 2);
        Assert.All(first, b => Assert.Contains(b, all));
    }

    [Fact]
    public void FailureAfterTheAlterAndIndexDrops_RollsEverythingBack_ToAnIntactV6Database()
    {
        CreatePopulatedV6Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // V007 adds the column, drops both V003 name indexes, then fails on this index name.
            Db.Exec(connection, "CREATE TABLE Unrelated (Value TEXT);");
            Db.Exec(connection, "CREATE INDEX UX_WorkspaceNodes_ActiveRootName ON Unrelated (Value);");
            Db.Exec(connection, "INSERT INTO Unrelated (Value) VALUES ('kept');");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(MigrationSets.ThroughV007).Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV6(beforeData, beforeSchema);
        using (var after = _root.Open())
        {
            Assert.DoesNotContain("TrashedAt", Names(after, "SELECT name FROM pragma_table_info('WorkspaceNodes');"));
            Assert.Equal(2, Db.ScalarLong(after, "SELECT count(*) FROM sqlite_schema WHERE name IN ('UX_WorkspaceNodes_RootName', 'UX_WorkspaceNodes_ChildName');"));
        }

        AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);
    }

    [Fact]
    public void FailureAtTheLastStatement_RollsEverythingBack_ToAnIntactV6Database()
    {
        CreatePopulatedV6Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // Every V007 statement but the last succeeds; the last trigger name is already taken.
            Db.Exec(connection, "CREATE TABLE Unrelated (Value TEXT);");
            Db.Exec(connection, "CREATE TRIGGER TR_ProjectTrashEntries_ProjectActive_Delete BEFORE DELETE ON Unrelated BEGIN SELECT 1; END;");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(MigrationSets.ThroughV007).Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV6(beforeData, beforeSchema);
        using (var after = _root.Open())
            Assert.Equal(0, Db.ScalarLong(after, "SELECT count(*) FROM sqlite_schema WHERE name IN ('TR_WorkspaceNodes_DeleteOnlyEmptyFolder', 'IX_WorkspaceNodes_Trashed', 'TR_ProjectNotes_ProjectActive_Delete');"));
        AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);
    }

    private void AssertUnchangedV6(List<string> beforeData, List<string> beforeSchema)
    {
        using var after = _root.Open();
        Assert.Equal(6, Db.ScalarLong(after, "PRAGMA user_version;"));
        Assert.Equal(beforeSchema, SchemaSnapshot(after));
        Assert.Equal(beforeData, DataSnapshot(after));
        Assert.Equal("ok", Db.ScalarString(after, "PRAGMA quick_check(1);"));
    }

    private List<string> AssertValidatedBackups(List<string> beforeData, List<string> beforeSchema, int expectedCount)
    {
        var backups = Directory.GetFiles(_root.Paths.MigrationBackupsDirectory, "workhub_v6_before_v7_*.db").Order().ToList();
        Assert.Equal(expectedCount, backups.Count);
        foreach (var file in backups)
        {
            using var backup = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            backup.Open();
            Assert.Equal("ok", Db.ScalarString(backup, "PRAGMA quick_check(1);"));
            Assert.Equal(6, Db.ScalarLong(backup, "PRAGMA user_version;"));
            Assert.Equal(beforeData, DataSnapshot(backup));
            Assert.Equal(beforeSchema, SchemaSnapshot(backup));
        }

        return backups;
    }
}
