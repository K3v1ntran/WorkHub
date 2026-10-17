using System.Globalization;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Settings → Data → Backups (Phase 9 §9.3; Phase 10 §13.1). Lists daily, pre-migration and before-restore backups,
/// quick-checks each listed backup, fully validates the selected one on request and opens the Backups folder. Phase 10 adds
/// <see cref="RestoreCommand"/>: a request (reservation, staging, confirmation) that is carried out after WorkHub restarts.
/// Nothing on this page deletes or overwrites a backup.
/// <para>
/// Late results: a Refresh starts a new generation; checks from an older generation are discarded. Within a row a full
/// result is never overwritten by a quick result that finishes later.
/// </para>
/// </summary>
public sealed class BackupsViewModel : ObservableObject
{
    public const string ScopeText =
        "Database backups only. Settings (settings.json) and the files, folders and web pages your links point to are not included.";

    public const string RetentionText =
        "WorkHub makes one database backup on the first start of each day (Japan time) and keeps the 3 most recent fully validated daily backups. Pre-migration backups are always kept.";

    public const string RecoveryText =
        "Restore… replaces your data with the selected backup when WorkHub restarts. Your current data is kept first (a safety backup or a preserved copy) and can be put back. Only fully validated backups of this or an older version can be restored.";

    private readonly IBackupCatalog _catalog;
    private IReadOnlyList<BackupRowViewModel> _rows = [];
    private BackupRowViewModel? _selectedRow;
    private CancellationTokenSource? _loadCts;
    private int _generation;
    private bool _activated;
    private bool _isLoading;
    private bool _isValidating;
    private string? _error;

    private readonly IRestoreRequester? _requester;
    private readonly IDialogService? _dialogs;
    private readonly IAppLifetime? _lifetime;
    private readonly IAppLog _log;
    private bool _isRestoring;

