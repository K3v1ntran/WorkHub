using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Project content, part 2: plain-text Notes at the Project root or inside a ProjectFolder. No rows are inserted.
/// <list type="bullet">
/// <item>Same ownership model as V004: <c>ProjectNodeId</c> → <c>Projects(NodeId)</c>, and the composite FK
/// <c>(ProjectNodeId, FolderId)</c> → <c>ProjectFolders(ProjectNodeId, Id)</c> keeps a Note's folder in the same Project
/// (NULL = Project root). Every FK is <c>ON DELETE RESTRICT</c>.</item>
/// <item><c>Revision</c> is the content revision: 1 on insert, and any UPDATE that lists Title, Content or Revision must
/// set it to exactly <c>OLD.Revision + 1</c>. A Move (FolderId, UpdatedAt) does not fire the trigger. The lost-update
/// guard (<c>WHERE Revision = $expected</c>) is the store's job: SQL cannot know the caller's expected revision.</item>
/// </list>
/// The SQL is the reviewed Phase 5B proposal §3.1 DDL, unchanged. Released migrations are frozen: never edit this file,
/// add V006+ instead.
/// </summary>
internal sealed class V005_ProjectNotes : ISchemaMigration
{
    public int Version => 5;

    public string Description => "Project notes";

    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.Execute(Sql, transaction);

    internal const string Sql = """
        -- V005_ProjectNotes
        -- Content inside a Project, part 2: plain-text Notes at the Project root or inside a ProjectFolder.
        -- No rows are inserted. Nothing here touches files or folders on disk.
        -- Every FK is ON DELETE RESTRICT: no existing or future delete can silently remove a Note.
        -- Released migrations are frozen: V001-V004 are never edited; this file is frozen once released.

        CREATE TABLE ProjectNotes (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            ProjectNodeId INTEGER NOT NULL
                                  CONSTRAINT FK_ProjectNotes_Project
                                  REFERENCES Projects (NodeId) ON DELETE RESTRICT,
            FolderId      INTEGER NULL,
            Title         TEXT    NOT NULL
                                  CONSTRAINT CK_ProjectNotes_Title
                                  CHECK (length(Title) BETWEEN 1 AND 200 AND Title = trim(Title)),
            Content       TEXT    NOT NULL DEFAULT ''
                                  CONSTRAINT CK_ProjectNotes_Content
                                  CHECK (length(Content) <= 100000),
            -- Content revision: 1 on insert; +1 with every Title/Content save. Moves never change it.
            Revision      INTEGER NOT NULL DEFAULT 1
                                  CONSTRAINT CK_ProjectNotes_Revision
                                  CHECK (Revision >= 1),
            CreatedAt     TEXT    NOT NULL,
            UpdatedAt     TEXT    NOT NULL,
            -- The containing folder must belong to the SAME Project. Not checked when FolderId IS NULL (Project root).
            CONSTRAINT FK_ProjectNotes_Folder
                FOREIGN KEY (ProjectNodeId, FolderId)
                REFERENCES ProjectFolders (ProjectNodeId, Id) ON DELETE RESTRICT
        ) STRICT;

        -- Container listing, the Project FK lookup and the composite folder FK lookup.
        CREATE INDEX IX_ProjectNotes_Container
            ON ProjectNotes (ProjectNodeId, FolderId);

        -- Identity, owning Project and creation time never change (no cross-Project moves).
        CREATE TRIGGER TR_ProjectNotes_Immutable
        BEFORE UPDATE OF Id, ProjectNodeId, CreatedAt ON ProjectNotes
        WHEN NEW.Id IS NOT OLD.Id
          OR NEW.ProjectNodeId IS NOT OLD.ProjectNodeId
          OR NEW.CreatedAt IS NOT OLD.CreatedAt
        BEGIN
            SELECT RAISE(ABORT, 'WH_CONTENT_IMMUTABLE');
        END;

        -- A new Note starts at Revision 1.
        CREATE TRIGGER TR_ProjectNotes_RevisionStartsAtOne
        BEFORE INSERT ON ProjectNotes
        WHEN NEW.Revision IS NOT 1
        BEGIN
            SELECT RAISE(ABORT, 'WH_NOTE_REVISION');
        END;

        -- Any UPDATE statement that lists Title, Content or Revision must set Revision to exactly OLD.Revision + 1.
        -- Statements that list none of them (a Move: FolderId, UpdatedAt) do not fire this trigger.
        CREATE TRIGGER TR_ProjectNotes_RevisionForward
        BEFORE UPDATE OF Title, Content, Revision ON ProjectNotes
        WHEN NEW.Revision IS NOT OLD.Revision + 1
        BEGIN
            SELECT RAISE(ABORT, 'WH_NOTE_REVISION');
        END;
        """;
}
