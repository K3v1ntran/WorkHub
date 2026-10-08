using WorkHub.Presentation.Mvvm;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Settings;
using WorkHub.Core.Startup;

namespace WorkHub.Presentation.ViewModels;

public enum ShellState
{
    Initializing,
    Ready,
    Failed,
}

public sealed class MainViewModel(
    AppInitializer initializer,
    SettingsService settings,
    RepositoryNavigationViewModel navigation,
    WorkspaceAreaViewModel workspace,
    IAppLog log)
    : ObservableObject
{
    private ShellState _state = ShellState.Initializing;
    private string _statusText = "Starting…";
    private string? _errorMessage;
    private string? _warningMessage;

    public RepositoryNavigationViewModel Navigation { get; } = navigation;

    public RepositoryAreaViewModel Repository => Navigation.Repository;

    public OwnerAreaViewModel Owner => Navigation.Owner;

    /// <summary>Workspace tab; loads on first selection of the tab, not at startup.</summary>
    public WorkspaceAreaViewModel Workspace { get; } = workspace;

    public ShellState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(IsReady));
        }
    }

    public bool IsReady => State == ShellState.Ready;

    /// <summary>The <c>--data-root</c> folder when WorkHub runs on isolated data; null for the normal profile.</summary>
    public string? CustomDataRoot { get; init; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string? WarningMessage
    {
        get => _warningMessage;
        private set => SetProperty(ref _warningMessage, value);
    }

    /// <summary>Runs startup I/O on the thread pool; property updates resume on the UI thread.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var result = await Task.Run(initializer.Initialize);
            settings.Initialize(result.Settings);

            if (result.Settings.Status == SettingsLoadStatus.InvalidFileUsingDefaults)
                WarningMessage = $"settings.json could not be read and was left unchanged; defaults are in use until you save Settings. ({result.Settings.Problem})";

            StatusText = $"Ready · schema v{result.Database.SchemaVersion}";
            State = ShellState.Ready;
        }
        catch (DatabaseStartupException ex)
        {
            log.Error($"Database startup refused ({ex.Failure}).", ex);
            Fail(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            log.Error("Startup failed.", ex);
            Fail("WorkHub could not finish starting. See the log in the Logs folder for details.");
            return;
        }

        await Repository.ActivateAsync();
        await Owner.ActivateAsync();
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        StatusText = "Startup failed";
        State = ShellState.Failed;
    }
}
