using WorkHub.Core.Diagnostics;

namespace WorkHub.Core.LegacyImport;

/// <summary>Reads a legacy WorkHub XML file. Implementations must treat the file as untrusted input.</summary>
public interface ILegacyXmlParser
{
    LegacyParseResult Parse(string path);
}

/// <summary>Persistence port for the legacy import.</summary>
public interface ILegacyImportStore
{
    /// <summary>Read-only lookup of the current state of the given SystemCodes (matched ignoring case).</summary>
    Task<IReadOnlyDictionary<string, ExistingSystemSnapshot>> LoadSnapshotAsync(IReadOnlyList<string> systemCodes, CancellationToken cancellationToken = default);

    /// <summary>
    /// In ONE transaction: re-reads the current state, re-plans with <see cref="LegacyImportPlanner"/>, inserts the new
    /// Systems and missing memberships, and commits. Any failure rolls back everything. Returns the plan actually applied.
    /// </summary>
    Task<LegacyImportPlan> ApplyAsync(IReadOnlyList<LegacyEntry> entries, CancellationToken cancellationToken = default);
}

/// <summary>
/// One-time/manual import of legacy System master data (SystemCode, SystemName, section) into SQLite.
/// XML is only a source: nothing is synchronized back, and no hosts, prefixes or other settings are imported.
/// </summary>
public sealed class LegacyImportService(ILegacyXmlParser parser, ILegacyImportStore store, IAppLog log)
{
    /// <summary>Parses and plans without writing anything. File and database work run off the caller's thread.</summary>
    public async Task<LegacyImportPreview> PreviewAsync(string path, CancellationToken cancellationToken = default)
    {
        var parse = await Task.Run(() => parser.Parse(path), cancellationToken).ConfigureAwait(false);
        if (!parse.IsValid)
            return new LegacyImportPreview(path, parse, null);

        var existing = await store.LoadSnapshotAsync(LegacyImportPlanner.CodesToLookUp(parse.Entries), cancellationToken).ConfigureAwait(false);
        var plan = LegacyImportPlanner.Plan(parse.Entries, code => existing.GetValueOrDefault(code));
        return new LegacyImportPreview(path, parse, plan);
    }

    /// <summary>Imports the entries the user previewed, atomically. Never reports success unless the transaction committed.</summary>
    public async Task<LegacyImportResult> ImportAsync(LegacyImportPreview preview, CancellationToken cancellationToken = default)
    {
        if (!preview.Parse.IsValid)
            return new LegacyImportResult(false, null, preview.Parse.Error);

        try
        {
            var applied = await store.ApplyAsync(preview.Parse.Entries, cancellationToken).ConfigureAwait(false);
            log.Info($"Legacy import completed: {applied.NewSystems} systems added, {applied.ExistingSystems} already existing, " +
                     $"{applied.NewMemberships} memberships added, {applied.ExistingMemberships} already existing, " +
                     $"{applied.Conflicts.Count} conflicts skipped, {applied.InvalidEntries.Count} invalid entries skipped.");
            return new LegacyImportResult(true, applied, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error("Legacy import failed and was rolled back.", ex);
            return new LegacyImportResult(false, null, "The import failed and was rolled back. No changes were made.");
        }
    }
}
