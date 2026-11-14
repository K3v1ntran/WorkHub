using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Restore;

/// <summary>
/// Phase 17 backups and Restore with Project tags (proposal R2b §5.5; tests X3, X4a–X4d), synthetic databases only: a v8 or v9
/// backup is restored and upgraded at the restart (PreMigration copy at the source version, empty ProjectTags); a v10 backup
/// brings its tags back exactly; the pre-restore copy keeps the replaced database's tags.
/// </summary>
public sealed class ProjectTagsRestoreTests : RestoreTestBase
{
    private string Db => Root.Paths.DatabaseFile;

    private (InitializationResult? Result, RestoreStartupException? Stop, Exception? Error) StartWithToken(string op) =>
        RestoreKit.Start(Root, Coordinator(token: op), new ScriptedRestoreUi());

    /// <summary>A current (v10) database with a Project carrying two tags (raw SQL, as a v10 build would store them).</summary>
    private long CurrentWithTags(params (string Key, string Name)[] tags)
    {
        BackupKit.CreateDatabase(Root);
        using var connection = BackupKit.Open(Db);
        var project = TestSupport.Db.InsertNode(connection, null, "Project", "Tagged");
        TestSupport.Db.InsertProjectDetails(connection, project, "000000000000000000000000000000b1");
        foreach (var (key, name) in tags)
            TestSupport.Db.Exec(connection, "INSERT INTO ProjectTags (ProjectNodeId, NameKey, Name, CreatedAt) VALUES ($p, $k, $n, '2026-11-01T01:02:03.004Z');", ("$p", project), ("$k", key), ("$n", name));
        return project;
    }

    private static List<string> TagRows(string file)
    {
        using var connection = BackupKit.Open(file, SqliteOpenMode.ReadOnly);
        return Persistence.ProjectContentMigrationTests.Names(connection,
            "SELECT ProjectNodeId || '|' || NameKey || '|' || Name || '|' || CreatedAt FROM ProjectTags ORDER BY ProjectNodeId, NameKey;");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public async Task X4a_X4b_X4d_AnOlderBackup_IsRestoredAndUpgraded_EmptyProjectTags_PreRestoreCopyKeepsTheTags(int version)
    {
        CurrentWithTags(("INFRA", "Infra"), ("OPS", "Ops"));
        var replacedTags = TagRows(Db);
        var entry = RestoreKit.PlaceOlderDaily(Root, version);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);

        var (result, _, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Migrated, result!.Database.Outcome);
        Assert.Equal((version, 10), (result.Database.PreviousSchemaVersion, result.Database.SchemaVersion));
        RestoreKit.ClearPools(Root);
        using (var restored = BackupKit.Open(Db, SqliteOpenMode.ReadOnly))
        {
            Assert.Equal(10L, BackupKit.Scalar(restored, "PRAGMA user_version;"));
            Assert.Equal(0L, BackupKit.Scalar(restored, "SELECT count(*) FROM ProjectTags;"));
            Assert.Equal(1L, BackupKit.Scalar(restored, "SELECT count(*) FROM Systems WHERE SystemCode = 'OLDV0001';"));
            Assert.Empty(SchemaReference.Frozen.DifferencesFrom(10, SchemaFingerprint.Read(restored)));
        }

        // The PreMigration copy is the restored backup at its SOURCE version: no ProjectTags table.
        var migration = result.Database.MigrationBackupFile!;
        Assert.StartsWith($"workhub_v{version}_before_v10_", Path.GetFileName(migration));
        Assert.True(BackupKit.Validator().Check(migration, BackupCheckLevel.Full, version).IsFullyValidated);
        using (var copy = BackupKit.Open(migration, SqliteOpenMode.ReadOnly))
            Assert.Equal(0L, BackupKit.Scalar(copy, "SELECT count(*) FROM sqlite_schema WHERE name = 'ProjectTags';"));

        // X4d: the pre-restore copy of the replaced v10 database keeps its tags.
        var journal = RestoreKit.Journal(Root, op);
        var safety = Path.Combine(Root.Paths.PreRestoreBackupsDirectory, journal.Safety!.FileName!);
        Assert.StartsWith("workhub_prerestore_v10_", journal.Safety.FileName);
        Assert.Equal(replacedTags, TagRows(safety));
    }

    [Fact]
    public async Task X3_X4c_AV10BackupWithTags_IsValid_AndRestoresItsTagsExactly()
    {
        var project = CurrentWithTags(("INFRA", "Infra"), ("OPS", "Ops"));
        var entry = RestoreKit.PlaceDaily(Root);
        var backup = BackupKit.Daily(Root, entry.FileName);
        Assert.Contains("_v10_", entry.FileName, StringComparison.Ordinal);
        Assert.True(BackupKit.Validator().Check(backup, BackupCheckLevel.Full, 10).IsFullyValidated); // X3
        var backupTags = TagRows(backup);
        Assert.Equal(2, backupTags.Count);

        using (var connection = BackupKit.Open(Db))
        {
            TestSupport.Db.Exec(connection, "DELETE FROM ProjectTags WHERE ProjectNodeId = $p AND NameKey = 'OPS';", ("$p", project));
            TestSupport.Db.Exec(connection, "INSERT INTO ProjectTags (ProjectNodeId, NameKey, Name, CreatedAt) VALUES ($p, 'WEB', 'Web', '2026-11-02T00:00:00.000Z');", ("$p", project));
        }

        Assert.NotEqual(backupTags, TagRows(Db));
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);

        var (result, _, error) = StartWithToken(op);

        Assert.Null(error);
        Assert.Equal(DatabaseInitializationOutcome.Opened, result!.Database.Outcome); // same version: no migration
        RestoreKit.ClearPools(Root);
        Assert.Equal(backupTags, TagRows(Db));
        Assert.Equal(BackupCompatibility.Current, RestoreKit.Journal(Root, op).Candidate!.Compatibility);
        Assert.Equal(10, SchemaMigrations.LatestVersion);
    }
}
