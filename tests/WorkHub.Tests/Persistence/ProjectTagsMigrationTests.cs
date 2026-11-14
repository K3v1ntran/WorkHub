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
/// V010 (Project tags; Phase 17 proposal R2b §5.2, §5.5, §6.1 M/S): the probed SQL, the upgrade of populated v8 and v9 databases
/// through the real initializer with their PreMigration copies at the SOURCE schema, rollback, a v9 build's refusal of a v10
/// database (automated part of C1), and the database guards asserted with raw SQL on the real migration.
/// </summary>
public sealed class ProjectTagsMigrationTests : IDisposable
{
    /// <summary>SHA-256 of the §5.2 DDL as probed (LF, no trailing newline): work\phase17-probes, P1a–P1l and P2.</summary>
    private const string ProbedSqlSha256 = "aa9c82607bb4565f4b6b4b4d115583b1a0aa61ed594daba3af909ff1e549926c";

    /// <summary>The candidate v10 reference of probe P2, reproduced from the registered migration before the fixture was edited.</summary>
    private const string V010CanonicalFingerprint = "48B72088FAAA7ACE2C41E7EDAE245D26993C7D9F122700721CBBFF308600BACE";

    private const string Insert = "INSERT INTO ProjectTags (ProjectNodeId, NameKey, Name, CreatedAt) VALUES ($p, $k, $n, '2026-11-01T01:02:03.004Z');";

    private static readonly string[] V010Objects =
        ["ProjectTags", "IX_ProjectTags_NameKey", "TR_ProjectTags_Insert", "TR_ProjectTags_Update", "TR_ProjectTags_Delete"];

    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    /// <summary>A populated database at <paramref name="version"/> (8 or 9): folders, Projects (one archived, one trashed), content.</summary>
    private Dictionary<string, long> CreatePopulated(int version)
    {
        var set = version == 8 ? MigrationSets.ThroughV008 : MigrationSets.ThroughV009;
        Assert.Equal(version, _root.CreateDatabaseInitializer(set).Initialize().SchemaVersion);
        using var c = _root.Open();
        var ids = new Dictionary<string, long> { ["F"] = Db.InsertNode(c, null, "Folder", "2026") };
        var n = 0;
        foreach (var name in new[] { "Alpha", "Beta", "Archived", "Trashed" })
        {
            ids[name] = Db.InsertNode(c, ids["F"], "Project", name);
            Db.InsertProjectDetails(c, ids[name], $"{++n:x2}".PadLeft(32, '0'));
        }

        Db.Exec(c, "INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, Content, CreatedAt, UpdatedAt) VALUES ($p, NULL, 'Kickoff', 'text', $now, $now);", ("$p", ids["Alpha"]), ("$now", Db.Now));
        Db.Exec(c, "UPDATE Projects SET Status = 'Archived', ArchiveSerial = ArchiveSerial + 1 WHERE NodeId = $id;", ("$id", ids["Archived"]));
        Db.Exec(c, "UPDATE WorkspaceNodes SET TrashedAt = '2026-02-01T00:00:00.000Z' WHERE Id = $id;", ("$id", ids["Trashed"]));
        return ids;
    }

    /// <summary>A fresh current (v10) database with Projects P, Q (live) and T (directly trashed after its tags were added).</summary>
    private (SqliteConnection Connection, long P, long Q, long T) FreshV10()
    {
        _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();
        var c = _root.Open();
        var p = Db.InsertNode(c, null, "Project", "P");
        Db.InsertProjectDetails(c, p, "000000000000000000000000000000a1");
        var q = Db.InsertNode(c, null, "Project", "Q");
        Db.InsertProjectDetails(c, q, "000000000000000000000000000000a2");
        var t = Db.InsertNode(c, null, "Project", "T");
        Db.InsertProjectDetails(c, t, "000000000000000000000000000000a3");
        Tag(c, p, "INFRA", "Infra");
        Tag(c, t, "INFRA", "Infra");
        Db.Exec(c, "UPDATE WorkspaceNodes SET TrashedAt = '2026-02-01T00:00:00.000Z' WHERE Id = $id;", ("$id", t));
        return (c, p, q, t);
    }

    private static void Tag(SqliteConnection c, long project, string key, string name) =>
        Db.Exec(c, Insert, ("$p", project), ("$k", key), ("$n", name));

    /// <summary>Every tag row (key order), compared before/after a refused statement.</summary>
    private static List<string> Tags(SqliteConnection c) =>
        Names(c, "SELECT ProjectNodeId || '|' || NameKey || '|' || Name || '|' || CreatedAt FROM ProjectTags ORDER BY ProjectNodeId, NameKey;");

