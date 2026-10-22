using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Workspace Folder Archive and Folder Trash (Phase 13 proposal revision 4, §6.2-§6.3; decisions D8-D10). The lifecycle state
/// is a marker on the Folder that roots the subtree (M1); descendant rows are never written by these operations, so every
/// nested Folder and Project keeps its own markers, status, <c>ArchiveSerial</c> and <c>TrashedAt</c>.
/// <list type="bullet">
/// <item><c>WorkspaceNodes.FolderArchivedAt</c> / <c>FolderTrashedAt</c>: Folder rows only, never both. Existing rows get NULL.</item>
/// <item><c>WorkspaceNodes.LifecycleSerial</c>: the folder lifecycle-event identity (0 for every existing row and every Project).
/// Entering Archived or Trashed from live raises it by exactly one (the application's statement assigns it; the trigger only
/// validates); clearing a marker leaves it unchanged. Undo and the confirmation token compare it.</item>
/// <item>Sibling names: a trashed Folder frees its name (as a trashed Project does); an archived Folder keeps it reserved. The V007
/// active-name indexes are replaced (V007 itself is not edited).</item>
/// <item>The database guards the MARKED row itself (read-only except clearing its marker, never deleted) and its DIRECT children
/// (nothing inserted or moved into it). Deeper descendants are enforced by the store inside each write transaction (D9).</item>
/// </list>
/// The SQL is the reviewed revision 4 §6.3 DDL, unchanged. Released migrations are frozen: never edit this file once released,
/// add V010+ instead.
/// </summary>
internal sealed class V009_WorkspaceFolderLifecycle : ISchemaMigration
{
    public int Version => 9;

    public string Description => "Workspace folder lifecycle";

    /// <remarks>
    /// Executed with <see cref="SqliteExtensions.ExecuteScript"/>, not <c>Execute</c>: this script alters a STRICT table (see
    /// <see cref="V006_ProjectContentTrash"/>).
    /// </remarks>
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.ExecuteScript(Sql, transaction);

