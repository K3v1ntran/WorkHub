using Microsoft.Data.Sqlite;
using WorkHub.Core.LegacyImport;
using WorkHub.Infrastructure.LegacyImport;

namespace WorkHub.Infrastructure.Persistence;

/// <summary>
/// The approved initial System master data (42 SYSTEM + 33 ORACLE entries: SystemCode, SystemName and section only),
/// embedded in this assembly so a new environment never depends on a file on disk or the network.
/// It is loaded only into a NEW database (see <see cref="DatabaseInitializer"/>), with the same parser protections,
/// planning rules and insert routine as the manual legacy import: SYSTEM joins SVN SYSTEM, GIT SYSTEM and RELEASE;
/// ORACLE joins SVN ORACLE and GIT ORACLE; memberships are enabled with no custom target.
/// </summary>
public static class InitialSystemsSeed
{
    public const string ResourceName = "WorkHub.Infrastructure.Seed.InitialSystems.xml";

    /// <summary>Parses the embedded data (no database access).</summary>
    public static LegacyParseResult Read()
    {
        var stream = typeof(InitialSystemsSeed).Assembly.GetManifestResourceStream(ResourceName)
                     ?? throw new InvalidDataException($"The initial data resource '{ResourceName}' is missing from the application.");
        return new LegacyXmlParser().Parse(stream);
    }

    /// <summary>
    /// Inserts the initial Systems inside the caller's transaction (the caller commits, or rolls back on any exception).
    /// The data must apply completely and cleanly to an empty database; anything else is an error, never a partial seed.
    /// </summary>
    /// <returns>A one-line summary for the log.</returns>
    public static string Apply(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset utcNow)
    {
        var parse = Read();
        if (!parse.IsValid)
            throw new InvalidDataException($"The initial data could not be read: {parse.Error}");

        var plan = SqliteLegacyImportStore.Apply(connection, transaction, parse.Entries, utcNow);
        if (plan.NewSystems == 0 || plan.ExistingSystems != 0 || plan.Conflicts.Count != 0 || plan.InvalidEntries.Count != 0)
        {
            throw new InvalidDataException(
                $"The initial data did not apply cleanly ({plan.NewSystems} new, {plan.ExistingSystems} existing, " +
                $"{plan.Conflicts.Count} conflicts, {plan.InvalidEntries.Count} invalid).");
        }

        return $"Loaded initial Systems: {plan.NewSystems} systems, {plan.NewMemberships} memberships " +
               $"({plan.SystemEntries} SYSTEM + {plan.OracleEntries} ORACLE entries).";
    }
}
