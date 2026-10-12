using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Project content Trash (soft removal). Nothing is deleted and nothing touches files or folders on disk.
/// <list type="bullet">
/// <item><c>ProjectTrashEntries</c> records each Move-to-Trash action (Project, root kind and Id, item count, time). An entry
/// is inserted by Trash only for an existing ACTIVE root of its kind in the same Project, is never updated, and is deleted
/// by a successful Restore — which the RESTRICT foreign keys allow only after every marker of the entry was cleared.</item>
/// <item><c>TrashEntryId</c> on ProjectFolders, ProjectLinks and ProjectNotes: NULL = active. Rows are always inserted
/// active; a marker changes only from NULL to an entry of the row's own Project, or from an entry back to NULL (so nested
/// entries stay independent). Trashed rows are read-only except their marker, and no active row can be created in or
/// moved into a trashed folder.</item>
/// <item>Folder-name uniqueness applies to ACTIVE sibling folders only: the V004 indexes are replaced by partial indexes
/// (V004 itself is not edited), so trashed folders reserve no names.</item>
/// </list>
/// What SQL cannot express — the root is marked with its own entry, the marked set is exactly the root plus its active
/// subtree, <c>ItemCount</c> equals the rows marked — is the store's job, in one <c>BEGIN IMMEDIATE</c> transaction.
/// The SQL is the approved Phase 7 proposal revision 2 §5.1 DDL, unchanged. Released migrations are frozen: never edit
/// this file once released, add V007+ instead.
/// </summary>
internal sealed class V006_ProjectContentTrash : ISchemaMigration
{
    public int Version => 6;

    public string Description => "Project content trash";

    /// <remarks>
    /// Executed with <see cref="SqliteExtensions.ExecuteScript"/>, not <c>Execute</c>: this script alters STRICT tables, and
    /// only stepping through every result guarantees that a failing later statement throws (and the initializer rolls the
    /// whole migration back) instead of silently ending the script.
    /// </remarks>
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.ExecuteScript(Sql, transaction);

