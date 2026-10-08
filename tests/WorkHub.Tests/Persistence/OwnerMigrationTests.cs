using Microsoft.Data.Sqlite;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>V002 (Owner Manager) on a fresh database and as an upgrade of an existing v1 (Phase 2) database.</summary>
public sealed class OwnerMigrationTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static List<string> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    private static List<string> Indexes(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_index_list('{table}') ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public void FreshDatabase_RunsV001ThroughV003()
    {
        var result = _root.CreateInitializer().Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Database.Outcome);
        Assert.Equal(3, result.Database.SchemaVersion);
        using var connection = _root.Open();
        Assert.Equal(3, Db.ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Equal(["Id", "Name", "NameKey", "SortOrder", "CreatedAt", "UpdatedAt"], Columns(connection, "OwnerGroups"));
        Assert.Equal(
            ["Id", "GroupId", "Name", "ActionType", "Target", "Arguments", "WorkingDirectory", "Username", "CredentialId",
             "Note", "SortOrder", "IsFavorite", "CreatedAt", "UpdatedAt"],
            Columns(connection, "OwnerItems"));
        Assert.Contains("IX_OwnerGroups_SortOrder", Indexes(connection, "OwnerGroups"));
        Assert.Contains("IX_OwnerItems_GroupId_SortOrder", Indexes(connection, "OwnerItems"));
        Assert.Equal(0, Db.Count(connection, "OwnerGroups"));
        Assert.Equal(0, Db.Count(connection, "OwnerItems"));
    }

    [Fact]
    public void OwnerItems_HasNoSecretColumns()
    {
        _root.CreateInitializer().Initialize();
        using var connection = _root.Open();

        foreach (var table in Db.UserTables(connection))
        {
            Assert.DoesNotContain(Columns(connection, table), c =>
                c.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || c.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || c.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || c.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void OwnerItems_CascadeWithTheirGroup_AndRequireAnExistingGroup()
    {
        _root.CreateInitializer().Initialize();
        using var connection = _root.Open();
        Db.Exec(connection, "INSERT INTO OwnerGroups (Id, Name, NameKey, CreatedAt, UpdatedAt) VALUES (1, 'Tools', 'TOOLS', $n, $n);", ("$n", Db.Now));
        Db.Exec(connection, "INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, CreatedAt, UpdatedAt) VALUES (1, 'a', 'Web', 'https://x.example.test', $n, $n);", ("$n", Db.Now));

        var fk = Assert.Throws<SqliteException>(() => Db.Exec(connection,
            "INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, CreatedAt, UpdatedAt) VALUES (99, 'b', 'Web', 'https://x.example.test', $n, $n);", ("$n", Db.Now)));
        Assert.Equal(787, fk.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_FOREIGNKEY

        Db.Exec(connection, "DELETE FROM OwnerGroups WHERE Id = 1;");
        Assert.Equal(0, Db.Count(connection, "OwnerItems"));
    }

    [Theory]
    [InlineData("ActionType", "'Password'")]
    [InlineData("IsFavorite", "2")]
    [InlineData("Target", "'   '")]
    [InlineData("CredentialId", "''")]
    public void OwnerItems_ConstraintsRejectInvalidValues(string column, string value)
    {
        _root.CreateInitializer().Initialize();
        using var connection = _root.Open();
        Db.Exec(connection, "INSERT INTO OwnerGroups (Id, Name, NameKey, CreatedAt, UpdatedAt) VALUES (1, 'Tools', 'TOOLS', $n, $n);", ("$n", Db.Now));
        var values = new Dictionary<string, string>
        {
            ["ActionType"] = "'Web'", ["IsFavorite"] = "0", ["Target"] = "'https://x.example.test'", ["CredentialId"] = "NULL",
        };
        values[column] = value;

        Assert.Throws<SqliteException>(() => Db.Exec(connection,
            $"INSERT INTO OwnerItems (GroupId, Name, ActionType, Target, CredentialId, IsFavorite, CreatedAt, UpdatedAt) " +
            $"VALUES (1, 'x', {values["ActionType"]}, {values["Target"]}, {values["CredentialId"]}, {values["IsFavorite"]}, $n, $n);", ("$n", Db.Now)));
    }

    [Fact]
    public void ExistingV1Database_IsBackedUpAndUpgraded_PreservingRepositoryData()
    {
        // A Phase 2 database: schema v1 with Systems and memberships.
        _root.CreateDatabaseInitializer(new V001_InitialSchema()).Initialize();
        using (var connection = _root.Open())
        {
            var id = Db.InsertSystem(connection, "ABC001", "販売管理システム");
            Db.InsertSystemRepository(connection, id, "SVN_SYSTEM");
            Db.InsertSystemRepository(connection, id, "RELEASE");
        }

        var result = _root.CreateInitializer().Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Database.Outcome);
        Assert.Equal(1, result.Database.PreviousSchemaVersion);
        Assert.Equal(3, result.Database.SchemaVersion);
        using (var connection = _root.Open())
        {
            Assert.Equal(3, Db.ScalarLong(connection, "PRAGMA user_version;"));
            Assert.Equal(1, Db.Count(connection, "Systems"));
            Assert.Equal(2, Db.Count(connection, "SystemRepositories"));
            Assert.Equal(1, Db.ScalarLong(connection, "SELECT count(*) FROM Systems WHERE SystemCode = 'ABC001' AND SystemName = '販売管理システム';"));
            Assert.Equal(0, Db.Count(connection, "OwnerGroups"));
        }

        // The pre-migration safety copy is the untouched v1 database.
        Assert.NotNull(result.Database.MigrationBackupFile);
        using var backup = new SqliteConnection($"Data Source={result.Database.MigrationBackupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(1, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal(["SystemRepositories", "Systems"], Db.UserTables(backup));
        Assert.Equal(1, Db.Count(backup, "Systems"));
    }

    [Fact]
    public void FailingLaterMigration_RollsBackAndKeepsV2()
    {
        _root.CreateDatabaseInitializer(new V001_InitialSchema(), new V002_OwnerManager()).Initialize();
        using (var connection = _root.Open())
            Db.Exec(connection, "INSERT INTO OwnerGroups (Name, NameKey, CreatedAt, UpdatedAt) VALUES ('Tools', 'TOOLS', $n, $n);", ("$n", Db.Now));

        var broken = new BrokenMigration();
        var ex = Assert.Throws<DatabaseStartupException>(() =>
            _root.CreateDatabaseInitializer(new V001_InitialSchema(), new V002_OwnerManager(), broken).Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        using var after = _root.Open();
        Assert.Equal(2, Db.ScalarLong(after, "PRAGMA user_version;"));
        Assert.DoesNotContain("Partial", Db.UserTables(after));
        Assert.Equal(1, Db.Count(after, "OwnerGroups"));
    }

    [Fact]
    public void V001_IsStillTheFrozenRepositoryOnlyMigration()
    {
        _root.CreateDatabaseInitializer(new V001_InitialSchema()).Initialize();
        using var connection = _root.Open();

        Assert.Equal(["SystemRepositories", "Systems"], Db.UserTables(connection));
    }

    private sealed class BrokenMigration : ISchemaMigration
    {
        public int Version => 3;

        public string Description => "broken test migration";

        public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
            connection.Execute("CREATE TABLE Partial (Id INTEGER); INSERT INTO DoesNotExist VALUES (1);", transaction);
    }
}
