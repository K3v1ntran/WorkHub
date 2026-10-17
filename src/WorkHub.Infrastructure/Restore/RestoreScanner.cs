using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;

namespace WorkHub.Infrastructure.Restore;

internal sealed record ScannedOp(string Op, RestoreJournal Journal);

/// <param name="Problem">Fail-closed reason (unreadable journal, unknown folder, guard violation, foreign journal, more than one non-terminal op).</param>
/// <param name="NonTerminal">The one operation that needs the startup loop (live <c>Staged</c> excluded), or null.</param>
internal sealed record RestoreScan(string? Problem, ScannedOp? NonTerminal, bool HasLiveStaging);

/// <summary>
/// Reads <c>Data\Restore\</c> (§10.2 steps 1–2, §6.1 Q1). With <c>cleanup</c> (the caller holds R) it also removes what is
/// provably dead: journal-less staging folders holding only <c>candidate.db</c>/<c>journal.json.tmp</c>, leftover
/// <c>journal.json.tmp</c> files, <c>Staged</c> ops (→ <c>Abandoned</c>, candidate deleted) and the leftover candidate of a
/// <c>Cancelled</c>/<c>Abandoned</c> op — the latter only AFTER that op passed the terminal-state guard (§9.5). Without R,
/// <c>Staged</c> and journal-less ops are treated as live. Nothing else is ever deleted.
/// </summary>
internal sealed class RestoreScanner(WorkHubPaths paths, IRestoreFileOps files, RestoreJournalStore store, IAppLog log, TimeProvider time)
{
    public RestoreScan Scan(bool cleanup)
    {
        if (!files.DirectoryExists(paths.RestoreDirectory))
            return new RestoreScan(null, null, false);

        ScannedOp? nonTerminal = null;
        var liveStaging = false;
        DirectoryIdentity? dataDir = null;
        foreach (var name in files.DirectoryNames(paths.RestoreDirectory).Order(StringComparer.Ordinal))
        {
            if (!RestoreOpId.IsValid(name))
                return Fail($"Data\\Restore contains an unexpected folder ({name}).");

            var opDir = paths.RestoreOperationDirectory(name);
            var read = store.Read(name);
            if (read.Missing)
            {
                var entries = files.List(opDir);
                var deadStaging = entries.All(e => e.FileId != Core.Restore.FileFact.DirectoryId && e.Name is RestoreNames.Candidate or RestoreNames.JournalTemp);
                if (!deadStaging)
                    return Fail($"The restore folder {name} has no journal and holds unexpected files.");
                if (!cleanup)
                {
                    liveStaging = true; // a requester may be writing its first journal right now
                    continue;
                }

                foreach (var entry in entries)
                    files.Delete(Path.Combine(opDir, entry.Name));
                files.DeleteEmptyDirectory(opDir);
                log.Info($"Restore: removed the unpublished staging folder {name}.");
                continue;
            }

            if (read.Journal is not { } journal)
                return Fail($"The restore journal of {name} cannot be trusted: {read.Problem}.");

            if (cleanup && files.FileExists(store.TempPath(name)))
                files.Delete(store.TempPath(name));

            if (journal.State == RestoreState.Staged)
            {
                if (!cleanup)
                {
                    liveStaging = true;
                    continue;
                }

                // Its owner would hold R; we hold R, so the requester is gone. Staging never moves data: anything set aside
                // means the journal is not what it claims, and nothing is abandoned.
                if (!RestoreFacts.Read(paths, files, name).PreviousEmpty || journal.Undo is not null)
                    return Fail($"The unconfirmed restore {name} is inconsistent with its files.");
                journal = journal.Next(RestoreState.Abandoned, time.GetUtcNow());
                store.Write(journal);
                DeleteCandidate(name);
                log.Info($"Restore: {name} was never confirmed; marked Abandoned.");
                continue;
            }

            if (RestoreStates.IsTerminal(journal.State))
            {
                if (RestoreReconciler.TerminalViolation(journal, RestoreFacts.Read(paths, files, name)) is { } violation)
                    return Fail($"The finished restore {name} is inconsistent with its files ({violation}).");
                if (cleanup && journal.State is RestoreState.Cancelled or RestoreState.Abandoned)
                    DeleteCandidate(name);
                continue;
            }

            if (nonTerminal is not null)
                return Fail("More than one restore operation is unfinished.");

            dataDir ??= RestoreFacts.DataDirectoryIdentity(paths);
            if (journal.DataDir != dataDir)
                return Fail($"The restore journal of {name} belongs to another data folder (the folder was copied).");
            nonTerminal = new ScannedOp(name, journal);
        }

        return new RestoreScan(null, nonTerminal, liveStaging);
    }

    private void DeleteCandidate(string op)
    {
        var candidate = Path.Combine(paths.RestoreOperationDirectory(op), RestoreNames.Candidate);
        if (files.FileExists(candidate))
        {
            files.Delete(candidate);
            log.Info($"Restore: removed the staged copy of {op}.");
        }
    }

    private RestoreScan Fail(string problem)
    {
        log.Error($"Restore: {problem}");
        return new RestoreScan(problem, null, false);
    }
}

/// <summary>Fresh file facts of one operation (§8.2, §9.4): read, never cached across a lock acquisition.</summary>
internal static class RestoreFacts
{
    public static RestoreFileFacts Read(WorkHubPaths paths, IRestoreFileOps files, string op)
    {
        var opDir = paths.RestoreOperationDirectory(op);
        var data = files.List(paths.DataDirectory)
            .Where(f => f.Name.StartsWith(RestoreNames.Database, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var previous = Path.Combine(opDir, RestoreNames.Previous);
        var restored = Path.Combine(opDir, RestoreNames.Restored);
        var candidate = Path.Combine(opDir, RestoreNames.Candidate);
        return new RestoreFileFacts(
            data,
            files.DirectoryExists(previous) ? files.List(previous) : null,
            files.DirectoryExists(restored) ? files.List(restored) : null,
            files.FileExists(candidate) ? new Core.Restore.FileFact(RestoreNames.Candidate, files.GetLength(candidate), files.FileId(candidate)) : null);
    }

    public static DirectoryIdentity DataDirectoryIdentity(WorkHubPaths paths)
    {
        var id = NativeFiles.FileId(paths.DataDirectory);
        return new DirectoryIdentity(id[..8], id);
    }

    /// <summary>The current set in <c>Data\</c> (database-set names only), with sizes and file IDs.</summary>
    public static IReadOnlyList<InventoryFile> CurrentSet(WorkHubPaths paths, IRestoreFileOps files) =>
        RestoreNames.DatabaseSet
            .Select(name => Path.Combine(paths.DataDirectory, name))
            .Where(files.FileExists)
            .Select(path => new InventoryFile(Path.GetFileName(path), files.GetLength(path), files.FileId(path)))
            .ToList();
}