    /// <param name="requester">Phase 10 restore requests; null hides Restore… (read-only page).</param>
    public BackupsViewModel(IBackupCatalog catalog, IRestoreRequester? requester = null, IDialogService? dialogs = null, IAppLifetime? lifetime = null, IAppLog? log = null)
    {
        _catalog = catalog;
        _requester = requester;
        _dialogs = dialogs;
        _lifetime = lifetime;
        _log = log ?? NullAppLog.Instance;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, onError: ex => Error = $"The backups could not be listed: {ex.Message}");
        ValidateCommand = new AsyncRelayCommand(ValidateSelectedAsync, () => CanValidate, ex => Error = $"The backup could not be checked: {ex.Message}");
        OpenFolderCommand = new RelayCommand(() => Error = _catalog.OpenFolder());
        RestoreCommand = new AsyncRelayCommand(RestoreSelectedAsync, () => CanRestore, ex => Error = $"The restore could not be prepared: {ex.Message}");
    }

    /// <summary>
    /// Phase 10 Q0–Q5: reserve, stage and validate the selected backup, confirm, then publish <c>Confirmed</c>, relaunch and
    /// exit. While the confirmation is open the Restore lock is held.
    /// </summary>
    public AsyncRelayCommand RestoreCommand { get; }

    /// <summary>True when this page can request a restore (Phase 10); the Restore… button is shown.</summary>
    public bool IsRestoreAvailable => _requester is not null;

    /// <summary>
    /// Q5b: resolves unsaved repository settings (Save / Don't save / Cancel) before <c>Confirmed</c> is published; false
    /// cancels the restore (Q5a). Set by the Settings window.
    /// </summary>
    public Func<Task<bool>>? BeforeConfirm { get; set; }

    public bool IsRestoring
    {
        get => _isRestoring;
        private set
        {
            if (SetProperty(ref _isRestoring, value))
                RaiseRestore();
        }
    }

    public bool CanRestore => _requester is not null && !IsRestoring && SelectedRow is { IsRestorable: true };

    /// <summary>Phase 10: set when a restore was confirmed and WorkHub is restarting (the window should close).</summary>
    public event EventHandler? RestoreConfirmed;

    private async Task RestoreSelectedAsync()
    {
        var row = SelectedRow;
        if (row is null || _requester is null || _dialogs is null || _lifetime is null)
            return;

        Error = null;
        IsRestoring = true;
        try
        {
            var start = await _requester.BeginAsync(row.Entry);
            if (start.Request is not { } request)
            {
                Error = start.Refusal;
                return;
            }

            using (request)
            {
                var confirmation = new RestoreConfirmationViewModel(request.Facts);
                if (!_dialogs.ShowRestoreConfirmation(confirmation) || (BeforeConfirm is { } before && !await before()))
                {
                    await request.CancelAsync(); // Q5a: candidate deleted, Cancelled published, R released
                    return;
                }

                var op = await request.ConfirmAsync();
                _log.Info($"Restore {op} confirmed; restarting.");
                RestoreConfirmed?.Invoke(this, EventArgs.Empty);
                if (!_lifetime.Relaunch(op))
                {
                    _log.Warn($"Restore {op}: WorkHub could not restart itself; the request stays confirmed.");
                    _dialogs.ShowMessage("WorkHub", "The restore was confirmed, but WorkHub could not restart itself. Start WorkHub again to continue or cancel the restore.");
                }

                _lifetime.Shutdown();
            }
        }
        finally
        {
            IsRestoring = false;
        }
    }

    private void RaiseRestore()
    {
        OnPropertyChanged(nameof(CanRestore));
        RestoreCommand.RaiseCanExecuteChanged();
    }

    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>Full validation (integrity_check, foreign keys, schema identity) of the selected backup.</summary>
    public AsyncRelayCommand ValidateCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public IReadOnlyList<BackupRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
                OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => !IsLoading && Rows.Count == 0;

    public BackupRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value))
                return;
            ValidateCommand.RaiseCanExecuteChanged();
            RaiseRestore();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsValidating
    {
        get => _isValidating;
        private set
        {
            if (SetProperty(ref _isValidating, value))
                ValidateCommand.RaiseCanExecuteChanged();
        }
    }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    public bool CanValidate => !IsValidating && SelectedRow is { IsRecognized: true };

    /// <summary>Loads the list the first time the page is shown. Never throws.</summary>
    public async Task ActivateAsync()
    {
        if (_activated)
            return;
        _activated = true;
        await RefreshCommand.ExecuteAsync();
    }

    /// <summary>Lists both folders (no file opened), then quick-checks each recognized backup, one at a time, off the UI thread.</summary>
    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var generation = ++_generation;
        Error = null;
        IsLoading = true;
        IReadOnlyList<BackupEntry> entries;
        try
        {
            entries = await _catalog.ListAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (generation == _generation)
                IsLoading = false;
        }

        if (generation != _generation)
            return;

        var previous = SelectedRow?.Entry;
        Rows = entries.Select(e => new BackupRowViewModel(e, _catalog.LatestSchemaVersion)).ToList();
        SelectedRow = previous is null ? null : Rows.FirstOrDefault(r => r.FileName == previous.FileName && r.Entry.Folder == previous.Folder);

        foreach (var row in Rows.Where(r => r.IsRecognized))
        {
            if (generation != _generation)
                return;

            row.IsChecking = true;
            try
            {
                var result = await _catalog.CheckAsync(row.Entry, BackupCheckLevel.Quick, cts.Token);
                if (generation == _generation)
                    row.Apply(result, BackupCheckLevel.Quick);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (generation == _generation)
                    row.ApplyError(ex.Message);
            }
            finally
            {
                row.IsChecking = false;
            }
        }
    }

    private async Task ValidateSelectedAsync()
    {
        var row = SelectedRow;
        if (row is null || !row.IsRecognized)
            return;

        IsValidating = true;
        row.IsValidating = true;
        Error = null;
        try
        {
            var result = await _catalog.CheckAsync(row.Entry, BackupCheckLevel.Full);
            // The list may have been refreshed meanwhile: a result only ever updates the row it was started for, and
            // only if that row is still shown.
            if (Rows.Contains(row))
                row.Apply(result, BackupCheckLevel.Full);
        }
        catch (Exception ex)
        {
            if (Rows.Contains(row))
                row.ApplyError(ex.Message);
        }
        finally
        {
            row.IsValidating = false;
            IsValidating = false;
        }
    }
}

/// <summary>One file on the Backups page.</summary>
public sealed class BackupRowViewModel : ObservableObject
{
    private readonly int _latestVersion;
    private BackupCheckResult? _result;
    private BackupCheckLevel? _level;
    private string? _checkError;
    private bool _isChecking;
    private bool _isValidating;

    public BackupRowViewModel(BackupEntry entry, int latestVersion)
    {
        Entry = entry;
        _latestVersion = latestVersion;
    }

    public BackupEntry Entry { get; }

    public string FileName => Entry.FileName;

    public bool IsRecognized => Entry.Kind != BackupKind.Unrecognized;

    /// <summary>Restore… may be tried on any recognized backup; the request validates it fully (never loosened).</summary>
    public bool IsRestorable => IsRecognized;

    public string KindText => Entry.Kind switch
    {
        BackupKind.Daily => "Daily",
        BackupKind.PreMigration => Entry.MigratedToVersion is { } to ? $"Before upgrade to v{to}" : "Pre-migration",
        BackupKind.PreRestore => "Before restore (validated safety backup)",
        BackupKind.Preserved => "Before restore (preserved copy, not a validated backup)",
        _ => $"Other file ({Entry.Folder})",
    };

    public string TakenText => Entry.Taken is { } taken
        ? taken.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " JST"
        : "—";

    public string SizeText => SizeTextOf(Entry.Size);

