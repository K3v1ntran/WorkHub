using System.Globalization;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Presentation.Mvvm;
using WorkHub.Presentation.Services;

namespace WorkHub.Presentation.ViewModels;

/// <summary>
/// The restore confirmation (Phase 10 §13.2). Shown while the Restore lock is held; nothing is published until
/// <see cref="ConfirmCommand"/>. Degraded, unreadable and needs-recovery current data require the checkbox.
/// </summary>
public sealed class RestoreConfirmationViewModel : ObservableObject
{
    private bool _isAcknowledged;

    public RestoreConfirmationViewModel(RestoreConfirmationFacts facts)
    {
        Facts = facts;
        ConfirmCommand = new RelayCommand(() => CloseRequested?.Invoke(this, true), () => CanConfirm);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(this, false));
    }

    public event EventHandler<bool>? CloseRequested;

    public RestoreConfirmationFacts Facts { get; }

    public RelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    public string Title => "Restore this backup?";

    public string BackupText
    {
        get
        {
            var kind = Facts.Source.Kind switch
            {
                BackupKind.Daily => "Daily backup",
                BackupKind.PreMigration => "Pre-migration backup",
                BackupKind.PreRestore => "Safety backup made before a restore",
                BackupKind.Preserved => "Preserved copy made before a restore",
                _ => "Backup",
            };
            var taken = Facts.Source.Taken is { } t ? t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (Japan time)" : "unknown time";
            return $"{kind}, taken {taken}, {BackupRowViewModel.SizeTextOf(Facts.Size)}. Fully validated just now.";
        }
    }

    public string SchemaText => Facts.Compatibility == BackupCompatibility.Older
        ? $"Schema v{Facts.SchemaVersion} — upgraded to v{Facts.LatestSchema} after restoring; a pre-migration backup is made first."
        : $"Schema v{Facts.SchemaVersion} (current).";

    public string ContentsText
    {
        get
        {
            var c = Facts.Counts;
            return $"Contents: {N(c.Systems)} Systems, {N(c.OwnerItems)} Owner items, {N(c.Projects)} Projects" +
                   (c.ProjectsInTrash is { } trashed ? $" ({trashed} in Workspace Trash)" : string.Empty) +
                   $", {N(c.Notes)} Notes, {N(c.ProjectTrashItems)} items in Project Trash.";

            static string N(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "—";
        }
    }

    public string CurrentDataText => Facts.RequestClass switch
    {
        { Class: CurrentDataClass.Healthy } => "Your current data: a validated safety backup is made first.",
        { Class: CurrentDataClass.Degraded, Sub: var sub } =>
            $"Your current database is {Describe(sub)}. A copy is kept, but it cannot be validated.",
        { Class: CurrentDataClass.Unreadable } => "Your current database cannot be read. Its files are kept unchanged; no backup can be made.",
        { Class: CurrentDataClass.NeedsRecovery } => "Your current database needs recovery after an interrupted write. Its files are kept exactly as found, and a checked copy is made if possible.",
        { Class: CurrentDataClass.Missing } => "There is no current database.",
        _ => string.Empty,
    };

    public bool RequiresAcknowledgement => AcknowledgementRule.NeedsExplicitAcknowledgement(Facts.RequestClass);

    public string AcknowledgementText => "I understand how my current data is kept.";

    public bool IsAcknowledged
    {
        get => _isAcknowledged;
        set
        {
            if (SetProperty(ref _isAcknowledged, value))
                ConfirmCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanConfirm => !RequiresAcknowledgement || IsAcknowledged;

    public IReadOnlyList<string> Notes { get; } =
    [
        "If your current data changes in this way before the restart, the restore stops without changing anything.",
        "Until WorkHub starts moving your data, you can still cancel the restore from the waiting screen. After that, it finishes or can be put back, but not cancelled.",
        "Changes made after this backup was taken will not be shown after restoring. Settings are not changed.",
        "WorkHub closes and starts again. Every other WorkHub window on this data folder must be closed (save open notes first). Older WorkHub versions are not detected reliably — do not run them on this data folder.",
    ];

    private static string Describe(DegradedKind sub) => sub switch
    {
        DegradedKind.Newer => "newer than this WorkHub",
        DegradedKind.Foreign => "not recognized",
        DegradedKind.StructureDiffers => "structurally different",
        DegradedKind.ForeignKeys => "showing broken links",
        _ => "not as expected",
    };
}

public enum RestoreScreenMode
{
    None,
    GateWait,
    Waiting,
    Asking,
    Progress,
}

/// <summary>
/// The startup screen of the restore protocol (§5.3, §13.3), implementing <see cref="IRestoreStartupUi"/> for the startup
/// worker thread. Updates are posted to the UI thread's synchronization context; <see cref="Ask"/> blocks the worker
/// (which holds the startup Gate) until a button is pressed.
/// </summary>
public sealed class RestoreStartupViewModel : ObservableObject, IRestoreStartupUi
{
    public const string GateWaitText = "A restore is in progress in another WorkHub window.";
    public const string WaitConfirmedText = "Restore is waiting for other WorkHub windows on this data folder to close… Nothing has been changed yet.";
    public const string WaitPutBackText = "Put back is waiting for other WorkHub windows on this data folder to close. Put back cannot be cancelled. If you exit, it continues the next time WorkHub starts. Your previous data stays in Data\\Restore\\{0}\\previous until then.";
    public const string WaitOwnedText = "Another WorkHub window is finishing a restore. Close it, or use Try again / Put back there.";

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly Lock _gate = new();
    private RestoreWaitAction _pending;
    private TaskCompletionSource<RestoreAskAnswer>? _answer;
    private RestoreScreenMode _mode;
    private RestoreWaitKind? _waitKind;
    private RestoreAskKind? _askKind;
    private string? _text;
    private string? _notice;
    private bool _isCloseBlocked;

    public RestoreStartupViewModel()
    {
        ExitCommand = new RelayCommand(() => Press(RestoreWaitAction.Exit, RestoreAskAnswer.NotNow));
        CancelRestoreCommand = new RelayCommand(() => Press(RestoreWaitAction.CancelRestore, RestoreAskAnswer.Cancel), () => CanCancelRestore);
        NowCommand = new RelayCommand(() => Answer(RestoreAskAnswer.Now), () => Mode == RestoreScreenMode.Asking);
        NotNowCommand = new RelayCommand(() => Answer(RestoreAskAnswer.NotNow), () => Mode == RestoreScreenMode.Asking);
    }

    public RelayCommand ExitCommand { get; }

    /// <summary>Only on W-Confirmed and on the Restore / interrupted asks — never for Put back (R25).</summary>
    public RelayCommand CancelRestoreCommand { get; }

    public RelayCommand NowCommand { get; }

    public RelayCommand NotNowCommand { get; }

    public RestoreScreenMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value))
                return;
            OnPropertyChanged(nameof(IsVisible));
            OnPropertyChanged(nameof(IsAsking));
            OnPropertyChanged(nameof(CanExit));
            RaiseCommands();
        }
    }

    public bool IsVisible => Mode != RestoreScreenMode.None;

    public bool IsAsking => Mode == RestoreScreenMode.Asking;

    public RestoreWaitKind? WaitKind { get => _waitKind; private set { if (SetProperty(ref _waitKind, value)) RaiseCommands(); } }

    public RestoreAskKind? AskKind { get => _askKind; private set { if (SetProperty(ref _askKind, value)) { RaiseCommands(); OnPropertyChanged(nameof(NowText)); } } }

    public bool CanCancelRestore =>
        (Mode == RestoreScreenMode.Waiting && WaitKind == RestoreWaitKind.Confirmed)
        || (Mode == RestoreScreenMode.Asking && AskKind is RestoreAskKind.Restore or RestoreAskKind.Interrupted);

    public bool CanExit => Mode is RestoreScreenMode.GateWait or RestoreScreenMode.Waiting;

    public string NowText => AskKind == RestoreAskKind.PutBack ? "Put back now" : "Restore now";

    public string? Text { get => _text; private set => SetProperty(ref _text, value); }

    public string? Notice { get => _notice; private set => SetProperty(ref _notice, value); }

    /// <summary>True during X5–X10 and U2–U6: the main window refuses to close.</summary>
    public bool IsCloseBlocked { get => _isCloseBlocked; private set => SetProperty(ref _isCloseBlocked, value); }

    /// <summary>Called when startup ended (the screen is hidden).</summary>
    public void Hide() => Post(() =>
    {
        Mode = RestoreScreenMode.None;
        IsCloseBlocked = false;
        Text = null;
    });

    // ---- IRestoreStartupUi (startup worker thread) ----------------------------------------------------------------

    public void ShowGateWait() => Post(() =>
    {
        Mode = RestoreScreenMode.GateWait;
        Text = GateWaitText;
    });

    public void ShowWaiting(RestoreWaitKind kind, RestoreOpSummary op) => Post(() =>
    {
        WaitKind = kind;
        Mode = RestoreScreenMode.Waiting;
        Text = kind switch
        {
            RestoreWaitKind.Confirmed => WaitConfirmedText,
            RestoreWaitKind.PutBack => string.Format(CultureInfo.InvariantCulture, WaitPutBackText, op.Op),
            _ => WaitOwnedText,
        };
    });

    public RestoreWaitAction TakeWaitAction()
    {
        lock (_gate)
        {
            var action = _pending;
            _pending = RestoreWaitAction.None;
            return action;
        }
    }

    public void ShowNotice(string message) => Post(() => Notice = message);

    public RestoreAskAnswer Ask(RestoreAskKind kind, RestoreOpSummary op)
    {
        var answer = new TaskCompletionSource<RestoreAskAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
            _answer = answer;
        Post(() =>
        {
            AskKind = kind;
            Mode = RestoreScreenMode.Asking;
            Text = kind switch
            {
                RestoreAskKind.Restore => $"A restore of {op.SourceFileName} was confirmed but has not started. Restore now, cancel it, or decide later (Not now closes WorkHub and changes nothing).",
                RestoreAskKind.Interrupted => $"The restore of {op.SourceFileName} was interrupted before anything was changed. Restore now, cancel it, or decide later.",
                _ => "Put back your previous data now? It was requested and cannot be cancelled. Not now closes WorkHub and changes nothing.",
            };
        });
        var result = answer.Task.GetAwaiter().GetResult();
        // R25: a Put-back question has no Cancel; anything but "now" leaves it unchanged.
        return kind == RestoreAskKind.PutBack && result == RestoreAskAnswer.Cancel ? RestoreAskAnswer.NotNow : result;
    }

    public void ShowProgress(string text, bool closeBlocked) => Post(() =>
    {
        Mode = RestoreScreenMode.Progress;
        Text = text;
        IsCloseBlocked = closeBlocked;
    });

    // ---- buttons (UI thread) ---------------------------------------------------------------------------------------

    private void Press(RestoreWaitAction waitAction, RestoreAskAnswer askAnswer)
    {
        if (Mode == RestoreScreenMode.Asking)
        {
            Answer(askAnswer);
            return;
        }

        if (waitAction == RestoreWaitAction.CancelRestore && !CanCancelRestore)
            return;
        lock (_gate)
            _pending = waitAction;
        Notice = null;
    }

    private void Answer(RestoreAskAnswer answer)
    {
        TaskCompletionSource<RestoreAskAnswer>? pending;
        lock (_gate)
        {
            pending = _answer;
            _answer = null;
        }

        if (pending is null)
            return;
        Mode = RestoreScreenMode.Progress;
        Text = answer == RestoreAskAnswer.Now ? "Starting…" : null;
        pending.TrySetResult(answer);
    }

    private void RaiseCommands()
    {
        CancelRestoreCommand.RaiseCanExecuteChanged();
        NowCommand.RaiseCanExecuteChanged();
        NotNowCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanCancelRestore));
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
            action();
        else
            _ui.Post(_ => action(), null);
    }
}

