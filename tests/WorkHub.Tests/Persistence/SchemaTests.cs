using Microsoft.Data.Sqlite;
using WorkHub.Core.Repositories;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

public sealed class SchemaTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnection _connection;

    public SchemaTests()
    {
        _root.CreateInitializer().Initialize();
        _connection = _root.Open();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _root.Dispose();
    }

    [Fact]
    public void SchemaVersion_IsStoredInUserVersion()
    {
        Assert.Equal(3, SchemaMigrations.LatestVersion);
        Assert.Equal(SchemaMigrations.LatestVersion, Db.ScalarLong(_connection, "PRAGMA user_version;"));
    }

    [Fact]
    public void Schema_HasExactlyTheExpectedTables()
    {
        Assert.Equal(["OwnerGroups", "OwnerItems", "Projects", "SystemRepositories", "Systems", "WorkspaceNodes"], Db.UserTables(_connection));
    }

    [Fact]
    public void ForeignKeys_AreEnabledOnFactoryConnections()
    {
        Assert.Equal(1, Db.ScalarLong(_connection, "PRAGMA foreign_keys;"));
    }

    [Fact]
    public void SystemRepository_RequiresExistingSystem()
    {
        var ex = Assert.Throws<SqliteException>(() => Db.InsertSystemRepository(_connection, 999, "SVN_SYSTEM"));
        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    [Theory]
    [InlineData("ABC001", "abc001")]
    [InlineData("abc001", "ABC001")]
    [InlineData("GeSL_Abc", "gesl_aBC")]
    public void SystemCode_CaseVariantsCannotCoexist_OnInsert(string existing, string duplicate)
    {
        Db.InsertSystem(_connection, existing);

        var ex = Assert.Throws<SqliteException>(() => Db.InsertSystem(_connection, duplicate));

        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
        Assert.Equal(2067, ex.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_UNIQUE
        Assert.Equal(1, Db.Count(_connection, "Systems"));
    }

    [Fact]
    public void SystemCode_CaseVariantsCannotCoexist_OnUpdate()
    {
        Db.InsertSystem(_connection, "ABC001");
        var otherId = Db.InsertSystem(_connection, "XYZ001");

        var ex = Assert.Throws<SqliteException>(() =>
            Db.Exec(_connection, "UPDATE Systems SET SystemCode = 'abc001' WHERE Id = $id;", ("$id", otherId)));

        Assert.Equal(2067, ex.SqliteExtendedErrorCode);
    }

    [Fact]
    public void SystemCode_StoredCaseIsPreserved_AndLookupIgnoresCase()
    {
        Db.InsertSystem(_connection, "ABC001");

        Assert.Equal(1, Db.ScalarLong(_connection, "SELECT count(*) FROM Systems WHERE SystemCode = 'abc001';"));
        Assert.Equal(1, Db.ScalarLong(_connection, "SELECT count(*) FROM Systems WHERE SystemCode = 'ABC001' COLLATE BINARY;"));
    }

    [Fact]
    public void SystemCode_UniqueIndexUsesNocaseCollation()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT il."unique", ix.coll
            FROM pragma_index_list('Systems') AS il
            JOIN pragma_index_xinfo(il.name) AS ix ON ix.key = 1
            WHERE ix.name = 'SystemCode';
            """;
        using var reader = command.ExecuteReader();

        Assert.True(reader.Read(), "No index on Systems.SystemCode");
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("NOCASE", reader.GetString(1));
        Assert.False(reader.Read(), "Expected exactly one index on Systems.SystemCode");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ABC")]
    [InlineData("ABC ")]
    [InlineData("A B")]
    [InlineData("A/B")]
    [InlineData("ÄBC")]
    [InlineData("ABC ")]
    public void SystemCode_RejectsEmptyUntrimmedOrNonAsciiValues(string code)
    {
        Assert.Throws<SqliteException>(() => Db.InsertSystem(_connection, code));
    }

    [Fact]
    public void System_CanParticipateInSeveralRepositoryTypesOnce()
    {
        var id = Db.InsertSystem(_connection, "ABC");
        foreach (var info in RepositoryCatalog.All)
            Db.InsertSystemRepository(_connection, id, info.StorageCode);

        Assert.Equal(1, Db.Count(_connection, "Systems"));
        Assert.Equal(RepositoryCatalog.All.Count, Db.Count(_connection, "SystemRepositories"));
        Assert.Throws<SqliteException>(() => Db.InsertSystemRepository(_connection, id, "GIT_SYSTEM"));
    }

    [Theory]
    [InlineData("SVN")]
    [InlineData("svn_system")]
    [InlineData("OWNER")]
    public void RepositoryType_RejectsCodesOutsideTheCatalog(string code)
    {
        var id = Db.InsertSystem(_connection, "ABC");
        Assert.Throws<SqliteException>(() => Db.InsertSystemRepository(_connection, id, code));
    }

    [Fact]
    public void DeletingSystem_RemovesItsRepositoryMemberships()
    {
        var id = Db.InsertSystem(_connection, "ABC");
        Db.InsertSystemRepository(_connection, id, "RELEASE");

        Db.Exec(_connection, "DELETE FROM Systems WHERE Id = $id;", ("$id", id));

        Assert.Equal(0, Db.Count(_connection, "SystemRepositories"));
    }

    [Fact]
    public void Tables_AreStrict()
    {
        Assert.Throws<SqliteException>(() => Db.Exec(_connection,
            "INSERT INTO Systems (SystemCode, SystemName, IsActive, CreatedAt, UpdatedAt) VALUES ('ABC', 'x', 'yes', $now, $now);",
            ("$now", Db.Now)));
    }
}