    public static string SizeTextOf(long size) => size switch
    {
        < 0 => "—",
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024.0:0.#} KB",
        _ => $"{size / (1024.0 * 1024.0):0.#} MB",
    };

    public BackupCheckResult? Result => _result;

    /// <summary>The level of the result shown; a quick result never replaces a full one.</summary>
    public BackupCheckLevel? Level => _level;

    public bool IsChecking { get => _isChecking; set { if (SetProperty(ref _isChecking, value)) OnPropertyChanged(nameof(StatusText)); } }

    public bool IsValidating { get => _isValidating; set { if (SetProperty(ref _isValidating, value)) OnPropertyChanged(nameof(StatusText)); } }

    public bool IsFullyValidated => _result?.IsFullyValidated == true;

    public bool IsProblem => _checkError is not null || _result is { PassedQuick: false, Status: not BackupStatus.Unrecognized };

    public string StatusText
    {
        get
        {
            if (IsValidating)
                return "Validating…";
            if (IsChecking)
                return "Checking…";
            if (_checkError is not null)
                return "Could not be checked";
            if (_result is null)
                return IsRecognized ? "Not checked" : "Not a WorkHub backup";
            return StatusName(_result.Status);
        }
    }

    public string? DetailText => _checkError ?? _result?.Detail;

    public string CompatibilityText => _result?.CompatibilityWith(_latestVersion) switch
    {
        BackupCompatibility.Current => $"Current (v{_result.SchemaVersion})",
        BackupCompatibility.Older => $"Older (v{_result.SchemaVersion}); startup would upgrade it",
        BackupCompatibility.Newer => $"Newer (v{_result.SchemaVersion}); not usable by this version",
        BackupCompatibility.NotUsable => "Not usable",
        _ => "—",
    };

    public static string StatusName(BackupStatus status) => status switch
    {
        BackupStatus.FullyValidated => "Fully validated",
        BackupStatus.QuickChecked => "Quick-checked",
        BackupStatus.Unrecognized => "Not a WorkHub backup",
        BackupStatus.NotStandalone => "Not standalone (journal/WAL files)",
        BackupStatus.Unreadable => "Unreadable",
        BackupStatus.IntegrityFailed => "Damaged",
        BackupStatus.Foreign => "Not a WorkHub database",
        BackupStatus.NewerSchema => "Newer WorkHub version",
        BackupStatus.SchemaMismatch => "Structure differs",
        BackupStatus.ForeignKeyViolations => "Foreign-key problems",
        BackupStatus.NameVersionMismatch => "Version does not match its name",
        BackupStatus.ValidationSideEffect => "Check left files behind",
        BackupStatus.NotEqualToSource => "Differs from its source",
        BackupStatus.Preserved => "Preserved copy (not validated)",
        _ => status.ToString(),
    };

    public void Apply(BackupCheckResult result, BackupCheckLevel level)
    {
        if (level == BackupCheckLevel.Quick && _level == BackupCheckLevel.Full)
            return;

        _result = result;
        _level = level;
        _checkError = null;
        RaiseResult();
    }

    public void ApplyError(string message)
    {
        _checkError = message;
        RaiseResult();
    }

    private void RaiseResult()
    {
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(CompatibilityText));
        OnPropertyChanged(nameof(IsFullyValidated));
        OnPropertyChanged(nameof(IsProblem));
    }
}

/// <summary>Startup notice texts for daily backup results (Phase 9 §9.2). Null means "no notice".</summary>
public static class DailyBackupNotices
{
    public static string? For(DailyBackupResult? result) => result switch
    {
        null => null,
        { Outcome: not DailyBackupOutcome.Failed } => null,
        { Failure: DailyBackupFailure.SourceBusy } =>
            "Today's database backup was skipped because the database was busy. WorkHub will try again the next time it starts.",
        { Failure: DailyBackupFailure.DiskFull } =>
            "Today's database backup could not be saved: not enough disk space. Your data is unchanged; free some space and WorkHub will try again the next time it starts.",
        { Failure: DailyBackupFailure.FolderUnavailable or DailyBackupFailure.PublishFailed } =>
            "Today's database backup could not be saved: the Backups folder could not be written. Your data is unchanged; WorkHub will try again the next time it starts.",
        { Failure: DailyBackupFailure.CandidateInvalid or DailyBackupFailure.NotStandalone } =>
            "Today's database backup failed its check and was discarded. Your data is unchanged; WorkHub will try again the next time it starts.",
        { Failure: DailyBackupFailure.SourceCheck } =>
            "WorkHub found a problem in its database while backing it up (integrity, foreign keys, or its structure differs from this version). No backup was made and no older backups were removed. See Settings → Backups and the log.",
        { Failure: DailyBackupFailure.SourceMissing } =>
            "Today's database backup was skipped because the database file could not be found. See the log.",
        _ => "Today's database backup could not be made. Your data is unchanged; WorkHub will try again the next time it starts. See the log for details.",
    };
}