    /// <summary>Runs a statement that must abort with <paramref name="message"/> and leave every tag row unchanged.</summary>
    private static void Refused(SqliteConnection c, string sql, string message, int extended, params (string Name, object? Value)[] parameters)
    {
        var before = Tags(c);
        var ex = Assert.Throws<SqliteException>(() => Db.Exec(c, sql, parameters));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.Equal(extended, ex.SqliteExtendedErrorCode);
        Assert.Equal(before, Tags(c));
    }

    private const int Trigger = 1811; // SQLITE_CONSTRAINT_TRIGGER
    private const int Check = 275;    // SQLITE_CONSTRAINT_CHECK
    private const int PrimaryKey = 1555;

    // ---- M: migration ---------------------------------------------------------------------------------------------------

    [Fact]
    public void M3_V010_IsTheProbedSql_RegisteredLast_AndV001ToV009AreUnchanged()
    {
        Assert.Equal(10, new V010_ProjectTags().Version);
        Assert.Equal("Project tags", new V010_ProjectTags().Description);
        Assert.IsType<V010_ProjectTags>(SchemaMigrations.All[^1]);
        Assert.Equal(10, SchemaMigrations.LatestVersion);

        var sql = V010_ProjectTags.Sql.ReplaceLineEndings("\n");
        Assert.Equal(ProbedSqlSha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        Assert.Contains(") STRICT, WITHOUT ROWID;", sql, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT PK_ProjectTags PRIMARY KEY (ProjectNodeId, NameKey)", sql, StringComparison.Ordinal);
        foreach (var statement in new[] { "INSERT INTO", "DELETE FROM", "ALTER TABLE", "DROP " })
            Assert.DoesNotContain(statement, sql, StringComparison.Ordinal); // no existing table or row is touched

        // The frozen migrations keep the golden fingerprints of v1-v9; v10 is the probed value.
        for (var v = 1; v <= 9; v++)
            Assert.Equal(SchemaReference.Frozen.CanonicalHash(v), new SchemaReference(MigrationSets.ThroughV009).CanonicalHash(v));
        Assert.Equal(V010CanonicalFingerprint, SchemaReference.Frozen.CanonicalHash(10));
    }

    [Theory]
    [InlineData(Checkout.Lf)]
    [InlineData(Checkout.Crlf)]
    [InlineData(Checkout.Mixed)]
    public void M2_TheV10Fingerprint_IsTheSameForLfCrlfAndMixedCheckouts(Checkout checkout)
    {
        Assert.Equal(V010CanonicalFingerprint, new SchemaReference(LineEndingMigrations.For(checkout)).CanonicalHash(10));
    }

    [Fact]
    public void M2a_FreshDatabase_HasTheProbedTableForm()
    {
        var result = _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();
        Assert.Equal(10, result.SchemaVersion);
        using var c = _root.Open();
        Assert.All(V010Objects, name => Assert.Equal(1, Db.ScalarLongWith(c, "SELECT count(*) FROM sqlite_schema WHERE name = $n;", ("$n", name))));
        Assert.Equal("table|4|1|1", Db.ScalarString(c, "SELECT type || '|' || ncol || '|' || wr || '|' || strict FROM pragma_table_list WHERE schema = 'main' AND name = 'ProjectTags';"));
        Assert.Equal(["ProjectNodeId:1", "NameKey:2", "Name:0", "CreatedAt:0"], Names(c, "SELECT name || ':' || pk FROM pragma_table_info('ProjectTags') ORDER BY cid;"));
        Assert.Equal(["ProjectNodeId", "NameKey"], Names(c, "SELECT x.name FROM pragma_index_list('ProjectTags') AS l JOIN pragma_index_xinfo(l.name) AS x WHERE l.origin = 'pk' AND x.\"key\" = 1 ORDER BY x.seqno;"));
        Assert.Contains("no such column: rowid", Assert.Throws<SqliteException>(() => Db.ScalarLong(c, "SELECT rowid FROM ProjectTags;")).Message, StringComparison.Ordinal);
        Assert.Equal(0, Db.ScalarLong(c, "SELECT count(*) FROM sqlite_sequence WHERE name = 'ProjectTags';"));
        Assert.Empty(SchemaReference.Frozen.DifferencesFrom(10, SchemaFingerprint.Read(c)));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(8)]
    public void M1_PopulatedDatabase_IsUpgradedToV10_EveryRowKept_EmptyProjectTags_PreMigrationCopyAtTheSourceSchema(int from)
    {
        CreatePopulated(from);
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var result = _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal((from, 10), (result.PreviousSchemaVersion, result.SchemaVersion));
        using (var connection = _root.Open())
        {
            Assert.Equal(10, Db.ScalarLong(connection, "PRAGMA user_version;"));
            Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA integrity_check;"));
            Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
            Assert.Equal(0, Db.Count(connection, "ProjectTags"));
            var after = DataSnapshot(connection);
            if (from == 9)
                Assert.Equal(beforeData, after); // V010 reads and writes no existing row
            else
                Assert.Equal(beforeData, after.Select(r => r.StartsWith("WorkspaceNodes|", StringComparison.Ordinal)
                    ? r.Replace("|FolderArchivedAt=NULL|FolderTrashedAt=NULL|LifecycleSerial=0", string.Empty, StringComparison.Ordinal) : r).ToList());
            if (from == 9)
                Assert.Equal(V010Objects.Order(StringComparer.Ordinal), SchemaSnapshot(connection).Except(beforeSchema).Select(r => r.Split('|')[1]).Order(StringComparer.Ordinal));
            Assert.Empty(SchemaReference.Frozen.DifferencesFrom(10, SchemaFingerprint.Read(connection)));
        }

        // M1a: ONE PreMigration copy, at the source version, validated as such, without ProjectTags.
        var backups = Directory.GetFiles(_root.Paths.MigrationBackupsDirectory, $"workhub_v{from}_before_v10_*.db");
        var backup = Assert.Single(backups);
        Assert.Equal(BackupStatus.FullyValidated, BackupKit.Validator().Check(backup, BackupCheckLevel.Full, nameVersion: from).Status);
        using var copy = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
        copy.Open();
        Assert.Equal(from, Db.ScalarLong(copy, "PRAGMA user_version;"));
        Assert.Equal(0, Db.ScalarLong(copy, "SELECT count(*) FROM sqlite_schema WHERE name = 'ProjectTags';"));
        Assert.Equal(beforeData, DataSnapshot(copy));
        Assert.Equal(beforeSchema, SchemaSnapshot(copy));
        Assert.Empty(SchemaReference.Frozen.DifferencesFrom(from, SchemaFingerprint.Read(copy)));
    }

    [Fact]
    public void M4_FailureAtTheLastStatement_RollsV010Back_ToAnIntactV9Database()
    {
        CreatePopulated(9);
        List<string> beforeData, beforeSchema;
        using (var connection = _root.Open())
        {
            // Everything up to the last trigger is created; its name is already taken by an unrelated trigger.
            Db.Exec(connection, "CREATE TABLE Unrelated (Value TEXT);");
            Db.Exec(connection, "CREATE TRIGGER TR_ProjectTags_Delete BEFORE DELETE ON Unrelated BEGIN SELECT 1; END;");
            beforeData = DataSnapshot(connection);
            beforeSchema = SchemaSnapshot(connection);
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        using var after = _root.Open();
        Assert.Equal(9, Db.ScalarLong(after, "PRAGMA user_version;"));
        Assert.Equal(beforeSchema, SchemaSnapshot(after));
        Assert.Equal(beforeData, DataSnapshot(after));
        Assert.Equal(0, Db.ScalarLong(after, "SELECT count(*) FROM sqlite_schema WHERE name IN ('ProjectTags', 'IX_ProjectTags_NameKey', 'TR_ProjectTags_Insert', 'TR_ProjectTags_Update');"));
    }

    /// <summary>C1 (automated part) / X4e: a v9 build (frozen V001-V009, i.e. dc7169a) refuses a v10 database and changes nothing.</summary>
    [Fact]
    public void C1_AV9Build_RefusesTheV10Database_AndLeavesItUnchanged()
    {
        var ids = CreatePopulated(9);
        _root.CreateDatabaseInitializer([.. SchemaMigrations.All]).Initialize();
        using (var c = _root.Open())
            Tag(c, ids["Alpha"], "INFRA", "Infra");
        BackupKit.ClearPools(_root.Paths.DatabaseFile);
        var before = BackupKit.Hash(_root.Paths.DatabaseFile);
        var backups = Directory.GetFiles(_root.Paths.MigrationBackupsDirectory);

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(MigrationSets.ThroughV009).Initialize());
        BackupKit.ClearPools(_root.Paths.DatabaseFile);

        Assert.Equal(DatabaseStartupFailure.NewerSchema, ex.Failure);
        Assert.Equal("The WorkHub database uses schema v10, newer than this build supports (v9). Update WorkHub.", ex.Message);
        Assert.Equal(before, BackupKit.Hash(_root.Paths.DatabaseFile));
        Assert.Equal(backups, Directory.GetFiles(_root.Paths.MigrationBackupsDirectory));
    }

    // ---- S: SQL guards on the real migration -------------------------------------------------------------------------------

    [Fact]
    public void S1_SameKeyOnOneProject_IsRefused_EvenWithAnotherSpelling_TheSameKeyOnAnotherProjectIsAllowed()
    {
        var (c, p, q, _) = FreshV10();
        using (c)
        {
            Refused(c, Insert, "WH_TAG_DUPLICATE", Trigger, ("$p", p), ("$k", "INFRA"), ("$n", "INFRA"));
            Refused(c, Insert, "WH_TAG_DUPLICATE", Trigger, ("$p", p), ("$k", "INFRA"), ("$n", "ＩＮＦＲＡ"));
            Tag(c, q, "INFRA", "Infra");
            Assert.Equal(["Infra"], Names(c, $"SELECT Name FROM ProjectTags WHERE ProjectNodeId = {p};"));
            Assert.Equal(1, Db.ScalarLong(c, $"SELECT count(*) FROM ProjectTags WHERE ProjectNodeId = {q} AND NameKey = 'INFRA';"));
        }
    }

    [Theory]
    [InlineData("INSERT OR REPLACE INTO ProjectTags (ProjectNodeId, NameKey, Name, CreatedAt) VALUES ($p, 'INFRA', 'Changed', '2026-11-14T00:00:00.000Z');")]
    [InlineData("INSERT INTO ProjectTags (ProjectNodeId, NameKey, Name, CreatedAt) VALUES ($p, 'INFRA', 'Changed', '2026-11-14T00:00:00.000Z') ON CONFLICT (ProjectNodeId, NameKey) DO UPDATE SET Name = excluded.Name, CreatedAt = excluded.CreatedAt;")]
    public void S1a_S1b_ReplaceAndUpsertOnAnExistingKey_AreRefused_LiveAndTrashed(string sql)
    {
        var (c, p, _, t) = FreshV10();
        using (c)
        {
            foreach (var mode in new[] { "OFF", "ON" })
            {
                Db.Exec(c, $"PRAGMA recursive_triggers = {mode};");
                Refused(c, sql, "WH_TAG_DUPLICATE", Trigger, ("$p", p));
                Refused(c, sql, "WH_PROJECT_TRASHED", Trigger, ("$p", t));
            }

            Assert.Equal("Infra|2026-11-01T01:02:03.004Z", Db.ScalarString(c, $"SELECT Name || '|' || CreatedAt FROM ProjectTags WHERE ProjectNodeId = {p} AND NameKey = 'INFRA';"));
        }
    }

    [Fact]
    public void S1c_PlainInsertsOfNewKeys_Succeed()
    {
        var (c, p, q, _) = FreshV10();
        using (c)
        {
            Tag(c, p, "WEB", "Web");
            Tag(c, q, "INFRA", "Infra");
            Assert.Equal(4, Db.Count(c, "ProjectTags")); // P: INFRA, WEB; Q: INFRA; T: INFRA
        }
    }

    [Theory]
    [InlineData("empty", "CK_ProjectTags_Name")]
    [InlineData("41 code points", "CK_ProjectTags_Name")]
    [InlineData("leading space", "CK_ProjectTags_Name")]
    [InlineData("NUL inside", "CK_ProjectTags_NameNoNul")]
    public void S2_NamesThatBreakTheChecks_AreRefused(string label, string constraint)
    {
        var name = label switch { "empty" => "", "41 code points" => new string('a', 41), "leading space" => " a", _ => "a\0b" };
        var (c, p, _, _) = FreshV10();
        using (c)
            Refused(c, Insert, constraint, Check, ("$p", p), ("$k", "K1"), ("$n", name));
    }

    /// <summary>Code points (not strings) are the theory data, so no control character reaches a test name or the TRX.</summary>
    public static TheoryData<int> ControlCodePoints() => [.. Enumerable.Range(1, 31).Append(127).Concat(Enumerable.Range(128, 32))];

    [Theory]
    [MemberData(nameof(ControlCodePoints))]
    public void S2_EveryControlCharacter_IsRefused(int codePoint)
    {
        var (c, p, _, _) = FreshV10();
        using (c)
            Refused(c, Insert, "CK_ProjectTags_NameNoControl", Check, ("$p", p), ("$k", $"CP{codePoint}"), ("$n", $"a{(char)codePoint}b"));
    }

    [Theory]
    [InlineData("40 code points", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("Japanese", "会議")]
    [InlineData("full width", "Ｔｅｓｔ")]
    [InlineData("U+2028 inside", "a\u2028b")]
    [InlineData("U+00A0 inside", "a\u00A0b")]
    public void S2a_OtherNames_AreAccepted(string label, string name)
    {
        var (c, p, _, _) = FreshV10();
        using (c)
        {
            Tag(c, p, "K" + label, name);
            Assert.Equal(name, Db.ScalarString(c, "SELECT Name FROM ProjectTags WHERE ProjectNodeId = $p AND NameKey = $k;", ("$p", p), ("$k", "K" + label)));
        }
    }

    [Fact]
    public void S3_ADirectlyTrashedProject_FreezesItsTags_AndRowsAreImmutable_S3b_InheritedStatesAreNotADatabaseCheck()
    {
        var (c, p, _, t) = FreshV10();
        using (c)
        {
            Refused(c, Insert, "WH_PROJECT_TRASHED", Trigger, ("$p", t), ("$k", "NEW"), ("$n", "New"));
            Refused(c, "UPDATE ProjectTags SET Name = 'X' WHERE ProjectNodeId = $p;", "WH_PROJECT_TRASHED", Trigger, ("$p", t));
            Refused(c, "DELETE FROM ProjectTags WHERE ProjectNodeId = $p;", "WH_PROJECT_TRASHED", Trigger, ("$p", t));
            Refused(c, "UPDATE ProjectTags SET Name = 'X' WHERE ProjectNodeId = $p;", "WH_TAG_IMMUTABLE", Trigger, ("$p", p));
            Refused(c, "UPDATE ProjectTags SET NameKey = 'OTHER' WHERE ProjectNodeId = $p;", "WH_TAG_IMMUTABLE", Trigger, ("$p", p));

            // S3b: a Project below a trashed Folder is not itself trashed; the database lets its tags change (the store refuses).
            var folder = Db.InsertNode(c, null, "Folder", "Box");
            var inside = Db.InsertNode(c, folder, "Project", "Inside");
            Db.InsertProjectDetails(c, inside, "000000000000000000000000000000a4");
            Db.Exec(c, "UPDATE WorkspaceNodes SET FolderTrashedAt = '2026-02-01T00:00:00.000Z', LifecycleSerial = LifecycleSerial + 1 WHERE Id = $id;", ("$id", folder));
            Tag(c, inside, "OPS", "Ops");
            Db.Exec(c, "DELETE FROM ProjectTags WHERE ProjectNodeId = $p;", ("$p", inside));
        }
    }

    [Fact]
    public void S4_The21stTag_IsRefused_S4a_TheTrashIsCheckedFirst_DuplicateBeforeLimit()
    {
        var (c, p, _, t) = FreshV10();
        using (c)
        {
            for (var i = 2; i <= 20; i++)
                Tag(c, p, $"T{i:D2}", $"T{i:D2}");
            Assert.Equal(20, Db.ScalarLong(c, $"SELECT count(*) FROM ProjectTags WHERE ProjectNodeId = {p};"));
            Refused(c, Insert, "WH_TAG_LIMIT", Trigger, ("$p", p), ("$k", "NEW"), ("$n", "New"));
            Refused(c, Insert, "WH_TAG_DUPLICATE", Trigger, ("$p", p), ("$k", "T05"), ("$n", "t05"));
            Refused(c, Insert, "WH_PROJECT_TRASHED", Trigger, ("$p", t), ("$k", "INFRA"), ("$n", "Infra"));
        }
    }

    [Fact]
    public void S5_ForeignKey_AndProjectsAreStillNeverDeleted_PrimaryKeyBackstop()
    {
        var (c, p, _, _) = FreshV10();
        using (c)
        {
            Refused(c, Insert, "FOREIGN KEY constraint failed", 787, ("$p", 999999L), ("$k", "X"), ("$n", "X"));
            Assert.Contains("WH_NODE_DELETE_FORBIDDEN", Assert.Throws<SqliteException>(() => Db.Exec(c, "DELETE FROM Projects WHERE NodeId = $p;", ("$p", p))).Message, StringComparison.Ordinal);

            // P1h on the real migration: without the INSERT trigger the primary key still refuses a duplicate key.
            Db.Exec(c, "DROP TRIGGER TR_ProjectTags_Insert;");
            Refused(c, Insert, "UNIQUE constraint failed: ProjectTags.ProjectNodeId, ProjectTags.NameKey", PrimaryKey, ("$p", p), ("$k", "INFRA"), ("$n", "INFRA"));
        }
    }
}
