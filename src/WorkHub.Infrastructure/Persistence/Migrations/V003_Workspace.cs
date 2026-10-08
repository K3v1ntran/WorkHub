using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Workspace foundation: the logical Workspace tree (<c>WorkspaceNodes</c>: Folders and Projects) and the 1:1
/// Project details (<c>Projects</c>). No rows are inserted, and nothing refers to filesystem paths.
/// <list type="bullet">
/// <item>Folders and Projects share one sibling namespace. <c>NameKey</c> (NFKC + invariant upper case) is computed by
/// the application; root and child uniqueness are separate partial unique indexes, so no sentinel replaces NULL.</item>
/// <item>Triggers enforce "parent is a Folder", node identity/type immutability, details only on Project nodes and an
/// immutable <c>ProjectId</c>. Cycle prevention for moves needs a recursive query, which SQLite does not allow in
/// triggers; it runs in the store's move transaction.</item>
/// <item><c>AUTOINCREMENT</c> keeps node ids unique forever (future restore refers to the original parent).</item>
/// </list>
/// The SQL is the reviewed V003 DDL, unchanged. Released migrations are frozen: never edit this file, add V004+ instead.
/// </summary>
internal sealed class V003_Workspace : ISchemaMigration
{
    public int Version => 3;

    public string Description => "Workspace folders and projects";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.Execute(Sql, transaction);

    internal const string Sql = """
        -- V003_Workspace
        -- Logical Workspace tree (Workspace Folders and Projects) plus Project details.
        -- No rows are inserted. Nothing here stores or refers to filesystem paths.
        -- Released migrations are frozen: V001/V002 are never edited; this file is frozen once released.

        CREATE TABLE WorkspaceNodes (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            ParentId  INTEGER NULL
                              CONSTRAINT FK_WorkspaceNodes_Parent
                              REFERENCES WorkspaceNodes (Id) ON DELETE RESTRICT,
            NodeType  TEXT    NOT NULL
                              CONSTRAINT CK_WorkspaceNodes_NodeType
                              CHECK (NodeType IN ('Folder', 'Project')),
            Name      TEXT    NOT NULL
                              CONSTRAINT CK_WorkspaceNodes_Name
                              CHECK (length(Name) BETWEEN 1 AND 200 AND Name = trim(Name)),
            NameKey   TEXT    NOT NULL
                              CONSTRAINT CK_WorkspaceNodes_NameKey
                              CHECK (length(NameKey) > 0),
            SortOrder INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT    NOT NULL,
            UpdatedAt TEXT    NOT NULL,
            CONSTRAINT CK_WorkspaceNodes_NotOwnParent CHECK (ParentId IS NULL OR ParentId <> Id)
        ) STRICT;

        -- Folders and Projects share one sibling namespace (NodeType is deliberately not part of either key).
        -- Root level and child level use separate partial unique indexes; no sentinel replaces NULL.
        CREATE UNIQUE INDEX UX_WorkspaceNodes_RootName
            ON WorkspaceNodes (NameKey)
            WHERE ParentId IS NULL;

        CREATE UNIQUE INDEX UX_WorkspaceNodes_ChildName
            ON WorkspaceNodes (ParentId, NameKey)
            WHERE ParentId IS NOT NULL;

        -- Child listing, HasChildren checks and the ParentId foreign key.
        CREATE INDEX IX_WorkspaceNodes_ParentId
            ON WorkspaceNodes (ParentId, NodeType, SortOrder);

        -- A parent must be an existing Folder (NULL = root). Project nodes never have main-tree children.
        CREATE TRIGGER TR_WorkspaceNodes_ParentIsFolder_Insert
        BEFORE INSERT ON WorkspaceNodes
        WHEN NEW.ParentId IS NOT NULL
         AND (SELECT NodeType FROM WorkspaceNodes WHERE Id = NEW.ParentId) IS NOT 'Folder'
        BEGIN
            SELECT RAISE(ABORT, 'WH_PARENT_NOT_FOLDER');
        END;

        CREATE TRIGGER TR_WorkspaceNodes_ParentIsFolder_Update
        BEFORE UPDATE OF ParentId ON WorkspaceNodes
        WHEN NEW.ParentId IS NOT NULL
         AND (SELECT NodeType FROM WorkspaceNodes WHERE Id = NEW.ParentId) IS NOT 'Folder'
        BEGIN
            SELECT RAISE(ABORT, 'WH_PARENT_NOT_FOLDER');
        END;

        -- Node identity, kind and creation time never change.
        CREATE TRIGGER TR_WorkspaceNodes_Immutable
        BEFORE UPDATE OF Id, NodeType, CreatedAt ON WorkspaceNodes
        WHEN NEW.Id IS NOT OLD.Id
          OR NEW.NodeType IS NOT OLD.NodeType
          OR NEW.CreatedAt IS NOT OLD.CreatedAt
        BEGIN
            SELECT RAISE(ABORT, 'WH_NODE_IMMUTABLE');
        END;

        -- Project details: one row per Project node, keyed by the node (1:1).
        -- Name and timestamps live on the node only.
        CREATE TABLE Projects (
            NodeId         INTEGER PRIMARY KEY
                                   CONSTRAINT FK_Projects_Node
                                   REFERENCES WorkspaceNodes (Id) ON DELETE CASCADE,
            ProjectId      TEXT    NOT NULL
                                   CONSTRAINT UQ_Projects_ProjectId UNIQUE
                                   CONSTRAINT CK_Projects_ProjectId
                                   CHECK (length(ProjectId) = 32 AND ProjectId NOT GLOB '*[^0-9a-f]*'),
            Status         TEXT    NOT NULL DEFAULT 'Active'
                                   CONSTRAINT CK_Projects_Status
                                   CHECK (Status IN ('Planning', 'Active', 'Maintenance', 'OnHold', 'Completed', 'Archived')),
            Description    TEXT    NULL
                                   CONSTRAINT CK_Projects_Description
                                   CHECK (Description IS NULL OR length(Description) BETWEEN 1 AND 4000),
            Owner          TEXT    NULL
                                   CONSTRAINT CK_Projects_Owner
                                   CHECK (Owner IS NULL OR (length(Owner) BETWEEN 1 AND 100 AND Owner = trim(Owner))),
            ProjectManager TEXT    NULL
                                   CONSTRAINT CK_Projects_ProjectManager
                                   CHECK (ProjectManager IS NULL
                                          OR (length(ProjectManager) BETWEEN 1 AND 100 AND ProjectManager = trim(ProjectManager)))
        ) STRICT;

        -- Details rows attach only to Project nodes.
        CREATE TRIGGER TR_Projects_NodeIsProject
        BEFORE INSERT ON Projects
        WHEN (SELECT NodeType FROM WorkspaceNodes WHERE Id = NEW.NodeId) IS NOT 'Project'
        BEGIN
            SELECT RAISE(ABORT, 'WH_NOT_A_PROJECT_NODE');
        END;

        -- The internal ProjectId and the node link are immutable.
        CREATE TRIGGER TR_Projects_Immutable
        BEFORE UPDATE OF NodeId, ProjectId ON Projects
        WHEN NEW.NodeId IS NOT OLD.NodeId
          OR NEW.ProjectId IS NOT OLD.ProjectId
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECTID_IMMUTABLE');
        END;
        """;
}
