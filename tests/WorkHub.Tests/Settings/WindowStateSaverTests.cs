using System.Diagnostics;
using System.Text.Json.Nodes;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Settings;
using WorkHub.Presentation.Services;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Settings;

/// <summary>
/// Phase 16A close and relaunch (proposal §4.2–4.3, D2/D12, acceptance L1–L6c; L7 is in RestoreViewModelTests, L8–L10 in
/// WindowStateSettingsTests and WindowPlacementPolicyTests). The saver, a fake <see cref="IAppLifetime"/>, recording or real
/// stores on a test root, and a fake clock. No window is created.
/// </summary>
public sealed class WindowStateSaverTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private string File_ => _root.Paths.SettingsFile;

    private sealed class FakeLifetime : IAppLifetime
    {
        public List<string> Calls { get; } = [];

        public bool Relaunch(string? restoreOp)
        {
            Calls.Add("relaunch");
            return true;
        }

        public void Shutdown() => Calls.Add("shutdown");
    }

    /// <summary>A window whose bounds the test moves; counts snapshots.</summary>
    private sealed class FakeWindow
    {
        public UiStateChanges State { get; set; } = UiStates.Bounds(100, 100);

        public int Snapshots { get; private set; }

        public UiStateChanges? Capture()
        {
            Snapshots++;
            return State;
        }
    }

    private static (WindowStateSaver Saver, RecordingSettingsStore Store, FakeWindow Window, ListLog Log) Recording()
    {
        var store = new RecordingSettingsStore();
        var service = new SettingsService(store, _ => true);
        var window = new FakeWindow();
        var log = new ListLog();
        var saver = new WindowStateSaver(service, log) { Capture = window.Capture };
        return (saver, store, window, log);
    }

    // ---- L1 / L2: cancelled closes ---------------------------------------------------------------------------------------

    [Fact]
    public void L1_ABlockedClose_SavesNothing_ALaterSuccessfulClose_SavesTheLaterBounds()
    {
        var (saver, store, window, _) = Recording();
        var isCloseBlocked = true;

        var cancel = false;
        cancel |= isCloseBlocked; // MainWindow's Closing handler
        saver.OnWindowClosing(cancel);

        Assert.Empty(store.Edits);
        Assert.Equal(0, window.Snapshots);

        isCloseBlocked = false;
        window.State = UiStates.Bounds(-1700, 40, 1300, 850);
        cancel = false;
        cancel |= isCloseBlocked;
        saver.OnWindowClosing(cancel);

        var edit = Assert.Single(store.Edits);
        Assert.Equal(-1700, edit.Ui!.Left);
        Assert.Equal(40, edit.Ui.Top);
        Assert.Equal(1, window.Snapshots);
    }

    [Fact]
    public void L2_AnotherClosingHandlerCancels_SavesNothing()
    {
        var (saver, store, window, _) = Recording();

        saver.OnWindowClosing(cancelled: true);

        Assert.Empty(store.Edits);
        Assert.Equal(0, window.Snapshots);
        Assert.Null(saver.Result);
    }

    // ---- L3 / L4: the two triggers ---------------------------------------------------------------------------------------

    [Fact]
    public void L3_TriggerA_ThenTriggerBInsideAppLifetimeShutdown_OneSnapshotAndOneUpdate_FromTriggerA()
    {
        var (saver, store, window, _) = Recording();
        var inner = new FakeLifetime();
        var lifetime = new SavingAppLifetime(inner, saver);
        window.State = UiStates.Bounds(10, 20);

        saver.OnWindowClosing(cancelled: false);                 // trigger (a): a successful close
        window.State = UiStates.Bounds(999, 999);
        lifetime.Shutdown();                                     // trigger (b): called directly, no callback from Application.Shutdown

        Assert.Equal(1, window.Snapshots);
        var edit = Assert.Single(store.Edits);
        Assert.Equal(10, edit.Ui!.Left);                         // from trigger (a)
        Assert.Equal(["shutdown"], inner.Calls);                 // trigger (b) still shuts down; its save is a no-op
    }

    [Fact]
    public void L4_TriggerB_ThenTriggerA_OneSnapshotAndOneUpdate_FromTriggerB()
    {
        var inner = new FakeLifetime();
        var store = new RecordingSettingsStore(inner.Calls); // one list: records the order of save and shutdown
        var window = new FakeWindow { State = UiStates.Bounds(30, 40) };
        var saver = new WindowStateSaver(new SettingsService(store, _ => true), NullAppLog.Instance) { Capture = window.Capture };
        var lifetime = new SavingAppLifetime(inner, saver);

        lifetime.Shutdown();                                     // trigger (b): e.g. the Exit command
        window.State = UiStates.Bounds(999, 999);
        saver.OnWindowClosing(cancelled: false);                 // trigger (a): a no-op

        Assert.Equal(1, window.Snapshots);
        var edit = Assert.Single(store.Edits);
        Assert.Equal(30, edit.Ui!.Left);
        Assert.Equal(["update", "shutdown"], inner.Calls);       // saved before Application.Shutdown is queued
    }

    // ---- L5: failures --------------------------------------------------------------------------------------------------

    public static TheoryData<string> Failures() => new() { "LockTimeout", "FileUnreadable", "IOException" };

    [Theory]
    [MemberData(nameof(Failures))]
    public void L5_AFailedSave_IsLoggedOnce_TheCloseContinues_NoRetry(string failure)
    {
        var (saver, store, _, log) = Recording();
        switch (failure)
        {
            case "LockTimeout": store.Status = SettingsUpdateStatus.LockTimeout; break;
            case "FileUnreadable": store.Status = SettingsUpdateStatus.FileUnreadable; break;
            default: store.Throw = new IOException("disk full (test)"); break;
        }

        var inner = new FakeLifetime();
        new SavingAppLifetime(inner, saver).Shutdown();
        saver.OnWindowClosing(cancelled: false); // a later trigger does not retry

        Assert.Single(store.Edits);
        Assert.Single(log.NotSaved);
        Assert.Equal(["shutdown"], inner.Calls);
        Assert.False(saver.Result!.Saved);
    }

    // ---- L6 / L6b / L6c: D12, recorded separately ---------------------------------------------------------------------

    private const string StartFile = """
        {
          "settingsVersion": 2,
          "ui": {
            "lastPrimaryTab": "Repository",
            "window": { "left": 10, "top": 20, "width": 900, "height": 600, "isMaximized": false },
            "workspace": { "showTreeToolbar": true, "showSelectionToolbar": true, "showContentToolbar": true }
          }
        }
        """;

    private void WriteStartFile()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(File_, StartFile);
    }

    private string[] TempFiles() => Directory.GetFiles(_root.Paths.ConfigDirectory, "settings.json.*.tmp");

    /// <summary>Wraps the real store: runs an action before each Update (before the lock wait) and can hold toolbar saves.</summary>
    private sealed class WrappingStore(JsonSettingsStore inner) : ISettingsStore
    {
        public Action? BeforeUiUpdate { get; set; }

        public ManualResetEventSlim? HoldToolbarSave { get; set; }

        public TaskCompletionSource ToolbarEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int UiUpdates;

        public int ToolbarUpdates;

        public SettingsLoadResult LoadOrCreate() => inner.LoadOrCreate();

        public void Save(AppSettings settings) => inner.Save(settings);

        public SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback)
        {
            if (edit.Toolbars is not null)
            {
                Interlocked.Increment(ref ToolbarUpdates);
                ToolbarEntered.TrySetResult();
                Assert.True(HoldToolbarSave?.Wait(TimeSpan.FromSeconds(10)) ?? true);
            }

            return inner.Update(edit, fallback);
        }

        public SettingsUpdateResult Update(SettingsEdit edit, AppSettings fallback, SaveBudget budget)
        {
            if (edit.Ui is not null)
                Interlocked.Increment(ref UiUpdates);
            BeforeUiUpdate?.Invoke();
            return inner.Update(edit, fallback, budget);
        }
    }

    [Fact]
    public async Task L6_TheBudgetRunsOutWhileWaitingForTheLock_DroppedBeforeWriting_ByteIdentical_OneLog_NoRetry()
    {
        WriteStartFile();
        var clock = new SplitClock(); // Advance = real time passing: the monotonic timestamp and UTC move together
        var files = new SpyFileOps();
        var real = new JsonSettingsStore(File_, clock) { Files = files }; // lock ceiling: the default 2 s
        var store = new WrappingStore(real) { BeforeUiUpdate = () => clock.Advance(TimeSpan.FromSeconds(1.5)) }; // 1.5 s of 3 s remain
        var service = new SettingsService(store, _ => true);
        service.Initialize(real.LoadOrCreate());
        var log = new ListLog();
        var saver = new WindowStateSaver(service, log, clock) { Capture = () => UiStates.Bounds(-1800, 100) };
        var before = File.ReadAllBytes(File_);
        var startedAt = clock.GetUtcNow();

        WindowStateSaveResult? result;
        TimeSpan realWait;
        Task mover;
        using (new FileStream(real.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            // While the store waits for the lock, the fake clock passes the deadline (1.6 s > the 1.5 s remaining).
            mover = Task.Run(async () =>
            {
                await Task.Delay(200);
                clock.Advance(TimeSpan.FromSeconds(1.6));
            });
            var watch = Stopwatch.StartNew();
            saver.OnWindowClosing(cancelled: false); // synchronous, as on the closing UI thread
            realWait = watch.Elapsed;
            result = saver.Result;
        }

        await mover.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WindowStateSaveOutcome.DeadlineExpired, result!.Outcome);
        Assert.Empty(files.Calls);                                   // no temp-file write, no replace
        Assert.Equal(before, File.ReadAllBytes(File_));              // byte-identical
        Assert.Empty(TempFiles());
        Assert.Contains("budget expired before writing", Assert.Single(log.NotSaved));
        // Timing applies only to the wait before writing: it ended when the budget ran out (fake 3.1 s after the snapshot),
        // before the 2 s lock ceiling (fake 1.6 s of lock wait, real well under 2 s of waiting).
        Assert.Equal(TimeSpan.FromSeconds(3.1), clock.GetUtcNow() - startedAt);
        Assert.True(realWait < TimeSpan.FromSeconds(1.9), realWait.ToString());

        saver.OnProgrammaticExit();                                  // no retry
        Assert.Equal(1, store.UiUpdates);
        Assert.Single(log.NotSaved);
    }

    [Fact]
    public async Task L6b_TheGateIsHeldByAToolbarSave_TheWindowSaveReturnsAtOnce_NoUpdate_OneLog_NoRetry()
    {
        WriteStartFile();
        var clock = new ManualTimeProvider(); // never advanced: the drop needs no wait
        var real = new JsonSettingsStore(File_, clock);
        using var hold = new ManualResetEventSlim(false);
        var store = new WrappingStore(real) { HoldToolbarSave = hold };
        var service = new SettingsService(store, _ => true);
        service.Initialize(real.LoadOrCreate());
        var log = new ListLog();
        var saver = new WindowStateSaver(service, log, clock) { Capture = () => UiStates.Bounds(-1800, 100, tab: "Workspace") };

        var toolbarSave = service.SaveWorkspaceToolbarAsync(WorkspaceToolbar.Content, false); // takes the gate
        await store.ToolbarEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var watch = Stopwatch.StartNew();
        saver.OnWindowClosing(cancelled: false);
        var elapsed = watch.Elapsed;

        Assert.False(toolbarSave.IsCompleted);                       // returned while the toolbar save still holds the gate
        Assert.True(elapsed < TimeSpan.FromSeconds(1), elapsed.ToString());
        Assert.Equal(WindowStateSaveOutcome.GateBusy, saver.Result!.Outcome);
        Assert.Equal(0, store.UiUpdates);                            // Update was not called
        Assert.Contains("another settings save is in progress", Assert.Single(log.NotSaved));

        hold.Set();
        Assert.True((await toolbarSave.WaitAsync(TimeSpan.FromSeconds(10))).Saved); // the toolbar save completes its own change
        saver.OnProgrammaticExit();                                  // no retry, no background save

        var after = JsonNode.Parse(File.ReadAllText(File_))!;
        Assert.False(after["ui"]!["workspace"]!["showContentToolbar"]!.GetValue<bool>());
        Assert.Equal(10, after["ui"]!["window"]!["left"]!.GetValue<double>());          // unchanged from before the test
        Assert.Equal(900, after["ui"]!["window"]!["width"]!.GetValue<double>());
        Assert.Equal("Repository", after["ui"]!["lastPrimaryTab"]!.GetValue<string>());
        Assert.Equal(0, store.UiUpdates);
        Assert.Single(log.NotSaved);
    }

    [Fact]
    public void L6c_TheClockPassesTheDeadlineWhileTheTempFileIsWritten_TheWriteCompletes()
    {
        WriteStartFile();
        var clock = new SplitClock();
        var files = new SpyFileOps { DuringTempWrite = () => clock.Advance(TimeSpan.FromSeconds(5)) };
        var real = new JsonSettingsStore(File_, clock) { Files = files };
        var service = new SettingsService(real, _ => true);
        service.Initialize(real.LoadOrCreate());
        var log = new ListLog();
        var saver = new WindowStateSaver(service, log, clock) { Capture = () => UiStates.Bounds(-1800, 100, 1250, 820, tab: "Workspace") };

        saver.OnWindowClosing(cancelled: false);

        Assert.Equal(WindowStateSaveOutcome.Saved, saver.Result!.Outcome);
        Assert.Equal(["write-temp", "replace"], files.Calls);         // replace called once, not interrupted
        var saved = new JsonSettingsStore(File_).PeekUi()!;
        Assert.Equal(-1800, saved.Window.Left);
        Assert.Equal(1250, saved.Window.Width);
        Assert.Equal("Workspace", saved.LastPrimaryTab);
        Assert.Empty(TempFiles());
        Assert.Empty(log.NotSaved);
    }

    [Fact]
    public void NoWindowYet_NothingIsSaved_AndALaterTriggerStillSaves()
    {
        var store = new RecordingSettingsStore();
        var saver = new WindowStateSaver(new SettingsService(store, _ => true), NullAppLog.Instance);

        saver.OnProgrammaticExit(); // e.g. an exit before the window exists
        Assert.Empty(store.Edits);

        saver.Capture = () => UiStates.Bounds(1, 2);
        saver.OnWindowClosing(cancelled: false);
        Assert.Single(store.Edits);
    }

    [Fact]
    public void TheBudgetGivenToTheStore_IsThreeSeconds_StartedAtTheSnapshot_OnTheSaversClock()
    {
        var clock = new SplitClock();
        var store = new RecordingSettingsStore();
        var saver = new WindowStateSaver(new SettingsService(store, _ => true), NullAppLog.Instance, clock)
        {
            Capture = () =>
            {
                clock.Advance(TimeSpan.FromSeconds(10)); // time spent before and while reading the window is not counted
                return UiStates.Bounds(1, 2);
            },
        };

        saver.OnWindowClosing(cancelled: false);

        var budget = Assert.Single(store.Budgets)!;
        Assert.Same(clock, budget.Time);                         // one time source from the snapshot to the last check
        Assert.Equal(TimeSpan.FromSeconds(3), budget.Limit);
        Assert.Equal(TimeSpan.Zero, budget.Elapsed);             // started after Capture returned
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(2.9));
        Assert.False(budget.IsExpired);
        clock.AdvanceMonotonic(TimeSpan.FromSeconds(0.1));
        Assert.True(budget.IsExpired);
    }

    // ---- D12 regression: system-clock jumps (Codex review). The budget is monotonic; UTC changes never move it. --------

    private (WindowStateSaver Saver, WrappingStore Store, SpyFileOps Files, ListLog Log, JsonSettingsStore Real) RealStore(
        SplitClock clock, Action? beforeLastBudgetCheck = null, Action? duringWritePreparation = null)
    {
        WriteStartFile();
        var files = new SpyFileOps();
        var real = new JsonSettingsStore(File_, clock)
        {
            Files = files,
            BeforeLastBudgetCheck = beforeLastBudgetCheck,
            DuringWritePreparation = duringWritePreparation,
        };
        var store = new WrappingStore(real);
        var service = new SettingsService(store, _ => true);
        service.Initialize(real.LoadOrCreate());
        var log = new ListLog();
        var saver = new WindowStateSaver(service, log, clock) { Capture = () => UiStates.Bounds(-1800, 100, 1250, 820, tab: "Workspace") };
        return (saver, store, files, log, real);
    }

    [Fact]
    public async Task D12_UtcJumpsForward_DuringTheLockWaitAndBeforeTheLastCheck_TheSaveIsNotDroppedEarly()
    {
        var clock = new SplitClock();
        var (saver, store, files, log, real) = RealStore(clock, beforeLastBudgetCheck: () => clock.JumpUtc(TimeSpan.FromDays(1)));
        store.BeforeUiUpdate = () => clock.JumpUtc(TimeSpan.FromHours(1)); // right after the snapshot: +1 h on the wall clock

        Task releaser;
        var lockHandle = new FileStream(real.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            // While the store waits for the lock: another +1 h wall-clock jump, 0.5 s of real (monotonic) time, then release.
            releaser = Task.Run(async () =>
            {
                await Task.Delay(200);
                clock.JumpUtc(TimeSpan.FromHours(1));
                clock.AdvanceMonotonic(TimeSpan.FromSeconds(0.5));
                await Task.Delay(100);
                lockHandle.Dispose();
            });
            saver.OnWindowClosing(cancelled: false);
        }
        finally
        {
            lockHandle.Dispose();
        }

        await releaser.WaitAsync(TimeSpan.FromSeconds(10));

        // The wall clock moved 1 day + 2 h past the snapshot; the monotonic budget used 0.5 s of 3 s.
        Assert.Equal(WindowStateSaveOutcome.Saved, saver.Result!.Outcome);
        Assert.Equal(["write-temp", "replace"], files.Calls);
        var saved = new JsonSettingsStore(File_).PeekUi()!;
        Assert.Equal(-1800, saved.Window.Left);
        Assert.Equal(1250, saved.Window.Width);
        Assert.Equal("Workspace", saved.LastPrimaryTab);
        Assert.Empty(TempFiles());
        Assert.Empty(log.NotSaved);
    }

    [Fact]
    public async Task D12_UtcJumpsBack_WhileTheLockWaitUsesUpTheMonotonicBudget_WritingNeverStarts_ByteIdentical_OneLog()
    {
        var clock = new SplitClock();
        var (saver, store, files, log, real) = RealStore(clock);
        store.BeforeUiUpdate = () => clock.Advance(TimeSpan.FromSeconds(1.5)); // 1.5 s of 3 s remain
        var before = File.ReadAllBytes(File_);

        Task mover;
        using (new FileStream(real.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            // 1.6 s of monotonic time pass (budget gone, lock ceiling of 2 s not reached) while the wall clock goes back 1 h.
            mover = Task.Run(async () =>
            {
                await Task.Delay(200);
                clock.JumpUtc(TimeSpan.FromHours(-1));
                clock.AdvanceMonotonic(TimeSpan.FromSeconds(1.6));
            });
            saver.OnWindowClosing(cancelled: false);
        }

        await mover.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WindowStateSaveOutcome.DeadlineExpired, saver.Result!.Outcome);
        Assert.Empty(files.Calls);
        Assert.Equal(before, File.ReadAllBytes(File_));
        Assert.Empty(TempFiles());
        Assert.Contains("budget expired before writing", Assert.Single(log.NotSaved));
        saver.OnProgrammaticExit(); // no retry
        Assert.Equal(1, store.UiUpdates);
        Assert.Single(log.NotSaved);
    }

    [Fact]
    public void D12_UtcJumpsBack_AfterTheLockWasTaken_TheLastCheckStillDropsTheSave_ByteIdentical_OneLog()
    {
        var clock = new SplitClock();
        // Between taking the lock and the last check: 3.2 s of monotonic time, and the wall clock goes back 1 h (it now
        // reads well before the snapshot, so a wall-clock deadline would still allow writing).
        var (saver, store, files, log, _) = RealStore(clock, beforeLastBudgetCheck: () =>
        {
            clock.AdvanceMonotonic(TimeSpan.FromSeconds(3.2));
            clock.JumpUtc(TimeSpan.FromHours(-1));
        });
        var before = File.ReadAllBytes(File_);
        var snapshotUtc = clock.GetUtcNow();

        saver.OnWindowClosing(cancelled: false);

        Assert.True(clock.GetUtcNow() < snapshotUtc);
        Assert.Equal(WindowStateSaveOutcome.DeadlineExpired, saver.Result!.Outcome);
        Assert.Empty(files.Calls);                                   // no temp file, no replace
        Assert.Equal(before, File.ReadAllBytes(File_));              // byte-identical
        Assert.Empty(TempFiles());
        Assert.False(File.Exists(File_ + ".lock") && IsLocked(File_ + ".lock")); // the lock was released
        Assert.Contains("budget expired before writing", Assert.Single(log.NotSaved));
        saver.OnProgrammaticExit();
        Assert.Equal(1, store.UiUpdates);
        Assert.Single(log.NotSaved);
    }

    // ---- D12 regression (Codex review, round 2): the last check sits immediately before the temp-file write -----------

    [Fact]
    public void D12_TheBudgetRunsOutWhileTheWriteIsPrepared_AfterTheLockWasTaken_NoWriteNoReplace_ByteIdentical_OneLog()
    {
        var clock = new SplitClock();
        var preparationRan = false;
        var (saver, store, files, log, real) = RealStore(clock, duringWritePreparation: () =>
        {
            // Reached only after the lock was taken (with budget left) and the document was read, merged and serialized.
            preparationRan = true;
            clock.Advance(TimeSpan.FromSeconds(0.2)); // 2.9 s + 0.2 s: the budget runs out inside the preparation
        });
        store.BeforeUiUpdate = () => clock.Advance(TimeSpan.FromSeconds(2.9)); // 0.1 s of 3 s left when the lock is taken
        var before = File.ReadAllBytes(File_);

        saver.OnWindowClosing(cancelled: false);

        Assert.True(preparationRan);                                 // the lock wait did not end it: the budget was still valid there
        Assert.Equal(WindowStateSaveOutcome.DeadlineExpired, saver.Result!.Outcome);
        Assert.Empty(files.Calls);                                   // no WriteAllText, no Move
        Assert.Equal(before, File.ReadAllBytes(File_));              // byte-identical
        Assert.Empty(TempFiles());
        Assert.False(File.Exists(real.LockFile) && IsLocked(real.LockFile)); // the lock was released
        Assert.Contains("budget expired before writing", Assert.Single(log.NotSaved));
        saver.OnProgrammaticExit();                                  // no retry
        Assert.Equal(1, store.UiUpdates);
        Assert.Single(log.NotSaved);
    }

    [Fact]
    public void D12_TheBudgetIsStillValidAtTheLastCheck_TheWriteStarts_AndCompletesPastTheBudget()
    {
        var clock = new SplitClock();
        var (saver, store, files, log, _) = RealStore(clock, duringWritePreparation: () => clock.Advance(TimeSpan.FromSeconds(0.05)));
        store.BeforeUiUpdate = () => clock.Advance(TimeSpan.FromSeconds(2.9)); // 2.95 s used at the last check: still valid
        files.DuringTempWrite = () => clock.Advance(TimeSpan.FromSeconds(5)); // the budget runs out while writing

        saver.OnWindowClosing(cancelled: false);

        Assert.Equal(WindowStateSaveOutcome.Saved, saver.Result!.Outcome);
        Assert.Equal(["write-temp", "replace"], files.Calls);
        Assert.Equal(-1800, new JsonSettingsStore(File_).PeekUi()!.Window.Left);
        Assert.Empty(TempFiles());
        Assert.Empty(log.NotSaved);
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
