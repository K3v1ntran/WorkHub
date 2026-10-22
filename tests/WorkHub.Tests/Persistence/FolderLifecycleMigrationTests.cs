using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;
using static WorkHub.Tests.Persistence.ProjectContentMigrationTests;

namespace WorkHub.Tests.Persistence;

/// <summary>
/// V009 (Workspace folder lifecycle; Phase 13 proposal revision 4 §6.3, §7.6, §7.8, §10.2 B1/B9/B11): the approved SQL, a fresh
/// database, the upgrade of a populated v8 database through the real initializer with its validated pre-migration backup,
/// rollback to an intact v8 database, the v8 build's refusal of a v9 database, and the database-level guards of §7.6 asserted
/// with raw SQL (including what the database deliberately does NOT block: existing children of a marked Folder).
/// </summary>
public sealed class FolderLifecycleMigrationTests : IDisposable
{
    /// <summary>SHA-256 of the approved revision 4 §6.3 SQL as compiled (LF line endings, no trailing newline); 13B-0 probe pin.</summary>
    private const string ApprovedSqlSha256 = "ebaa014c0ad8f56e1345b10d8b785cd1526165e2f9eb995be368ccfcaae620bf";

    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static readonly string[] V009Objects =
    [
        "UX_WorkspaceNodes_LiveRootName", "UX_WorkspaceNodes_LiveChildName", "IX_WorkspaceNodes_FolderArchived", "IX_WorkspaceNodes_FolderTrashed",
        "TR_WorkspaceNodes_InsertFolderLive", "TR_WorkspaceNodes_FolderMarkersExclusive", "TR_WorkspaceNodes_FolderMarkerOnly",
        "TR_WorkspaceNodes_LifecycleSerialTransition", "TR_WorkspaceNodes_FolderMarkedReadOnly", "TR_WorkspaceNodes_ParentLive_Insert",
        "TR_WorkspaceNodes_ParentLive_Update", "TR_WorkspaceNodes_DeleteOnlyLiveFolder",
    ];

    /// <summary>
    /// A v8 database (frozen V001-V008): nested folders three levels deep, Projects in every editable status, an archived Project
    /// (serial 1), a trashed Project, an archived-plus-trashed Project, content with a revised note and a Phase 7 Trash entry.
    /// </summary>
    private Dictionary<string, long> CreatePopulatedV8Database()
    {
        Assert.Equal(8, _root.CreateDatabaseInitializer(MigrationSets.ThroughV008).Initialize().SchemaVersion);
        using var c = _root.Open();
        var system = Db.InsertSystem(c, "4000000009", "M-Find");
        Db.InsertSystemRepository(c, system, "SVN_SYSTEM");
        var ids = new Dictionary<string, long>
        {
            ["2024"] = Db.InsertNode(c, null, "Folder", "2024"),
            ["2026"] = Db.InsertNode(c, null, "Folder", "2026"),
        };
        ids["Q1"] = Db.InsertNode(c, ids["2024"], "Folder", "Q1");
        ids["Deep"] = Db.InsertNode(c, ids["Q1"], "Folder", "東京支社");
        var n = 0;
        foreach (var (name, parent, status) in new (string, string?, string)[]
                 {
                     ("Alpha", "Deep", "Active"), ("Beta", "Q1", "Completed"), ("Gamma", "2024", "OnHold"), ("Delta", null, "Planning"),
                     ("Epsilon", "2026", "Maintenance"), ("Archived", "Q1", "Active"), ("Trashed", "2024", "Active"), ("ArchivedTrashed", "Deep", "Active"),
                 })
        {
            var id = Db.InsertNode(c, parent is null ? null : ids[parent], "Project", name);
            Db.InsertProjectDetails(c, id, $"{++n:x2}".PadLeft(32, '0'));
            Db.Exec(c, "UPDATE Projects SET Status = $s, Description = $d WHERE NodeId = $id;", ("$s", status), ("$d", $"Desc {name}\nline 2"), ("$id", id));
            ids[name] = id;
        }

        void Content(string sql) => Db.Exec(c, sql, ("$p", ids["Alpha"]), ("$now", Db.Now));
        Content("INSERT INTO ProjectFolders (ProjectNodeId, ParentFolderId, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Specs', 'SPECS', $now, $now);");
        Content("INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES ($p, 1, 'File', 'Spec', 'C:\\Docs\\spec.docx', $now, $now);");
        Content("INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, 1, 'Kickoff', 'line1', $now, $now);");
        Content("UPDATE ProjectNotes SET Content = 'v2', Revision = Revision + 1, UpdatedAt = $now WHERE Id = 1;");
        Content("INSERT INTO ProjectLinks (ProjectNodeId, FolderId, LinkType, Name, Target, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Web', 'Wiki', 'https://wiki.example.test/', $now, $now);");
        Content("INSERT INTO ProjectTrashEntries (ProjectNodeId, RootKind, RootId, ItemCount, TrashedAt) VALUES ($p, 'Link', 2, 1, '2026-01-04T00:00:00.000Z');");
        Content("UPDATE ProjectLinks SET TrashEntryId = 1 WHERE Id = 2;");
        Db.Exec(c, "UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId IN ($a, $b);", ("$a", ids["Archived"]), ("$b", ids["ArchivedTrashed"]));
        Db.Exec(c, "UPDATE WorkspaceNodes SET TrashedAt = '2026-02-01T00:00:00.000Z' WHERE Id IN ($a, $b);", ("$a", ids["Trashed"]), ("$b", ids["ArchivedTrashed"]));
        return ids;
    }

