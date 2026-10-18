using Microsoft.Data.Sqlite;

namespace WorkHub.Infrastructure.Persistence.Migrations;

/// <summary>
/// One forward-only schema step. Applied inside a transaction together with the
/// <c>PRAGMA user_version</c> bump, so a failed step leaves the database unchanged.
/// Released migrations are frozen: never edit one, add a new version instead.
/// </summary>
public interface ISchemaMigration
{
    /// <summary>Schema version this migration produces. Independent of the application version.</summary>
    int Version { get; }

    string Description { get; }

    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}

public static class SchemaMigrations
{
    public static IReadOnlyList<ISchemaMigration> All { get; } =
    [
        new V001_InitialSchema(),
        new V002_OwnerManager(),
        new V003_Workspace(),
        new V004_ProjectContent(),
        new V005_ProjectNotes(),
        new V006_ProjectContentTrash(),
        new V007_WorkspaceProjectTrash(),
        new V008_ProjectArchiveSerial(),
    ];

    public static int LatestVersion => All[^1].Version;
}
