using WorkHub.Core.LegacyImport;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// Settings → Data → Import Legacy XML. Choose file → read-only preview (counts, conflicts, invalid entries) →
/// explicit Import → result. No XML or SQL here: everything goes through <see cref="LegacyImportService"/>.
/// The import commits immediately and is independent of the Settings window's Save/Cancel.
/// </summary>
public sealed class LegacyImportViewModel : ObservableObject
{
    public const string XmlFilter = "Legacy WorkHub XML (*.xml)|*.xml|All files (*.*)|*.*";

    private readonly LegacyImportService _service;
    private readonly IDialogService _dialogs;
    private readonly Func<bool> _canStartImport;
    private LegacyImportPreview? _preview;
    private bool _isBusy;
    private bool _isImporting;
    private string? _error;
    private string? _resultMessage;

    /// <param name="canStartImport">Extra gate for starting an import (the Settings window blocks it while its own Save runs).</param>
    public LegacyImportViewModel(LegacyImportService service, IDialogService dialogs, Func<bool>? canStartImport = null)
    {
        _service = service;
        _dialogs = dialogs;
        _canStartImport = canStartImport ?? (() => true);
        ChooseFileCommand = new AsyncRelayCommand(ChooseFileAsync, () => !IsBusy, ex => Error = $"The file could not be previewed: {ex.Message}");
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => CanImport, ex => Error = $"The import failed: {ex.Message}");
        ClearCommand = new RelayCommand(Clear, () => !IsBusy && (HasPreview || Error is not null));
    }

    public AsyncRelayCommand ChooseFileCommand { get; }

    /// <summary>Enabled only for a valid preview with something to add, and never while an operation runs.</summary>
    public AsyncRelayCommand ImportCommand { get; }

    public RelayCommand ClearCommand { get; }

    public bool ImportedAny { get; private set; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                RaiseCommands();
        }
    }

    /// <summary>True from the Import click until the transaction committed or failed. The host window must stay open meanwhile.</summary>
    public bool IsImporting { get => _isImporting; private set => SetProperty(ref _isImporting, value); }

    public string? Error { get => _error; private set => SetProperty(ref _error, value); }

    public string? ResultMessage { get => _resultMessage; private set => SetProperty(ref _resultMessage, value); }

    public LegacyImportPreview? Preview
    {
        get => _preview;
        private set
        {
            if (!SetProperty(ref _preview, value))
                return;

            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(Plan));
            OnPropertyChanged(nameof(SourceName));
            OnPropertyChanged(nameof(NothingToImportMessage));
            OnPropertyChanged(nameof(Conflicts));
            OnPropertyChanged(nameof(InvalidEntries));
            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(HasInvalidEntries));
            RaiseCommands();
        }
    }

    public bool HasPreview => Plan is not null;

    public LegacyImportPlan? Plan => Preview?.Plan;

    public string? SourceName => Preview?.SourceName;

    public bool CanImport => !IsBusy && Plan is { HasChanges: true } && _canStartImport();

    public string? NothingToImportMessage => Plan switch
    {
        null => null,
        { SystemEntries: 0, OracleEntries: 0 } => "The file has no SYSTEM or ORACLE entries. Nothing to import.",
        { HasChanges: false } => "Everything in this file is already in WorkHub (or skipped). Nothing to import.",
        _ => null,
    };

    public IReadOnlyList<LegacyConflictRow> Conflicts =>
        Plan?.Conflicts.Select(c => new LegacyConflictRow(c.SystemCode, c.CurrentName ?? "—", string.Join(" / ", c.LegacyNames), c.Reason)).ToList() ?? [];

    public IReadOnlyList<LegacyInvalidRow> InvalidEntries =>
        Plan?.InvalidEntries.Select(i => new LegacyInvalidRow(
            LegacyMapping.XmlName(i.Section),
            i.SystemCode.Length == 0 ? "<empty>" : i.SystemCode,
            i.SystemName.Length == 0 ? "<empty>" : i.SystemName,
            i.Reason)).ToList() ?? [];

    public bool HasConflicts => Conflicts.Count > 0;

    public bool HasInvalidEntries => InvalidEntries.Count > 0;

    /// <summary>Previews a file without writing anything. Used by the file picker flow and by tests.</summary>
    public async Task PreviewAsync(string path)
    {
        IsBusy = true;
        Error = null;
        ResultMessage = null;
        try
        {
            var preview = await _service.PreviewAsync(path);
            if (!preview.Parse.IsValid)
            {
                Preview = null;
                Error = preview.Parse.Error;
                return;
            }

            Preview = preview;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ChooseFileAsync()
    {
        var path = _dialogs.PickFile("Choose legacy WorkHub XML", null, XmlFilter);
        if (path is not null)
            await PreviewAsync(path);
    }

    private async Task ImportAsync()
    {
        var preview = Preview!;
        IsBusy = true;
        IsImporting = true;
        Error = null;
        try
        {
            var result = await _service.ImportAsync(preview);
            if (!result.Succeeded)
            {
                Error = result.Error;
                return;
            }

            var applied = result.Applied!;
            ImportedAny = true;
            Preview = null;
            ResultMessage =
                "Import completed.\n\n" +
                $"Systems added: {applied.NewSystems}\n" +
                $"Systems already existing: {applied.ExistingSystems}\n" +
                $"Memberships added: {applied.NewMemberships}\n" +
                $"Memberships already existing: {applied.ExistingMemberships}\n" +
                $"Conflicts skipped: {applied.Conflicts.Count}\n" +
                $"Invalid entries skipped: {applied.InvalidEntries.Count}";
        }
        finally
        {
            IsBusy = false;
            IsImporting = false; // last: the result or error is already set when the host may close again
        }
    }

    private void Clear()
    {
        Preview = null;
        Error = null;
        ResultMessage = null;
    }

    internal void RaiseCommands()
    {
        OnPropertyChanged(nameof(CanImport));
        ChooseFileCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }
}

public sealed record LegacyConflictRow(string SystemCode, string CurrentName, string LegacyName, string Reason);

public sealed record LegacyInvalidRow(string Section, string SystemCode, string SystemName, string Reason);
