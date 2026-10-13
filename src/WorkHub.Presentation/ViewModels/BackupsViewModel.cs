using System.Globalization;
using WorkHub.Core.Backup;
using WorkHub.Presentation.Mvvm;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Settings → Data → Backups (Phase 9 §9.3). Read-only: lists daily and pre-migration backups, quick-checks each listed
/// backup, fully validates the selected one on request and opens the Backups folder. There is no command that restores,
/// replaces, deletes or writes anything.
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
        "WorkHub does not restore backups itself. To recover, close every WorkHub window first and follow \"Recovering from a backup\" in the README. Use only a backup shown as Fully validated.";

    private readonly IBackupCatalog _catalog;
    private IReadOnlyList<BackupRowViewModel> _rows = [];
    private BackupRowViewModel? _selectedRow;
    private CancellationTokenSource? _loadCts;
    private int _generation;
    private bool _activated;
    private bool _isLoading;
    private bool _isValidating;
    private string? _error;

    public BackupsViewModel(IBackupCatalog catalog)
    {
        _catalog = catalog;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, onError: ex => Error = $"The backups could not be listed: {ex.Message}");
        ValidateCommand = new AsyncRelayCommand(ValidateSelectedAsync, () => CanValidate, ex => Error = $"The backup could not be checked: {ex.Message}");
        OpenFolderCommand = new RelayCommand(() => Error = _catalog.OpenFolder());
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
            if (SetProperty(ref _selectedRow, value))
                ValidateCommand.RaiseCanExecuteChanged();
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

    public string KindText => Entry.Kind switch
    {
        BackupKind.Daily => "Daily",
        BackupKind.PreMigration => Entry.MigratedToVersion is { } to ? $"Before upgrade to v{to}" : "Pre-migration",
        _ => $"Other file ({Entry.Folder})",
    };

    public string TakenText => Entry.Taken is { } taken
        ? taken.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " JST"
        : "—";

    public string SizeText => Entry.Size switch
    {
        < 0 => "—",
        < 1024 => $"{Entry.Size} B",
        < 1024 * 1024 => $"{Entry.Size / 1024.0:0.#} KB",
        _ => $"{Entry.Size / (1024.0 * 1024.0):0.#} MB",
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
