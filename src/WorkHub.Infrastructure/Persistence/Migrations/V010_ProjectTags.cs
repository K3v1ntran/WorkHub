using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Project Tags (Phase 17 proposal R2b §5.2; decisions D2, D3, D11). One new table, <c>ProjectTags</c>, and its own index and
/// triggers; no existing table, column, index, trigger or row is read or written.
/// <list type="bullet">
/// <item>A tag is identified by <c>(ProjectNodeId, NameKey)</c>: <c>STRICT, WITHOUT ROWID</c>, no surrogate id. <c>NameKey</c> is the
/// Workspace name key of <c>Name</c> (NFKC, upper case), computed by the application.</item>
/// <item><c>TR_ProjectTags_Insert</c> refuses, in this order: a directly trashed target Project (<c>WH_PROJECT_TRASHED</c>), an
/// existing key (<c>WH_TAG_DUPLICATE</c>; it runs before conflict resolution, so it also stops INSERT OR REPLACE and UPSERT) and
/// a 21st tag (<c>WH_TAG_LIMIT</c>). Rows are immutable (<c>TR_ProjectTags_Update</c>) and are not deleted while their Project is
/// directly in the Trash (<c>TR_ProjectTags_Delete</c>). Archived Projects and states inherited from ancestor Folders are store
/// checks, as for every other Project write since V009.</item>
/// </list>
/// The SQL is the §5.2 DDL exactly as probed (P1a–P1l, P2; work\phase17-probes), comments included: the comments inside the
/// CREATE statements are part of the stored schema and of the v10 fingerprint. Released migrations are frozen: never edit this
/// file once released, add V011+ instead.
/// </summary>
internal sealed class V010_ProjectTags : ISchemaMigration
{
    public int Version => 10;

    public string Description => "Project tags";

    /// <remarks>Executed with <see cref="SqliteExtensions.ExecuteScript"/>, so an error in any statement is thrown here.</remarks>
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.ExecuteScript(Sql, transaction);

    internal const string Sql = """
        -- R2b: the tag is identified by its composite key (ProjectNodeId, NameKey); no surrogate Id, no rowid
        -- (same table form as V001's SystemRepositories, V001_InitialSchema.cs:42-49). Column order puts the key first.
        CREATE TABLE ProjectTags (
            ProjectNodeId INTEGER NOT NULL
                                  CONSTRAINT FK_ProjectTags_Project
                                  REFERENCES Projects (NodeId) ON DELETE RESTRICT,
            NameKey       TEXT    NOT NULL
                                  CONSTRAINT CK_ProjectTags_NameKey
                                  CHECK (length(NameKey) > 0 AND instr(CAST(NameKey AS BLOB), x'00') = 0),
            Name          TEXT    NOT NULL
                                  CONSTRAINT CK_ProjectTags_Name
                                  CHECK (length(Name) BETWEEN 1 AND 40 AND Name = trim(Name))
                                  -- NUL: checked on the bytes (length() and GLOB stop at a NUL in text).
                                  CONSTRAINT CK_ProjectTags_NameNoNul
                                  CHECK (instr(CAST(Name AS BLOB), x'00') = 0)
                                  -- C0 U+0001-U+001F, DEL U+007F, C1 U+0080-U+009F (= .NET char.IsControl without U+0000).
                                  CONSTRAINT CK_ProjectTags_NameNoControl
                                  CHECK (Name NOT GLOB '*[' || char(1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,
                                         25,26,27,28,29,30,31,127,128,129,130,131,132,133,134,135,136,137,138,139,140,141,142,143,144,
                                         145,146,147,148,149,150,151,152,153,154,155,156,157,158,159) || ']*'),
            CreatedAt     TEXT    NOT NULL,
            CONSTRAINT PK_ProjectTags PRIMARY KEY (ProjectNodeId, NameKey)
        ) STRICT, WITHOUT ROWID;

        -- Suggestions group by key across projects; the primary key already serves per-project reads in display order.
        CREATE INDEX IX_ProjectTags_NameKey ON ProjectTags (NameKey);

        -- INSERT: fixed order inside ONE trigger (several BEFORE triggers on one event have no guaranteed order).
        -- A BEFORE INSERT trigger runs before conflict resolution (where REPLACE would delete the conflicting row), so the duplicate
        -- guard also stops INSERT OR REPLACE and UPSERT on the primary key, with or without PRAGMA recursive_triggers. The primary key
        -- is the only uniqueness constraint of the table, so there is no other key a REPLACE could conflict on.
        --   1. target Project directly in the Trash          -> WH_PROJECT_TRASHED
        --   2. same (ProjectNodeId, NameKey) already exists  -> WH_TAG_DUPLICATE
        --   3. 20 rows already on the target                 -> WH_TAG_LIMIT
        CREATE TRIGGER TR_ProjectTags_Insert
        BEFORE INSERT ON ProjectTags
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED')
             WHERE (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = NEW.ProjectNodeId) IS NOT NULL;
            SELECT RAISE(ABORT, 'WH_TAG_DUPLICATE')
             WHERE EXISTS (SELECT 1 FROM ProjectTags WHERE ProjectNodeId = NEW.ProjectNodeId AND NameKey = NEW.NameKey);
            SELECT RAISE(ABORT, 'WH_TAG_LIMIT')
             WHERE (SELECT count(*) FROM ProjectTags WHERE ProjectNodeId = NEW.ProjectNodeId) >= 20;
        END;

        -- UPDATE: tag rows are immutable. A directly trashed Project reports the Trash first (as the store does).
        CREATE TRIGGER TR_ProjectTags_Update
        BEFORE UPDATE ON ProjectTags
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED')
             WHERE (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.ProjectNodeId) IS NOT NULL;
            SELECT RAISE(ABORT, 'WH_TAG_IMMUTABLE');
        END;

        -- DELETE: refused while the Project itself is in the Workspace Trash (same rule and message as V007's content triggers).
        CREATE TRIGGER TR_ProjectTags_Delete
        BEFORE DELETE ON ProjectTags
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;
        """;
}
