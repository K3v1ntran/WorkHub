using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Settings;
using WorkHub.Core.Startup;

namespace WorkHub.Presentation.ViewModels;

public enum ShellState
{
    Initializing,
    Ready,
    Failed,
}

/// <param name="restoreOwner">Phase 10: this process's restore owner actions; null disables the restore UI (tests).</param>
/// <param name="restoreBackups">Phase 10: the backups list with Restore…, for the Failed shell.</param>
public sealed class MainViewModel(
    AppInitializer initializer,
    SettingsService settings,
    RepositoryNavigationViewModel navigation,
    WorkspaceAreaViewModel workspace,
    IAppLog log,
    IRestoreOwner? restoreOwner = null,
    IAppLifetime? lifetime = null,
    IDialogService? dialogs = null,
    Func<BackupsViewModel>? restoreBackups = null)
    : ObservableObject
{
    public const string RecoveryPrefix = "WorkHub found restore files it cannot finish safely, so it did not open, create or change any database.";

    private ShellState _state = ShellState.Initializing;
    private string _statusText = "Starting…";
    private string? _technicalStatus;
    private string? _errorMessage;
    private string? _warningMessage;
    private bool _isRecoveryRequired;
    private bool _canRestoreFromBackup;
    private RelayCommand? _restoreFromBackupCommand;
    private RelayCommand? _openRestoreFolderCommand;
    private RelayCommand? _exitCommand;

    public RepositoryNavigationViewModel Navigation { get; } = navigation;

    public RepositoryAreaViewModel Repository => Navigation.Repository;

    public OwnerAreaViewModel Owner => Navigation.Owner;

    /// <summary>Workspace tab; loads on first selection of the tab, not at startup.</summary>
    public WorkspaceAreaViewModel Workspace { get; } = workspace;

    /// <summary>Phase 10: waiting, asking and progress of the restore startup protocol.</summary>
    public RestoreStartupViewModel RestoreScreen { get; } = new();

    /// <summary>Phase 10: the owner window's actions for an unfinished restore; null without restore support.</summary>
    public RestoreOwnerViewModel? RestoreOwner { get; } =
        restoreOwner is not null && dialogs is not null && lifetime is not null ? new RestoreOwnerViewModel(restoreOwner, dialogs, lifetime, log) : null;

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

    /// <summary>
    /// The startup status. Phase 13A: the header shows it only while it is not routine (starting, failed, closing); once
    /// Ready, the schema and backup details are in <see cref="TechnicalStatus"/> (Settings → Backups and the brand tooltip).
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>The application version shown in the brand tooltip (set by the composition root).</summary>
    public string? AppVersion { get; init; }

    /// <summary>"Database schema v8 · Today's backup: …" once startup succeeded; null before.</summary>
    public string? TechnicalStatus
    {
        get => _technicalStatus;
        private set
        {
            if (SetProperty(ref _technicalStatus, value))
                OnPropertyChanged(nameof(BrandToolTip));
        }
    }

    /// <summary>The brand mark's tooltip and accessible help text.</summary>
    public string BrandToolTip => TechnicalStatus is null
        ? $"WorkHub {AppVersion}".TrimEnd()
        : $"WorkHub {AppVersion}".TrimEnd() + Environment.NewLine + TechnicalStatus;

    /// <summary>The Settings → Backups status line for one startup result.</summary>
    public static string TechnicalStatusFor(int schemaVersion, DailyBackupResult? backup)
    {
        var schema = $"Database schema v{schemaVersion}";
        return backup switch
        {
            null => $"{schema} · Daily backup did not run.",
            { Outcome: DailyBackupOutcome.Created, FileName: { } created } => $"{schema} · Today's backup: {created} (created now)",
            { Outcome: DailyBackupOutcome.AlreadyDone, FileName: { } existing } => $"{schema} · Today's backup: {existing}",
            { BackedUpToday: true } => $"{schema} · Backed up today",
            _ => $"{schema} · No backup was made today (see the notice above the tabs).",
        };
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

    /// <summary>Phase 10 fail closed: only "Open the Restore folder" and Exit are offered.</summary>
    public bool IsRecoveryRequired
    {
        get => _isRecoveryRequired;
        private set => SetProperty(ref _isRecoveryRequired, value);
    }

    /// <summary>Failed shell with no unfinished restore operation: "Restore from a backup…" (§10.1).</summary>
    public bool CanRestoreFromBackup
    {
        get => _canRestoreFromBackup;
        private set
        {
            if (SetProperty(ref _canRestoreFromBackup, value))
                RestoreFromBackupCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand RestoreFromBackupCommand => _restoreFromBackupCommand ??= new RelayCommand(
        () => dialogs!.ShowRestoreBackups(restoreBackups!()), () => CanRestoreFromBackup);

    public RelayCommand OpenRestoreFolderCommand => _openRestoreFolderCommand ??= new RelayCommand(() =>
    {
        if (restoreOwner?.OpenRestoreFolder() is { } problem)
            dialogs?.ShowMessage("WorkHub", problem);
    });

    public RelayCommand ExitCommand => _exitCommand ??= new RelayCommand(() => lifetime?.Shutdown());

    /// <summary>True while the restore moves database files (X5–X10, U2–U6): the window must not close.</summary>
    public bool IsCloseBlocked => RestoreScreen.IsCloseBlocked;

    /// <summary>Runs startup I/O on the thread pool; property updates resume on the UI thread.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var result = await Task.Run(() => initializer.Initialize(RestoreScreen));
            RestoreScreen.Hide();
            settings.Initialize(result.Settings);

            var warnings = new List<string>();
            if (result.RestoreNotice is { } restoreNotice)
                warnings.Add(restoreNotice);
            if (result.Settings.Status == SettingsLoadStatus.InvalidFileUsingDefaults)
                warnings.Add($"settings.json could not be read and was left unchanged; defaults are in use until you save Settings. ({result.Settings.Problem})");
            if (DailyBackupNotices.For(result.DailyBackup) is { } backupNotice)
                warnings.Add(backupNotice);
            WarningMessage = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
            RestoreOwner?.Refresh(); // Completed-not-recorded: banner with Try again

            StatusText = result.DailyBackup?.BackedUpToday == true
                ? $"Ready · schema v{result.Database.SchemaVersion} · backed up today"
                : $"Ready · schema v{result.Database.SchemaVersion}";
            TechnicalStatus = TechnicalStatusFor(result.Database.SchemaVersion, result.DailyBackup);
            State = ShellState.Ready;
        }
        catch (RestoreStartupException ex)
        {
            RestoreScreen.Hide();
            switch (ex.Kind)
            {
                case RestoreEntryKind.Exit:
                    log.Info("Startup ended by the user while a restore was pending; nothing was changed.");
                    StatusText = "Closing";
                    lifetime?.Shutdown();
                    return;
                case RestoreEntryKind.RecoveryRequired:
                    IsRecoveryRequired = true;
                    Fail($"{RecoveryPrefix} {ex.Message} See \"Unfinished restore\" in the README.");
                    return;
                default:
                    RestoreOwner?.Refresh();
                    Fail(ex.Message);
                    return;
            }
        }
        catch (DatabaseStartupException ex)
        {
            RestoreScreen.Hide();
            log.Error($"Database startup refused ({ex.Failure}).", ex);
            RestoreOwner?.Refresh();
            Fail(RestoreOwner?.Status is { } owned ? $"{owned.Message} ({ex.Message})" : ex.Message);
            await OfferRestoreAsync();
            return;
        }
        catch (Exception ex)
        {
            RestoreScreen.Hide();
            log.Error("Startup failed.", ex);
            Fail("WorkHub could not finish starting. See the log in the Logs folder for details.");
            await OfferRestoreAsync();
            return;
        }

        await Repository.ActivateAsync();
        await Owner.ActivateAsync();
    }

    /// <summary>"Restore from a backup…" only when no restore operation is unfinished (R16: no supersession).</summary>
    private async Task OfferRestoreAsync()
    {
        if (restoreOwner is null || restoreBackups is null || dialogs is null || RestoreOwner?.Status is not null)
            return;
        CanRestoreFromBackup = !await Task.Run(restoreOwner.HasUnfinishedOperation);
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        StatusText = "Startup failed";
        State = ShellState.Failed;
    }
}
