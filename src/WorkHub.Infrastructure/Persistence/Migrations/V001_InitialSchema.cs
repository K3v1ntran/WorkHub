using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Repository tables only (Systems, SystemRepositories).
/// Built-in repository types are NOT a table: they are application-defined
/// (see <c>RepositoryCatalog</c>) and only referenced here by stable storage code.
/// Owner and Workspace tables arrive in their own later migrations. No rows are inserted.
/// Timestamps are ISO-8601 UTC text. Booleans are 0/1 integers.
/// </summary>
/// <remarks>
/// SystemCode integrity: the column is declared <c>COLLATE NOCASE</c>, so the UNIQUE constraint's
/// automatic index compares with NOCASE and rejects codes differing only by case (ABC001 vs abc001).
/// NOCASE folds ASCII letters only, so a CHECK restricts codes to ASCII <c>[A-Za-z0-9._-]</c>,
/// which makes the case-insensitive guarantee complete.
/// </remarks>
internal sealed class V001_InitialSchema : ISchemaMigration
{
    public int Version => 1;

    public string Description => "Repository systems tables";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.Execute(Sql, transaction);

    private const string Sql = """
        CREATE TABLE Systems (
            Id          INTEGER PRIMARY KEY,
            SystemCode  TEXT    NOT NULL COLLATE NOCASE
                                CONSTRAINT UQ_Systems_SystemCode UNIQUE
                                CONSTRAINT CK_Systems_SystemCode
                                CHECK (length(SystemCode) BETWEEN 1 AND 100
                                       AND SystemCode NOT GLOB '*[^A-Za-z0-9._-]*'),
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
        """;
}
