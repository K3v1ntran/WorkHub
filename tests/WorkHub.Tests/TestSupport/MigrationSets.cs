using WorkHub.Infrastructure.Persistence.Migrations;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Historical migration sets. Schema tests written for an older version run against exactly the migrations they were
/// written for, so later migrations (for example V007's delete guards and replaced name indexes) do not change what they
/// verify. Latest-schema behavior is covered by the tests of the newest migration.
/// </summary>
internal static class MigrationSets
{
    public static ISchemaMigration[] ThroughV006 =>
    [
        new V001_InitialSchema(), new V002_OwnerManager(), new V003_Workspace(),
        new V004_ProjectContent(), new V005_ProjectNotes(), new V006_ProjectContentTrash(),
    ];

    /// <summary>The schema v7 migrations (before V008's archive serial).</summary>
    public static ISchemaMigration[] ThroughV007 => [.. ThroughV006, new V007_WorkspaceProjectTrash()];

    /// <summary>The schema v8 migrations (before V009's folder lifecycle).</summary>
    public static ISchemaMigration[] ThroughV008 => [.. ThroughV007, new V008_ProjectArchiveSerial()];
}
