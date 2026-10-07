using Microsoft.Data.Sqlite;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

public sealed class DatabaseSafetyTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void NonSqliteFile_IsRefusedAndLeftUnchanged()
    {
        Directory.CreateDirectory(_root.Paths.DataDirectory);
        var garbage = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();
        File.WriteAllBytes(_root.Paths.DatabaseFile, garbage);

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateInitializer().Initialize());

        Assert.Equal(DatabaseStartupFailure.Unreadable, ex.Failure);
        SqliteConnection.ClearAllPools();
        Assert.Equal(garbage, File.ReadAllBytes(_root.Paths.DatabaseFile));
    }

    [Fact]
    public void NewerSchema_IsRefusedAndLeftUnchanged()
    {
        _root.CreateInitializer().Initialize();
        using (var connection = _root.Open())
        {
            Db.InsertSystem(connection, "KEEP");
            Db.Exec(connection, "PRAGMA user_version = 99;");
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateInitializer().Initialize());

        Assert.Equal(DatabaseStartupFailure.NewerSchema, ex.Failure);
        using var reopened = _root.Open();
        Assert.Equal(99, Db.ScalarLong(reopened, "PRAGMA user_version;"));
        Assert.Equal(1, Db.Count(reopened, "Systems"));
    }

    [Fact]
    public void ForeignSqliteDatabase_IsRefusedAndLeftUnchanged()
    {
        Directory.CreateDirectory(_root.Paths.DataDirectory);
        using (var connection = new SqliteConnection($"Data Source={_root.Paths.DatabaseFile};Pooling=False"))
        {
            connection.Open();
            Db.Exec(connection, "CREATE TABLE Unrelated (Value TEXT); INSERT INTO Unrelated VALUES ('x');");
        }

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateInitializer().Initialize());

        Assert.Equal(DatabaseStartupFailure.UnrecognizedDatabase, ex.Failure);
        using var reopened = _root.Open();
        Assert.Equal(["Unrelated"], Db.UserTables(reopened));
        Assert.Equal(0, Db.ScalarLong(reopened, "PRAGMA user_version;"));
    }

    [Fact]
    public void EmptyDatabaseFile_IsInitialized()
    {
        Directory.CreateDirectory(_root.Paths.DataDirectory);
        File.WriteAllBytes(_root.Paths.DatabaseFile, []);

        var result = _root.CreateInitializer().Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Created, result.Database.Outcome);
        Assert.Equal(SchemaMigrations.LatestVersion, result.Database.SchemaVersion);
    }

    [Fact]
    public void OlderSchema_IsBackedUpThenMigrated()
    {
        var v1 = new TestMigration(1, "CREATE TABLE A (Id INTEGER PRIMARY KEY);");
        var v2 = new TestMigration(2, "CREATE TABLE B (Id INTEGER PRIMARY KEY);");
        _root.CreateDatabaseInitializer(v1).Initialize();
        using (var connection = _root.Open())
            Db.Exec(connection, "INSERT INTO A (Id) VALUES (42);");

        var result = _root.CreateDatabaseInitializer(v1, v2).Initialize();

        Assert.Equal(DatabaseInitializationOutcome.Migrated, result.Outcome);
        Assert.Equal(1, result.PreviousSchemaVersion);
        Assert.Equal(2, result.SchemaVersion);
        Assert.NotNull(result.MigrationBackupFile);
        Assert.StartsWith(_root.Paths.MigrationBackupsDirectory, result.MigrationBackupFile);

        using var backup = new SqliteConnection($"Data Source={result.MigrationBackupFile};Mode=ReadOnly;Pooling=False");
        backup.Open();
        Assert.Equal(1, Db.ScalarLong(backup, "PRAGMA user_version;"));
        Assert.Equal(1, Db.Count(backup, "A"));
        Assert.Equal(["A"], Db.UserTables(backup));

        using var migrated = _root.Open();
        Assert.Equal(2, Db.ScalarLong(migrated, "PRAGMA user_version;"));
        Assert.Equal(["A", "B"], Db.UserTables(migrated));
    }

    [Fact]
    public void FailingMigration_RollsBackAndKeepsPreviousVersion()
    {
        var v1 = new TestMigration(1, "CREATE TABLE A (Id INTEGER PRIMARY KEY);");
        var broken = new TestMigration(2, "CREATE TABLE B (Id INTEGER PRIMARY KEY); INSERT INTO Missing VALUES (1);");
        _root.CreateDatabaseInitializer(v1).Initialize();

        var ex = Assert.Throws<DatabaseStartupException>(() => _root.CreateDatabaseInitializer(v1, broken).Initialize());

        Assert.Equal(DatabaseStartupFailure.MigrationFailed, ex.Failure);
        using var connection = _root.Open();
        Assert.Equal(1, Db.ScalarLong(connection, "PRAGMA user_version;"));
        Assert.Equal(["A"], Db.UserTables(connection));
    }

    [Fact]
    public void FreshDatabase_DoesNotCreateMigrationBackup()
    {
        _root.CreateInitializer().Initialize();

        Assert.False(Directory.Exists(_root.Paths.MigrationBackupsDirectory));
    }

    [Fact]
    public void MigrationsMustBeContiguous()
    {
        Assert.Throws<ArgumentException>(() => _root.CreateDatabaseInitializer(new TestMigration(2, "SELECT 1;")));
    }

    [Fact]
    public void OpenExisting_NeverCreatesADatabaseFile()
    {
        var factory = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        Directory.CreateDirectory(_root.Paths.DataDirectory);

        Assert.Throws<SqliteException>(() => factory.OpenExisting());
        Assert.False(File.Exists(_root.Paths.DatabaseFile));
    }

    private sealed class TestMigration(int version, string sql) : ISchemaMigration
    {
        public int Version => version;

        public string Description => $"test v{version}";

        public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
            connection.Execute(sql, transaction);
    }
}
