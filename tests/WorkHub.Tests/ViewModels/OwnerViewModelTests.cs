using WorkHub.Core.Diagnostics;
using WorkHub.Core.Owner;
using WorkHub.Core.Repositories;
using WorkHub.Core.Settings;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Settings;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.Owner;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>Owner view models and sidebar navigation against a real SQLite file; no window, no external process.</summary>
public sealed class OwnerViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly OwnerService _owners;
    private readonly SystemService _systems;
    private readonly OwnerServiceTests.RecordingOwnerLauncher _launcher = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly OwnerAreaViewModel _owner;
    private readonly RepositoryAreaViewModel _repository;
    private readonly RepositoryNavigationViewModel _navigation;

    public OwnerViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _owners = new OwnerService(new SqliteOwnerStore(connections), _launcher);
        _systems = new SystemService(new SqliteSystemStore(connections));
        var settingsStore = new JsonSettingsStore(_root.Paths.SettingsFile);
        var settings = new SettingsService(settingsStore, _ => true);
        settings.Initialize(settingsStore.LoadOrCreate());

        _owner = new OwnerAreaViewModel(_owners, _dialogs, NullAppLog.Instance);
        _repository = new RepositoryAreaViewModel(_systems, new RepositoryOpenService(settings, new NoLauncher()), settings, _root.CreateLegacyImportService(), _dialogs,
            NullAppLog.Instance, () => null, searchDelayMs: 10);
        _navigation = new RepositoryNavigationViewModel(_repository, _owner);
    }

    public void Dispose() => _root.Dispose();

    private async Task<OwnerGroup> SeedGroupAsync(string name) => (await _owners.CreateGroupAsync(name)).Group!;

    private async Task<long> SeedItemAsync(long groupId, string name, OwnerActionType type = OwnerActionType.Web, string? target = null) =>
        (await _owners.CreateItemAsync(groupId, new OwnerItemDraft(name, type, target ?? $"https://{name.ToLowerInvariant()}.example.test/"))).Id!.Value;

    private async Task ActivateAsync()
    {
        await _repository.ActivateAsync();
        await _owner.ActivateAsync();
    }

    private async Task SelectGroupAsync(string name)
    {
        _owner.SelectedGroup = _owner.Groups.Single(g => g.Name == name);
        await _owner.CurrentLoad;
    }

    [Fact]
    public async Task Activation_LoadsGroupsButNoItems_AndRepositoryStaysActive()
    {
        var tools = await SeedGroupAsync("Tools");
        await SeedItemAsync(tools.Id, "Portal");

        await ActivateAsync();

        Assert.Equal(["Tools"], _owner.Groups.Select(g => g.Name));
        Assert.Null(_owner.SelectedGroup);
        Assert.Empty(_owner.Items);
        Assert.True(_navigation.IsRepositoryActive);
        Assert.Equal(RepositoryType.SvnSystem, _navigation.SelectedCategory?.Type);
        Assert.Equal(5, _repository.Categories.Count);
    }

    [Fact]
    public async Task SelectingOwnerGroup_ShowsOnlyItsItems_AndSwitchesPane()
    {
        var tools = await SeedGroupAsync("Tools");
        var docs = await SeedGroupAsync("Docs");
        await SeedItemAsync(tools.Id, "Portal");
        await SeedItemAsync(docs.Id, "Handbook");
        await ActivateAsync();

        await SelectGroupAsync("Tools");

        Assert.True(_navigation.IsOwnerActive);
        Assert.Null(_navigation.SelectedCategory);
        Assert.Equal(["Portal"], _owner.Items.Select(i => i.Name));

        await SelectGroupAsync("Docs");
        Assert.Equal(["Handbook"], _owner.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task SelectingRepositoryGroup_DeactivatesOwnerAndReleasesItems()
    {
        var tools = await SeedGroupAsync("Tools");
        await SeedItemAsync(tools.Id, "Portal");
        await ActivateAsync();
        await SelectGroupAsync("Tools");

        _navigation.SelectedCategory = _repository.Categories.Single(c => c.Type == RepositoryType.GitSystem);
        await _repository.CurrentLoad;

        Assert.True(_navigation.IsRepositoryActive);
        Assert.Null(_owner.SelectedGroup);
        Assert.Empty(_owner.Items);
        Assert.Equal(RepositoryType.GitSystem, _repository.SelectedCategory.Type);
    }

    [Fact]
    public async Task AddGroup_AppendsAndSelectsIt_WithoutTouchingRepository()
    {
        await ActivateAsync();
        _dialogs.OnOwnerGroupEditor = editor =>
        {
            editor.Name = "  社内ツール ";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return editor.CreatedGroup is not null;
        };

        await _owner.AddGroupCommand.ExecuteAsync();
        await _owner.CurrentLoad;

        Assert.Equal("社内ツール", _owner.SelectedGroup?.Name);
        Assert.True(_navigation.IsOwnerActive);
        Assert.Equal(["社内ツール"], (await _owners.ListGroupsAsync()).Select(g => g.Name));
    }

    [Fact]
    public async Task AddGroup_DuplicateName_KeepsDialogOpenWithError()
    {
        await SeedGroupAsync("Tools");
        await ActivateAsync();
        OwnerGroupEditorViewModel? seen = null;
        _dialogs.OnOwnerGroupEditor = editor =>
        {
            seen = editor;
            editor.Name = "tools";
            var closed = false;
            editor.CloseRequested += (_, _) => closed = true;
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return closed;
        };

        await _owner.AddGroupCommand.ExecuteAsync();

        Assert.Contains("already exists", seen!.Error);
        Assert.Single(_owner.Groups);
    }

    [Fact]
    public async Task RenameGroup_KeepsSelectionAndItems()
    {
        var tools = await SeedGroupAsync("Tools");
        await SeedItemAsync(tools.Id, "Portal");
        await ActivateAsync();
        await SelectGroupAsync("Tools");
        var selected = _owner.SelectedGroup;
        _dialogs.OnOwnerGroupEditor = editor =>
        {
            editor.Name = "Company Tools";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return true;
        };

        await _owner.RenameGroupCommand.ExecuteAsync();

        Assert.Same(selected, _owner.SelectedGroup);
        Assert.Equal("Company Tools", _owner.SelectedGroup!.Name);
        Assert.Equal(["Portal"], _owner.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task MoveGroupDown_PersistsAndReordersSidebarInPlace()
    {
        await SeedGroupAsync("A");
        await SeedGroupAsync("B");
        await ActivateAsync();
        await SelectGroupAsync("A");

        await _owner.MoveGroupDownCommand.ExecuteAsync();

        Assert.Equal(["B", "A"], _owner.Groups.Select(g => g.Name));
        Assert.Equal(["B", "A"], (await _owners.ListGroupsAsync()).Select(g => g.Name));
        Assert.Equal("A", _owner.SelectedGroup?.Name);
    }

    [Fact]
    public async Task DeleteNonEmptyGroup_WarnsWithCount_AndSelectsNeighbour()
    {
        var tools = await SeedGroupAsync("Company Tools");
        await SeedGroupAsync("Docs");
        await SeedItemAsync(tools.Id, "One");
        await SeedItemAsync(tools.Id, "Two");
        await ActivateAsync();
        await SelectGroupAsync("Company Tools");

        _dialogs.ConfirmAnswer = false;
        await _owner.DeleteGroupCommand.ExecuteAsync();
        Assert.Equal(2, _owner.Groups.Count);

        _dialogs.ConfirmAnswer = true;
        await _owner.DeleteGroupCommand.ExecuteAsync();

        var (title, message, confirmText) = _dialogs.Confirmations.Last();
        Assert.Equal("Delete", confirmText);
        Assert.Contains("Delete \"Company Tools\"?", message);
        Assert.Contains("This will remove 2 launcher entries from WorkHub.", message);
        Assert.Contains("External files, folders, websites and programs will NOT be deleted.", message);
        Assert.Equal(["Docs"], _owner.Groups.Select(g => g.Name));
        Assert.Equal("Docs", _owner.SelectedGroup?.Name);
        _ = title;
    }

    [Fact]
    public async Task DeletingLastGroup_FallsBackToRepositoryPane()
    {
        await SeedGroupAsync("Only");
        await ActivateAsync();
        await SelectGroupAsync("Only");

        await _owner.DeleteGroupCommand.ExecuteAsync();

        Assert.Empty(_owner.Groups);
        Assert.True(_navigation.IsRepositoryActive);
        Assert.Equal(_repository.SelectedCategory, _navigation.SelectedCategory);
    }

    [Fact]
    public async Task AddItem_RefreshesOnlyTheCurrentGroupAndSelectsTheNewItem()
    {
        await SeedGroupAsync("Tools");
        await ActivateAsync();
        await SelectGroupAsync("Tools");
        _dialogs.OnOwnerItemEditor = editor =>
        {
            Assert.False(editor.IsExecutable);
            editor.Name = "Release Folder";
            editor.ActionType = OwnerActionType.Folder;
            editor.Target = @"C:\Releases";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return editor.SavedId is not null;
        };

        await _owner.AddItemCommand.ExecuteAsync();

        Assert.Equal("Release Folder", _owner.SelectedItem?.Name);
        Assert.Equal(OwnerActionType.Folder, _owner.SelectedItem?.ActionType);
    }

    [Fact]
    public async Task ItemEditor_InvalidUrl_StaysOpenWithFieldError()
    {
        await SeedGroupAsync("Tools");
        await ActivateAsync();
        await SelectGroupAsync("Tools");
        OwnerItemEditorViewModel? seen = null;
        _dialogs.OnOwnerItemEditor = editor =>
        {
            seen = editor;
            editor.Name = "Bad";
            editor.Target = "javascript:alert(1)";
            var closed = false;
            editor.CloseRequested += (_, _) => closed = true;
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return closed;
        };

        await _owner.AddItemCommand.ExecuteAsync();

        Assert.NotNull(seen!.TargetError);
        Assert.Empty(_owner.Items);
    }

    [Fact]
    public async Task EditItem_ReloadsAndKeepsSelection()
    {
        var tools = await SeedGroupAsync("Tools");
        await SeedItemAsync(tools.Id, "First");
        var id = await SeedItemAsync(tools.Id, "Second");
        await ActivateAsync();
        await SelectGroupAsync("Tools");
        _owner.SelectedItem = _owner.Items.Single(i => i.Id == id);
        _dialogs.OnOwnerItemEditor = editor =>
        {
            Assert.True(editor.IsEdit);
            editor.Name = "Second (edited)";
            editor.Username = "svc.account";
            editor.SaveCommand.ExecuteAsync().GetAwaiter().GetResult();
            return true;
        };

        await _owner.EditItemCommand.ExecuteAsync();

        Assert.Equal(id, _owner.SelectedItem?.Id);
        Assert.Equal("Second (edited)", _owner.SelectedItem?.Name);
        Assert.Equal("svc.account", (await _owners.GetItemAsync(id))!.Username);
    }

    [Fact]
    public async Task MoveItemToGroup_RemovesItFromCurrentViewAndAddsToDestination()
    {
        var source = await SeedGroupAsync("Source");
        var destination = await SeedGroupAsync("Destination");
        var id = await SeedItemAsync(source.Id, "Moving");
        await SeedItemAsync(source.Id, "Staying");
        await ActivateAsync();
        await SelectGroupAsync("Source");
        _owner.SelectedItem = _owner.Items.Single(i => i.Id == id);
        _dialogs.OnMoveOwnerItem = chooser =>
        {
            Assert.Equal(["Destination"], chooser.Destinations.Select(g => g.Name));
            chooser.SelectedGroup = chooser.Destinations.Single();
            return true;
        };

        await _owner.MoveItemToGroupCommand.ExecuteAsync();

        Assert.Equal(["Staying"], _owner.Items.Select(i => i.Name));
        await SelectGroupAsync("Destination");
        Assert.Equal(id, Assert.Single(_owner.Items).Id);
    }

    [Fact]
    public async Task MoveItemUp_PersistsOrderAndKeepsSelection()
    {
        var tools = await SeedGroupAsync("Tools");
        await SeedItemAsync(tools.Id, "A");
        var b = await SeedItemAsync(tools.Id, "B");
        await ActivateAsync();
        await SelectGroupAsync("Tools");
        _owner.SelectedItem = _owner.Items.Single(i => i.Id == b);

        await _owner.MoveItemUpCommand.ExecuteAsync();

        Assert.Equal(["B", "A"], _owner.Items.Select(i => i.Name));
        Assert.Equal(b, _owner.SelectedItem?.Id);
        Assert.Equal(["B", "A"], (await _owners.ListItemsAsync(tools.Id)).Select(i => i.Name));
    }

    [Fact]
    public async Task DeleteItem_ConfirmsAndRemovesFromView()
    {
        var tools = await SeedGroupAsync("Tools");
        await SeedItemAsync(tools.Id, "Doc", OwnerActionType.File, @"C:\Example\document.txt");
        await ActivateAsync();
        await SelectGroupAsync("Tools");

        await _owner.DeleteItemCommand.ExecuteAsync();

        Assert.Contains("is NOT deleted", _dialogs.Confirmations.Single().Message);
        Assert.Empty(_owner.Items);
        Assert.True(_owner.IsEmpty);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task OpenFailure_ShowsBannerWithTargetAndEdit_AndAppStaysUsable()
    {
        var tools = await SeedGroupAsync("Tools");
        await SeedItemAsync(tools.Id, "Share", OwnerActionType.Folder, @"Z:\Company\Tools");
        await ActivateAsync();
        await SelectGroupAsync("Tools");
        _launcher.Result = new OwnerLaunchResult(OwnerLaunchProblem.TargetMissing, "Folder could not be found.");

        await _owner.OpenItemCommand.ExecuteAsync();

        Assert.Equal("Folder could not be found.", _owner.BannerMessage);
        Assert.Equal(@"Z:\Company\Tools", _owner.BannerTarget);
        Assert.True(_owner.BannerCanEdit);
        Assert.Single(_owner.Items); // the item is never removed
        Assert.True(_owner.EditFromBannerCommand.CanExecute(null));
        Assert.Null(_repository.BannerMessage); // Repository is unaffected
    }

    [Fact]
    public async Task CopyTarget_CopiesTheStoredTarget()
    {
        var tools = await SeedGroupAsync("Tools");
        await SeedItemAsync(tools.Id, "Portal", OwnerActionType.Web, "https://portal.example.test/x");
        await ActivateAsync();
        await SelectGroupAsync("Tools");

        _owner.CopyTargetCommand.Execute(null);

        Assert.Equal("https://portal.example.test/x", _dialogs.Clipboard);
    }

    [Fact]
    public void ItemEditor_ForNewItem_DoesNotExposePasswordOrCredential()
    {
        var properties = typeof(OwnerItemEditorViewModel).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(properties, p => p.Contains("Password", StringComparison.OrdinalIgnoreCase)
                                               || p.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                                               || p.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RepositoryGroups_StillWorkAlongsideOwner()
    {
        await _systems.CreateAsync(new SystemDraft("ABC001", "販売管理システム", null, true, [new MembershipDraft(RepositoryType.SvnSystem, true)]));
        await SeedGroupAsync("Tools");
        await ActivateAsync();
        await SelectGroupAsync("Tools");

        _navigation.SelectedCategory = _repository.Categories.Single(c => c.Type == RepositoryType.SvnSystem);
        await _repository.CurrentLoad;

        Assert.Equal(["ABC001"], _repository.Items.Select(i => i.SystemCode));
        _repository.SearchText = "販売";
        await _repository.CurrentLoad;
        Assert.Equal(["ABC001"], _repository.Items.Select(i => i.SystemCode));
    }

    private sealed class NoLauncher : IRepositoryLauncher
    {
        public LaunchResult Launch(LaunchRequest request) => LaunchResult.Ok;
    }
}
