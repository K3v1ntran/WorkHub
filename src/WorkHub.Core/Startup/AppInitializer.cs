using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Settings;

namespace WorkHub.Core.Startup;

/// <param name="DailyBackup">Null when no daily backup is configured (tests that only initialize).</param>
/// <param name="RestoreNotice">Phase 10: a message about a restore or Put back that finished (or stopped) during this start.</param>
public sealed record InitializationResult(
    WorkHubPaths Paths,
    SettingsLoadResult Settings,
    DatabaseInitializationResult Database,
    DailyBackupResult? DailyBackup = null,
    string? RestoreNotice = null);

/// <summary>
/// Startup use case run on every launch: folders, then settings, then (Phase 10) the restore startup protocol, then the
/// database, then the daily backup. Synchronous by design; the UI calls it off the dispatcher thread.
/// </summary>
public sealed class AppInitializer(
    WorkHubPaths paths,
    ILocalStorage storage,
    ISettingsStore settingsStore,
    IDatabaseInitializer databaseInitializer,
    IAppLog log,
    IDailyBackup? dailyBackup = null,
    IRestoreStartup? restoreStartup = null)
{
    public InitializationResult Initialize() => Initialize(null);

    /// <param name="restoreUi">Shows waiting screens and asks; without one an unfinished restore is left unchanged (Not now).</param>
    /// <exception cref="RestoreStartupException">Exit, fail closed or an owner stop; nothing was initialized.</exception>
    public InitializationResult Initialize(IRestoreStartupUi? restoreUi)
    {
        storage.EnsureRequiredDirectories();

        var settings = settingsStore.LoadOrCreate();
        if (settings.Status == SettingsLoadStatus.InvalidFileUsingDefaults)
            log.Warn($"Settings file could not be read; using defaults in memory and leaving the file unchanged. {settings.Problem}");
        else
            log.Info($"Settings {settings.Status.ToString().ToLowerInvariant()}.");
        if (settings.UpgradedFromVersion is { } from)
            log.Info($"Settings format upgraded in memory from v{from} to v{AppSettings.CurrentSettingsVersion}; the file is rewritten on the next save.");

        // Phase 10: recovery runs before the missing-file / empty-file paths of the initializer. The session holds the
        // startup Gate (and, after a restore, exclusive Presence and the Restore lock) through initialization (R6).
        IRestoreStartupSession? session = null;
        if (restoreStartup is not null)
        {
            var entry = restoreStartup.Enter(restoreUi ?? NonInteractiveRestoreUi.Instance);
            if (entry.Kind != RestoreEntryKind.Proceed)
                throw new RestoreStartupException(entry.Kind, entry.Message ?? string.Empty);
            session = entry.Session;
        }

        DatabaseInitializationResult database;
        string? restoreNotice;
        try
        {
            // A refused database throws here: the daily backup below never runs and nothing is pruned.
            database = databaseInitializer.Initialize();
        }
        catch (Exception ex)
        {
            session?.InitializationFailed(ex);
            throw;
        }

        restoreNotice = session?.InitializationSucceeded(database);
        log.Info($"Database {database.Outcome.ToString().ToLowerInvariant()} (schema v{database.PreviousSchemaVersion} -> v{database.SchemaVersion}).");

        return new InitializationResult(paths, settings, database, RunDailyBackup(), restoreNotice);
    }

    /// <summary>A backup problem never fails startup (Phase 9 §5.3): every exception becomes a Failed result.</summary>
    private DailyBackupResult? RunDailyBackup()
    {
        if (dailyBackup is null)
            return null;

        try
        {
            return dailyBackup.Run();
        }
        catch (Exception ex)
        {
            log.Error("Daily backup failed unexpectedly; startup continues.", ex);
            return DailyBackupResult.Fail(DailyBackupFailure.Unexpected, ex.GetType().Name);
        }
    }
}