    /// <summary>The v9 data snapshot without the three new (default-valued) WorkspaceNodes columns, comparable with v8.</summary>
    private static List<string> DataWithoutLifecycleColumns(SqliteConnection connection) =>
        DataSnapshot(connection)
            .Select(r => r.StartsWith("WorkspaceNodes|", StringComparison.Ordinal)
                ? r.Replace("|FolderArchivedAt=NULL|FolderTrashedAt=NULL|LifecycleSerial=0", string.Empty, StringComparison.Ordinal)
                : r)
            .ToList();

    [Fact]
    public void V009_IsTheApprovedSql_AndRegisteredAfterTheFrozenMigrations()
    {
        Assert.Equal(9, new V009_WorkspaceFolderLifecycle().Version);
        Assert.Equal("Workspace folder lifecycle", new V009_WorkspaceFolderLifecycle().Description);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], SchemaMigrations.All.Select(m => m.Version));
        Assert.IsType<V009_WorkspaceFolderLifecycle>(SchemaMigrations.All[^1]);
        Assert.Equal(9, SchemaMigrations.LatestVersion);

        // The raw string has no trailing newline; the hash is over its LF form (the 13B-0 probe's approved SQL file).
        var sql = V009_WorkspaceFolderLifecycle.Sql.ReplaceLineEndings("\n");
        Assert.Equal(ApprovedSqlSha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        Assert.StartsWith("-- V009_WorkspaceFolderLifecycle\n", sql);
        Assert.DoesNotContain("INSERT INTO", V009_WorkspaceFolderLifecycle.Sql); // no row is inserted, updated or deleted
        Assert.DoesNotContain("DELETE FROM", V009_WorkspaceFolderLifecycle.Sql);
        Assert.DoesNotContain("UPDATE WorkspaceNodes", V009_WorkspaceFolderLifecycle.Sql);
        Assert.DoesNotContain("FolderArchivedAt", V007_WorkspaceProjectTrash.Sql);
        Assert.DoesNotContain("LifecycleSerial", V008_ProjectArchiveSerial.Sql);
    }

    [Fact]
    public void FreshDatabase_HasTheReviewedV009Schema()
    {
        var result = _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Outcome);
        Assert.Equal(9, result.SchemaVersion);
        using var connection = _root.Open();
        Assert.Equal(9, Db.ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Equal(["Id", "ParentId", "NodeType", "Name", "NameKey", "SortOrder", "CreatedAt", "UpdatedAt", "TrashedAt", "FolderArchivedAt", "FolderTrashedAt", "LifecycleSerial"],
            Names(connection, "SELECT name FROM pragma_table_info('WorkspaceNodes') ORDER BY cid;"));
        Assert.Equal("0", Db.ScalarString(connection, "SELECT dflt_value FROM pragma_table_info('WorkspaceNodes') WHERE name = 'LifecycleSerial';"));
        Assert.Equal(1, Db.ScalarLong(connection, "SELECT \"notnull\" FROM pragma_table_info('WorkspaceNodes') WHERE name = 'LifecycleSerial';"));
        Assert.Equal(1, Db.ScalarLong(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'WorkspaceNodes' AND sql LIKE '%STRICT%';"));
        Assert.All(V009Objects, name => Assert.Equal(1, Db.ScalarLongWith(connection, "SELECT count(*) FROM sqlite_schema WHERE name = $n;", ("$n", name))));
        Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM sqlite_schema WHERE name IN ('UX_WorkspaceNodes_ActiveRootName', 'UX_WorkspaceNodes_ActiveChildName');"));
        Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA integrity_check;"));
    }

    [Fact]
    public void PopulatedV8Database_IsBackedUpValidatedAndUpgraded_EveryV8ColumnUnchanged_NewColumnsAtDefaults()
    {
        var ids = CreatePopulatedV8Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var result = _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal((8, 9), (result.PreviousSchemaVersion, result.SchemaVersion));
        using (var connection = _root.Open())
        {
            Assert.Equal(9, Db.ScalarLong(connection, "PRAGMA user_version;"));
            Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA integrity_check;"));
            Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
            var nodes = DataSnapshot(connection).Where(r => r.StartsWith("WorkspaceNodes|", StringComparison.Ordinal)).ToList();
            Assert.Equal(ids.Count, nodes.Count);
            Assert.All(nodes, r => Assert.EndsWith("|FolderArchivedAt=NULL|FolderTrashedAt=NULL|LifecycleSerial=0", r));
            Assert.Equal(beforeData, DataWithoutLifecycleColumns(connection)); // every v8 column of every row of every table
            Assert.Equal("Archived|1", Db.ScalarString(connection, "SELECT Status || '|' || ArchiveSerial FROM Projects WHERE NodeId = $id;", ("$id", ids["ArchivedTrashed"])));

            // Exactly the V009 delta: the altered table text, the two replaced name indexes and the twelve new objects.
            var afterSchema = SchemaSnapshot(connection);
            Assert.Equal(["index|UX_WorkspaceNodes_ActiveChildName", "index|UX_WorkspaceNodes_ActiveRootName", "table|WorkspaceNodes"],
                beforeSchema.Except(afterSchema).Select(Key).Order(StringComparer.Ordinal));
            Assert.Equal(V009Objects.Select(n => (n.StartsWith("TR_", StringComparison.Ordinal) ? "trigger|" : "index|") + n).Append("table|WorkspaceNodes").Order(StringComparer.Ordinal),
                afterSchema.Except(beforeSchema).Select(Key).Order(StringComparer.Ordinal));
            Assert.Empty(SchemaReference.Frozen.DifferencesFrom(9, SchemaFingerprint.Read(connection))); // golden v9 = runtime
        }

        // The pre-migration backup was published only after validation; it is the v8 database, fully valid as v8.
        var backupFile = result.MigrationBackupFile!;
        Assert.Matches(@"^workhub_v8_before_v9_\d{8}-\d{9}\.db$", Path.GetFileName(backupFile));
        Assert.Equal(BackupStatus.FullyValidated, BackupKit.Validator().Check(backupFile, BackupCheckLevel.Full, nameVersion: 8).Status);
        using var backup = new SqliteConnection($"Data Source={backupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(8, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal(beforeData, DataSnapshot(backup));
        Assert.Equal(beforeSchema, SchemaSnapshot(backup));
    }

    private static string Key(string schemaEntry)
    {
        var parts = schemaEntry.Split('|');
        return $"{parts[0]}|{parts[1]}";
    }

    [Fact]
    public void FailureAtTheFirstStatement_LeavesAnIntactV8Database_OnEveryRetry()
    {
        CreatePopulatedV8Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // The first statement (ADD COLUMN FolderArchivedAt) fails because the column already exists.
            Db.Exec(connection, "ALTER TABLE WorkspaceNodes ADD COLUMN FolderArchivedAt TEXT NULL;");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize());
        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV8(beforeData, beforeSchema);
        var first = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);

        Thread.Sleep(5); // distinct backup timestamp
        Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize());
        AssertUnchangedV8(beforeData, beforeSchema);
        var all = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 2);
        Assert.All(first, b => Assert.Contains(b, all));
    }

    [Fact]
    public void FailureAtTheLastStatement_RollsTheColumnsIndexesAndTriggersBack_ToAnIntactV8Database()
    {
        CreatePopulatedV8Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // Everything up to the last trigger is created; its name is already taken by an unrelated trigger.
            Db.Exec(connection, "CREATE TABLE Unrelated (Value TEXT);");
            Db.Exec(connection, "CREATE TRIGGER TR_WorkspaceNodes_DeleteOnlyLiveFolder BEFORE DELETE ON Unrelated BEGIN SELECT 1; END;");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV8(beforeData, beforeSchema);
        using (var after = _root.Open())
        {
            Assert.DoesNotContain("LifecycleSerial", Names(after, "SELECT name FROM pragma_table_info('WorkspaceNodes');"));
            Assert.Equal(2, Db.ScalarLong(after, "SELECT count(*) FROM sqlite_schema WHERE name IN ('UX_WorkspaceNodes_ActiveRootName', 'UX_WorkspaceNodes_ActiveChildName');"));
            Assert.Equal(0, Db.ScalarLong(after, "SELECT count(*) FROM sqlite_schema WHERE name = 'TR_WorkspaceNodes_ParentLive_Update';"));
        }

        AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);
    }

    [Fact]
    public void AV8Build_RefusesTheV9Database_AndLeavesItUnchanged()
    {
        CreatePopulatedV8Database();
        _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();
        BackupKit.ClearPools(_root.Paths.DatabaseFile); // this database only: other classes run in parallel
        var before = BackupKit.Hash(_root.Paths.DatabaseFile);
        List<string> beforeData;
        using (var connection = _root.Open())
            beforeData = DataSnapshot(connection);
        BackupKit.ClearPools(_root.Paths.DatabaseFile);
        var backups = Directory.GetFiles(_root.Paths.MigrationBackupsDirectory);

        // The v8 migration list, as a v8 build (v1.1.0 has the same V001-V008) would register it.
        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(MigrationSets.ThroughV008).Initialize());
        BackupKit.ClearPools(_root.Paths.DatabaseFile);

        Assert.Equal(DatabaseStartupFailure.NewerSchema, ex.Failure);
        Assert.Equal("The WorkHub database uses schema v9, newer than this build supports (v8). Update WorkHub.", ex.Message);
        Assert.Equal(before, BackupKit.Hash(_root.Paths.DatabaseFile));
        using (var connection = _root.Open())
            Assert.Equal(beforeData, DataSnapshot(connection));
        Assert.Equal(backups, Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
    }

    // ---- Database-level guards (§7.6 "DB" column; the 13B-0 probe battery, now on the real migration) -------------------

    /// <summary>
    /// Each case starts from a fresh v9 database: Folder F (top level) with a direct child Folder C, a grandchild Folder G under C
    /// and a Project P under C; an empty top-level Folder E; a top-level Project R; a live top-level Folder L. <c>$f</c>, <c>$c</c>,
    /// <c>$g</c>, <c>$p</c>, <c>$e</c>, <c>$r</c>, <c>$l</c> are their Ids. The starting marker is applied to F and E with the valid
    /// statement first.
    /// </summary>
    public static TheoryData<string, string, string, string> GuardCases() => new()
    {
        // label, starting state of F and E (live / archived / trashed), statement, expected (OK, a RAISE code, UNIQUE or CHECK)
        { "Archive without serial +1", "live", "UPDATE WorkspaceNodes SET FolderArchivedAt = 't' WHERE Id = $f;", "WH_LIFECYCLE_SERIAL" },
        { "Archive with serial +2", "live", "UPDATE WorkspaceNodes SET FolderArchivedAt = 't', LifecycleSerial = LifecycleSerial + 2 WHERE Id = $f;", "WH_LIFECYCLE_SERIAL" },
        { "Archive with serial +1", "live", "UPDATE WorkspaceNodes SET FolderArchivedAt = 't', LifecycleSerial = LifecycleSerial + 1 WHERE Id = $f;", "OK" },
        { "Trash with serial +1", "live", "UPDATE WorkspaceNodes SET FolderTrashedAt = 't', LifecycleSerial = LifecycleSerial + 1 WHERE Id = $f;", "OK" },
        { "Archive that also touches UpdatedAt", "live", "UPDATE WorkspaceNodes SET FolderArchivedAt = 't', LifecycleSerial = LifecycleSerial + 1, UpdatedAt = 'u' WHERE Id = $f;", "WH_FOLDER_MARKER_ONLY" },
        { "Trash that also moves the folder", "live", "UPDATE WorkspaceNodes SET FolderTrashedAt = 't', LifecycleSerial = LifecycleSerial + 1, ParentId = $e WHERE Id = $f;", "WH_FOLDER_MARKER_ONLY" },
        { "Both markers at once", "live", "UPDATE WorkspaceNodes SET FolderArchivedAt = 'a', FolderTrashedAt = 't', LifecycleSerial = LifecycleSerial + 1 WHERE Id = $f;", "WH_FOLDER_MARKERS_EXCLUSIVE" },
        { "Serial change while live", "live", "UPDATE WorkspaceNodes SET LifecycleSerial = 5 WHERE Id = $f;", "WH_LIFECYCLE_SERIAL" },
        { "Serial on a Project row", "live", "UPDATE WorkspaceNodes SET LifecycleSerial = 1 WHERE Id = $r;", "WH_LIFECYCLE_SERIAL" },
        { "Folder marker on a Project row", "live", "UPDATE WorkspaceNodes SET FolderArchivedAt = 't', LifecycleSerial = LifecycleSerial + 1 WHERE Id = $r;", "CHECK" },
        { "Insert an archived folder", "live", "INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt, FolderArchivedAt) VALUES (NULL, 'Folder', 'X', 'X', 'n', 'n', 't');", "WH_NODE_INSERT_LIFECYCLE" },
        { "Insert with a serial", "live", "INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt, LifecycleSerial) VALUES (NULL, 'Folder', 'X', 'X', 'n', 'n', 1);", "WH_NODE_INSERT_LIFECYCLE" },
        { "Rename an archived folder", "archived", "UPDATE WorkspaceNodes SET Name = 'F2', NameKey = 'F2', UpdatedAt = 'u' WHERE Id = $f;", "WH_FOLDER_NOT_LIVE" },
        { "Move an archived folder", "archived", "UPDATE WorkspaceNodes SET ParentId = $l WHERE Id = $f;", "WH_FOLDER_NOT_LIVE" },
        { "Archived to trashed", "archived", "UPDATE WorkspaceNodes SET FolderArchivedAt = NULL, FolderTrashedAt = 't' WHERE Id = $f;", "WH_FOLDER_NOT_LIVE" },
        { "Re-archive while archived", "archived", "UPDATE WorkspaceNodes SET FolderArchivedAt = 't2', LifecycleSerial = LifecycleSerial + 1 WHERE Id = $f;", "WH_FOLDER_NOT_LIVE" },
        { "Unarchive (clear only)", "archived", "UPDATE WorkspaceNodes SET FolderArchivedAt = NULL WHERE Id = $f;", "OK" },
        { "Unarchive that changes the serial", "archived", "UPDATE WorkspaceNodes SET FolderArchivedAt = NULL, LifecycleSerial = 0 WHERE Id = $f;", "WH_LIFECYCLE_SERIAL" },
        { "Unarchive that also renames", "archived", "UPDATE WorkspaceNodes SET FolderArchivedAt = NULL, Name = 'F2', NameKey = 'F2' WHERE Id = $f;", "WH_FOLDER_MARKER_ONLY" },
        { "Restore (clear only)", "trashed", "UPDATE WorkspaceNodes SET FolderTrashedAt = NULL WHERE Id = $f;", "OK" },
        { "Trashed to archived", "trashed", "UPDATE WorkspaceNodes SET FolderTrashedAt = NULL, FolderArchivedAt = 't' WHERE Id = $f;", "WH_FOLDER_NOT_LIVE" },
        { "Insert into an archived folder", "archived", "INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($f, 'Folder', 'X', 'X', 'n', 'n');", "WH_PARENT_NOT_LIVE" },
        { "Insert into a trashed folder", "trashed", "INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($f, 'Project', 'X', 'X', 'n', 'n');", "WH_PARENT_NOT_LIVE" },
        { "Move a Project into an archived folder", "archived", "UPDATE WorkspaceNodes SET ParentId = $f WHERE Id = $r;", "WH_PARENT_NOT_LIVE" },
        { "Move a Project into a trashed folder", "trashed", "UPDATE WorkspaceNodes SET ParentId = $f WHERE Id = $r;", "WH_PARENT_NOT_LIVE" },
        { "Delete an empty archived folder", "archived", "DELETE FROM WorkspaceNodes WHERE Id = $e;", "WH_NODE_DELETE_FORBIDDEN" },
        { "Delete an empty trashed folder", "trashed", "DELETE FROM WorkspaceNodes WHERE Id = $e;", "WH_NODE_DELETE_FORBIDDEN" },
        // NOT blocked by the database (§7.6: existing children and deeper descendants are the store's job).
        { "Rename a direct child of an archived folder", "archived", "UPDATE WorkspaceNodes SET Name = 'C2', NameKey = 'C2', UpdatedAt = 'u' WHERE Id = $c;", "OK" },
        { "Move a direct child out of a trashed folder", "trashed", "UPDATE WorkspaceNodes SET ParentId = NULL WHERE Id = $c;", "OK" },
        { "Insert into an unmarked child of an archived folder", "archived", "INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt) VALUES ($c, 'Folder', 'X', 'X', 'n', 'n');", "OK" },
        { "Move a Project into a grandchild of a trashed folder", "trashed", "UPDATE WorkspaceNodes SET ParentId = $g WHERE Id = $r;", "OK" },
        // Sibling names (D10): archived keeps its name reserved; trashed frees it; Restore over a taken name is refused.
        { "Archived folder keeps its name", "archived", "INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt) VALUES (NULL, 'Folder', 'f', 'F', 'n', 'n');", "UNIQUE" },
        { "Trashed folder frees its name", "trashed", "INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt) VALUES (NULL, 'Folder', 'f', 'F', 'n', 'n');", "OK" },
        { "Restore over a taken name", "trashed", "INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt) VALUES (NULL, 'Project', 'f', 'F', 'n', 'n'); UPDATE WorkspaceNodes SET FolderTrashedAt = NULL WHERE Id = $f;", "UNIQUE" },
    };

    [Theory]
    [MemberData(nameof(GuardCases))]
    public void TheV009Guards_RefuseExactlyWhatSection76Says_AndWriteNothingWhenTheyRefuse(string label, string start, string statement, string expected)
    {
        _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();
        using var c = _root.Open();
        var f = Db.InsertNode(c, null, "Folder", "F");
        var child = Db.InsertNode(c, f, "Folder", "C");
        var g = Db.InsertNode(c, child, "Folder", "G");
        var p = Db.InsertNode(c, child, "Project", "P");
        Db.InsertProjectDetails(c, p, "000000000000000000000000000000aa");
        var e = Db.InsertNode(c, null, "Folder", "E");
        var r = Db.InsertNode(c, null, "Project", "R");
        Db.InsertProjectDetails(c, r, "000000000000000000000000000000bb");
        var l = Db.InsertNode(c, null, "Folder", "L");
        var marker = start switch { "archived" => "FolderArchivedAt", "trashed" => "FolderTrashedAt", _ => null };
        if (marker is not null)
            Db.Exec(c, $"UPDATE WorkspaceNodes SET {marker} = '2026-01-02T00:00:00.000Z', LifecycleSerial = LifecycleSerial + 1 WHERE Id IN ($f, $e);", ("$f", f), ("$e", e));
        var before = DataSnapshot(c);

        string outcome;
        using (var transaction = c.BeginTransaction())
        {
            outcome = Raise(() =>
            {
                using var command = c.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = statement;
                foreach (var (name, value) in new[] { ("$f", f), ("$c", child), ("$g", g), ("$p", p), ("$e", e), ("$r", r), ("$l", l) })
                    command.Parameters.AddWithValue(name, value);
                using var reader = command.ExecuteReader();
                while (reader.NextResult())
                {
                }
            });
            if (outcome == "OK")
                transaction.Commit();
            else
                transaction.Rollback();
        }

        Assert.True(expected == outcome, $"{label}: {outcome}");
        if (expected != "OK")
            Assert.Equal(before, DataSnapshot(c)); // a refused statement writes nothing
    }

    [Fact]
    public void TheFolderListings_UseThePartialIndexes()
    {
        _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();
        using var c = _root.Open();
        Assert.Contains("USING COVERING INDEX IX_WorkspaceNodes_FolderTrashed", Plan(c, "SELECT Id FROM WorkspaceNodes WHERE FolderTrashedAt IS NOT NULL ORDER BY FolderTrashedAt DESC;"));
        Assert.Contains("USING INDEX IX_WorkspaceNodes_FolderArchived", Plan(c, "SELECT Id FROM WorkspaceNodes WHERE FolderArchivedAt IS NOT NULL;"));
        Assert.Contains("USING INDEX UX_WorkspaceNodes_LiveChildName", Plan(c, "SELECT Id FROM WorkspaceNodes WHERE ParentId = 5 AND NameKey = 'X' AND TrashedAt IS NULL AND FolderTrashedAt IS NULL;"));
    }

    private static string Plan(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(3));
        return string.Join(" | ", details);
    }

    private static string Raise(Action action)
    {
        try
        {
            action();
            return "OK";
        }
        catch (SqliteException ex)
        {
            foreach (var code in new[]
                     {
                         "WH_LIFECYCLE_SERIAL", "WH_FOLDER_MARKER_ONLY", "WH_FOLDER_MARKERS_EXCLUSIVE", "WH_FOLDER_NOT_LIVE", "WH_NODE_INSERT_LIFECYCLE",
                         "WH_PARENT_NOT_LIVE", "WH_NODE_DELETE_FORBIDDEN",
                     })
            {
                if (ex.Message.Contains(code, StringComparison.Ordinal))
                    return code;
            }

            if (ex.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal))
                return "UNIQUE";
            if (ex.Message.Contains("CHECK constraint failed", StringComparison.Ordinal))
                return "CHECK";
            return $"SQLITE {ex.SqliteExtendedErrorCode}: {ex.Message}";
        }
    }

    private void AssertUnchangedV8(List<string> beforeData, List<string> beforeSchema)
    {
        using var after = _root.Open();
        Assert.Equal(8, Db.ScalarLong(after, "PRAGMA user_version;"));
        Assert.Equal(beforeSchema, SchemaSnapshot(after));
        Assert.Equal(beforeData, DataSnapshot(after));
        Assert.Equal("ok", Db.ScalarString(after, "PRAGMA quick_check(1);"));
    }

    private List<string> AssertValidatedBackups(List<string> beforeData, List<string> beforeSchema, int expectedCount)
    {
        var backups = Directory.GetFiles(_root.Paths.MigrationBackupsDirectory, "workhub_v8_before_v9_*.db").Order().ToList();
        Assert.Equal(expectedCount, backups.Count);
        foreach (var file in backups)
        {
            using var backup = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            backup.Open();
            Assert.Equal("ok", Db.ScalarString(backup, "PRAGMA quick_check(1);"));
            Assert.Equal(8, Db.ScalarLong(backup, "PRAGMA user_version;"));
            Assert.Equal(beforeData, DataSnapshot(backup));
            Assert.Equal(beforeSchema, SchemaSnapshot(backup));
        }

        return backups;
    }
}
