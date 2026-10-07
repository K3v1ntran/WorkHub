using WorkHub.Core.Diagnostics;
using WorkHub.Core.Settings;

namespace WorkHub.Core.Startup;

public sealed record InitializationResult(
    WorkHubPaths Paths,
    SettingsLoadResult Settings,
    DatabaseInitializationResult Database);

/// <summary>
/// Startup use case run on every launch: folders, then settings, then database.
/// Synchronous by design; the UI calls it off the dispatcher thread.
/// </summary>
public sealed class AppInitializer(
    WorkHubPaths paths,
    ILocalStorage storage,
    ISettingsStore settingsStore,
    IDatabaseInitializer databaseInitializer,
    IAppLog log)
{
    public InitializationResult Initialize()
    {
        storage.EnsureRequiredDirectories();

        var settings = settingsStore.LoadOrCreate();
        if (settings.Status == SettingsLoadStatus.InvalidFileUsingDefaults)
            log.Warn($"Settings file could not be read; using defaults in memory and leaving the file unchanged. {settings.Problem}");
        else
            log.Info($"Settings {settings.Status.ToString().ToLowerInvariant()}.");

        var database = databaseInitializer.Initialize();
        log.Info($"Database {database.Outcome.ToString().ToLowerInvariant()} (schema v{database.PreviousSchemaVersion} -> v{database.SchemaVersion}).");

        return new InitializationResult(paths, settings, database);
    }
}
