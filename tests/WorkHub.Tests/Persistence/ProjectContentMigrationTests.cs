using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>V004 (ProjectFolders, ProjectLinks) on a fresh database, as an upgrade of a populated v3 database, and its rollback.</summary>
public sealed class ProjectContentMigrationTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    internal static List<string> Names(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>Every schema object with its exact SQL.</summary>
    internal static List<string> SchemaSnapshot(SqliteConnection connection) =>
        Names(connection, "SELECT type || '|' || name || '|' || tbl_name || '|' || coalesce(sql, '') FROM sqlite_schema ORDER BY type, name;");

    /// <summary>Every row of every user table (and sqlite_sequence), as sorted text.</summary>
    internal static List<string> DataSnapshot(SqliteConnection connection)
    {
        var rows = new List<string>();
        var tables = Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_stat%' ORDER BY name;");
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\";";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var values = Enumerable.Range(0, reader.FieldCount)
                    .Select(i => $"{reader.GetName(i)}={(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))}");
                rows.Add($"{table}|{string.Join('|', values)}");
            }
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>A v3 database (V001–V003 only) with Repository, Owner and Workspace data, including edge cases.</summary>
    private void CreatePopulatedV3Database()
    {
        _root.CreateDatabaseInitializer(new V001_InitialSchema(), new V002_OwnerManager(), new V003_Workspace()).Initialize();
        using var connection = _root.Open();
        var system = Db.InsertSystem(connection, "4000000002", "M-Find");
        Db.InsertSystemRepository(connection, system, "SVN_SYSTEM");
        Db.Exec(connection, "INSERT INTO SystemRepositories (SystemId, RepositoryType, IsEnabled, CustomTarget) VALUES ($id, 'GIT_SYSTEM', 0, NULL);", ("$id", system));
        Db.Exec(connection, "INSERT INTO SystemRepositories (SystemId, RepositoryType, IsEnabled, CustomTarget) VALUES ($id, 'RELEASE', 1, 'http://example.test/custom');", ("$id", system));
        var other = Db.InsertSystem(connection, "ABC001", "販売管理");
        Db.Exec(connection, "UPDATE Systems SET IsActive = 0, Description = 'inactive' WHERE Id = $id;", ("$id", other));

        Db.Exec(connection, "INSERT INTO OwnerGroups (Name, NameKey, SortOrder, CreatedAt, UpdatedAt) VALUES ('Tools', 'TOOLS', 1, $now, $now);", ("$now", Db.Now));
        Db.Exec(connection, """
            INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, Arguments, Username, Note, SortOrder, IsFavorite, CreatedAt, UpdatedAt)
            VALUES (1, 'Portal', 'Web', 'https://portal.example.test/', NULL, 'me', 'note', 0, 1, $now, $now);
            """, ("$now", Db.Now));

        var folder = Db.InsertNode(connection, null, "Folder", "Clients");
        var project = Db.InsertNode(connection, folder, "Project", "Alpha");
        Db.InsertProjectDetails(connection, project);
        Db.Exec(connection, "UPDATE Projects SET Status = 'OnHold', Description = 'line1\nline2', Owner = 'A', ProjectManager = 'B' WHERE NodeId = $id;", ("$id", project));
    }

    [Fact]
    public void FreshDatabase_HasTheReviewedV004Schema()
    {
        // Exactly V001–V004: later migrations (V006 Trash) extend these tables and are covered by their own tests.
        var result = _root.CreateDatabaseInitializer(new V001_InitialSchema(), new V002_OwnerManager(), new V003_Workspace(), new V004_ProjectContent()).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Outcome);
        Assert.Equal(4, result.SchemaVersion);
        using var connection = _root.Open();
        Assert.Equal(4, Db.ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Equal(
            ["Id", "ProjectNodeId", "ParentFolderId", "Name", "NameKey", "CreatedAt", "UpdatedAt"],
            Names(connection, "SELECT name FROM pragma_table_info('ProjectFolders') ORDER BY cid;"));
        Assert.Equal(
            ["Id", "ProjectNodeId", "FolderId", "LinkType", "Name", "Target", "CreatedAt", "UpdatedAt"],
            Names(connection, "SELECT name FROM pragma_table_info('ProjectLinks') ORDER BY cid;"));
        Assert.Equal(
            ["IX_ProjectFolders_Container", "IX_ProjectLinks_Container", "UX_ProjectFolders_ChildName", "UX_ProjectFolders_RootName"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'index' AND tbl_name IN ('ProjectFolders', 'ProjectLinks') AND sql IS NOT NULL ORDER BY name;"));
        Assert.Equal(
            ["sqlite_autoindex_ProjectFolders_1"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'index' AND tbl_name IN ('ProjectFolders', 'ProjectLinks') AND sql IS NULL ORDER BY name;"));
        Assert.Equal(
            ["TR_ProjectFolders_Immutable", "TR_ProjectLinks_Immutable"],
            Names(connection, "SELECT name FROM sqlite_schema WHERE type = 'trigger' AND tbl_name IN ('ProjectFolders', 'ProjectLinks') ORDER BY name;"));
        Assert.Equal(1, Db.ScalarLong(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'ProjectFolders' AND sql LIKE '%STRICT';"));
        Assert.Equal(1, Db.ScalarLong(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'ProjectLinks' AND sql LIKE '%STRICT';"));
        Assert.Equal(0, Db.Count(connection, "ProjectFolders"));
        Assert.Equal(0, Db.Count(connection, "ProjectLinks"));

        // Every new foreign key restricts deletes; nothing cascades.
        var actions = Names(connection, """
            SELECT "table" || ':' || "from" || ':' || on_delete FROM pragma_foreign_key_list('ProjectFolders')
            UNION ALL
            SELECT "table" || ':' || "from" || ':' || on_delete FROM pragma_foreign_key_list('ProjectLinks')
            ORDER BY 1;
            """);
        Assert.Equal(6, actions.Count);
        Assert.All(actions, a => Assert.EndsWith(":RESTRICT", a));
    }

    [Fact]
    public void V004_IsTheReviewedSql_AndOlderMigrationsAreUntouched()
    {
        Assert.Equal(4, new V004_ProjectContent().Version);
        Assert.Contains("CONSTRAINT FK_ProjectLinks_Folder", V004_ProjectContent.Sql);
        Assert.DoesNotContain("ProjectNotes", V004_ProjectContent.Sql);
        Assert.DoesNotContain("CASCADE", V004_ProjectContent.Sql);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], SchemaMigrations.All.Select(m => m.Version));
    }

    [Fact]
    public void PopulatedV3Database_IsBackedUpAndUpgraded_PreservingEveryRow()
    {
        CreatePopulatedV3Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        Assert.Contains(beforeData, r => r.StartsWith("SystemRepositories|", StringComparison.Ordinal) && r.Contains("IsEnabled=0", StringComparison.Ordinal));
        Assert.Contains(beforeData, r => r.Contains("CustomTarget=http://example.test/custom", StringComparison.Ordinal));

        var result = _root.CreateDatabaseInitializer(MigrationSets.ThroughV006).Initialize(); // pinned: the v3 objects V007 replaces are checked below

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal(3, result.PreviousSchemaVersion);
        Assert.Equal(6, result.SchemaVersion);
        using (var connection = _root.Open())
        {
            Assert.Equal(6, Db.ScalarLong(connection, "PRAGMA user_version;"));
            Assert.Equal(beforeData, DataSnapshot(connection)); // every pre-existing row unchanged; no seed into an existing database
            var afterSchema = SchemaSnapshot(connection);
            Assert.All(beforeSchema, entry => Assert.Contains(entry, afterSchema)); // every v3 object unchanged
            Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA quick_check(1);"));
        }

        // The validated pre-migration copy is the untouched v3 database.
        var backupFile = result.MigrationBackupFile!;
        Assert.StartsWith(_root.Paths.MigrationBackupsDirectory, backupFile);
        Assert.Contains("_v3_before_v6_", Path.GetFileName(backupFile));
        using var backup = new SqliteConnection($"Data Source={backupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(3, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal("ok", Db.ScalarString(backup, "PRAGMA quick_check(1);"));
        Assert.Equal(beforeData, DataSnapshot(backup));
        Assert.Equal(beforeSchema, SchemaSnapshot(backup));
    }

    [Fact]
    public void FailureAtTheFirstStatement_KeepsTheConflictingTable_AndLeavesNoV004Objects()
    {
        CreatePopulatedV3Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            Db.Exec(connection, "CREATE TABLE ProjectFolders (Legacy TEXT);");
            Db.Exec(connection, "INSERT INTO ProjectFolders (Legacy) VALUES ('kept');");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateInitializer().Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV3(beforeData, beforeSchema);
        using (var after = _root.Open())
        {
            Assert.Equal(["Legacy"], Names(after, "SELECT name FROM pragma_table_info('ProjectFolders');"));
            Assert.Equal("kept", Db.ScalarString(after, "SELECT Legacy FROM ProjectFolders;"));
        }

        var firstBackups = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);

        // A second start fails the same way, changes nothing, and keeps the earlier backup.
        Thread.Sleep(5); // distinct backup timestamp
        Assert.Throws<DatabaseStartupException>(() => _root.CreateInitializer().Initialize());
        AssertUnchangedV3(beforeData, beforeSchema);
        var allBackups = AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 2);
        Assert.All(firstBackups, b => Assert.Contains(b, allBackups));
    }

    [Fact]
    public void FailureAfterPartialDdl_RollsBackTheCreatedObjects_AndKeepsThePreExistingIndex()
    {
        CreatePopulatedV3Database();
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // V004 creates ProjectFolders, its indexes and trigger, and ProjectLinks before failing on this name.
            Db.Exec(connection, "CREATE TABLE Unrelated (Value TEXT);");
            Db.Exec(connection, "CREATE INDEX IX_ProjectLinks_Container ON Unrelated (Value);");
            Db.Exec(connection, "INSERT INTO Unrelated (Value) VALUES ('kept');");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateInitializer().Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        AssertUnchangedV3(beforeData, beforeSchema);
        using (var after = _root.Open())
        {
            Assert.DoesNotContain("ProjectFolders", Db.UserTables(after));
            Assert.DoesNotContain("ProjectLinks", Db.UserTables(after));
            Assert.Equal("Unrelated", Db.ScalarString(after, "SELECT tbl_name FROM sqlite_schema WHERE name = 'IX_ProjectLinks_Container';"));
        }

        AssertValidatedBackups(beforeData, beforeSchema, expectedCount: 1);
    }

    private void AssertUnchangedV3(List<string> beforeData, List<string> beforeSchema)
    {
        using var after = _root.Open();
        Assert.Equal(3, Db.ScalarLong(after, "PRAGMA user_version;"));
        Assert.Equal(beforeSchema, SchemaSnapshot(after)); // no V004 object of any kind remains
        Assert.Equal(beforeData, DataSnapshot(after));
        Assert.Empty(Names(after, "SELECT name FROM sqlite_schema WHERE name IN ('UX_ProjectFolders_RootName', 'UX_ProjectFolders_ChildName', 'IX_ProjectFolders_Container', 'TR_ProjectFolders_Immutable', 'TR_ProjectLinks_Immutable', 'ProjectLinks');"));
    }

    private List<string> AssertValidatedBackups(List<string> beforeData, List<string> beforeSchema, int expectedCount)
    {
        var backups = Directory.GetFiles(_root.Paths.MigrationBackupsDirectory, $"workhub_v3_before_v{SchemaMigrations.LatestVersion}_*.db").Order().ToList();
        Assert.Equal(expectedCount, backups.Count);
        foreach (var file in backups)
        {
            using var backup = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
            backup.Open();
            Assert.Equal("ok", Db.ScalarString(backup, "PRAGMA quick_check(1);"));
            Assert.Equal(3, Db.ScalarLong(backup, "PRAGMA user_version;"));
            Assert.Equal(beforeData, DataSnapshot(backup));
            Assert.Equal(beforeSchema, SchemaSnapshot(backup));
        }

        return backups;
    }
}
