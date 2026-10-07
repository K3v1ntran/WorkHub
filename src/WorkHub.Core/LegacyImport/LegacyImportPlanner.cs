using WorkHub.Core.Repositories;

namespace WorkHub.Core.LegacyImport;

/// <summary>
/// Pure planning logic shared by preview (read-only) and import (inside the write transaction), so the counts the
/// user confirms and the rows actually written come from the same rules.
/// <list type="bullet">
/// <item>Entries are validated with the current System rules (<see cref="SystemCode"/>, required trimmed name,
/// <see cref="SystemDraftValidator.MaxNameLength"/>). Invalid entries are reported and never inserted.</item>
/// <item>Entries are grouped by SystemCode ignoring case (the database identity). Identical duplicates collapse;
/// the same code with different names in the file is a conflict and that code is skipped entirely.</item>
/// <item>Memberships are the union of the sections the code appears in (SYSTEM → SvnSystem, GitSystem, Release;
/// ORACLE → SvnOracle, GitOracle).</item>
/// <item>An existing System with the same name only gains missing memberships; existing memberships (enabled or
/// disabled, with or without CustomTarget) are left exactly as they are. An existing System with a different name
/// is a conflict and is skipped entirely: the current name is never overwritten and nothing is attached to it.</item>
/// </list>
/// Names are compared exactly after trimming; legacy text is never "fixed".
/// </summary>
public static class LegacyImportPlanner
{
    public static LegacyImportPlan Plan(IReadOnlyList<LegacyEntry> entries, Func<string, ExistingSystemSnapshot?> findExisting)
    {
        var invalid = new List<LegacyInvalidEntry>();
        var valid = new List<(LegacySection Section, string Code, string Name)>();

        foreach (var entry in entries)
        {
            var rawCode = entry.SystemCode ?? string.Empty;
            var rawName = entry.SystemName ?? string.Empty;
            if (!SystemCode.TryNormalize(rawCode, out var code, out var codeError))
            {
                invalid.Add(new(entry.Section, rawCode, rawName, codeError!, entry.LineNumber));
                continue;
            }

            var name = rawName.Trim();
            if (name.Length == 0)
                invalid.Add(new(entry.Section, rawCode, rawName, "SystemName is required.", entry.LineNumber));
            else if (name.Length > SystemDraftValidator.MaxNameLength)
                invalid.Add(new(entry.Section, rawCode, rawName, $"SystemName is longer than {SystemDraftValidator.MaxNameLength} characters.", entry.LineNumber));
            else
                valid.Add((entry.Section, code, name));
        }

        var systems = new List<PlannedSystem>();
        var conflicts = new List<LegacyConflict>();
        var duplicates = 0;

        // Preserve first-appearance order so the plan is deterministic and readable.
        foreach (var group in valid.GroupBy(v => v.Code, StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            var names = items.Select(i => i.Name).Distinct(StringComparer.Ordinal).ToList();
            var code = items[0].Code;

            if (names.Count > 1)
            {
                conflicts.Add(new(LegacyConflictKind.NamesDifferInFile, code, null, names,
                    "The legacy file gives this SystemCode different SystemNames."));
                continue;
            }

            var name = names[0];
            var sectionTypes = items.Select(i => i.Section).Distinct().SelectMany(LegacyMapping.TypesFor).Distinct().Order().ToList();
            duplicates += items.Count - items.Select(i => i.Section).Distinct().Count();

            var existing = findExisting(code);
            if (existing is null)
            {
                systems.Add(new PlannedSystem(code, name, null, sectionTypes, [], []));
                continue;
            }

            if (!string.Equals(existing.SystemName, name, StringComparison.Ordinal))
            {
                conflicts.Add(new(LegacyConflictKind.ExistingNameDiffers, existing.SystemCode, existing.SystemName, [name],
                    "Existing SystemName differs; the current name is kept and this entry is skipped."));
                continue;
            }

            var toAdd = sectionTypes.Where(t => !existing.Memberships.ContainsKey(t)).ToList();
            var present = sectionTypes.Where(existing.Memberships.ContainsKey).ToList();
            var disabled = present.Where(t => !existing.Memberships[t]).ToList();
            systems.Add(new PlannedSystem(existing.SystemCode, existing.SystemName, existing.Id, toAdd, present, disabled));
        }

        return new LegacyImportPlan(
            entries.Count(e => e.Section == LegacySection.System),
            entries.Count(e => e.Section == LegacySection.Oracle),
            systems,
            conflicts,
            invalid,
            duplicates);
    }

    /// <summary>The normalized codes whose current database state the plan depends on.</summary>
    public static IReadOnlyList<string> CodesToLookUp(IReadOnlyList<LegacyEntry> entries) =>
        entries
            .Select(e => SystemCode.TryNormalize(e.SystemCode, out var code, out _) ? code : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
