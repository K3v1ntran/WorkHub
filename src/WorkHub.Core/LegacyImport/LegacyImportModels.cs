using WorkHub.Core.Repositories;

namespace WorkHub.Core.LegacyImport;

/// <summary>Sections of the legacy WorkHub XML (<c>HubData/SYSTEM</c>, <c>HubData/ORACLE</c>).</summary>
public enum LegacySection
{
    System,
    Oracle,
}

/// <summary>The approved mapping from legacy sections to current repository memberships.</summary>
public static class LegacyMapping
{
    private static readonly RepositoryType[] SystemTypes = [RepositoryType.SvnSystem, RepositoryType.GitSystem, RepositoryType.Release];
    private static readonly RepositoryType[] OracleTypes = [RepositoryType.SvnOracle, RepositoryType.GitOracle];

    public static IReadOnlyList<RepositoryType> TypesFor(LegacySection section) =>
        section == LegacySection.System ? SystemTypes : OracleTypes;

    public static string XmlName(LegacySection section) => section == LegacySection.System ? "SYSTEM" : "ORACLE";
}

/// <summary>One raw <c>Item</c> as read from the file (untrimmed, possibly invalid).</summary>
public sealed record LegacyEntry(LegacySection Section, string? SystemCode, string? SystemName, int? LineNumber = null);

/// <summary>Result of reading a legacy file. Either <see cref="Error"/> is set or <see cref="Entries"/> is meaningful.</summary>
public sealed record LegacyParseResult(IReadOnlyList<LegacyEntry> Entries, string? Error = null, int IgnoredElements = 0)
{
    public bool IsValid => Error is null;

    public int SystemEntryCount => Entries.Count(e => e.Section == LegacySection.System);

    public int OracleEntryCount => Entries.Count(e => e.Section == LegacySection.Oracle);

    public static LegacyParseResult Failed(string error) => new([], error);
}

/// <summary>What the database currently holds for one SystemCode (read-only snapshot used for planning).</summary>
public sealed record ExistingSystemSnapshot(
    long Id,
    string SystemCode,
    string SystemName,
    IReadOnlyDictionary<RepositoryType, bool> Memberships);

public sealed record LegacyInvalidEntry(LegacySection Section, string SystemCode, string SystemName, string Reason, int? LineNumber);

public enum LegacyConflictKind
{
    /// <summary>The database already has this SystemCode with a different SystemName.</summary>
    ExistingNameDiffers,

    /// <summary>The file itself gives this SystemCode more than one SystemName.</summary>
    NamesDifferInFile,
}

public sealed record LegacyConflict(
    LegacyConflictKind Kind,
    string SystemCode,
    string? CurrentName,
    IReadOnlyList<string> LegacyNames,
    string Reason);

/// <summary>A System the import will create (ExistingId null) or extend with missing memberships.</summary>
public sealed record PlannedSystem(
    string SystemCode,
    string SystemName,
    long? ExistingId,
    IReadOnlyList<RepositoryType> MembershipsToAdd,
    IReadOnlyList<RepositoryType> ExistingMemberships,
    IReadOnlyList<RepositoryType> DisabledMembershipsKept)
{
    public bool IsNew => ExistingId is null;
}

/// <summary>The complete, deterministic outcome of applying a file to a database state. Also used as the import result.</summary>
public sealed record LegacyImportPlan(
    int SystemEntries,
    int OracleEntries,
    IReadOnlyList<PlannedSystem> Systems,
    IReadOnlyList<LegacyConflict> Conflicts,
    IReadOnlyList<LegacyInvalidEntry> InvalidEntries,
    int DuplicateEntriesMerged)
{
    public int NewSystems => Systems.Count(s => s.IsNew);

    public int ExistingSystems => Systems.Count(s => !s.IsNew);

    public int NewMemberships => Systems.Sum(s => s.MembershipsToAdd.Count);

    public int ExistingMemberships => Systems.Sum(s => s.ExistingMemberships.Count);

    /// <summary>Existing memberships that are disabled and stay disabled (counted within <see cref="ExistingMemberships"/>).</summary>
    public int DisabledMembershipsKept => Systems.Sum(s => s.DisabledMembershipsKept.Count);

    public bool HasChanges => NewSystems > 0 || NewMemberships > 0;
}

public sealed record LegacyImportPreview(string SourcePath, LegacyParseResult Parse, LegacyImportPlan? Plan)
{
    public string SourceName => Path.GetFileName(SourcePath);
}

public sealed record LegacyImportResult(bool Succeeded, LegacyImportPlan? Applied, string? Error);
