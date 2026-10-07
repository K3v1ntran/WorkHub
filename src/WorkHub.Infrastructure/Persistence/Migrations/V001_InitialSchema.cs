using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Repository (Systems, SystemRepositories) and Owner (OwnerGroups, OwnerItems) tables.
/// Built-in repository types are NOT a table: they are application-defined
/// (see <c>RepositoryCatalog</c>) and only referenced here by stable storage code.
/// Workspace tables arrive in a later migration. No rows are inserted.
/// Timestamps are ISO-8601 UTC text. Booleans are 0/1 integers.
/// </summary>
internal sealed class V001_InitialSchema : ISchemaMigration
{
    public int Version => 1;

    public string Description => "Repository systems and Owner launcher tables";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.Execute(Sql, transaction);

    private const string Sql = """
        CREATE TABLE Systems (
            Id          INTEGER PRIMARY KEY,
            SystemCode  TEXT    NOT NULL COLLATE NOCASE
                                CONSTRAINT UQ_Systems_SystemCode UNIQUE
                                CHECK (length(SystemCode) BETWEEN 1 AND 100 AND SystemCode = trim(SystemCode)),
            SystemName  TEXT    NOT NULL CHECK (length(trim(SystemName)) > 0),
            Description TEXT    NULL,
            IsActive    INTEGER NOT NULL DEFAULT 1 CHECK (IsActive IN (0, 1)),
            CreatedAt   TEXT    NOT NULL,
            UpdatedAt   TEXT    NOT NULL
        ) STRICT;

        CREATE TABLE SystemRepositories (
            SystemId       INTEGER NOT NULL REFERENCES Systems (Id) ON DELETE CASCADE,
            RepositoryType TEXT    NOT NULL
                                   CHECK (RepositoryType IN ('SVN_SYSTEM', 'SVN_ORACLE', 'GIT_SYSTEM', 'GIT_ORACLE', 'RELEASE')),
            IsEnabled      INTEGER NOT NULL DEFAULT 1 CHECK (IsEnabled IN (0, 1)),
            CustomTarget   TEXT    NULL CHECK (CustomTarget IS NULL OR length(trim(CustomTarget)) > 0),
            PRIMARY KEY (SystemId, RepositoryType)
        ) STRICT, WITHOUT ROWID;

        CREATE INDEX IX_SystemRepositories_RepositoryType ON SystemRepositories (RepositoryType, SystemId);

        CREATE TABLE OwnerGroups (
            Id        INTEGER PRIMARY KEY,
            Name      TEXT    NOT NULL COLLATE NOCASE
                              CONSTRAINT UQ_OwnerGroups_Name UNIQUE
                              CHECK (length(trim(Name)) > 0),
            SortOrder INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT    NOT NULL,
            UpdatedAt TEXT    NOT NULL
        ) STRICT;

        CREATE TABLE OwnerItems (
            Id        INTEGER PRIMARY KEY,
            GroupId   INTEGER NOT NULL REFERENCES OwnerGroups (Id) ON DELETE RESTRICT,
            Name      TEXT    NOT NULL CHECK (length(trim(Name)) > 0),
            ItemType  TEXT    NOT NULL CHECK (ItemType IN ('Web', 'File', 'Folder', 'Executable')),
            Target    TEXT    NOT NULL CHECK (length(trim(Target)) > 0),
            Arguments TEXT    NULL,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT    NOT NULL,
            UpdatedAt TEXT    NOT NULL
        ) STRICT;

        CREATE INDEX IX_OwnerItems_GroupId_SortOrder ON OwnerItems (GroupId, SortOrder);
        """;
}
