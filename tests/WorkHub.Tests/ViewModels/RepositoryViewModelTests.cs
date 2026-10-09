using WorkHub.Presentation.Services;
using WorkHub.Presentation.ViewModels;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// View-model behaviour against a real SQLite file, with fakes for dialogs and process launching.
/// No window is created and no external program is started.
/// </summary>
public sealed class RepositoryViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly CountingStore _store;
    private readonly SystemService _systems;
    private readonly SettingsService _settings;
    private readonly FakeLauncher _launcher = new();
    private readonly FakeDialogs _dialogs = new();

    public RepositoryViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _store = new CountingStore(new SqliteSystemStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _systems = new SystemService(_store);
        var settingsStore = new JsonSettingsStore(_root.Paths.SettingsFile);
        _settings = new SettingsService(settingsStore, _ => true);
        settingsStore.Save(AppSettings.CreateDefault()); // start from an existing, unconfigured profile (no hosts)
        _settings.Initialize(settingsStore.LoadOrCreate());
    }

    public void Dispose() => _root.Dispose();

    private RepositoryAreaViewModel CreateArea(int searchDelayMs = 30) =>
        new(_systems, new RepositoryOpenService(_settings, _launcher), _settings, _root.CreateLegacyImportService(), _dialogs, NullAppLog.Instance, () => null, searchDelayMs);

    private SettingsViewModel CreateSettings(SettingsSection section = SettingsSection.Svn) =>
        new(_settings, _systems, _root.CreateLegacyImportService(), _dialogs, () => null, section);

    private async Task SeedAsync(string code, string name, params RepositoryType[] types) =>
        Assert.True((await _systems.CreateAsync(new SystemDraft(code, name, null, true, types.Select(t => new MembershipDraft(t, true)).ToList()))).Succeeded);

    private async Task ConfigureAsync(Action<RepositorySettings> configure)
    {
        var draft = _settings.CreateRepositoryDraft();
        configure(draft);
        Assert.True((await _settings.SaveRepositoryAsync(draft, RepositoryUsage.None)).Saved);
    }

    [Fact]
    public void FixedGroups_AreAlwaysPresentWithoutData()
    {
        var area = CreateArea();

        Assert.Equal(["SVN SYSTEM", "SVN ORACLE", "GIT SYSTEM", "GIT ORACLE", "RELEASE"], area.Categories.Select(c => c.DisplayName));
        Assert.Equal(RepositoryType.SvnSystem, area.SelectedCategory.Type);
    }

    [Fact]
    public async Task SelectingAGroup_ListsOnlyThatGroup()
    {
        await SeedAsync("SVN01", "Svn", RepositoryType.SvnSystem);
        await SeedAsync("GIT01", "Git", RepositoryType.GitSystem);
        var area = CreateArea();
        await area.ActivateAsync();

        Assert.Equal(["SVN01"], area.Items.Select(i => i.SystemCode));
        Assert.Equal("SVN01", area.SelectedItem?.SystemCode);

        area.SelectedCategory = area.Categories.Single(c => c.Type == RepositoryType.GitSystem);
        await area.CurrentLoad;

        Assert.Equal(["GIT01"], area.Items.Select(i => i.SystemCode));
        area.SelectedCategory = area.Categories.Single(c => c.Type == RepositoryType.Release);
        await area.CurrentLoad;
        Assert.True(area.IsEmpty);
    }

    [Fact]
    public async Task Search_IsDebouncedAndOnlyTheLastQueryRuns()
    {
        await SeedAsync("ABC001", "Billing System", RepositoryType.SvnSystem);
        await SeedAsync("ABC002", "Payroll", RepositoryType.SvnSystem);
        var area = CreateArea(searchDelayMs: 150);
        await area.ActivateAsync();
        var queriesBefore = _store.QueryCount;

        area.SearchText = "b";
        area.SearchText = "bi";
        area.SearchText = "bil";
        area.SearchText = "bill";
        await area.CurrentLoad;

        Assert.Equal(1, _store.QueryCount - queriesBefore);
        Assert.Equal(["ABC001"], area.Items.Select(i => i.SystemCode));
    }

    [Theory]
    [InlineData("販売", "ABC001")]
    [InlineData("bill", "XYZ002")]
    public async Task Search_MatchesSystemNameThroughTheViewModel(string query, string expected)
    {
        await SeedAsync("ABC001", "販売管理システム", RepositoryType.SvnSystem);
        await SeedAsync("XYZ002", "Billing System", RepositoryType.SvnSystem);
        var area = CreateArea();
        await area.ActivateAsync();

        area.SearchText = query;
        await area.CurrentLoad;

        Assert.Equal([expected], area.Items.Select(i => i.SystemCode));
    }

    [Fact]
    public async Task CloseSearch_ClearsThenHides()
    {
        await SeedAsync("ABC001", "Billing", RepositoryType.SvnSystem);
        await SeedAsync("ABC002", "Payroll", RepositoryType.SvnSystem);
        var area = CreateArea();
        await area.ActivateAsync();
        area.ShowSearchCommand.Execute(null);
        area.SearchText = "pay";
        await area.CurrentLoad;
        Assert.Single(area.Items);

        area.CloseSearchCommand.Execute(null);
        await area.CurrentLoad;
        Assert.True(area.IsSearchVisible);
        Assert.Equal(2, area.Items.Count);

        area.CloseSearchCommand.Execute(null);
        Assert.False(area.IsSearchVisible);
    }

    [Fact]
    public async Task AddSystem_RefreshesCurrentListAndSelectsTheNewSystem()
    {
        var area = CreateArea();
        await area.ActivateAsync();
        _dialogs.OnSystemEditor = editor =>
        {
            editor.SystemCode = "NEW001";
            editor.SystemName = "新システム";
            editor.Memberships.Single(m => m.Type == RepositoryType.SvnSystem).IsSelected = true;
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return editor.SavedId is not null;
        };

        await area.AddSystemCommand.ExecuteAsync();

        Assert.Equal("NEW001", area.SelectedItem?.SystemCode);
        Assert.Equal("新システム", area.SelectedItem?.SystemName);
    }

    [Fact]
    public async Task EditingMemberships_UpdatesTheVisibleGroup()
    {
        await SeedAsync("ABC001", "Billing", RepositoryType.SvnSystem);
        var area = CreateArea();
        await area.ActivateAsync();
        _dialogs.OnSystemEditor = editor =>
        {
            editor.Memberships.Single(m => m.Type == RepositoryType.SvnSystem).IsSelected = false;
            editor.Memberships.Single(m => m.Type == RepositoryType.GitSystem).IsSelected = true;
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return editor.SavedId is not null;
        };

        await area.EditSystemCommand.ExecuteAsync();

        Assert.Empty(area.Items);
        area.SelectedCategory = area.Categories.Single(c => c.Type == RepositoryType.GitSystem);
        await area.CurrentLoad;
        Assert.Equal(["ABC001"], area.Items.Select(i => i.SystemCode));
    }

    [Fact]
    public async Task Deactivate_AsksForConfirmation_AndHidesTheSystem()
    {
        await SeedAsync("ABC001", "Billing", RepositoryType.SvnSystem);
        var area = CreateArea();
        await area.ActivateAsync();

        _dialogs.ConfirmAnswer = false;
        await area.ToggleActiveCommand.ExecuteAsync();
        Assert.Single(area.Items);

        _dialogs.ConfirmAnswer = true;
        await area.ToggleActiveCommand.ExecuteAsync();
        Assert.Empty(area.Items);

        area.ShowInactive = true;
        await area.CurrentLoad;
        Assert.False(Assert.Single(area.Items).IsActive);
    }

    [Fact]
    public async Task OpenWithoutTortoise_ShowsFixableBanner_AndDoesNotThrow()
    {
        await SeedAsync("ABC001", "Billing", RepositoryType.SvnSystem);
        await ConfigureAsync(s => s.Svn.Host = "https://svn.example.test/svn/");
        _launcher.Result = new LaunchResult(LaunchProblemKind.TortoiseNotConfigured, "TortoiseSVN is not configured or cannot be found.", SettingsSection.Svn);
        var area = CreateArea();
        await area.ActivateAsync();

        await area.OpenSelectedCommand.ExecuteAsync();

        Assert.Equal("TortoiseSVN is not configured or cannot be found.", area.BannerMessage);
        Assert.True(area.BannerCanFixSettings);
        Assert.True(area.FixSettingsCommand.CanExecute(null));
        Assert.Equal(LaunchKind.TortoiseSvn, _launcher.Requests.Single().Kind);
    }

    [Fact]
    public async Task OpenGitWithoutHost_ReportsGitProblemOnly()
    {
        await SeedAsync("ABC001", "Billing", RepositoryType.GitSystem);
        var area = CreateArea();
        await area.ActivateAsync();
        area.SelectedCategory = area.Categories.Single(c => c.Type == RepositoryType.GitSystem);
        await area.CurrentLoad;

        await area.OpenSelectedCommand.ExecuteAsync();

        Assert.Equal("GitBucket host is not configured.", area.BannerMessage);
        Assert.Empty(_launcher.Requests);
    }

    [Fact]
    public async Task Open_PassesResolvedTargetToTheLauncher()
    {
        await SeedAsync("ABC001", "Billing", RepositoryType.GitSystem);
        await ConfigureAsync(s => s.Git.Host = "https://git.example.test/gitbucket/root");
        var area = CreateArea();
        await area.ActivateAsync();
        area.SelectedCategory = area.Categories.Single(c => c.Type == RepositoryType.GitSystem);
        await area.CurrentLoad;

        await area.OpenSelectedCommand.ExecuteAsync();

        var request = Assert.Single(_launcher.Requests);
        Assert.Equal(LaunchKind.Browser, request.Kind);
        Assert.Equal("https://git.example.test/gitbucket/root/GeSL_src_ABC001", request.Target);
        Assert.Null(area.BannerMessage);
    }

    [Fact]
    public void NewSystemEditor_PreselectsNoMembership()
    {
        var editor = SystemEditorViewModel.ForCreate(_systems);

        Assert.All(editor.Memberships, m => Assert.False(m.IsSelected));
        Assert.Empty(editor.BuildDraft().Memberships);
    }

    [Fact]
    public async Task Editor_ShowsFieldErrorsAndStaysOpen()
    {
        var editor = SystemEditorViewModel.ForCreate(_systems);
        var closed = false;
        editor.CloseRequested += (_, _) => closed = true;
        editor.SystemCode = "bad code";

        await editor.SaveCommand.ExecuteAsync();

        Assert.False(closed);
        Assert.NotNull(editor.CodeError);
        Assert.NotNull(editor.NameError);
        Assert.NotNull(editor.MembershipsError);
    }

    [Fact]
    public async Task SettingsCancel_PersistsNothing()
    {
        var before = File.ReadAllBytes(_root.Paths.SettingsFile);
        var vm = CreateSettings();
        bool? closedWith = null;
        vm.CloseRequested += (_, saved) => closedWith = saved;
        await vm.LoadUsageAsync();

        vm.SvnHost = "https://svn.example.test/svn/";
        vm.CancelCommand.Execute(null);

        Assert.False(closedWith);
        Assert.Equal(string.Empty, _settings.Current.Repository.Svn.Host);
        Assert.Equal(before, File.ReadAllBytes(_root.Paths.SettingsFile));
    }

    [Fact]
    public async Task SettingsPreview_UsesTheResolverOnTheDraft()
    {
        var vm = CreateSettings();
        await vm.LoadUsageAsync();

        vm.SvnHost = "https://svn.example.test/svn";
        vm.GitHost = "https://git.example.test/gitbucket/root/";

        var preview = vm.Preview.ToDictionary(p => p.DisplayName);
        Assert.Equal("https://svn.example.test/svn/GeSL_doc_TEST001", preview["SVN SYSTEM"].Text);
        Assert.Equal("https://svn.example.test/svn/GeSL_rel_TEST001", preview["RELEASE"].Text);
        Assert.Equal("https://git.example.test/gitbucket/root/GeSL_src_TEST001", preview["GIT SYSTEM"].Text);
        Assert.Equal("SVN ORACLE prefix is not configured.", preview["SVN ORACLE"].Text);
        Assert.True(preview["SVN ORACLE"].IsProblem);
        foreach (var row in vm.Preview.Where(r => !r.IsProblem))
        {
            var type = RepositoryCatalog.All.Single(i => i.DisplayName == row.DisplayName).Type;
            Assert.Equal(RepositoryTargetResolver.Resolve(type, "TEST001", null, vm.Draft).Target, row.Text);
        }
    }

    [Fact]
    public async Task SettingsSave_WithInvalidValue_StaysOpenAndKeepsInput()
    {
        var vm = CreateSettings(SettingsSection.Git);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;
        await vm.LoadUsageAsync();

        vm.GitHost = "https://git.example.test/";
        vm.SvnHost = "not-a-url";
        await vm.SaveCommand.ExecuteAsync();

        Assert.False(closed);
        Assert.Equal("not-a-url", vm.SvnHost);
        Assert.Equal(SettingsSection.Svn, vm.SelectedSection);
        Assert.NotNull(vm.SaveError);
        Assert.Equal(string.Empty, _settings.Current.Repository.Git.Host);
    }

    [Fact]
    public async Task SettingsSave_UpdatesResolverInput()
    {
        var vm = CreateSettings();
        await vm.LoadUsageAsync();
        vm.SvnHost = "https://svn.example.test/svn/";

        await vm.SaveCommand.ExecuteAsync();

        Assert.Equal("https://svn.example.test/svn/", _settings.Current.Repository.Svn.Host);
    }

    private sealed class FakeLauncher : IRepositoryLauncher
    {
        public List<LaunchRequest> Requests { get; } = [];

        public LaunchResult Result { get; set; } = LaunchResult.Ok;

        public LaunchResult Launch(LaunchRequest request)
        {
            Requests.Add(request);
            return Result;
        }
    }

    private sealed class CountingStore(ISystemStore inner) : ISystemStore
    {
        private int _queryCount;

        public int QueryCount => Volatile.Read(ref _queryCount);

        public Task<SystemQueryResult> QueryAsync(SystemQuery query, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _queryCount);
            return inner.QueryAsync(query, cancellationToken);
        }

        public Task<SystemDetails?> GetAsync(long id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);

        public Task<long> CreateAsync(SystemDraft draft, CancellationToken cancellationToken = default) => inner.CreateAsync(draft, cancellationToken);

        public Task<bool> UpdateAsync(long id, SystemDraft draft, CancellationToken cancellationToken = default) => inner.UpdateAsync(id, draft, cancellationToken);

        public Task<bool> SetActiveAsync(long id, bool isActive, CancellationToken cancellationToken = default) => inner.SetActiveAsync(id, isActive, cancellationToken);

        public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);

        public Task<RepositoryUsage> GetUsageAsync(CancellationToken cancellationToken = default) => inner.GetUsageAsync(cancellationToken);
    }
}
