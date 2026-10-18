using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// Project Archive (Phase 12 proposal revision 3, package M). <c>Projects.Status = 'Archived'</c> stays the only archived-state
/// representation; this migration adds the archive-event identity and the archived listing index.
/// <list type="bullet">
/// <item><c>Projects.ArchiveSerial</c>: every existing Project gets 0. The application's Archive statement raises it by one
/// (<c>ArchiveSerial = ArchiveSerial + 1</c>); Unarchive and Undo leave it unchanged. Undo acts only on the archive event
/// whose serial it was given.</item>
/// <item><c>TR_Projects_ArchiveSerialTransition</c> VALIDATES the serial transition and never assigns a value: entering
/// 'Archived' must raise it by exactly one, every other update must leave it unchanged.</item>
/// <item><c>IX_Projects_Archived</c>: the Archived panel listing and its count. The new schema version is also the gate
/// that makes v7 builds started afterwards refuse the database; it cannot stop an old instance that is already running.</item>
/// </list>
/// The SQL is the reviewed revision 3 §5.1 DDL, unchanged. Released migrations are frozen: never edit this file once
/// released, add V009+ instead.
/// </summary>
internal sealed class V008_ProjectArchiveSerial : ISchemaMigration
{
    public int Version => 8;

    public string Description => "Project archive serial";

    /// <remarks>
    /// Executed with <see cref="SqliteExtensions.ExecuteScript"/>, not <c>Execute</c>: this script alters a STRICT table (see
    /// <see cref="V006_ProjectContentTrash"/>).
    /// </remarks>
    public void Apply(SqliteConnection connection, SqliteTransaction transaction) =>
        connection.ExecuteScript(Sql, transaction);

    internal const string Sql = """
        -- V008_ProjectArchiveSerial
        -- Project Archive: the archive-event serial, its transition guard and the archived-project index.
        -- Projects.Status = 'Archived' remains the only archived-state representation. No rows are inserted, updated or deleted;
        -- existing Projects get ArchiveSerial 0. Released migrations V001-V007 are frozen; this file is frozen once released.
        -- Applied with ExecuteScript: ALTER TABLE on a STRICT table.

        -- 1. Archive-event serial. The application's Archive statement raises it by one; nothing raises it automatically.
        ALTER TABLE Projects ADD COLUMN ArchiveSerial INTEGER NOT NULL DEFAULT 0
            CONSTRAINT CK_Projects_ArchiveSerial
            CHECK (ArchiveSerial >= 0);

        -- 2. Transition guard. It validates and never changes a value: an update that moves Status into 'Archived' must raise the
        --    serial by exactly one; every other update must leave it unchanged.
        CREATE TRIGGER TR_Projects_ArchiveSerialTransition
        BEFORE UPDATE ON Projects
        WHEN CASE
                 WHEN OLD.Status IS NOT 'Archived' AND NEW.Status IS 'Archived'
                     THEN NEW.ArchiveSerial IS NOT OLD.ArchiveSerial + 1
                 ELSE NEW.ArchiveSerial IS NOT OLD.ArchiveSerial
             END
        BEGIN
            SELECT RAISE(ABORT, 'WH_ARCHIVE_SERIAL');
        END;

        -- 3. Archived Projects: the Archived panel listing and its count.
        CREATE INDEX IX_Projects_Archived
            ON Projects (NodeId)
            WHERE Status = 'Archived';
        """;
}