    internal const string Sql = """
        -- V009_WorkspaceFolderLifecycle
        -- Folder Archive and Folder Trash as markers on the Folder that roots the subtree. Descendant rows are never written by
        -- these operations: each Project keeps its own Status, ArchiveSerial and TrashedAt. No rows are inserted, updated or
        -- deleted. Released migrations V001-V008 are frozen; this file is frozen once released.
        -- Applied with ExecuteScript: ALTER TABLE on a STRICT table.

        -- 1. Markers (Folder nodes only) and the folder lifecycle-event serial.
        ALTER TABLE WorkspaceNodes ADD COLUMN FolderArchivedAt TEXT NULL
            CONSTRAINT CK_WorkspaceNodes_FolderArchivedAt
            CHECK (FolderArchivedAt IS NULL OR (NodeType = 'Folder' AND length(FolderArchivedAt) > 0));

        ALTER TABLE WorkspaceNodes ADD COLUMN FolderTrashedAt TEXT NULL
            CONSTRAINT CK_WorkspaceNodes_FolderTrashedAt
            CHECK (FolderTrashedAt IS NULL OR (NodeType = 'Folder' AND length(FolderTrashedAt) > 0));

        ALTER TABLE WorkspaceNodes ADD COLUMN LifecycleSerial INTEGER NOT NULL DEFAULT 0
            CONSTRAINT CK_WorkspaceNodes_LifecycleSerial
            CHECK (LifecycleSerial >= 0 AND (NodeType = 'Folder' OR LifecycleSerial = 0));

        -- 2. Sibling names: a trashed Folder frees its name (like a trashed Project); an archived Folder keeps it reserved.
        DROP INDEX UX_WorkspaceNodes_ActiveRootName;
        DROP INDEX UX_WorkspaceNodes_ActiveChildName;

        CREATE UNIQUE INDEX UX_WorkspaceNodes_LiveRootName
            ON WorkspaceNodes (NameKey)
            WHERE ParentId IS NULL AND TrashedAt IS NULL AND FolderTrashedAt IS NULL;

        CREATE UNIQUE INDEX UX_WorkspaceNodes_LiveChildName
            ON WorkspaceNodes (ParentId, NameKey)
            WHERE ParentId IS NOT NULL AND TrashedAt IS NULL AND FolderTrashedAt IS NULL;

        -- 3. Panel listings and counts.
        CREATE INDEX IX_WorkspaceNodes_FolderArchived
            ON WorkspaceNodes (Id)
            WHERE FolderArchivedAt IS NOT NULL;

        CREATE INDEX IX_WorkspaceNodes_FolderTrashed
            ON WorkspaceNodes (FolderTrashedAt)
            WHERE FolderTrashedAt IS NOT NULL;

        -- 4. Nodes are created live, with serial 0.
        CREATE TRIGGER TR_WorkspaceNodes_InsertFolderLive
        BEFORE INSERT ON WorkspaceNodes
        WHEN NEW.FolderArchivedAt IS NOT NULL OR NEW.FolderTrashedAt IS NOT NULL OR NEW.LifecycleSerial IS NOT 0
        BEGIN
            SELECT RAISE(ABORT, 'WH_NODE_INSERT_LIFECYCLE');
        END;

        -- 5. A Folder is never archived and trashed at the same time.
        CREATE TRIGGER TR_WorkspaceNodes_FolderMarkersExclusive
        BEFORE UPDATE ON WorkspaceNodes
        WHEN NEW.FolderArchivedAt IS NOT NULL AND NEW.FolderTrashedAt IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_FOLDER_MARKERS_EXCLUSIVE');
        END;

        -- 6. A marker transition changes only the markers and the serial (parent, name, order, UpdatedAt are preserved).
        CREATE TRIGGER TR_WorkspaceNodes_FolderMarkerOnly
        BEFORE UPDATE ON WorkspaceNodes
        WHEN ((OLD.FolderArchivedAt IS NULL) <> (NEW.FolderArchivedAt IS NULL)
              OR (OLD.FolderTrashedAt IS NULL) <> (NEW.FolderTrashedAt IS NULL))
         AND (NEW.ParentId IS NOT OLD.ParentId
              OR NEW.Name IS NOT OLD.Name
              OR NEW.NameKey IS NOT OLD.NameKey
              OR NEW.SortOrder IS NOT OLD.SortOrder
              OR NEW.UpdatedAt IS NOT OLD.UpdatedAt)
        BEGIN
            SELECT RAISE(ABORT, 'WH_FOLDER_MARKER_ONLY');
        END;

        -- 7. Event serial: entering Archived or Trashed from live raises it by exactly one; every other update leaves it.
        CREATE TRIGGER TR_WorkspaceNodes_LifecycleSerialTransition
        BEFORE UPDATE ON WorkspaceNodes
        WHEN CASE
                 WHEN OLD.FolderArchivedAt IS NULL AND OLD.FolderTrashedAt IS NULL
                  AND (NEW.FolderArchivedAt IS NOT NULL OR NEW.FolderTrashedAt IS NOT NULL)
                     THEN NEW.LifecycleSerial IS NOT OLD.LifecycleSerial + 1
                 ELSE NEW.LifecycleSerial IS NOT OLD.LifecycleSerial
             END
        BEGIN
            SELECT RAISE(ABORT, 'WH_LIFECYCLE_SERIAL');
        END;

        -- 8. An archived or trashed Folder row is read-only except for clearing its own marker (no rename, move, archive<->trash).
        CREATE TRIGGER TR_WorkspaceNodes_FolderMarkedReadOnly
        BEFORE UPDATE ON WorkspaceNodes
        WHEN (OLD.FolderArchivedAt IS NOT NULL AND NEW.FolderArchivedAt IS NOT NULL)
          OR (OLD.FolderTrashedAt IS NOT NULL AND NEW.FolderTrashedAt IS NOT NULL)
          OR (OLD.FolderArchivedAt IS NOT NULL AND NEW.FolderTrashedAt IS NOT NULL)
          OR (OLD.FolderTrashedAt IS NOT NULL AND NEW.FolderArchivedAt IS NOT NULL)
        BEGIN
            SELECT RAISE(ABORT, 'WH_FOLDER_NOT_LIVE');
        END;

        -- 9. Nothing is created in, or moved into, a Folder that is itself archived or trashed. This guards the DIRECT parent
        --    only; it does not restrict updates to existing children or deeper descendants (the store checks those).
        CREATE TRIGGER TR_WorkspaceNodes_ParentLive_Insert
        BEFORE INSERT ON WorkspaceNodes
        WHEN NEW.ParentId IS NOT NULL
         AND (SELECT FolderArchivedAt IS NOT NULL OR FolderTrashedAt IS NOT NULL FROM WorkspaceNodes WHERE Id = NEW.ParentId)
        BEGIN
            SELECT RAISE(ABORT, 'WH_PARENT_NOT_LIVE');
        END;

        CREATE TRIGGER TR_WorkspaceNodes_ParentLive_Update
        BEFORE UPDATE OF ParentId ON WorkspaceNodes
        WHEN NEW.ParentId IS NOT NULL AND NEW.ParentId IS NOT OLD.ParentId
         AND (SELECT FolderArchivedAt IS NOT NULL OR FolderTrashedAt IS NOT NULL FROM WorkspaceNodes WHERE Id = NEW.ParentId)
        BEGIN
            SELECT RAISE(ABORT, 'WH_PARENT_NOT_LIVE');
        END;

        -- 10. A marked Folder is never deleted (even when empty); Remove applies to live empty Folders only.
        CREATE TRIGGER TR_WorkspaceNodes_DeleteOnlyLiveFolder
        BEFORE DELETE ON WorkspaceNodes
        WHEN OLD.FolderArchivedAt IS NOT NULL OR OLD.FolderTrashedAt IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_NODE_DELETE_FORBIDDEN');
        END;
        """;
}
