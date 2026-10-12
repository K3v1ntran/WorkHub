using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Workspace Trash for Projects (soft removal). Nothing is deleted except, through the application, a truly empty Workspace
/// Folder, and nothing touches files or folders on disk.
/// <list type="bullet">
/// <item><c>WorkspaceNodes.TrashedAt</c>: NULL = active. Only Project nodes can carry it; nodes are always inserted active;
/// Trash and Restore change only the marker, so the Project keeps its original parent, name, order and UpdatedAt.</item>
/// <item>Sibling-name uniqueness applies to ACTIVE nodes only: the V003 name indexes are replaced by partial indexes (V003
/// itself is not edited), so trashed Projects reserve no names.</item>
/// <item>A trashed Project is frozen: its node, details, content and Project-level (Phase 7) Trash entries are read-only.</item>
/// <item>Deletion: a Workspace node can be deleted only if it is a Folder with no child row (active or trashed); Project
/// nodes and <c>Projects</c> rows can never be deleted, which leaves the V003 Project-details cascade unreachable.</item>
/// </list>
/// FK <c>ON DELETE RESTRICT</c> (V003-V006) protects only referenced parent rows; leaf content of an ACTIVE Project is not
/// protected against a raw SQL delete — the application simply has no content-delete operation.
/// The SQL is the approved Phase 8 proposal revision 2 §6.1 DDL, unchanged. Released migrations are frozen: never edit
/// this file once released, add V008+ instead.
/// </summary>
internal sealed class V007_WorkspaceProjectTrash : ISchemaMigration
{
    public int Version => 7;

    public string Description => "Workspace project trash";

    /// <remarks>
    /// Executed with <see cref="SqliteExtensions.ExecuteScript"/>, not <c>Execute</c>: this script alters a STRICT table (see
    /// <see cref="V006_ProjectContentTrash"/>).
    /// </remarks>
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.ExecuteScript(Sql, transaction);

    internal const string Sql = """
        -- V007_WorkspaceProjectTrash
        -- Workspace Trash for Projects: one marker column, active-only sibling names, a frozen trashed Project (node, details,
        -- content and Project-level Trash entries), and deletion limited to empty Workspace Folders.
        -- No rows are inserted, updated or deleted. Released migrations V001-V006 are frozen; this file is frozen once released.
        -- Applied with ExecuteScript: ALTER TABLE on a STRICT table.

        -- 1. Marker. Only Project nodes can be in the Trash.
        ALTER TABLE WorkspaceNodes ADD COLUMN TrashedAt TEXT NULL
            CONSTRAINT CK_WorkspaceNodes_TrashedAt
            CHECK (TrashedAt IS NULL OR (NodeType = 'Project' AND length(TrashedAt) > 0));

        -- 2. Sibling names are unique among ACTIVE nodes only (Folders and Projects still share one namespace).
        DROP INDEX UX_WorkspaceNodes_RootName;
        DROP INDEX UX_WorkspaceNodes_ChildName;

        CREATE UNIQUE INDEX UX_WorkspaceNodes_ActiveRootName
            ON WorkspaceNodes (NameKey)
            WHERE ParentId IS NULL AND TrashedAt IS NULL;

        CREATE UNIQUE INDEX UX_WorkspaceNodes_ActiveChildName
            ON WorkspaceNodes (ParentId, NameKey)
            WHERE ParentId IS NOT NULL AND TrashedAt IS NULL;

        -- Workspace Trash listing (newest first) and its count.
        CREATE INDEX IX_WorkspaceNodes_Trashed
            ON WorkspaceNodes (TrashedAt)
            WHERE TrashedAt IS NOT NULL;

        -- 3. Nodes are created active.
        CREATE TRIGGER TR_WorkspaceNodes_InsertActive
        BEFORE INSERT ON WorkspaceNodes
        WHEN NEW.TrashedAt IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_NODE_INSERT_TRASHED');
        END;

        -- 4. Trash and Restore change ONLY the marker (parent, name, order and UpdatedAt are preserved).
        CREATE TRIGGER TR_WorkspaceNodes_MarkerTransition
        BEFORE UPDATE ON WorkspaceNodes
        WHEN (OLD.TrashedAt IS NULL) <> (NEW.TrashedAt IS NULL)
         AND (NEW.ParentId IS NOT OLD.ParentId
              OR NEW.Name IS NOT OLD.Name
              OR NEW.NameKey IS NOT OLD.NameKey
              OR NEW.SortOrder IS NOT OLD.SortOrder
              OR NEW.UpdatedAt IS NOT OLD.UpdatedAt)
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASH_MARKER_ONLY');
        END;

        -- 5. A trashed node is read-only; only Restore (marker -> NULL) may change it.
        CREATE TRIGGER TR_WorkspaceNodes_TrashedReadOnly
        BEFORE UPDATE ON WorkspaceNodes
        WHEN OLD.TrashedAt IS NOT NULL AND NEW.TrashedAt IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        -- 6. Deletion: only an empty Workspace Folder (no child row at all, active or trashed). Projects are never deleted.
        CREATE TRIGGER TR_WorkspaceNodes_DeleteOnlyEmptyFolder
        BEFORE DELETE ON WorkspaceNodes
        WHEN OLD.NodeType IS NOT 'Folder'
          OR EXISTS (SELECT 1 FROM WorkspaceNodes WHERE ParentId = OLD.Id)
        BEGIN
            SELECT RAISE(ABORT, 'WH_NODE_DELETE_FORBIDDEN');
        END;

        CREATE TRIGGER TR_Projects_NoDelete
        BEFORE DELETE ON Projects
        BEGIN
            SELECT RAISE(ABORT, 'WH_NODE_DELETE_FORBIDDEN');
        END;

        -- 7. A trashed Project is frozen: details, content and Project-level Trash entries.
        CREATE TRIGGER TR_Projects_TrashedReadOnly
        BEFORE UPDATE ON Projects
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.NodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectFolders_ProjectActive_Insert
        BEFORE INSERT ON ProjectFolders
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = NEW.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectFolders_ProjectActive_Update
        BEFORE UPDATE ON ProjectFolders
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectFolders_ProjectActive_Delete
        BEFORE DELETE ON ProjectFolders
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectLinks_ProjectActive_Insert
        BEFORE INSERT ON ProjectLinks
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = NEW.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectLinks_ProjectActive_Update
        BEFORE UPDATE ON ProjectLinks
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectLinks_ProjectActive_Delete
        BEFORE DELETE ON ProjectLinks
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectNotes_ProjectActive_Insert
        BEFORE INSERT ON ProjectNotes
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = NEW.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectNotes_ProjectActive_Update
        BEFORE UPDATE ON ProjectNotes
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectNotes_ProjectActive_Delete
        BEFORE DELETE ON ProjectNotes
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectTrashEntries_ProjectActive_Insert
        BEFORE INSERT ON ProjectTrashEntries
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = NEW.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectTrashEntries_ProjectActive_Update
        BEFORE UPDATE ON ProjectTrashEntries
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectTrashEntries_ProjectActive_Delete
        BEFORE DELETE ON ProjectTrashEntries
        WHEN (SELECT TrashedAt FROM WorkspaceNodes WHERE Id = OLD.ProjectNodeId) IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_PROJECT_TRASHED');
        END;
        """;
}
