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
/// V008 (Project archive serial; Phase 12 proposal revision 3 §5, §9, §10, §11.1): the approved SQL, a fresh database, the
/// upgrade of a populated v7 database through the real initializer with its validated pre-migration backup, rollback to an
/// intact v7 database, the serial transition guard, and the v7 build's refusal of a v8 database.
/// </summary>
public sealed class ProjectArchiveMigrationTests : IDisposable
{
    /// <summary>SHA-256 of the approved revision 3 §5.1 SQL as compiled (LF line endings, no trailing newline).</summary>
    private const string ApprovedSqlSha256 = "5cc5b7f1151b7a4c8997ea3e04f5a231e5a4a1296e4130570a4c5d479cd7c85b";

    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static readonly string[] V008Objects = ["TR_Projects_ArchiveSerialTransition", "IX_Projects_Archived"];

    /// <summary>
    /// A v7 database (frozen V001-V007): nested folders, Projects in several statuses, content with a revised note and a
    /// Phase 7 Trash entry, a trashed Project, and the two archived states an earlier binary or raw SQL can leave: an
    /// archived Project and an archived-plus-trashed Project.
    /// </summary>
    private Dictionary<string, long> CreatePopulatedV7Database()
    {
        Assert.Equal(7, _root.CreateDatabaseInitializer(MigrationSets.ThroughV007).Initialize().SchemaVersion);
        using var c = _root.Open();
        var system = Db.InsertSystem(c, "4000000002", "M-Find");
        Db.InsertSystemRepository(c, system, "SVN_SYSTEM");
        var clients = Db.InsertNode(c, null, "Folder", "Clients");
        var tokyo = Db.InsertNode(c, clients, "Folder", "東京支社");
        var ids = new Dictionary<string, long>();
        var n = 0;
        foreach (var (name, parent, status) in new (string, long?, string)[]
                 {
                     ("Alpha", tokyo, "Active"), ("Beta", null, "Completed"), ("Gamma", clients, "OnHold"),
                     ("RawArchived", tokyo, "Archived"), ("Trashed", clients, "Planning"), ("ArchivedTrashed", null, "Archived"),
                 })
        {
            var id = Db.InsertNode(c, parent, "Project", name);
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
        Db.Exec(c, "INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Archived note', 'kept', $now, $now);", ("$p", ids["RawArchived"]), ("$now", Db.Now));
        Db.Exec(c, "UPDATE WorkspaceNodes SET TrashedAt = '2026-02-01T00:00:00.000Z' WHERE Id IN ($a, $b);", ("$a", ids["Trashed"]), ("$b", ids["ArchivedTrashed"]));
        return ids;
    }

    /// <summary>The v8 data snapshot without the (all-zero) serial, comparable with v7.</summary>
    private static List<string> DataWithoutSerials(SqliteConnection connection) =>
        DataSnapshot(connection)
            .Select(r => r.StartsWith("Projects|", StringComparison.Ordinal) ? r.Replace("|ArchiveSerial=0", string.Empty, StringComparison.Ordinal) : r)
            .ToList();

    [Fact]
    public void V008_IsTheApprovedSql_AndRegisteredAfterTheFrozenMigrations()
    {
        Assert.Equal(8, new V008_ProjectArchiveSerial().Version);
        Assert.Equal("Project archive serial", new V008_ProjectArchiveSerial().Description);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], SchemaMigrations.All.Select(m => m.Version));
        Assert.IsType<V008_ProjectArchiveSerial>(SchemaMigrations.All[^1]);
        Assert.Equal(8, SchemaMigrations.LatestVersion);

        // The raw string has no trailing newline; the hash is over its LF form (the approved evidence file minus its final newline).
        var sql = V008_ProjectArchiveSerial.Sql.ReplaceLineEndings("\n");
        Assert.Equal(ApprovedSqlSha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        Assert.DoesNotContain("INSERT INTO", V008_ProjectArchiveSerial.Sql);
        Assert.DoesNotContain("DELETE FROM", V008_ProjectArchiveSerial.Sql);
        Assert.DoesNotContain("UPDATE Projects", V008_ProjectArchiveSerial.Sql); // the trigger validates; nothing assigns a value
        Assert.DoesNotContain("ArchiveSerial", V003_Workspace.Sql);
    }

