using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Repositories;
using WorkHub.Core.Restore;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;
using WorkHub.Presentation.Services;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Restore;

/// <summary>
/// Phase 10 presentation (tests H drafts/cancel/confirmation, U6 Failed-shell actions, P8 no-cancel Put back in the view
/// models, waiting/asking/progress screens, close guard, late results).
/// </summary>
public sealed class RestoreViewModelTests : RestoreTestBase
{
    private static readonly BackupEntry Daily = new("workhub_daily_20261001_v7_20261001T090000000.db", BackupFolder.Daily, BackupKind.Daily, 4096, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(9)), 7);

    private static RestoreConfirmationFacts Facts(DataClassification current, BackupCompatibility compatibility = BackupCompatibility.Current, int version = 7) =>
        new("20261016-000000-00000001", Daily, version, compatibility, 7, 4096, new RestoreContentCounts(75, 5, 3, 1, 4, 2), current);

    // ---- confirmation (§13.2) ----------------------------------------------------------------------------------------

    public static TheoryData<DataClassification, string, bool> ConfirmationClasses() => new()
    {
        { new DataClassification(CurrentDataClass.Healthy, Version: 7), "a validated safety backup is made first", false },
        { new DataClassification(CurrentDataClass.Degraded, DegradedKind.Newer, 8), "newer than this WorkHub. A copy is kept, but it cannot be validated", true },
        { new DataClassification(CurrentDataClass.Degraded, DegradedKind.Foreign, 0), "not recognized", true },
        { DataClassification.Unreadable, "cannot be read. Its files are kept unchanged; no backup can be made", true },
        { new DataClassification(CurrentDataClass.NeedsRecovery), "kept exactly as found, and a checked copy is made if possible", true },
        { DataClassification.Missing, "There is no current database", false },
    };

    [Theory]
    [MemberData(nameof(ConfirmationClasses))]
    public void Confirmation_DescribesTheCurrentDataClass_AndRequiresTheCheckboxWhereNeeded(DataClassification current, string text, bool checkbox)
    {
        var vm = new RestoreConfirmationViewModel(Facts(current));

        Assert.Contains(text, vm.CurrentDataText);
        Assert.Equal(checkbox, vm.RequiresAcknowledgement);
        Assert.Equal(!checkbox, vm.ConfirmCommand.CanExecute(null));
        vm.IsAcknowledged = true;
        Assert.True(vm.ConfirmCommand.CanExecute(null));
        Assert.Contains(vm.Notes, n => n.Contains("can still cancel the restore from the waiting screen", StringComparison.Ordinal) && n.Contains("not cancelled", StringComparison.Ordinal));
        Assert.Contains(vm.Notes, n => n.Contains("Every other WorkHub window on this data folder must be closed", StringComparison.Ordinal));
        Assert.Contains("Fully validated just now", vm.BackupText);
        Assert.Contains("75 Systems, 5 Owner items, 3 Projects (1 in Workspace Trash), 4 Notes, 2 items in Project Trash", vm.ContentsText);
    }

    [Fact]
    public void Confirmation_OlderBackup_SaysItIsUpgradedAfterRestoring()
    {
        var vm = new RestoreConfirmationViewModel(Facts(new DataClassification(CurrentDataClass.Healthy, Version: 7), BackupCompatibility.Older, 5));
        Assert.Equal("Schema v5 — upgraded to v7 after restoring; a pre-migration backup is made first.", vm.SchemaText);
    }

    // ---- the startup screen (§13.3) ------------------------------------------------------------------------------------

    private static RestoreOpSummary Op(RestoreState state) => new("20261016-000000-00000001", state, 3, Daily.FileName, null, null);

    [Fact]
    public void P8_WPutBack_OffersExitOnly_ACancelPressIsIgnored()
    {
        var screen = new RestoreStartupViewModel();
        screen.ShowWaiting(RestoreWaitKind.PutBack, Op(RestoreState.UndoRequested));

        Assert.False(screen.CanCancelRestore);
        Assert.False(screen.CancelRestoreCommand.CanExecute(null));
        Assert.True(screen.CanExit);
        Assert.Contains("Put back cannot be cancelled", screen.Text);
        Assert.Contains(@"Data\Restore\20261016-000000-00000001\previous", screen.Text);
        screen.CancelRestoreCommand.Execute(null);
        Assert.Equal(RestoreWaitAction.None, screen.TakeWaitAction());
        screen.ExitCommand.Execute(null);
        Assert.Equal(RestoreWaitAction.Exit, screen.TakeWaitAction());
        Assert.Equal(RestoreWaitAction.None, screen.TakeWaitAction()); // consumed once
    }

    [Fact]
    public void WConfirmed_OffersCancelRestoreAndExit()
    {
        var screen = new RestoreStartupViewModel();
        screen.ShowWaiting(RestoreWaitKind.Confirmed, Op(RestoreState.Confirmed));

        Assert.True(screen.CancelRestoreCommand.CanExecute(null));
        screen.CancelRestoreCommand.Execute(null);
        Assert.Equal(RestoreWaitAction.CancelRestore, screen.TakeWaitAction());
        Assert.Equal(RestoreStartupViewModel.WaitConfirmedText, screen.Text);
    }

    [Fact]
    public void WOwned_OffersExitOnly()
    {
        var screen = new RestoreStartupViewModel();
        screen.ShowWaiting(RestoreWaitKind.Owned, Op(RestoreState.Published));

        Assert.False(screen.CanCancelRestore);
        Assert.Equal(RestoreStartupViewModel.WaitOwnedText, screen.Text);
    }

    [Fact]
    public async Task P8_ThePutBackAsk_HasNoCancel_PutBackNowOrNotNow()
    {
        var screen = new RestoreStartupViewModel();
        var answer = Task.Run(() => screen.Ask(RestoreAskKind.PutBack, Op(RestoreState.UndoRequested)));
        await WaitUntil(() => screen.IsAsking);

        Assert.Equal("Put back now", screen.NowText);
        Assert.False(screen.CanCancelRestore);
        Assert.False(screen.CancelRestoreCommand.CanExecute(null));
        Assert.Contains("cannot be cancelled", screen.Text);
        screen.NotNowCommand.Execute(null);

        Assert.Equal(RestoreAskAnswer.NotNow, await answer);
    }

    [Fact]
    public async Task TheRestoreAsk_OffersRestoreNowCancelRestoreAndNotNow()
    {
        var screen = new RestoreStartupViewModel();
        var answer = Task.Run(() => screen.Ask(RestoreAskKind.Restore, Op(RestoreState.Confirmed)));
        await WaitUntil(() => screen.IsAsking);

        Assert.Equal("Restore now", screen.NowText);
        Assert.True(screen.CancelRestoreCommand.CanExecute(null));
        screen.CancelRestoreCommand.Execute(null);

        Assert.Equal(RestoreAskAnswer.Cancel, await answer);
        Assert.False(screen.IsAsking);
    }

    [Fact]
    public void Progress_CloseIsBlockedOnlyWhileFilesMove_AndTheMainWindowSeesIt()
    {
        var main = NewMain(out _);
        main.RestoreScreen.ShowProgress("Restoring: moving your current data aside…", closeBlocked: true);
        Assert.True(main.IsCloseBlocked);
        main.RestoreScreen.ShowProgress("Restoring: opening the restored data…", closeBlocked: false);
        Assert.False(main.IsCloseBlocked);
        main.RestoreScreen.Hide();
        Assert.False(main.RestoreScreen.IsVisible);
    }

    // ---- Backups page: Restore… (Q0–Q5, H) -----------------------------------------------------------------------------

    private sealed class FakeRequest(RestoreConfirmationFacts facts) : IRestoreRequest
    {
        public List<string> Calls { get; } = [];

        public RestoreConfirmationFacts Facts { get; } = facts;

        public Task<string> ConfirmAsync()
        {
            Calls.Add("confirm");
            return Task.FromResult(Facts.Op);
        }

        public Task CancelAsync()
        {
            Calls.Add("cancel");
            return Task.CompletedTask;
        }

        public void Dispose() => Calls.Add("dispose");
    }

    private sealed class FakeRequester(Func<RestoreRequestStart> begin) : IRestoreRequester
    {
        public int Calls { get; private set; }

        public Task<RestoreRequestStart> BeginAsync(BackupEntry entry, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(begin());
        }
    }

    private sealed class FakeLifetime : IAppLifetime
    {
        public bool RelaunchSucceeds { get; set; } = true;

        public List<string> Calls { get; } = [];

        public bool Relaunch(string? restoreOp)
        {
            Calls.Add($"relaunch {restoreOp ?? "-"}");
            return RelaunchSucceeds;
        }

        public void Shutdown() => Calls.Add("shutdown");
    }

    private sealed class StaticCatalog(params BackupEntry[] entries) : IBackupCatalog
    {
        public int LatestSchemaVersion => 7;

        public Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BackupEntry>>(entries);

        public Task<BackupCheckResult> CheckAsync(BackupEntry entry, BackupCheckLevel level, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupCheckResult(level == BackupCheckLevel.Full ? BackupStatus.FullyValidated : BackupStatus.QuickChecked, 7));

        public string? OpenFolder() => null;
    }

    private static async Task<(BackupsViewModel Vm, FakeRequest Request, FakeDialogs Dialogs, FakeLifetime Lifetime)> BackupsAsync(bool confirm, bool relaunch = true)
    {
        var request = new FakeRequest(Facts(new DataClassification(CurrentDataClass.Healthy, Version: 7)));
        var dialogs = new FakeDialogs { OnRestoreConfirmation = _ => confirm };
        var lifetime = new FakeLifetime { RelaunchSucceeds = relaunch };
        var vm = new BackupsViewModel(new StaticCatalog(Daily, new BackupEntry("notes.txt", BackupFolder.Daily, BackupKind.Unrecognized, 1, null, null)),
            new FakeRequester(() => new RestoreRequestStart(request, null)), dialogs, lifetime);
        await vm.LoadAsync();
        return (vm, request, dialogs, lifetime);
    }

    [Fact]
    public async Task Q5a_TheConfirmationIsCancelled_TheRequestIsCancelled_NoRelaunch()
    {
        var (vm, request, _, lifetime) = await BackupsAsync(confirm: false);
        vm.SelectedRow = vm.Rows[0];

        await vm.RestoreCommand.ExecuteAsync();

        Assert.Equal(["cancel", "dispose"], request.Calls);
        Assert.Empty(lifetime.Calls);
        Assert.False(vm.IsRestoring);
    }

    [Fact]
    public async Task Q5b_Confirmed_PublishesThenRelaunchesWithTheTokenAndExits()
    {
        var (vm, request, _, lifetime) = await BackupsAsync(confirm: true);
        vm.SelectedRow = vm.Rows[0];
        var confirmedRaised = false;
        vm.RestoreConfirmed += (_, _) => confirmedRaised = true;

        await vm.RestoreCommand.ExecuteAsync();

        Assert.Equal(["confirm", "dispose"], request.Calls);
        Assert.Equal(["relaunch 20261016-000000-00000001", "shutdown"], lifetime.Calls);
        Assert.True(confirmedRaised);
    }

    [Fact]
    public async Task Q5_RelaunchFails_TheRequestStaysConfirmed_TheUserIsToldToStartAgain()
    {
        var (vm, request, dialogs, lifetime) = await BackupsAsync(confirm: true, relaunch: false);
        vm.SelectedRow = vm.Rows[0];

        await vm.RestoreCommand.ExecuteAsync();

        Assert.Contains("confirm", request.Calls);
        Assert.Contains("Start WorkHub again to continue or cancel the restore", dialogs.Messages.Single().Message);
        Assert.Equal("shutdown", lifetime.Calls[^1]);
    }

    [Fact]
    public async Task H_UnsavedSettingsCancel_CancelsTheRestore()
    {
        var (vm, request, _, lifetime) = await BackupsAsync(confirm: true);
        vm.BeforeConfirm = () => Task.FromResult(false); // the unsaved-settings prompt answered Cancel
        vm.SelectedRow = vm.Rows[0];

        await vm.RestoreCommand.ExecuteAsync();

        Assert.Equal(["cancel", "dispose"], request.Calls);
        Assert.Empty(lifetime.Calls);
    }

    [Fact]
    public async Task ARefusal_IsShown_NothingElseHappens()
    {
        var dialogs = new FakeDialogs();
        var requester = new FakeRequester(() => new RestoreRequestStart(null, "Another WorkHub window is preparing or performing a restore."));
        var vm = new BackupsViewModel(new StaticCatalog(Daily), requester, dialogs, new FakeLifetime());
        await vm.LoadAsync();
        vm.SelectedRow = vm.Rows[0];

        await vm.RestoreCommand.ExecuteAsync();

        Assert.Equal("Another WorkHub window is preparing or performing a restore.", vm.Error);
        Assert.Empty(dialogs.Messages);
    }

    [Fact]
    public async Task Restore_IsOfferedOnlyForRecognizedBackups_AndOnlyWithARequester()
    {
        var (vm, _, _, _) = await BackupsAsync(confirm: false);
        vm.SelectedRow = vm.Rows[1]; // notes.txt
        Assert.False(vm.RestoreCommand.CanExecute(null));
        vm.SelectedRow = vm.Rows[0];
        Assert.True(vm.RestoreCommand.CanExecute(null));
        Assert.True(vm.IsRestoreAvailable);
    }

    [Fact]
    public async Task H_SettingsUnsavedChanges_SaveDiscardOrCancelBeforeConfirming()
    {
        BackupKit.CreateDatabase(Root);
        var store = new JsonSettingsStore(Root.Paths.SettingsFile);
        var settings = new SettingsService(store, _ => true);
        settings.Initialize(store.LoadOrCreate());
        var dialogs = new FakeDialogs();
        var backups = new BackupsViewModel(new StaticCatalog(Daily), new FakeRequester(() => new RestoreRequestStart(null, "x")), dialogs, new FakeLifetime());
        var vm = new SettingsViewModel(settings, new SystemService(new SqliteSystemStore(new SqliteConnectionFactory(Root.Paths.DatabaseFile))),
            Root.CreateLegacyImportService(), dialogs, () => null, backups: backups);

        Assert.True(await backups.BeforeConfirm!()); // nothing unsaved: no prompt
        Assert.Equal(0, dialogs.UnsavedChangesPrompts);

        vm.GitHost = "https://changed.example.test/";
        dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Cancel;
        Assert.False(await backups.BeforeConfirm!());
        dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Discard;
        Assert.True(await backups.BeforeConfirm!());
        Assert.NotEqual("https://changed.example.test/", settings.Current.Repository.Git.Host);
        dialogs.OnUnsavedChanges = _ => UnsavedChangesChoice.Save;
        Assert.True(await backups.BeforeConfirm!());
        Assert.Equal("https://changed.example.test/", settings.Current.Repository.Git.Host);
        Assert.Equal(3, dialogs.UnsavedChangesPrompts);
    }

    // ---- owner window (§10.1, U6) ----------------------------------------------------------------------------------------

    private sealed class FakeOwner : IRestoreOwner
    {
        public RestoreOwnerStatus? Status { get; set; }

        public bool Unfinished { get; set; }

        public List<string> Calls { get; } = [];

        public OwnerActionResult NextPutBack { get; set; } = new(true, RelaunchOp: "20261016-000000-00000001");

        public OwnerActionResult RecordCompletion()
        {
            Calls.Add("record");
            Status = null;
            return new OwnerActionResult(true);
        }

        public OwnerActionResult RequestPutBack()
        {
            Calls.Add("putback");
            return NextPutBack;
        }

        public bool HasUnfinishedOperation() => Unfinished;

        public string? OpenRestoreFolder()
        {
            Calls.Add("open");
            return null;
        }
    }

    [Theory]
    [InlineData(RestoreOwnerSituation.PublishedInitializationFailed, true, false, true, "Try starting again")]
    [InlineData(RestoreOwnerSituation.MovedAside, true, false, true, "Retry")]
    [InlineData(RestoreOwnerSituation.MovingAside, false, false, true, "Retry")]
    [InlineData(RestoreOwnerSituation.PutBackStopped, false, false, true, "Retry")]
    [InlineData(RestoreOwnerSituation.CompletionUnrecorded, false, true, false, "Retry")]
    public void Owner_ActionsBySituation(RestoreOwnerSituation situation, bool putBack, bool tryAgain, bool restart, string restartText)
    {
        var owner = new FakeOwner { Status = new RestoreOwnerStatus(situation, Op(RestoreState.Published), "message") };
        var vm = new RestoreOwnerViewModel(owner, new FakeDialogs(), new FakeLifetime(), NullAppLog.Instance);

        Assert.True(vm.IsVisible);
        Assert.Equal(putBack, vm.CanPutBack);
        Assert.Equal(tryAgain, vm.CanTryAgain);
        Assert.Equal(restart, vm.CanRestart);
        Assert.Equal(restartText, vm.RestartText);
    }

    [Fact]
    public async Task Owner_PutBack_IsConfirmedAsFinal_DecliningWritesNothing_AcceptingRelaunchesWithTheToken()
    {
        var owner = new FakeOwner { Status = new RestoreOwnerStatus(RestoreOwnerSituation.PublishedInitializationFailed, Op(RestoreState.Published), "m") };
        var dialogs = new FakeDialogs { ConfirmAnswer = false };
        var lifetime = new FakeLifetime();
        var vm = new RestoreOwnerViewModel(owner, dialogs, lifetime, NullAppLog.Instance);

        await vm.PutBackCommand.ExecuteAsync();
        Assert.Empty(owner.Calls); // declined: nothing written
        Assert.Contains("Once you choose Put back, it cannot be cancelled", dialogs.Confirmations.Single().Message);
        Assert.Contains("continues the next time WorkHub starts", dialogs.Confirmations.Single().Message);

        dialogs.ConfirmAnswer = true;
        await vm.PutBackCommand.ExecuteAsync();
        Assert.Equal(["putback"], owner.Calls);
        Assert.Equal(["relaunch 20261016-000000-00000001", "shutdown"], lifetime.Calls);
    }

    [Fact]
    public async Task Owner_PutBackRefusedOnTheReRead_ShowsTheReason_NoRelaunch()
    {
        var owner = new FakeOwner
        {
            Status = new RestoreOwnerStatus(RestoreOwnerSituation.MovedAside, Op(RestoreState.MovedAside), "m"),
            NextPutBack = new OwnerActionResult(false, "The restore state changed; nothing was written."),
        };
        var lifetime = new FakeLifetime();
        var vm = new RestoreOwnerViewModel(owner, new FakeDialogs(), lifetime, NullAppLog.Instance);

        await vm.PutBackCommand.ExecuteAsync();

        Assert.Equal("The restore state changed; nothing was written.", vm.Message);
        Assert.Empty(lifetime.Calls);
    }

    [Fact]
    public async Task Owner_CompletionUnrecorded_TryAgainRecords_AndTheBannerHides()
    {
        var owner = new FakeOwner { Status = new RestoreOwnerStatus(RestoreOwnerSituation.CompletionUnrecorded, Op(RestoreState.Published), "m") };
        var vm = new RestoreOwnerViewModel(owner, new FakeDialogs(), new FakeLifetime(), NullAppLog.Instance);

        await vm.TryAgainCommand.ExecuteAsync();

        Assert.Equal(["record"], owner.Calls);
        Assert.False(vm.IsVisible);
    }

    // ---- main window with the real startup ---------------------------------------------------------------------------

    private MainViewModel NewMain(out FakeLifetime lifetime, IRestoreOwner? owner = null, WorkHubServices? services = null, FakeDialogs? dialogs = null)
    {
        services ??= Own(WorkHubServices.Create(Root.Paths));
        dialogs ??= new FakeDialogs();
        lifetime = new FakeLifetime();
        var repository = new RepositoryAreaViewModel(services.Systems, services.RepositoryOpener, services.Settings, services.LegacyImport, dialogs, NullAppLog.Instance, () => null);
        var ownerArea = new OwnerAreaViewModel(services.Owners, dialogs, NullAppLog.Instance);
        var workspace = new WorkspaceAreaViewModel(services.Workspace, dialogs, NullAppLog.Instance, services.ProjectContent, services.LinkChecker, services.WorkspaceSearch);
        var backupsServices = services;
        return new MainViewModel(services.Initializer, services.Settings, new RepositoryNavigationViewModel(repository, ownerArea), workspace, NullAppLog.Instance,
            owner ?? services.RestoreOwner, lifetime, dialogs, () => new BackupsViewModel(backupsServices.Backups, backupsServices.RestoreRequester, dialogs, new FakeLifetime()));
    }

    [Fact]
    public async Task U6_FailedShell_OffersRestoreFromABackup_OnlyWithoutAnUnfinishedOperation()
    {
        BackupKit.CreateDatabase(Root);
        BackupKit.CorruptPage(Root.Paths.DatabaseFile);
        var dialogs = new FakeDialogs();
        var main = NewMain(out _, dialogs: dialogs);

        await main.InitializeAsync();

        Assert.Equal(ShellState.Failed, main.State);
        Assert.True(main.CanRestoreFromBackup);
        main.RestoreFromBackupCommand.Execute(null);
        Assert.True(dialogs.RestoreBackupsShown.Single().IsRestoreAvailable);

        var withUnfinished = NewMain(out _, owner: new FakeOwner { Unfinished = true });
        await withUnfinished.InitializeAsync();
        Assert.False(withUnfinished.CanRestoreFromBackup);
        Assert.False(withUnfinished.RestoreFromBackupCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecoveryRequired_OffersOnlyOpenTheRestoreFolderAndExit()
    {
        BackupKit.CreateDatabase(Root);
        Directory.CreateDirectory(Path.Combine(Root.Paths.RestoreDirectory, "unexpected-folder"));
        var main = NewMain(out _);

        await main.InitializeAsync();

        Assert.Equal(ShellState.Failed, main.State);
        Assert.True(main.IsRecoveryRequired);
        Assert.False(main.CanRestoreFromBackup);
        Assert.StartsWith(MainViewModel.RecoveryPrefix, main.ErrorMessage);
        Assert.Contains("Unfinished restore", main.ErrorMessage);
    }

    [Fact]
    public async Task NotNowOrExit_ClosesThisWindow_ChangingNothing()
    {
        BackupKit.CreateDatabase(Root);
        var entry = RestoreKit.PlaceDaily(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, entry);
        var main = NewMain(out var lifetime);

        var start = main.InitializeAsync();
        await WaitUntil(() => main.RestoreScreen.IsAsking);
        Assert.Equal(RestoreAskKind.Restore, main.RestoreScreen.AskKind);
        main.RestoreScreen.NotNowCommand.Execute(null);
        await start;

        Assert.Equal(["shutdown"], lifetime.Calls);
        Assert.Equal(RestoreState.Confirmed, RestoreKit.Journal(Root, op).State);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached");
            await Task.Delay(10);
        }
    }
}
