using WorkHub.Core.Diagnostics;
using WorkHub.Core.Settings;

namespace WorkHub.Presentation.Services;

/// <summary>
/// Phase 16A (§4.2, D2, D12): the one window-state save of this process. The first of the two triggers takes the snapshot
/// and saves; every later trigger does nothing.
/// <list type="bullet">
/// <item>(a) <see cref="OnWindowClosing"/>: the main window's close, after every <c>Closing</c> handler ran. A cancelled close
/// (another handler, or <c>IsCloseBlocked</c>) takes no snapshot and saves nothing; a later close that succeeds saves the
/// state at that later moment.</item>
/// <item>(b) <see cref="OnProgrammaticExit"/>: <see cref="IAppLifetime.Shutdown"/> (through <see cref="SavingAppLifetime"/>),
/// before it queues <c>Application.Shutdown</c>. <c>Application.Shutdown</c> never calls back into it.</item>
/// </list>
/// The save is synchronous on the calling (UI) thread: the 3 s budget before writing starts at the snapshot and is measured
/// on the monotonic timestamp of <c>time</c> (<see cref="SaveBudget"/>), so a wall-clock change cannot shorten or extend it;
/// the save gate is tried once; any failure is logged once and never retried, and the close continues.
/// </summary>
public sealed class WindowStateSaver(SettingsService settings, IAppLog log, TimeProvider? time = null)
{
    /// <summary>The budget before writing starts, counted from the snapshot (D12).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private bool _done;

    /// <summary>Reads the window and shell state on the UI thread; null when there is no window to save (yet).</summary>
    public Func<UiStateChanges?>? Capture { get; set; }

    /// <summary>The outcome of the one save; null while no trigger has saved.</summary>
    public WindowStateSaveResult? Result { get; private set; }

    /// <summary>Trigger (a). <paramref name="cancelled"/> is the close's final <c>Cancel</c> value.</summary>
    public void OnWindowClosing(bool cancelled)
    {
        if (!cancelled)
            SaveOnce();
    }

    /// <summary>Trigger (b).</summary>
    public void OnProgrammaticExit() => SaveOnce();

    private void SaveOnce()
    {
        if (_done)
            return;
        UiStateChanges? snapshot;
        try
        {
            snapshot = Capture?.Invoke();
        }
        catch (Exception ex)
        {
            _done = true; // the close continues; nothing is retried
            Result = new WindowStateSaveResult(WindowStateSaveOutcome.Failed, ex.Message);
            log.Warn($"Window state not saved: the window state could not be read ({ex.Message}).");
            return;
        }

        if (snapshot is null)
            return; // no window yet: nothing to save, and a later trigger may still save
        _done = true;
        var budget = SaveBudget.StartNow(_time, Budget); // starts at the snapshot, on the monotonic timestamp (D12)
        WindowStateSaveResult result;
        try
        {
            result = settings.SaveWindowState(snapshot, budget);
        }
        catch (Exception ex)
        {
            result = new WindowStateSaveResult(WindowStateSaveOutcome.Failed, ex.Message);
        }

        Result = result;
        if (!result.Saved)
            log.Warn($"Window state not saved: {Describe(result)}");
    }

    private static string Describe(WindowStateSaveResult result) => result.Outcome switch
    {
        WindowStateSaveOutcome.GateBusy => "another settings save is in progress.",
        WindowStateSaveOutcome.DeadlineExpired => "budget expired before writing.",
        WindowStateSaveOutcome.LockTimeout => "settings.json is locked by another WorkHub window.",
        WindowStateSaveOutcome.FileUnreadable => "settings.json could not be read.",
        _ => $"{result.Error}",
    };
}

/// <summary>
/// Phase 16A: <see cref="IAppLifetime"/> with trigger (b). <see cref="Relaunch"/> is passed through unchanged, so Restore,
/// Put back and Restart keep their order <c>Relaunch(op)</c> → <c>Shutdown()</c> (D15); the save happens inside
/// <see cref="Shutdown"/>, after the new process was started and before <c>Application.Shutdown</c> is queued.
/// </summary>
public sealed class SavingAppLifetime(IAppLifetime inner, WindowStateSaver saver) : IAppLifetime
{
    public bool Relaunch(string? restoreOp) => inner.Relaunch(restoreOp);

    public void Shutdown()
    {
        saver.OnProgrammaticExit();
        inner.Shutdown();
    }
}