    [Fact]
    public void FreshDatabase_HasTheReviewedV008Schema()
    {
        var result = _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Outcome);
        Assert.Equal(8, result.SchemaVersion);
        using var connection = _root.Open();
        Assert.Equal(8, Db.ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Equal(["NodeId", "ProjectId", "Status", "Description", "Owner", "ProjectManager", "ArchiveSerial"],
            Names(connection, "SELECT name FROM pragma_table_info('Projects') ORDER BY cid;"));
        Assert.Equal("0", Db.ScalarString(connection, "SELECT dflt_value FROM pragma_table_info('Projects') WHERE name = 'ArchiveSerial';"));
        Assert.Equal(1, Db.ScalarLong(connection, "SELECT \"notnull\" FROM pragma_table_info('Projects') WHERE name = 'ArchiveSerial';"));
        Assert.Equal(1, Db.ScalarLong(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'Projects' AND sql LIKE '%STRICT%';"));
        Assert.All(V008Objects, name => Assert.Equal(1, Db.ScalarLongWith(connection, "SELECT count(*) FROM sqlite_schema WHERE name = $n;", ("$n", name))));

        var project = Db.InsertNode(connection, null, "Project", "P");
        Db.InsertProjectDetails(connection, project);
        Assert.Equal(0, Db.ScalarLongWith(connection, "SELECT ArchiveSerial FROM Projects WHERE NodeId = $id;", ("$id", project)));
    }

    [Fact]
    public void PopulatedV7Database_IsBackedUpValidatedAndUpgraded_EveryRowUnchangedExceptSerialZero()
    {
        var ids = CreatePopulatedV7Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var result = _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal((7, 8), (result.PreviousSchemaVersion, result.SchemaVersion));
        using (var connection = _root.Open())
        {
            Assert.Equal(8, Db.ScalarLong(connection, "PRAGMA user_version;"));
            Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA integrity_check;"));
            Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
            var after = DataSnapshot(connection);
            var projects = after.Where(r => r.StartsWith("Projects|", StringComparison.Ordinal)).ToList();
            Assert.Equal(6, projects.Count);
            Assert.All(projects, r => Assert.EndsWith("|ArchiveSerial=0", r));
            Assert.Equal(beforeData, DataWithoutSerials(connection));
            Assert.Equal("Archived", Db.ScalarString(connection, "SELECT Status FROM Projects WHERE NodeId = $id;", ("$id", ids["ArchivedTrashed"])));

            // Exactly the V008 delta: the altered Projects table text and the two new objects; everything else byte-identical.
            var afterSchema = SchemaSnapshot(connection);
            Assert.Equal(["table|Projects"], beforeSchema.Except(afterSchema).Select(Key));
            Assert.Equal(["index|IX_Projects_Archived", "table|Projects", "trigger|TR_Projects_ArchiveSerialTransition"], afterSchema.Except(beforeSchema).Select(Key).Order(StringComparer.Ordinal));
            Assert.Empty(SchemaReference.Frozen.DifferencesFrom(8, SchemaFingerprint.Read(connection)));
        }

        // The pre-migration backup was published only after validation; it is the v7 database, fully valid as v7.
        var backupFile = result.MigrationBackupFile!;
        Assert.Matches(@"^workhub_v7_before_v8_\d{8}-\d{9}\.db$", Path.GetFileName(backupFile));
        Assert.Equal(BackupStatus.FullyValidated, BackupKit.Validator().Check(backupFile, BackupCheckLevel.Full, nameVersion: 7).Status);
        using var backup = new SqliteConnection($"Data Source={backupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(7, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal(beforeData, DataSnapshot(backup));
        Assert.Equal(beforeSchema, SchemaSnapshot(backup));
    }

    private static string Key(string schemaEntry)
    {
        var parts = schemaEntry.Split('|');
        return $"{parts[0]}|{parts[1]}";
    }

    [Fact]
    public void FailureAtTheFirstStatement_LeavesAnIntactV7Database_OnEveryRetry()
    {
        CreatePopulatedV7Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // The first statement (ADD COLUMN ArchiveSerial) fails because the column already exists.
            Db.Exec(connection, "ALTER TABLE Projects ADD COLUMN ArchiveSerial INTEGER NULL;");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize());
        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV7(beforeData, beforeSchema);
        var first = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);

        Thread.Sleep(5); // distinct backup timestamp
        Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize());
        AssertUnchangedV7(beforeData, beforeSchema);
        var all = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 2);
        Assert.All(first, b => Assert.Contains(b, all));
    }

    [Fact]
    public void FailureAtTheLastStatement_RollsTheColumnAndTriggerBack_ToAnIntactV7Database()
    {
        CreatePopulatedV7Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // The column and the trigger are created; the index name is already taken by an unrelated object.
            Db.Exec(connection, "CREATE TABLE Unrelated (Value TEXT);");
            Db.Exec(connection, "CREATE INDEX IX_Projects_Archived ON Unrelated (Value);");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV7(beforeData, beforeSchema);
        using (var after = _root.Open())
        {
            Assert.DoesNotContain("ArchiveSerial", Names(after, "SELECT name FROM pragma_table_info('Projects');"));
            Assert.Equal(0, Db.ScalarLong(after, "SELECT count(*) FROM sqlite_schema WHERE name = 'TR_Projects_ArchiveSerialTransition';"));
        }

        AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);
    }

    [Fact]
    public void AV7Build_RefusesTheV8Database_AndLeavesItUnchanged()
    {
        CreatePopulatedV7Database();
        _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();
        BackupKit.ClearPools(_root.Paths.DatabaseFile); // this database only: other classes run in parallel
        var before = BackupKit.Hash(_root.Paths.DatabaseFile);
        var backups = Directory.GetFiles(_root.Paths.MigrationBackupsDirectory);

        // The v7 migration list, as a v7 build (v1.0.0 has the same V001-V007) would register it.
        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(MigrationSets.ThroughV007).Initialize());
        BackupKit.ClearPools(_root.Paths.DatabaseFile); // this database only: other classes run in parallel

        Assert.Equal(DatabaseStartupFailure.NewerSchema, ex.Failure);
        Assert.Equal("The WorkHub database uses schema v8, newer than this build supports (v7). Update WorkHub.", ex.Message);
        Assert.Equal(before, BackupKit.Hash(_root.Paths.DatabaseFile));
        Assert.Equal(backups, Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
    }

    // ---- The serial transition guard (§5.4, §9 P2/P3) -------------------------------------------------------------------

    public static TheoryData<string, string, string> GuardCases() => new()
    {
        // label, starting status, statement → expected (OK or the RAISE message)
        { "Archive without increment", "Active", "UPDATE Projects SET Status = 'Archived' WHERE NodeId = $id;|WH_ARCHIVE_SERIAL" },
        { "Archive with a skipped increment", "Active", "UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 2 WHERE NodeId = $id;|WH_ARCHIVE_SERIAL" },
        { "Serial change while active", "Active", "UPDATE Projects SET ArchiveSerial = 1 WHERE NodeId = $id;|WH_ARCHIVE_SERIAL" },
        { "Serial below zero", "Active", "UPDATE Projects SET ArchiveSerial = -1 WHERE NodeId = $id;|WH_ARCHIVE_SERIAL" },
        { "Edit Project statement shape", "Active", "UPDATE Projects SET Status = 'Completed', Description = 'd', Owner = NULL, ProjectManager = NULL WHERE NodeId = $id;|OK" },
        { "Archive with increment", "Active", "UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;|OK" },
        { "Serial change while archived", "Archived", "UPDATE Projects SET ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;|WH_ARCHIVE_SERIAL" },
        { "Re-archive with increment while archived", "Archived", "UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;|WH_ARCHIVE_SERIAL" },
        { "Unarchive that changes the serial", "Archived", "UPDATE Projects SET Status = 'Active', ArchiveSerial = 0 WHERE NodeId = $id;|WH_ARCHIVE_SERIAL" },
        { "Unarchive preserving the serial", "Archived", "UPDATE Projects SET Status = 'Completed' WHERE NodeId = $id;|OK" },
    };

    [Theory]
    [MemberData(nameof(GuardCases))]
    public void TheTransitionGuard_ValidatesTheSerial_AndNeverAssignsIt(string label, string startStatus, string statementAndExpected)
    {
        var (statement, expected) = (statementAndExpected.Split('|')[0], statementAndExpected.Split('|')[1]);
        _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();
        using var c = _root.Open();
        var id = Db.InsertNode(c, null, "Project", "P");
        Db.InsertProjectDetails(c, id);
        if (startStatus == "Archived")
            Db.Exec(c, "UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;", ("$id", id));
        var serialBefore = Db.ScalarLongWith(c, "SELECT ArchiveSerial FROM Projects WHERE NodeId = $id;", ("$id", id));
        var before = DataSnapshot(c);

        var outcome = Raise(() => Db.Exec(c, statement, ("$id", id)));

        Assert.True(expected == outcome, $"{label}: {outcome}");
        if (expected == "OK")
        {
            var serialAfter = Db.ScalarLongWith(c, "SELECT ArchiveSerial FROM Projects WHERE NodeId = $id;", ("$id", id));
            Assert.Equal(statement.Contains("ArchiveSerial + 1", StringComparison.Ordinal) ? serialBefore + 1 : serialBefore, serialAfter);
        }
        else
        {
            Assert.Equal(before, DataSnapshot(c)); // a refused statement writes nothing
        }
    }

    [Fact]
    public void TheTrashedGuard_StillRefusesAnyProjectsUpdate_IncludingAValidArchive()
    {
        var ids = CreatePopulatedV7Database();
        _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();
        using var c = _root.Open();

        Assert.Equal("WH_PROJECT_TRASHED", Raise(() => Db.Exec(c, "UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;", ("$id", ids["Trashed"]))));
        Assert.Equal("WH_PROJECT_TRASHED", Raise(() => Db.Exec(c, "UPDATE Projects SET Status = 'Active' WHERE NodeId = $id;", ("$id", ids["ArchivedTrashed"]))));
        Assert.Equal("WH_NODE_DELETE_FORBIDDEN", Raise(() => Db.Exec(c, "DELETE FROM Projects WHERE NodeId = $id;", ("$id", ids["Trashed"]))));
        // The Workspace Trash Restore (marker only) of the archived-plus-trashed Project leaves it archived with serial 0.
        Assert.Equal("OK", Raise(() => Db.Exec(c, "UPDATE WorkspaceNodes SET TrashedAt = NULL WHERE Id = $id;", ("$id", ids["ArchivedTrashed"]))));
        Assert.Equal("Archived|0", Db.ScalarString(c, "SELECT Status || '|' || ArchiveSerial FROM Projects WHERE NodeId = $id;", ("$id", ids["ArchivedTrashed"])));
    }

    [Fact]
    public void TheArchivedCount_UsesThePartialIndex()
    {
        _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();
        using var c = _root.Open();
        using var command = c.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT count(*) FROM Projects WHERE Status = 'Archived';";
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
            details.Add(reader.GetString(3));
        Assert.Contains("USING COVERING INDEX IX_Projects_Archived", Assert.Single(details));
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
            foreach (var code in new[] { "WH_ARCHIVE_SERIAL", "WH_PROJECT_TRASHED", "WH_NODE_DELETE_FORBIDDEN" })
            {
                if (ex.Message.Contains(code, StringComparison.Ordinal))
                    return code;
            }

            return $"SQLITE {ex.SqliteExtendedErrorCode}: {ex.Message}";
        }
    }

    private void AssertUnchangedV7(List<string> beforeData, List<string> beforeSchema)
    {
        using var after = _root.Open();
        Assert.Equal(7, Db.ScalarLong(after, "PRAGMA user_version;"));
        Assert.Equal(beforeSchema, SchemaSnapshot(after));
        Assert.Equal(beforeData, DataSnapshot(after));
        Assert.Equal("ok", Db.ScalarString(after, "PRAGMA quick_check(1);"));
    }

    private List<string> AssertValidatedBackups(List<string> beforeData, List<string> beforeSchema, int expectedCount)
    {
        var backups = Directory.GetFiles(_root.Paths.MigrationBackupsDirectory, "workhub_v7_before_v8_*.db").Order().ToList();
        Assert.Equal(expectedCount, backups.Count);
        foreach (var file in backups)
        {
            using var backup = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            backup.Open();
            Assert.Equal("ok", Db.ScalarString(backup, "PRAGMA quick_check(1);"));
            Assert.Equal(7, Db.ScalarLong(backup, "PRAGMA user_version;"));
            Assert.Equal(beforeData, DataSnapshot(backup));
            Assert.Equal(beforeSchema, SchemaSnapshot(backup));
        }

        return backups;
    }
}
