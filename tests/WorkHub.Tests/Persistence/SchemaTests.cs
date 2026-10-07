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
        Assert.Equal(1, SchemaMigrations.LatestVersion);
        Assert.Equal(SchemaMigrations.LatestVersion, Db.ScalarLong(_connection, "PRAGMA user_version;"));
    }

    [Fact]
    public void InitialSchema_HasExactlyTheExpectedTables()
    {
        Assert.Equal(["OwnerGroups", "OwnerItems", "SystemRepositories", "Systems"], Db.UserTables(_connection));
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

    [Fact]
    public void SystemCode_IsUniqueIgnoringCase()
    {
        Db.InsertSystem(_connection, "Abc");
        Assert.Throws<SqliteException>(() => Db.InsertSystem(_connection, "ABC"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ABC")]
    [InlineData("ABC ")]
    public void SystemCode_RejectsEmptyOrUntrimmedValues(string code)
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
    public void OwnerGroupWithItems_CannotBeDeletedImplicitly()
    {
        Db.Exec(_connection, "INSERT INTO OwnerGroups (Id, Name, CreatedAt, UpdatedAt) VALUES (1, 'Tools', $now, $now);", ("$now", Db.Now));
        Db.Exec(_connection,
            "INSERT INTO OwnerItems (GroupId, Name, ItemType, Target, CreatedAt, UpdatedAt) VALUES (1, 'Docs', 'Web', 'https://example.invalid', $now, $now);",
            ("$now", Db.Now));

        Assert.Throws<SqliteException>(() => Db.Exec(_connection, "DELETE FROM OwnerGroups WHERE Id = 1;"));
    }

    [Fact]
    public void OwnerItemType_RejectsUnknownKinds()
    {
        Db.Exec(_connection, "INSERT INTO OwnerGroups (Id, Name, CreatedAt, UpdatedAt) VALUES (1, 'Tools', $now, $now);", ("$now", Db.Now));
        Assert.Throws<SqliteException>(() => Db.Exec(_connection,
            "INSERT INTO OwnerItems (GroupId, Name, ItemType, Target, CreatedAt, UpdatedAt) VALUES (1, 'Secret', 'Password', 'x', $now, $now);",
            ("$now", Db.Now)));
    }

    [Fact]
    public void Tables_AreStrict()
    {
        Assert.Throws<SqliteException>(() => Db.Exec(_connection,
            "INSERT INTO Systems (SystemCode, SystemName, IsActive, CreatedAt, UpdatedAt) VALUES ('ABC', 'x', 'yes', $now, $now);",
            ("$now", Db.Now)));
    }
}
