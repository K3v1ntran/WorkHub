using System.Collections.Concurrent;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>Settings → Backups page, startup notices and status (Phase 9 §9; tests H58–H59).</summary>
public sealed class BackupsViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();

    public void Dispose() => _root.Dispose();

    private static BackupEntry Daily(string name) => new(name, BackupFolder.Daily, BackupKind.Daily, 4096, DateTimeOffset.Now, 7);

    private static BackupEntry Migration(string name) => new(name, BackupFolder.Migrations, BackupKind.PreMigration, 4096, DateTimeOffset.Now, 6, 7);

    private static BackupEntry Other(string name) => new(name, BackupFolder.Daily, BackupKind.Unrecognized, 10, null, null);

    [Fact]
    public async Task Load_ListsEverything_QuickChecksRecognizedRowsOnly()
    {
        var catalog = new GatedCatalog([Daily("d1.db"), Migration("m1.db"), Other("notes.txt")]) { AutoComplete = true };
        var vm = new BackupsViewModel(catalog);

        await vm.LoadAsync();

        Assert.Equal(["d1.db", "m1.db", "notes.txt"], vm.Rows.Select(r => r.FileName));
        Assert.Equal(["d1.db:Quick", "m1.db:Quick"], catalog.Requests);
        Assert.Equal("Quick-checked", vm.Rows[0].StatusText);
        Assert.Equal("Current (v7)", vm.Rows[0].CompatibilityText);
        Assert.Equal("Older (v6); startup would upgrade it", vm.Rows[1].CompatibilityText);
        Assert.Equal("Not a WorkHub backup", vm.Rows[2].StatusText);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Validate_RunsAFullCheckOnTheSelectedRow_UnrecognizedRowsCannotBeValidated()
    {
        var catalog = new GatedCatalog([Daily("d1.db"), Other("notes.txt")]) { AutoComplete = true };
        var vm = new BackupsViewModel(catalog);
        await vm.LoadAsync();

        vm.SelectedRow = vm.Rows[1];
        Assert.False(vm.ValidateCommand.CanExecute(null));

        vm.SelectedRow = vm.Rows[0];
        await vm.ValidateCommand.ExecuteAsync();

        Assert.Equal("d1.db:Full", catalog.Requests[^1]);
        Assert.Equal("Fully validated", vm.Rows[0].StatusText);
        Assert.True(vm.Rows[0].IsFullyValidated);
    }

    [Fact]
    public async Task ALateQuickResult_NeverReplacesAFullResult()
    {
        var catalog = new GatedCatalog([Daily("d1.db")]);
        var vm = new BackupsViewModel(catalog);
        var load = vm.LoadAsync();
        await catalog.WaitForRequestAsync("d1.db:Quick");

        vm.SelectedRow = vm.Rows[0];
        var validate = vm.ValidateCommand.ExecuteAsync();
        await catalog.WaitForRequestAsync("d1.db:Full");
        catalog.Complete("d1.db:Full", new BackupCheckResult(BackupStatus.IntegrityFailed, 7, "damaged"));
        await validate;
        catalog.Complete("d1.db:Quick", new BackupCheckResult(BackupStatus.QuickChecked, 7));
        await load;

        Assert.Equal("Damaged", vm.Rows[0].StatusText);
        Assert.Equal(BackupCheckLevel.Full, vm.Rows[0].Level);
        Assert.True(vm.Rows[0].IsProblem);
    }

    [Fact]
    public async Task Refresh_DiscardsResultsOfTheOlderList()
    {
        var catalog = new GatedCatalog([Daily("d1.db")]);
        var vm = new BackupsViewModel(catalog);
        var first = vm.LoadAsync();
        await catalog.WaitForRequestAsync("d1.db:Quick");
        var oldRow = vm.Rows[0];

        catalog.AutoComplete = true;
        catalog.Entries = [Daily("d1.db"), Daily("d2.db")];
        await vm.LoadAsync();
        catalog.Complete("d1.db:Quick", new BackupCheckResult(BackupStatus.IntegrityFailed, 7, "late"));
        await first;

        Assert.Equal(["d1.db", "d2.db"], vm.Rows.Select(r => r.FileName));
        Assert.DoesNotContain(oldRow, vm.Rows);
        Assert.Equal("Quick-checked", vm.Rows[0].StatusText); // the late "damaged" result of the old list is not shown
        Assert.Null(oldRow.Result);
    }

    [Fact]
    public async Task AValidationResultForARowNoLongerShown_IsDropped_SelectionIsKeptByName()
    {
        var catalog = new GatedCatalog([Daily("d1.db")]) { AutoComplete = true };
        var vm = new BackupsViewModel(catalog);
        await vm.LoadAsync();
        vm.SelectedRow = vm.Rows[0];
        catalog.AutoComplete = false;
        var validate = vm.ValidateCommand.ExecuteAsync();
        await catalog.WaitForRequestAsync("d1.db:Full");
        var oldRow = vm.Rows[0];

        catalog.AutoComplete = true;
        await vm.LoadAsync();
        catalog.Complete("d1.db:Full", new BackupCheckResult(BackupStatus.FullyValidated, 7));
        await validate;

        Assert.NotSame(oldRow, vm.Rows[0]);
        Assert.Same(vm.Rows[0], vm.SelectedRow);
        Assert.Equal("Quick-checked", vm.Rows[0].StatusText);
    }

    [Fact]
    public async Task CheckErrors_AreShownOnTheRow_AndOpenFolderErrorsOnThePage()
    {
        var catalog = new GatedCatalog([Daily("d1.db")]) { Throw = new IOException("disk gone"), FolderError = "The Backups folder does not exist yet." };
        var vm = new BackupsViewModel(catalog);

        await vm.LoadAsync();
        vm.OpenFolderCommand.Execute(null);

        Assert.Equal("Could not be checked", vm.Rows[0].StatusText);
        Assert.Equal("disk gone", vm.Rows[0].DetailText);
        Assert.Equal("The Backups folder does not exist yet.", vm.Error);
    }

    [Fact]
    public void TheCatalogStaysReadOnly_RestoreIsTheOnlyChangingCommand_AndGoesThroughTheRequester()
    {
        // Phase 10 (R1/R2) adds Restore… to this page. It is a request carried out after a restart (IRestoreRequester);
        // the catalog itself still has no operation that writes, replaces or deletes anything.
        var commands = typeof(BackupsViewModel).GetProperties().Where(p => p.PropertyType.Name.Contains("Command", StringComparison.Ordinal)).Select(p => p.Name).Order();
        Assert.Equal(["OpenFolderCommand", "RefreshCommand", "RestoreCommand", "ValidateCommand"], commands);
        Assert.False(new BackupsViewModel(new GatedCatalog([])).IsRestoreAvailable);
        Assert.Equal(["CheckAsync", "ListAsync", "OpenFolder", "get_LatestSchemaVersion"], typeof(IBackupCatalog).GetMethods().Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.Contains("settings.json", BackupsViewModel.ScopeText);
        Assert.Contains("links point to", BackupsViewModel.ScopeText);
        Assert.Contains("when WorkHub restarts", BackupsViewModel.RecoveryText);
        Assert.Contains("can be put back", BackupsViewModel.RecoveryText);
        Assert.Contains("fully validated", BackupsViewModel.RecoveryText);
    }

    [Fact]
    public async Task RealCatalog_ListsBothFolders_AndChecksFilesWithoutChangingThem()
    {
        BackupKit.CreateDatabase(_root);
        var daily = BackupKit.PlaceValidBackup(_root, BackupKit.NameFor(new DateOnly(2026, 10, 12)));
        Directory.CreateDirectory(_root.Paths.MigrationBackupsDirectory);
        var migration = Path.Combine(_root.Paths.MigrationBackupsDirectory, "workhub_v6_before_v7_20261001-000000000.db");
        BackupKit.BuildSchema(migration, 6);
        File.WriteAllText(Path.Combine(_root.Paths.DailyBackupsDirectory, "notes.txt"), "x");
        File.WriteAllText(_root.Paths.DailyBackupLockFile, "");
        var hashes = new[] { daily, migration }.ToDictionary(f => f, BackupKit.Hash);
        var starter = new RecordingProcessStarter();
        var vm = new BackupsViewModel(new BackupCatalog(_root.Paths, starter));

        await vm.LoadAsync();
        vm.SelectedRow = vm.Rows[1];
        await vm.ValidateCommand.ExecuteAsync();
        vm.OpenFolderCommand.Execute(null);

        Assert.Equal([Path.GetFileName(daily), Path.GetFileName(migration), "notes.txt"], vm.Rows.Select(r => r.FileName));
        Assert.Equal("Quick-checked", vm.Rows[0].StatusText);
        Assert.Equal("Fully validated", vm.Rows[1].StatusText);
        Assert.Equal("Before upgrade to v7", vm.Rows[1].KindText);
        Assert.Equal("2026-10-01 09:00 JST", vm.Rows[1].TakenText);
        Assert.All(hashes, kv => Assert.Equal(kv.Value, BackupKit.Hash(kv.Key)));
        Assert.Empty(BackupKit.Sidecars(daily));
        Assert.Equal(_root.Paths.BackupsDirectory, Assert.Single(starter.Started).FileName);
    }

    [Fact]
    public void SettingsShowsTheBackupsPageUnderData_AndLoadsItWhenSelected()
    {
        _root.CreateInitializer().Initialize();
        var store = new JsonSettingsStore(_root.Paths.SettingsFile);
        var settings = new SettingsService(store, _ => true);
        settings.Initialize(store.LoadOrCreate());
        var catalog = new GatedCatalog([]) { AutoComplete = true };
        var vm = new SettingsViewModel(settings, new SystemService(new SqliteSystemStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile))),
            _root.CreateLegacyImportService(), new FakeDialogs(), () => null, backups: new BackupsViewModel(catalog));

        Assert.Equal(["REPOSITORY", "REPOSITORY", "WORKSPACE", "DATA", "DATA"], vm.Pages.Select(p => p.Group)); // Phase 13A Workspace page
        Assert.Equal(0, catalog.ListCalls);
        vm.SelectedPage = vm.Pages.Single(p => p.Page == SettingsPage.Backups);

        Assert.True(vm.IsBackupsSelected);
        Assert.False(vm.IsRepositoryPageSelected);
        Assert.False(vm.IsLegacyImportSelected);
        Assert.Equal(1, catalog.ListCalls);
    }

    [Theory]
    [InlineData(DailyBackupFailure.SourceBusy, "busy")]
    [InlineData(DailyBackupFailure.DiskFull, "not enough disk space")]
    [InlineData(DailyBackupFailure.FolderUnavailable, "Backups folder could not be written")]
    [InlineData(DailyBackupFailure.PublishFailed, "Backups folder could not be written")]
    [InlineData(DailyBackupFailure.CandidateInvalid, "failed its check")]
    [InlineData(DailyBackupFailure.NotStandalone, "failed its check")]
    [InlineData(DailyBackupFailure.SourceCheck, "No backup was made and no older backups were removed")]
    [InlineData(DailyBackupFailure.SourceMissing, "could not be found")]
    [InlineData(DailyBackupFailure.Unexpected, "could not be made")]
    public void FailureNotices(DailyBackupFailure failure, string expected) =>
        Assert.Contains(expected, DailyBackupNotices.For(DailyBackupResult.Fail(failure)));

    [Theory]
    [InlineData(DailyBackupOutcome.Created)]
    [InlineData(DailyBackupOutcome.AlreadyDone)]
    [InlineData(DailyBackupOutcome.SkippedBusy)]
    public void NoNoticeUnlessFailed(DailyBackupOutcome outcome)
    {
        Assert.Null(DailyBackupNotices.For(new DailyBackupResult(outcome)));
        Assert.Null(DailyBackupNotices.For(null));
    }

    [Fact]
    public async Task MainWindow_RealStartup_ShowsBackedUpTodayInTheStatus()
    {
        using var services = WorkHubServices.Create(_root.Paths);
        var main = NewMain(services);

        await main.InitializeAsync();

        Assert.Equal(ShellState.Ready, main.State);
        Assert.Equal("Ready · schema v9 · backed up today", main.StatusText);
        Assert.Null(main.WarningMessage);
        Assert.Single(BackupKit.PublishedBackups(_root));
        new SqliteConnectionFactory(_root.Paths.DatabaseFile).ClearPools();
    }

    [Fact]
    public async Task MainWindow_BackupFailureAndInvalidSettings_ShowBothNotices_StartupStillReady()
    {
        Directory.CreateDirectory(_root.Paths.ConfigDirectory);
        File.WriteAllText(_root.Paths.SettingsFile, "{ not json");
        Directory.CreateDirectory(_root.Paths.BackupsDirectory);
        File.WriteAllText(_root.Paths.DailyBackupsDirectory, "a file where the Daily folder should be");
        using var services = WorkHubServices.Create(_root.Paths);
        var main = NewMain(services);

        await main.InitializeAsync();

        Assert.Equal(ShellState.Ready, main.State);
        Assert.Equal("Ready · schema v9", main.StatusText);
        var lines = main.WarningMessage!.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Contains("settings.json", lines[0]);
        Assert.Contains("Backups folder could not be written", lines[1]);
        new SqliteConnectionFactory(_root.Paths.DatabaseFile).ClearPools();
    }

    private static MainViewModel NewMain(WorkHubServices services)
    {
        var dialogs = new FakeDialogs();
        var repository = new RepositoryAreaViewModel(services.Systems, services.RepositoryOpener, services.Settings, services.LegacyImport, dialogs, NullAppLog.Instance, () => null);
        var owner = new OwnerAreaViewModel(services.Owners, dialogs, NullAppLog.Instance);
        var workspace = new WorkspaceAreaViewModel(services.Workspace, dialogs, NullAppLog.Instance, services.ProjectContent, services.LinkChecker, services.WorkspaceSearch);
        return new MainViewModel(services.Initializer, services.Settings, new RepositoryNavigationViewModel(repository, owner), workspace, NullAppLog.Instance);
    }

    /// <summary>A catalog whose checks complete only when the test says so (or immediately with AutoComplete).</summary>
    private sealed class GatedCatalog(IReadOnlyList<BackupEntry> entries) : IBackupCatalog
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<BackupCheckResult>> _pending = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _requested = new();

        public IReadOnlyList<BackupEntry> Entries { get; set; } = entries;

        public bool AutoComplete { get; set; }

        public Exception? Throw { get; set; }

        public string? FolderError { get; set; }

        public int ListCalls { get; private set; }

        public List<string> Requests { get; } = [];

        public int LatestSchemaVersion => 7;

        public Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(Entries);
        }

        public Task<BackupCheckResult> CheckAsync(BackupEntry entry, BackupCheckLevel level, CancellationToken cancellationToken = default)
        {
            var key = $"{entry.FileName}:{level}";
            lock (Requests)
                Requests.Add(key);
            _requested.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            if (Throw is not null)
                return Task.FromException<BackupCheckResult>(Throw);
            if (AutoComplete)
                return Task.FromResult(new BackupCheckResult(level == BackupCheckLevel.Full ? BackupStatus.FullyValidated : BackupStatus.QuickChecked, entry.NameVersion));
            return _pending.GetOrAdd(key, _ => new TaskCompletionSource<BackupCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        public string? OpenFolder() => FolderError;

        public Task WaitForRequestAsync(string key) =>
            _requested.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(30));

        public void Complete(string key, BackupCheckResult result) =>
            _pending.GetOrAdd(key, _ => new TaskCompletionSource<BackupCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(result);
    }
}
