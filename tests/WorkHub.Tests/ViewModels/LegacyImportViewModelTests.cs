using WorkHub.Core.Diagnostics;
using WorkHub.Core.LegacyImport;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.LegacyImport;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>Settings → Data → Import Legacy XML, driven through view models (no window, no real file dialog).</summary>
public sealed class LegacyImportViewModelTests : IDisposable
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyHubData.xml");

    private readonly TempWorkHubRoot _root = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly SystemService _systems;
    private readonly SettingsService _settings;

    public LegacyImportViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _systems = new SystemService(new SqliteSystemStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        var store = new JsonSettingsStore(_root.Paths.SettingsFile);
        _settings = new SettingsService(store, _ => true);
        _settings.Initialize(store.LoadOrCreate());
    }

    public void Dispose() => _root.Dispose();

    private SettingsViewModel CreateSettings() => new(_settings, _systems, _root.CreateLegacyImportService(), _dialogs, () => null);

    private long SystemCount()
    {
        using var connection = _root.Open();
        return Db.Count(connection, "Systems");
    }

    [Fact]
    public void SettingsHasADataPageForLegacyImport()
    {
        var vm = CreateSettings();

        Assert.Equal(["REPOSITORY", "REPOSITORY", "WORKSPACE", "DATA"], vm.Pages.Select(p => p.Group)); // Phase 13A Workspace page
        vm.SelectedPage = vm.Pages.Single(p => p.Page == SettingsPage.LegacyImport);

        Assert.True(vm.IsLegacyImportSelected);
        Assert.False(vm.IsRepositoryPageSelected);
        Assert.False(vm.IsSvnSelected || vm.IsGitSelected);
        Assert.Equal(SettingsSection.Svn, vm.SelectedSection); // repository-facing API unaffected
    }

    [Fact]
    public async Task ChooseFile_UsesXmlFilter_AndCancelDoesNothing()
    {
        var vm = CreateSettings().LegacyImport;
        _dialogs.PickFileResult = null;

        await vm.ChooseFileCommand.ExecuteAsync();

        Assert.Equal(LegacyImportViewModel.XmlFilter, _dialogs.LastPickFileFilter);
        Assert.False(vm.HasPreview);
        Assert.False(vm.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Preview_ShowsCounts_WithoutWriting_ThenImportCommits()
    {
        var vm = CreateSettings().LegacyImport;
        _dialogs.PickFileResult = FixturePath;

        await vm.ChooseFileCommand.ExecuteAsync();

        Assert.Equal("LegacyHubData.xml", vm.SourceName);
        Assert.Equal((42, 33, 75, 0, 192, 0), (vm.Plan!.SystemEntries, vm.Plan.OracleEntries, vm.Plan.NewSystems, vm.Plan.ExistingSystems, vm.Plan.NewMemberships, vm.Plan.ExistingMemberships));
        Assert.Empty(vm.Conflicts);
        Assert.Empty(vm.InvalidEntries);
        Assert.True(vm.ImportCommand.CanExecute(null));
        Assert.Equal(0, SystemCount());

        await vm.ImportCommand.ExecuteAsync();

        Assert.Equal(75, SystemCount());
        Assert.True(vm.ImportedAny);
        Assert.False(vm.HasPreview);
        Assert.Contains("Import completed.", vm.ResultMessage);
        Assert.Contains("Systems added: 75", vm.ResultMessage);
        Assert.Contains("Memberships added: 192", vm.ResultMessage);
        Assert.Contains("Conflicts skipped: 0", vm.ResultMessage);
    }

    [Fact]
    public async Task ClearDiscardsPreviewWithoutWriting()
    {
        var vm = CreateSettings().LegacyImport;
        await vm.PreviewAsync(FixturePath);

        vm.ClearCommand.Execute(null);

        Assert.False(vm.HasPreview);
        Assert.False(vm.ImportedAny);
        Assert.Equal(0, SystemCount());
    }

    [Fact]
    public async Task InvalidFile_ShowsErrorAndImportStaysDisabled()
    {
        var path = Path.Combine(_root.Paths.Root, "bad.xml");
        File.WriteAllText(path, "<NotHubData />");
        var vm = CreateSettings().LegacyImport;

        await vm.PreviewAsync(path);

        Assert.Contains("<HubData>", vm.Error);
        Assert.False(vm.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task SecondPreviewAfterImport_HasNothingToImport()
    {
        var vm = CreateSettings().LegacyImport;
        await vm.PreviewAsync(FixturePath);
        await vm.ImportCommand.ExecuteAsync();

        await vm.PreviewAsync(FixturePath);

        Assert.Equal((0, 75, 0, 192), (vm.Plan!.NewSystems, vm.Plan.ExistingSystems, vm.Plan.NewMemberships, vm.Plan.ExistingMemberships));
        Assert.NotNull(vm.NothingToImportMessage);
        Assert.False(vm.ImportCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConflictsAndInvalidEntries_AreListedForTheUser()
    {
        await _systems.CreateAsync(new SystemDraft("4000000375", "ConSite API New", null, true, [new MembershipDraft(RepositoryType.SvnSystem, true)]));
        var path = Path.Combine(_root.Paths.Root, "mixed.xml");
        File.WriteAllText(path, """<HubData><SYSTEM><Item SystemCode="4000000375" SystemName="ConSite API" /><Item SystemCode="" SystemName="Test" /></SYSTEM></HubData>""");
        var vm = CreateSettings().LegacyImport;

        await vm.PreviewAsync(path);

        var conflict = Assert.Single(vm.Conflicts);
        Assert.Equal(("4000000375", "ConSite API New", "ConSite API"), (conflict.SystemCode, conflict.CurrentName, conflict.LegacyName));
        var invalid = Assert.Single(vm.InvalidEntries);
        Assert.Equal(("SYSTEM", "<empty>", "Test", "System code is required."), (invalid.Section, invalid.SystemCode, invalid.SystemName, invalid.Reason));
    }

    [Fact]
    public async Task RepositoryList_RefreshesAfterImportFromSettings_EvenWhenSettingsIsCancelled()
    {
        var area = new RepositoryAreaViewModel(_systems, new RepositoryOpenService(_settings, new NoLauncher()), _settings,
            _root.CreateLegacyImportService(), _dialogs, NullAppLog.Instance, () => null, searchDelayMs: 10);
        await area.ActivateAsync();
        Assert.Empty(area.Items);
        _dialogs.OnSettings = settings =>
        {
            settings.LegacyImport.PreviewAsync(FixturePath).GetAwaiter().GetResult();
            settings.LegacyImport.ImportCommand.ExecuteAsync().GetAwaiter().GetResult();
            return false; // user closes Settings with Cancel
        };

        await area.OpenSettingsCommand.ExecuteAsync();
        await area.CurrentLoad;

        Assert.Equal(42, area.Items.Count);
        Assert.Contains(area.Items, i => i.SystemCode == "4000000211" && i.SystemName == "Repor Service");
    }

    [Fact]
    public async Task SettingsCannotCloseWhileImportIsPending_ThenSuccessRefreshesTheSelectedList()
    {
        var gated = new GatedImportStore(new SqliteLegacyImportStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        var area = CreateArea(gated);
        await area.ActivateAsync();
        area.SelectedCategory = area.Categories.Single(c => c.Type == RepositoryType.SvnOracle);
        await area.CurrentLoad;
        Assert.Empty(area.Items);

        _dialogs.OnSettings = settings => PlayUserClosingDuringImport(settings, gated, () => gated.Release());

        await area.OpenSettingsCommand.ExecuteAsync();
        await area.CurrentLoad;

        Assert.Equal(75, SystemCount());
        Assert.Equal(33, area.Items.Count); // the selected group (SVN ORACLE) was re-queried
        Assert.Equal(RepositoryType.SvnOracle, area.SelectedCategory.Type);
    }

    [Fact]
    public async Task SettingsCannotCloseWhileImportIsPending_ThenFailureStaysVisibleWithoutSuccess()
    {
        var gated = new GatedImportStore(new SqliteLegacyImportStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        var area = CreateArea(gated);
        await area.ActivateAsync();
        SettingsViewModel? shown = null;

        _dialogs.OnSettings = settings =>
        {
            shown = settings;
            return PlayUserClosingDuringImport(settings, gated, () => gated.Fail(new InvalidOperationException("disk I/O error")));
        };

        await area.OpenSettingsCommand.ExecuteAsync();

        var import = shown!.LegacyImport;
        Assert.Equal("The import failed and was rolled back. No changes were made.", import.Error);
        Assert.Null(import.ResultMessage);
        Assert.False(import.ImportedAny);
        Assert.False(shown.DataChanged);
        Assert.True(import.HasPreview); // the preview stays so the user can see what was attempted
        Assert.Equal(0, SystemCount());
        Assert.Empty(area.Items);
    }

    /// <summary>
    /// Plays the modal Settings window: start the import, try every close path while the store is held, then let the
    /// store finish and close with Cancel. Returns the dialog result like ShowDialog would.
    /// </summary>
    private static bool PlayUserClosingDuringImport(SettingsViewModel settings, GatedImportStore gated, Action finishStore)
    {
        var closeRequests = new List<bool>();
        settings.CloseRequested += (_, saved) => closeRequests.Add(saved);
        settings.SelectedPage = settings.Pages.Single(p => p.Page == SettingsPage.LegacyImport);
        settings.LegacyImport.PreviewAsync(FixturePath).GetAwaiter().GetResult();

        var import = settings.LegacyImport.ImportCommand.ExecuteAsync();
        Assert.True(gated.Entered.Wait(TimeSpan.FromSeconds(10)), "import never reached the store");

        // Save, Cancel / Escape (IsCancel button → CancelCommand) and title-bar close / Alt+F4 (Closing → CanClose).
        Assert.True(settings.LegacyImport.IsImporting);
        Assert.False(settings.CanClose);
        Assert.False(settings.CancelCommand.CanExecute(null));
        Assert.False(settings.SaveCommand.CanExecute(null));
        settings.CancelCommand.Execute(null);
        settings.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
        Assert.Empty(closeRequests);
        Assert.False(import.IsCompleted);

        finishStore();
        import.GetAwaiter().GetResult();

        Assert.False(settings.LegacyImport.IsImporting);
        Assert.True(settings.CanClose); // normal closing is back
        Assert.True(settings.CancelCommand.CanExecute(null));
        Assert.True(settings.SaveCommand.CanExecute(null));
        settings.CancelCommand.Execute(null);
        Assert.Equal([false], closeRequests);
        return false;
    }

    private RepositoryAreaViewModel CreateArea(ILegacyImportStore store) =>
        new(_systems, new RepositoryOpenService(_settings, new NoLauncher()), _settings,
            new LegacyImportService(new LegacyXmlParser(), store, NullAppLog.Instance), _dialogs, NullAppLog.Instance, () => null, searchDelayMs: 10);

    /// <summary>Holds <see cref="ApplyAsync"/> until the test releases it (then applies for real) or fails it.</summary>
    private sealed class GatedImportStore(ILegacyImportStore inner) : ILegacyImportStore
    {
        private readonly TaskCompletionSource<Exception?> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Entered { get; } = new();

        public void Release() => _gate.SetResult(null);

        public void Fail(Exception exception) => _gate.SetResult(exception);

        public Task<IReadOnlyDictionary<string, ExistingSystemSnapshot>> LoadSnapshotAsync(IReadOnlyList<string> systemCodes, CancellationToken cancellationToken = default) =>
            inner.LoadSnapshotAsync(systemCodes, cancellationToken);

        public async Task<LegacyImportPlan> ApplyAsync(IReadOnlyList<LegacyEntry> entries, CancellationToken cancellationToken = default)
        {
            Entered.Set();
            if (await _gate.Task is { } failure)
                throw failure;
            return await inner.ApplyAsync(entries, cancellationToken);
        }
    }

    private sealed class NoLauncher : IRepositoryLauncher
    {
        public LaunchResult Launch(LaunchRequest request) => LaunchResult.Ok;
    }
}
