using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Owner Manager: user-defined launcher groups and items. No rows are inserted.
/// <list type="bullet">
/// <item><c>OwnerGroups.NameKey</c> is the case-/width-insensitive uniqueness key computed by the application
/// (NFKC + invariant upper case), because SQLite NOCASE only folds ASCII and names may be Japanese.</item>
/// <item>A group owns its items: deleting a group cascades to its <c>OwnerItems</c> rows (never to external targets).</item>
/// <item><c>ActionType</c> is application-defined (CHECK list, no lookup table).</item>
/// <item><c>CredentialId</c> is an opaque, nullable reference reserved for the future encrypted vault.
/// There is deliberately no password/secret/token column.</item>
/// </list>
/// Released migrations are frozen: never edit this file, add V003+ instead.
/// </summary>
internal sealed class V002_OwnerManager : ISchemaMigration
{
    public int Version => 2;

    public string Description => "Owner Manager groups and launcher items";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.Execute(Sql, transaction);

    private const string Sql = """
        CREATE TABLE OwnerGroups (
            Id        INTEGER PRIMARY KEY,
            Name      TEXT    NOT NULL CHECK (length(Name) BETWEEN 1 AND 100 AND Name = trim(Name)),
            NameKey   TEXT    NOT NULL CONSTRAINT UQ_OwnerGroups_NameKey UNIQUE CHECK (length(NameKey) > 0),
            SortOrder INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT    NOT NULL,
            UpdatedAt TEXT    NOT NULL
        ) STRICT;

        CREATE INDEX IX_OwnerGroups_SortOrder ON OwnerGroups (SortOrder, Id);

        CREATE TABLE OwnerItems (
            Id               INTEGER PRIMARY KEY,
            GroupId          INTEGER NOT NULL REFERENCES OwnerGroups (Id) ON DELETE CASCADE,
            Name             TEXT    NOT NULL CHECK (length(trim(Name)) > 0 AND length(Name) <= 200),
            ActionType       TEXT    NOT NULL CHECK (ActionType IN ('Web', 'File', 'Folder', 'Executable')),
            Target           TEXT    NOT NULL CHECK (length(trim(Target)) > 0 AND length(Target) <= 2048),
            Arguments        TEXT    NULL,
            WorkingDirectory TEXT    NULL,
            Username         TEXT    NULL,
            CredentialId     TEXT    NULL CHECK (CredentialId IS NULL OR length(CredentialId) BETWEEN 1 AND 64),
            Note             TEXT    NULL,
            SortOrder        INTEGER NOT NULL DEFAULT 0,
            IsFavorite       INTEGER NOT NULL DEFAULT 0 CHECK (IsFavorite IN (0, 1)),
            CreatedAt        TEXT    NOT NULL,
            UpdatedAt        TEXT    NOT NULL
        ) STRICT;

        CREATE INDEX IX_OwnerItems_GroupId_SortOrder ON OwnerItems (GroupId, SortOrder, Id);
        """;
}
