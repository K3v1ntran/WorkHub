using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Project content, part 1: ProjectFolders (logical, nestable containers inside one Project) and ProjectLinks
/// (references to folders, files and web pages). No rows are inserted, and nothing touches the filesystem.
/// <list type="bullet">
/// <item>Every row is owned by a Project (<c>ProjectNodeId</c> → <c>Projects(NodeId)</c>). Composite foreign keys
/// <c>(ProjectNodeId, ParentFolderId|FolderId)</c> → <c>ProjectFolders(ProjectNodeId, Id)</c> keep a folder's children and
/// a link's folder inside the same Project; NULL means the Project root and is not checked.</item>
/// <item>Every FK is <c>ON DELETE RESTRICT</c>: a future delete of a Project node is blocked while it has content, instead of
/// cascading through <c>Projects</c>.</item>
/// <item>Folder names are unique among sibling folders only (partial unique indexes on the application-computed
/// <c>NameKey</c>); Link names may repeat. Cycle prevention needs a recursive query and runs in the store's move
/// transaction.</item>
/// </list>
/// The SQL is the reviewed revision 3 §5.2 DDL, unchanged. Released migrations are frozen: never edit this file, add V005+ instead.
/// </summary>
internal sealed class V004_ProjectContent : ISchemaMigration
{
    public int Version => 4;

    public string Description => "Project folders and links";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.Execute(Sql, transaction);

    internal const string Sql = """
        -- V004_ProjectContent
        -- Content inside a Project, part 1: ProjectFolders (logical, nestable) and ProjectLinks (references only).
        -- No rows are inserted. Nothing here creates, moves or deletes files or folders on disk.
        -- Every FK is ON DELETE RESTRICT: no existing or future delete can silently remove Project content.
        -- Released migrations are frozen: V001-V003 are never edited; this file is frozen once released.

        CREATE TABLE ProjectFolders (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            ProjectNodeId  INTEGER NOT NULL
                                   CONSTRAINT FK_ProjectFolders_Project
                                   REFERENCES Projects (NodeId) ON DELETE RESTRICT,
            ParentFolderId INTEGER NULL,
            Name           TEXT    NOT NULL
                                   CONSTRAINT CK_ProjectFolders_Name
                                   CHECK (length(Name) BETWEEN 1 AND 200 AND Name = trim(Name)),
            NameKey        TEXT    NOT NULL
                                   CONSTRAINT CK_ProjectFolders_NameKey
                                   CHECK (length(NameKey) > 0),
            CreatedAt      TEXT    NOT NULL,
            UpdatedAt      TEXT    NOT NULL,
            -- Parent key for the composite FKs (Id alone is already unique; the pair is what children reference).
            CONSTRAINT UQ_ProjectFolders_ProjectScope UNIQUE (ProjectNodeId, Id),
            -- A parent folder must belong to the SAME Project. Not checked when ParentFolderId IS NULL (Project root).
            CONSTRAINT FK_ProjectFolders_Parent
                FOREIGN KEY (ProjectNodeId, ParentFolderId)
                REFERENCES ProjectFolders (ProjectNodeId, Id) ON DELETE RESTRICT,
            CONSTRAINT CK_ProjectFolders_NotOwnParent
                CHECK (ParentFolderId IS NULL OR ParentFolderId <> Id)
        ) STRICT;

        -- Folder names are unique among sibling FOLDERS only (Links and Notes are not part of this key).
        -- Root level and child level use separate partial unique indexes; no sentinel replaces NULL.
        CREATE UNIQUE INDEX UX_ProjectFolders_RootName
            ON ProjectFolders (ProjectNodeId, NameKey)
            WHERE ParentFolderId IS NULL;

        CREATE UNIQUE INDEX UX_ProjectFolders_ChildName
            ON ProjectFolders (ParentFolderId, NameKey)
            WHERE ParentFolderId IS NOT NULL;

        -- Container listing, the Project FK lookup and the composite parent FK lookup.
        CREATE INDEX IX_ProjectFolders_Container
            ON ProjectFolders (ProjectNodeId, ParentFolderId);

        -- Identity, owning Project and creation time never change (no cross-Project moves).
        CREATE TRIGGER TR_ProjectFolders_Immutable
        BEFORE UPDATE OF Id, ProjectNodeId, CreatedAt ON ProjectFolders
        WHEN NEW.Id IS NOT OLD.Id
          OR NEW.ProjectNodeId IS NOT OLD.ProjectNodeId
          OR NEW.CreatedAt IS NOT OLD.CreatedAt
        BEGIN
            SELECT RAISE(ABORT, 'WH_CONTENT_IMMUTABLE');
        END;

        CREATE TABLE ProjectLinks (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            ProjectNodeId INTEGER NOT NULL
                                  CONSTRAINT FK_ProjectLinks_Project
                                  REFERENCES Projects (NodeId) ON DELETE RESTRICT,
            FolderId      INTEGER NULL,
            LinkType      TEXT    NOT NULL
                                  CONSTRAINT CK_ProjectLinks_LinkType
                                  CHECK (LinkType IN ('Folder', 'File', 'Web')),
            Name          TEXT    NOT NULL
                                  CONSTRAINT CK_ProjectLinks_Name
                                  CHECK (length(Name) BETWEEN 1 AND 200 AND Name = trim(Name)),
            Target        TEXT    NOT NULL
                                  CONSTRAINT CK_ProjectLinks_Target
                                  CHECK (length(Target) BETWEEN 1 AND 2048 AND Target = trim(Target)),
            CreatedAt     TEXT    NOT NULL,
            UpdatedAt     TEXT    NOT NULL,
            -- The containing folder must belong to the SAME Project. Not checked when FolderId IS NULL (Project root).
            CONSTRAINT FK_ProjectLinks_Folder
                FOREIGN KEY (ProjectNodeId, FolderId)
                REFERENCES ProjectFolders (ProjectNodeId, Id) ON DELETE RESTRICT
        ) STRICT;

        -- Container listing, the Project FK lookup and the composite folder FK lookup.
        CREATE INDEX IX_ProjectLinks_Container
            ON ProjectLinks (ProjectNodeId, FolderId);

        CREATE TRIGGER TR_ProjectLinks_Immutable
        BEFORE UPDATE OF Id, ProjectNodeId, CreatedAt ON ProjectLinks
        WHEN NEW.Id IS NOT OLD.Id
          OR NEW.ProjectNodeId IS NOT OLD.ProjectNodeId
          OR NEW.CreatedAt IS NOT OLD.CreatedAt
        BEGIN
            SELECT RAISE(ABORT, 'WH_CONTENT_IMMUTABLE');
        END;
        """;
}