/// <summary>
/// The owner window of an unfinished restore (§8.4, §9.2, §10.1): the actions this window may take, each re-checked by
/// the coordinator under the Restore lock. Put back is confirmed explicitly and, once requested, cannot be cancelled.
/// </summary>
public sealed class RestoreOwnerViewModel : ObservableObject
{
    public const string PutBackConfirmTitle = "Put back your previous data?";

    private readonly IRestoreOwner _owner;
    private readonly IDialogService _dialogs;
    private readonly IAppLifetime _lifetime;
    private readonly IAppLog _log;
    private RestoreOwnerStatus? _status;
    private string? _message;
    private bool _isBusy;

    public RestoreOwnerViewModel(IRestoreOwner owner, IDialogService dialogs, IAppLifetime lifetime, IAppLog log)
    {
        _owner = owner;
        _dialogs = dialogs;
        _lifetime = lifetime;
        _log = log;
        _status = owner.Status;
        PutBackCommand = new AsyncRelayCommand(PutBackAsync, () => CanPutBack && !_isBusy, ex => Message = ex.Message);
        TryAgainCommand = new AsyncRelayCommand(TryAgainAsync, () => CanTryAgain && !_isBusy, ex => Message = ex.Message);
        RestartCommand = new RelayCommand(Restart, () => CanRestart && !_isBusy);
        ExitCommand = new RelayCommand(lifetime.Shutdown);
    }