    internal const string Sql = """
        -- V006_ProjectContentTrash
        -- Soft removal of Project content. Nothing here deletes user content or touches files on disk.
        -- Released migrations are frozen: V001-V005 are never edited.

        CREATE TABLE ProjectTrashEntries (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            ProjectNodeId INTEGER NOT NULL
                                  CONSTRAINT FK_ProjectTrashEntries_Project
                                  REFERENCES Projects (NodeId) ON DELETE RESTRICT,
            RootKind      TEXT    NOT NULL
                                  CONSTRAINT CK_ProjectTrashEntries_RootKind
                                  CHECK (RootKind IN ('Folder', 'Link', 'Note')),
            RootId        INTEGER NOT NULL,
            ItemCount     INTEGER NOT NULL
                                  CONSTRAINT CK_ProjectTrashEntries_ItemCount
                                  CHECK (ItemCount >= 1),
            TrashedAt     TEXT    NOT NULL,
            CONSTRAINT UQ_ProjectTrashEntries_Root UNIQUE (RootKind, RootId)
        ) STRICT;

        CREATE INDEX IX_ProjectTrashEntries_Project
            ON ProjectTrashEntries (ProjectNodeId, TrashedAt);

        -- An entry is created only for an existing, ACTIVE root row of its kind in the SAME Project.
        CREATE TRIGGER TR_ProjectTrashEntries_ValidRoot
        BEFORE INSERT ON ProjectTrashEntries
        WHEN NOT (
               (NEW.RootKind = 'Folder' AND EXISTS (SELECT 1 FROM ProjectFolders WHERE Id = NEW.RootId AND ProjectNodeId = NEW.ProjectNodeId AND TrashEntryId IS NULL))
            OR (NEW.RootKind = 'Link'   AND EXISTS (SELECT 1 FROM ProjectLinks   WHERE Id = NEW.RootId AND ProjectNodeId = NEW.ProjectNodeId AND TrashEntryId IS NULL))
            OR (NEW.RootKind = 'Note'   AND EXISTS (SELECT 1 FROM ProjectNotes   WHERE Id = NEW.RootId AND ProjectNodeId = NEW.ProjectNodeId AND TrashEntryId IS NULL)))
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASH_ROOT');
        END;

        -- Entry metadata is immutable for the entry's whole lifetime (inserted by Trash, deleted by Restore).
        CREATE TRIGGER TR_ProjectTrashEntries_Immutable
        BEFORE UPDATE ON ProjectTrashEntries
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASH_ENTRY_IMMUTABLE');
        END;

        -- NULL = active. A column added by ALTER TABLE may reference another table when its default is NULL.
        ALTER TABLE ProjectFolders ADD COLUMN TrashEntryId INTEGER NULL
            CONSTRAINT FK_ProjectFolders_TrashEntry REFERENCES ProjectTrashEntries (Id) ON DELETE RESTRICT;
        ALTER TABLE ProjectLinks ADD COLUMN TrashEntryId INTEGER NULL
            CONSTRAINT FK_ProjectLinks_TrashEntry REFERENCES ProjectTrashEntries (Id) ON DELETE RESTRICT;
        ALTER TABLE ProjectNotes ADD COLUMN TrashEntryId INTEGER NULL
            CONSTRAINT FK_ProjectNotes_TrashEntry REFERENCES ProjectTrashEntries (Id) ON DELETE RESTRICT;

        CREATE INDEX IX_ProjectFolders_TrashEntry ON ProjectFolders (TrashEntryId) WHERE TrashEntryId IS NOT NULL;
        CREATE INDEX IX_ProjectLinks_TrashEntry   ON ProjectLinks   (TrashEntryId) WHERE TrashEntryId IS NOT NULL;
        CREATE INDEX IX_ProjectNotes_TrashEntry   ON ProjectNotes   (TrashEntryId) WHERE TrashEntryId IS NOT NULL;

        -- T4: trashed folders do not reserve sibling names. The V004 indexes are replaced (V004 itself is not edited);
        -- the store maps unique violations by error code, not by index name.
        DROP INDEX UX_ProjectFolders_RootName;
        DROP INDEX UX_ProjectFolders_ChildName;

        CREATE UNIQUE INDEX UX_ProjectFolders_ActiveRootName
            ON ProjectFolders (ProjectNodeId, NameKey)
            WHERE ParentFolderId IS NULL AND TrashEntryId IS NULL;

        CREATE UNIQUE INDEX UX_ProjectFolders_ActiveChildName
            ON ProjectFolders (ParentFolderId, NameKey)
            WHERE ParentFolderId IS NOT NULL AND TrashEntryId IS NULL;

        -- Rows are always inserted ACTIVE (this also rules out a cross-Project marker on INSERT).
        CREATE TRIGGER TR_ProjectFolders_InsertActive
        BEFORE INSERT ON ProjectFolders
        WHEN NEW.TrashEntryId IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASH_INSERT');
        END;

        CREATE TRIGGER TR_ProjectLinks_InsertActive
        BEFORE INSERT ON ProjectLinks
        WHEN NEW.TrashEntryId IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASH_INSERT');
        END;

        CREATE TRIGGER TR_ProjectNotes_InsertActive
        BEFORE INSERT ON ProjectNotes
        WHEN NEW.TrashEntryId IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASH_INSERT');
        END;

        -- A marker changes only NULL -> entry of the SAME Project, or entry -> NULL (never entry -> another entry, which keeps
        -- nested entries independent). ProjectNodeId of content rows is already immutable (V004/V005 triggers).
        CREATE TRIGGER TR_ProjectFolders_MarkerTransition
        BEFORE UPDATE OF TrashEntryId ON ProjectFolders
        WHEN NEW.TrashEntryId IS NOT NULL
         AND (OLD.TrashEntryId IS NOT NULL
              OR NOT EXISTS (SELECT 1 FROM ProjectTrashEntries WHERE Id = NEW.TrashEntryId AND ProjectNodeId = NEW.ProjectNodeId))
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASH_MARKER');
        END;

        CREATE TRIGGER TR_ProjectLinks_MarkerTransition
        BEFORE UPDATE OF TrashEntryId ON ProjectLinks
        WHEN NEW.TrashEntryId IS NOT NULL
         AND (OLD.TrashEntryId IS NOT NULL
              OR NOT EXISTS (SELECT 1 FROM ProjectTrashEntries WHERE Id = NEW.TrashEntryId AND ProjectNodeId = NEW.ProjectNodeId))
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASH_MARKER');
        END;

        CREATE TRIGGER TR_ProjectNotes_MarkerTransition
        BEFORE UPDATE OF TrashEntryId ON ProjectNotes
        WHEN NEW.TrashEntryId IS NOT NULL
         AND (OLD.TrashEntryId IS NOT NULL
              OR NOT EXISTS (SELECT 1 FROM ProjectTrashEntries WHERE Id = NEW.TrashEntryId AND ProjectNodeId = NEW.ProjectNodeId))
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASH_MARKER');
        END;

        -- Trashed rows are read-only: only TrashEntryId may change (restore).
        CREATE TRIGGER TR_ProjectFolders_TrashedReadOnly
        BEFORE UPDATE OF Name, NameKey, ParentFolderId, UpdatedAt ON ProjectFolders
        WHEN OLD.TrashEntryId IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectLinks_TrashedReadOnly
        BEFORE UPDATE OF LinkType, Name, Target, FolderId, UpdatedAt ON ProjectLinks
        WHEN OLD.TrashEntryId IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectNotes_TrashedReadOnly
        BEFORE UPDATE OF Title, Content, Revision, FolderId, UpdatedAt ON ProjectNotes
        WHEN OLD.TrashEntryId IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'WH_TRASHED');
        END;

        -- No active row may be created in, or moved into, a trashed folder.
        CREATE TRIGGER TR_ProjectFolders_NoActiveChildOfTrashedInsert
        BEFORE INSERT ON ProjectFolders
        WHEN NEW.ParentFolderId IS NOT NULL
         AND EXISTS (SELECT 1 FROM ProjectFolders WHERE Id = NEW.ParentFolderId AND TrashEntryId IS NOT NULL)
        BEGIN
            SELECT RAISE(ABORT, 'WH_CONTAINER_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectFolders_NoActiveChildOfTrashedMove
        BEFORE UPDATE OF ParentFolderId ON ProjectFolders
        WHEN NEW.ParentFolderId IS NOT NULL AND NEW.TrashEntryId IS NULL
         AND EXISTS (SELECT 1 FROM ProjectFolders WHERE Id = NEW.ParentFolderId AND TrashEntryId IS NOT NULL)
        BEGIN
            SELECT RAISE(ABORT, 'WH_CONTAINER_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectLinks_NoActiveInTrashedInsert
        BEFORE INSERT ON ProjectLinks
        WHEN NEW.FolderId IS NOT NULL
         AND EXISTS (SELECT 1 FROM ProjectFolders WHERE Id = NEW.FolderId AND TrashEntryId IS NOT NULL)
        BEGIN
            SELECT RAISE(ABORT, 'WH_CONTAINER_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectLinks_NoActiveInTrashedMove
        BEFORE UPDATE OF FolderId ON ProjectLinks
        WHEN NEW.FolderId IS NOT NULL AND NEW.TrashEntryId IS NULL
         AND EXISTS (SELECT 1 FROM ProjectFolders WHERE Id = NEW.FolderId AND TrashEntryId IS NOT NULL)
        BEGIN
            SELECT RAISE(ABORT, 'WH_CONTAINER_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectNotes_NoActiveInTrashedInsert
        BEFORE INSERT ON ProjectNotes
        WHEN NEW.FolderId IS NOT NULL
         AND EXISTS (SELECT 1 FROM ProjectFolders WHERE Id = NEW.FolderId AND TrashEntryId IS NOT NULL)
        BEGIN
            SELECT RAISE(ABORT, 'WH_CONTAINER_TRASHED');
        END;

        CREATE TRIGGER TR_ProjectNotes_NoActiveInTrashedMove
        BEFORE UPDATE OF FolderId ON ProjectNotes
        WHEN NEW.FolderId IS NOT NULL AND NEW.TrashEntryId IS NULL
         AND EXISTS (SELECT 1 FROM ProjectFolders WHERE Id = NEW.FolderId AND TrashEntryId IS NOT NULL)
        BEGIN
            SELECT RAISE(ABORT, 'WH_CONTAINER_TRASHED');
        END;
        """;
}
