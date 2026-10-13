using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Settings;

namespace WorkHub.Core.Startup;

/// <param name="DailyBackup">Null when no daily backup is configured (tests that only initialize).</param>
public sealed record InitializationResult(
    WorkHubPaths Paths,
    SettingsLoadResult Settings,
    DatabaseInitializationResult Database,
    DailyBackupResult? DailyBackup = null);

/// <summary>
/// Startup use case run on every launch: folders, then settings, then database, then the daily backup.
/// Synchronous by design; the UI calls it off the dispatcher thread.
/// </summary>
public sealed class AppInitializer(
    WorkHubPaths paths,
    ILocalStorage storage,
    ISettingsStore settingsStore,
    IDatabaseInitializer databaseInitializer,
    IAppLog log,
    IDailyBackup? dailyBackup = null)
{
    public InitializationResult Initialize()
    {
        storage.EnsureRequiredDirectories();

        var settings = settingsStore.LoadOrCreate();
        if (settings.Status == SettingsLoadStatus.InvalidFileUsingDefaults)
            log.Warn($"Settings file could not be read; using defaults in memory and leaving the file unchanged. {settings.Problem}");
        else
            log.Info($"Settings {settings.Status.ToString().ToLowerInvariant()}.");
        if (settings.UpgradedFromVersion is { } from)
            log.Info($"Settings format upgraded in memory from v{from} to v{AppSettings.CurrentSettingsVersion}; the file is rewritten on the next save.");

        // A refused database throws here: the daily backup below never runs and nothing is pruned.
        var database = databaseInitializer.Initialize();
        log.Info($"Database {database.Outcome.ToString().ToLowerInvariant()} (schema v{database.PreviousSchemaVersion} -> v{database.SchemaVersion}).");

        return new InitializationResult(paths, settings, database, RunDailyBackup());
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