    public AsyncRelayCommand PutBackCommand { get; }

    /// <summary>Completed-not-recorded banner: record that the restore finished.</summary>
    public AsyncRelayCommand TryAgainCommand { get; }

    /// <summary>"Try starting again" / "Retry": relaunch without changing the journal; the next start decides.</summary>
    public RelayCommand RestartCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RestoreOwnerStatus? Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value))
                return;
            OnPropertyChanged(nameof(IsVisible));
            OnPropertyChanged(nameof(CanPutBack));
            OnPropertyChanged(nameof(CanTryAgain));
            OnPropertyChanged(nameof(CanRestart));
            OnPropertyChanged(nameof(RestartText));
            OnPropertyChanged(nameof(StatusText));
            RaiseCommands();
        }
    }

    public bool IsVisible => Status is not null;

    public string? StatusText => Status?.Message;

    public bool CanPutBack => Status?.Situation is RestoreOwnerSituation.PublishedInitializationFailed or RestoreOwnerSituation.MovedAside;

    public bool CanTryAgain => Status?.Situation is RestoreOwnerSituation.CompletionUnrecorded;

    public bool CanRestart => Status?.Situation is RestoreOwnerSituation.PublishedInitializationFailed or RestoreOwnerSituation.MovedAside
        or RestoreOwnerSituation.MovingAside or RestoreOwnerSituation.PutBackStopped;

    public string RestartText => Status?.Situation == RestoreOwnerSituation.PublishedInitializationFailed ? "Try starting again" : "Retry";

    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public void Refresh() => Status = _owner.Status;

    public string PutBackConfirmText => Status?.Situation == RestoreOwnerSituation.MovedAside
        ? "Your data from before the restore is put back. The restored backup stays staged and is not used; nothing is deleted. WorkHub restarts.\n\n" + FinalNote
        : "Your data from before the restore is put back. The restored backup is moved to Data\\Restore\\" + Status?.Op.Op + "\\restored; nothing is deleted. WorkHub restarts.\n\n" + FinalNote;

    private const string FinalNote =
        "Once you choose Put back, it cannot be cancelled. If WorkHub is closed before it finishes, it continues the next time WorkHub starts. To use the restored backup afterwards, restore it again from Settings → Backups.";

    private async Task PutBackAsync()
    {
        if (!_dialogs.Confirm(PutBackConfirmTitle, PutBackConfirmText, "Put back and restart"))
            return; // nothing written: the op stays Published / MovedAside

        SetBusy(true);
        try
        {
            var result = await Task.Run(_owner.RequestPutBack);
            if (!result.Succeeded)
            {
                Message = result.Message;
                Refresh();
                return;
            }

            if (!_lifetime.Relaunch(result.RelaunchOp))
            {
                _log.Warn("Put back was requested but WorkHub could not restart itself.");
                _dialogs.ShowMessage("WorkHub", "Put back was requested. WorkHub could not restart itself: start WorkHub again to continue putting back your previous data.");
            }

            _lifetime.Shutdown();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task TryAgainAsync()
    {
        SetBusy(true);
        try
        {
            var result = await Task.Run(_owner.RecordCompletion);
            Message = result.Succeeded ? null : result.Message;
            Refresh();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Restart()
    {
        if (!_lifetime.Relaunch(null))
            _dialogs.ShowMessage("WorkHub", "WorkHub could not restart itself. Start WorkHub again; the restore continues from where it stopped.");
        _lifetime.Shutdown();
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        PutBackCommand.RaiseCanExecuteChanged();
        TryAgainCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }
}
