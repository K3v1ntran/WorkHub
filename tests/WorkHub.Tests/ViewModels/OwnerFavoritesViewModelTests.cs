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

/// <summary>
/// Phase 16B Favorites in the view models (proposal §5.2–5.4, D4/D6/D14, acceptance F5–F13) against a real SQLite file on a
/// test root. "Another window" is a second <see cref="OwnerService"/> on the same database.
/// </summary>
public sealed class OwnerFavoritesViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly OwnerService _owners;
    private readonly GatedOwnerStore _store;
    private readonly OwnerService _otherWindow;
    private readonly OwnerServiceTests.RecordingOwnerLauncher _launcher = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly OwnerAreaViewModel _owner;
    private readonly RepositoryAreaViewModel _repository;
    private readonly RepositoryNavigationViewModel _navigation;

    public OwnerFavoritesViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _store = new GatedOwnerStore(new SqliteOwnerStore(connections)); // passes everything through until a gate is armed
        _owners = new OwnerService(_store, _launcher);
        _otherWindow = new OwnerService(new SqliteOwnerStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)), new OwnerServiceTests.RecordingOwnerLauncher());
        var settingsStore = new JsonSettingsStore(_root.Paths.SettingsFile);
        var settings = new SettingsService(settingsStore, _ => true);
        settings.Initialize(settingsStore.LoadOrCreate());
        _owner = new OwnerAreaViewModel(_owners, _dialogs, NullAppLog.Instance);
        _repository = new RepositoryAreaViewModel(new SystemService(new SqliteSystemStore(connections)), new RepositoryOpenService(settings, new NoLauncher()), settings,
            _root.CreateLegacyImportService(), _dialogs, NullAppLog.Instance, () => null, searchDelayMs: 10);
        _navigation = new RepositoryNavigationViewModel(_repository, _owner);
    }

    public void Dispose() => _root.Dispose();

    private sealed class NoLauncher : IRepositoryLauncher
    {
        public LaunchResult Launch(LaunchRequest request) => LaunchResult.Ok;
    }

    private async Task<OwnerGroup> GroupAsync(string name) => (await _owners.CreateGroupAsync(name)).Group!;

    private async Task<long> ItemAsync(long groupId, string name, bool favorite = false)
    {
        var id = (await _owners.CreateItemAsync(groupId, new OwnerItemDraft(name, OwnerActionType.Web, $"https://{name.ToLowerInvariant().Replace(' ', '-')}.example.test/"))).Id!.Value; // "Build server" → a valid host
        if (favorite)
            await _owners.SetFavoriteAsync(id, true);
        return id;
    }

    private async Task ActivateAsync()
    {
        await _repository.ActivateAsync();
        await _owner.ActivateAsync();
    }

    private async Task SelectGroupAsync(string name)
    {
        _owner.SelectedOwnerGroup = _owner.Groups.Single(g => g.Name == name);
        await _owner.CurrentLoad;
    }

    private async Task OpenFavoritesAsync()
    {
        _owner.SelectedFavoritesEntry = _owner.FavoritesEntry;
        await _owner.CurrentLoad;
    }

    private void Select(string itemName) => _owner.SelectedItem = _owner.Items.Single(i => i.Name == itemName);

    // ---- F5 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task F5_TheEntryIsShownOnlyWithAFavorite_OrderingCommandsAreDisabledThere_OpenWorksAsInAGroup()
    {
        var tools = await GroupAsync("Tools");
        await GroupAsync("Docs");
        var portal = await ItemAsync(tools.Id, "Portal");
        await ActivateAsync();
        Assert.False(_owner.HasFavorites);
        Assert.Empty(_owner.FavoritesEntries);

        await SelectGroupAsync("Tools");
        Select("Portal");
        Assert.Equal("Add to Favorites", _owner.FavoriteActionText);
        await _owner.AddToFavoritesCommand.ExecuteAsync();
        Assert.True(_owner.HasFavorites);
        Assert.Same(_owner.FavoritesEntry, Assert.Single(_owner.FavoritesEntries));
        Assert.Equal("Remove from Favorites", _owner.FavoriteActionText);

        await OpenFavoritesAsync();
        Assert.True(_owner.IsFavoritesOpen);
        Assert.True(_navigation.IsOwnerActive);
        Assert.Null(_owner.SelectedOwnerGroup);
        var row = Assert.Single(_owner.Items);
        Assert.Equal("Tools", row.GroupName);
        Assert.Same(row, _owner.SelectedItem);
        Assert.False(_owner.AddItemCommand.CanExecute(null));
        Assert.False(_owner.MoveItemUpCommand.CanExecute(null));
        Assert.False(_owner.MoveItemDownCommand.CanExecute(null));
        Assert.False(_owner.MoveItemToGroupCommand.CanExecute(null));
        Assert.False(_owner.RenameGroupCommand.CanExecute(null));
        Assert.False(_owner.DeleteGroupCommand.CanExecute(null));
        Assert.True(_owner.OpenItemCommand.CanExecute(null));
        Assert.True(_owner.EditItemCommand.CanExecute(null));
        Assert.True(_owner.CopyTargetCommand.CanExecute(null));
        Assert.True(_owner.RemoveFromFavoritesCommand.CanExecute(null));
        Assert.True(_owner.DeleteItemCommand.CanExecute(null));

        await _owner.OpenItemCommand.ExecuteAsync();
        Assert.Equal(portal, Assert.Single(_launcher.Launched).Id);

        await SelectGroupAsync("Tools"); // back in a group, the ordering commands work again
        Assert.True(_owner.AddItemCommand.CanExecute(null));
        Assert.False(_owner.IsFavoritesOpen);
        Assert.Null(_owner.SelectedFavoritesEntry);
    }

    // ---- F6 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task F6_AddOrRemoveOnALauncherAnotherWindowDeleted_SaysItNoLongerExists_ReloadsAndWritesNothing()
    {
        var tools = await GroupAsync("Tools");
        var gone = await ItemAsync(tools.Id, "Gone");
        await ItemAsync(tools.Id, "Stays");
        await ActivateAsync();
        await SelectGroupAsync("Tools");
        Select("Gone");
        await _otherWindow.DeleteItemAsync(gone);

        await _owner.AddToFavoritesCommand.ExecuteAsync();

        Assert.Equal("This launcher no longer exists.", _owner.BannerMessage);
        Assert.Equal(["Stays"], _owner.Items.Select(i => i.Name)); // reloaded
        Assert.Empty(await _owners.ListFavoritesAsync());           // nothing written
        Assert.False(_owner.HasFavorites);

        var stays = _owner.Items.Single();
        await _owners.SetFavoriteAsync(stays.Id, true);
        _navigation.SelectedCategory = _repository.Categories[0]; // leave and reopen the group so its rows are re-read
        await SelectGroupAsync("Tools");
        Select("Stays");
        Assert.True(_owner.RemoveFromFavoritesCommand.CanExecute(null));
        await _otherWindow.DeleteItemAsync(stays.Id);
        await _owner.RemoveFromFavoritesCommand.ExecuteAsync();
        Assert.Equal("This launcher no longer exists.", _owner.BannerMessage);
        Assert.Empty(_owner.Items);
    }

    // ---- F7–F10: the last favorite goes while Favorites is open (§5.3) ------------------------------------------------

    [Fact]
    public async Task F7_TheOnlyFavoriteIsRemovedFromFavorites_EntryGone_SelectionCleared_BackToTheGroupItWasOpenedFrom()
    {
        var g1 = await GroupAsync("G1");
        var g2 = await GroupAsync("G2");
        await ItemAsync(g1.Id, "InG1");
        var only = await ItemAsync(g2.Id, "OnlyFavorite", favorite: true);
        await ActivateAsync();
        await SelectGroupAsync("G1");
        await OpenFavoritesAsync();
        Select("OnlyFavorite");

        await _owner.RemoveFromFavoritesCommand.ExecuteAsync();

        Assert.False(_owner.HasFavorites);
        Assert.Empty(_owner.FavoritesEntries);
        Assert.False(_owner.IsFavoritesOpen);
        Assert.Equal("G1", _owner.SelectedGroup?.Name);
        Assert.NotEqual(only, _owner.SelectedItem?.Id);              // the old launcher is selected nowhere
        Assert.Equal(["InG1"], _owner.Items.Select(i => i.Name));
        var stillThere = (await _owners.GetItemAsync(only))!;        // the launcher stays in its group, unmarked
        Assert.Equal(g2.Id, stillThere.GroupId);
        Assert.False(stillThere.IsFavorite);
    }

    [Fact]
    public async Task F8_TheOnlyFavoriteIsDeleted_EntryGone_LauncherGone_BackToTheGroupItWasOpenedFrom()
    {
        var g1 = await GroupAsync("G1");
        var g2 = await GroupAsync("G2");
        var only = await ItemAsync(g2.Id, "OnlyFavorite", favorite: true);
        await ActivateAsync();
        await SelectGroupAsync("G1");
        await OpenFavoritesAsync();
        Select("OnlyFavorite");
        _dialogs.ConfirmAnswer = true;

        await _owner.DeleteItemCommand.ExecuteAsync();

        Assert.False(_owner.HasFavorites);
        Assert.Null(await _owners.GetItemAsync(only));
        Assert.Empty(await _owners.ListItemsAsync(g2.Id));
        Assert.Null(_owner.SelectedItem);                            // G1 is empty
        Assert.Equal("G1", _owner.SelectedGroup?.Name);
        Assert.Equal(g1.Id, _owner.SelectedGroup!.Id);
    }

    [Fact]
    public async Task F9_TheGroupItWasOpenedFromWasDeletedElsewhere_TheFirstExistingGroupIsSelected()
    {
        var g1 = await GroupAsync("G1");
        var g2 = await GroupAsync("G2");
        var g3 = await GroupAsync("G3");
        await ItemAsync(g3.Id, "OnlyFavorite", favorite: true);
        await ActivateAsync();
        await SelectGroupAsync("G1");
        await OpenFavoritesAsync();
        Select("OnlyFavorite");
        await _otherWindow.DeleteGroupAsync(g1.Id);

        await _owner.RemoveFromFavoritesCommand.ExecuteAsync();

        Assert.Equal(g2.Id, _owner.SelectedGroup?.Id);               // first existing group in sidebar order
        Assert.Equal(["G2", "G3"], _owner.Groups.Select(g => g.Name)); // the deleted group left the sidebar
        Assert.False(_owner.HasFavorites);
    }

    [Fact]
    public async Task F10_AnotherWindowDeletedEveryGroup_TheStaleRemove_SaysItNoLongerExists_OwnerBecomesInactive()
    {
        var g1 = await GroupAsync("G1");
        var g2 = await GroupAsync("G2");
        await ItemAsync(g2.Id, "OnlyFavorite", favorite: true);
        await ActivateAsync();
        var repositoryGroup = _repository.Categories[2];
        _navigation.SelectedCategory = repositoryGroup;              // the Repository group active before Owner
        await SelectGroupAsync("G1");
        await OpenFavoritesAsync();
        Select("OnlyFavorite");
        await _otherWindow.DeleteGroupAsync(g1.Id);
        await _otherWindow.DeleteGroupAsync(g2.Id);                  // the cascade removes every favorite

        await _owner.RemoveFromFavoritesCommand.ExecuteAsync();

        Assert.Equal("This launcher no longer exists.", _owner.BannerMessage);
        Assert.False(_owner.HasFavorites);
        Assert.Empty(_owner.Groups);
        Assert.Null(_owner.SelectedGroup);
        Assert.False(_navigation.IsOwnerActive);
        Assert.Same(repositoryGroup, _navigation.SelectedCategory);
        Assert.Empty(_owner.Items);
    }

    // ---- F11 / F12: Delete vs. Remove from Favorites -----------------------------------------------------------------

    [Fact]
    public async Task F11_DeleteFromFavorites_NamesTheGroupAndContrastsRemoveFromFavorites_CancelWritesNothing()
    {
        var tools = await GroupAsync("Tools");
        var id = await ItemAsync(tools.Id, "Build server", favorite: true);
        await ActivateAsync();
        await OpenFavoritesAsync();
        _dialogs.ConfirmAnswer = false; // Cancel (the default button)

        await _owner.DeleteItemCommand.ExecuteAsync();

        var (title, message, confirm) = Assert.Single(_dialogs.Confirmations);
        Assert.Equal("Delete Launcher", title);
        Assert.Equal("Delete", confirm);
        Assert.Contains("Delete \"Build server\" from the group \"Tools\"?", message);
        Assert.Contains("not only from Favorites", message);
        Assert.Contains("Remove from Favorites", message);
        Assert.Contains("To keep it in \"Tools\"", message);
        Assert.Contains("The website itself is NOT deleted.", message);
        Assert.True((await _owners.GetItemAsync(id))!.IsFavorite);   // Cancel wrote nothing
        Assert.True(_owner.IsFavoritesOpen);

        // From a group, today's text plus one line for a favorite.
        await SelectGroupAsync("Tools");
        await _owner.DeleteItemCommand.ExecuteAsync();
        Assert.Contains("It is also removed from Favorites.", _dialogs.Confirmations[^1].Message);
        Assert.StartsWith("Remove \"Build server\" from WorkHub?", _dialogs.Confirmations[^1].Message);
    }

    [Fact]
    public async Task F12_RemoveFromFavorites_AsksNothing_TheGroupKeepsItsItems()
    {
        var tools = await GroupAsync("Tools");
        await ItemAsync(tools.Id, "A", favorite: true);
        await ItemAsync(tools.Id, "B", favorite: true);
        await ActivateAsync();
        await OpenFavoritesAsync();
        Select("A");

        await _owner.RemoveFromFavoritesCommand.ExecuteAsync();

        Assert.Empty(_dialogs.Confirmations);
        Assert.Equal(2, await _owners.CountItemsAsync(tools.Id));
        Assert.Equal(["B"], _owner.Items.Select(i => i.Name)); // Favorites stays open with the other favorite
        Assert.True(_owner.IsFavoritesOpen);
    }

    // ---- F13 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task F13_TheLastFavoriteIsRemovedWhileAGroupIsOpen_OnlyTheEntryDisappears()
    {
        var g1 = await GroupAsync("G1");
        var g2 = await GroupAsync("G2");
        await ItemAsync(g1.Id, "Elsewhere");
        await ItemAsync(g2.Id, "First");
        await ItemAsync(g2.Id, "Marked", favorite: true);
        await ActivateAsync();
        Assert.True(_owner.HasFavorites);
        await SelectGroupAsync("G2");
        Select("Marked");

        await _owner.RemoveFromFavoritesCommand.ExecuteAsync();

        Assert.False(_owner.HasFavorites);
        Assert.Equal("G2", _owner.SelectedGroup?.Name);
        Assert.Equal("Marked", _owner.SelectedItem?.Name);           // unchanged
        Assert.False(_owner.SelectedItem!.IsFavorite);
        Assert.Equal("Add to Favorites", _owner.FavoriteActionText);
    }

    [Fact]
    public async Task DeletingAGroupWithTheOnlyFavorites_HidesTheEntry()
    {
        var g1 = await GroupAsync("G1");
        await GroupAsync("G2");
        await ItemAsync(g1.Id, "Marked", favorite: true);
        await ActivateAsync();
        Assert.True(_owner.HasFavorites);
        await SelectGroupAsync("G1");
        _dialogs.ConfirmAnswer = true;

        await _owner.DeleteGroupCommand.ExecuteAsync();

        Assert.False(_owner.HasFavorites);
        Assert.Equal("G2", _owner.SelectedGroup?.Name);
    }

    [Fact]
    public async Task OpeningFavoritesFromARepositoryGroup_AndLosingTheLastFavorite_SelectsTheFirstGroup()
    {
        var g1 = await GroupAsync("G1");
        await GroupAsync("G2");
        await ItemAsync(g1.Id, "Marked", favorite: true);
        await ActivateAsync();
        Assert.True(_navigation.IsRepositoryActive);
        await OpenFavoritesAsync();
        Assert.True(_navigation.IsOwnerActive);
        Assert.Null(_navigation.SelectedCategory);

        await _owner.RemoveFromFavoritesCommand.ExecuteAsync();

        Assert.Equal("G1", _owner.SelectedGroup?.Name);
        Assert.True(_navigation.IsOwnerActive);
    }

    // ---- Regression (Codex review): a stale "leave empty Favorites" result never overwrites a newer screen -----------

    /// <summary>
    /// Favorites is open with one favorite that another window deleted. Remove from Favorites shows "This launcher no
    /// longer exists.", the reload finds Favorites empty and starts leaving it, and <c>ListGroupsAsync</c> is held. Meanwhile
    /// the user moves to another Owner group (or a Repository group). Releasing the gate must change nothing on that screen.
    /// </summary>
    [Theory]
    [InlineData("OwnerGroup")]
    [InlineData("RepositoryGroup")]
    public async Task StaleLeaveEmptyFavorites_AfterTheUserMovedOn_ChangesNeitherSelectionNorLaunchersNorBanner(string movedTo)
    {
        var g1 = await GroupAsync("G1");
        var g2 = await GroupAsync("G2");
        var g3 = await GroupAsync("G3");
        await ItemAsync(g1.Id, "InG1");
        await ItemAsync(g3.Id, "InG3a");
        await ItemAsync(g3.Id, "InG3b");
        var only = await ItemAsync(g2.Id, "OnlyFavorite", favorite: true);
        await ActivateAsync();
        var repositoryGroup = _repository.Categories[2];
        await SelectGroupAsync("G1");                                // where a current load would return to
        await OpenFavoritesAsync();
        Select("OnlyFavorite");
        await _otherWindow.DeleteItemAsync(only);
        var groupsBefore = _owner.Groups.ToList();

        var gate = _store.HoldNextListGroups();
        var remove = _owner.RemoveFromFavoritesCommand.ExecuteAsync();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));      // the empty-Favorites reload now waits for the groups
        Assert.Equal("This launcher no longer exists.", _owner.BannerMessage);

        if (movedTo == "OwnerGroup")
            await SelectGroupAsync("G3");
        else
            _navigation.SelectedCategory = repositoryGroup;
        var selectedBefore = _owner.SelectedGroup;
        var itemsBefore = _owner.Items;
        var selectedItemBefore = _owner.SelectedItem;

        gate.Release();
        await remove.WaitAsync(TimeSpan.FromSeconds(10));
        await _owner.CurrentLoad.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(selectedBefore, _owner.SelectedGroup);           // the selection of the new screen
        Assert.Same(itemsBefore, _owner.Items);                      // its launcher list, untouched
        Assert.Same(selectedItemBefore, _owner.SelectedItem);
        Assert.Null(_owner.BannerMessage);                           // the old refusal is not shown on the new screen
        Assert.Equal(groupsBefore, _owner.Groups);                   // the stale group list was not applied
        Assert.False(_owner.IsFavoritesOpen);
        Assert.False(_owner.HasFavorites);
        if (movedTo == "OwnerGroup")
        {
            Assert.Equal(g3.Id, _owner.SelectedGroup!.Id);           // not G1, where the stale load would have gone
            Assert.Equal(["InG3a", "InG3b"], _owner.Items.Select(i => i.Name));
            Assert.Equal("InG3a", _owner.SelectedItem?.Name);
            Assert.True(_navigation.IsOwnerActive);
        }
        else
        {
            Assert.Null(_owner.SelectedGroup);
            Assert.Empty(_owner.Items);
            Assert.True(_navigation.IsRepositoryActive);
            Assert.Same(repositoryGroup, _navigation.SelectedCategory);
        }
    }

    [Fact]
    public async Task LeaveEmptyFavorites_StillCurrentWhenTheGroupsArrive_ReturnsToTheGroupItWasOpenedFrom()
    {
        var g1 = await GroupAsync("G1");
        var g2 = await GroupAsync("G2");
        await ItemAsync(g1.Id, "InG1");
        await ItemAsync(g2.Id, "OnlyFavorite", favorite: true);
        await ActivateAsync();
        await SelectGroupAsync("G1");
        await OpenFavoritesAsync();
        Select("OnlyFavorite");

        var gate = _store.HoldNextListGroups();
        var remove = _owner.RemoveFromFavoritesCommand.ExecuteAsync();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(_owner.IsFavoritesOpen);                         // the user stays on Favorites while the groups load
        gate.Release();
        await remove.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(g1.Id, _owner.SelectedGroup?.Id);
        Assert.Equal(["InG1"], _owner.Items.Select(i => i.Name));
        Assert.False(_owner.HasFavorites);
    }

    /// <summary>
    /// Regression (Codex review, round 2): the held empty-Favorites group query of a load the user already left throws an
    /// ordinary exception (not a cancellation). The error must not reach the banner, selection or launchers of the new screen.
    /// </summary>
    [Theory]
    [InlineData("OwnerGroup")]
    [InlineData("RepositoryGroup")]
    public async Task StaleLeaveEmptyFavorites_FailsAfterTheUserMovedOn_TheNewScreenKeepsItsSelectionLaunchersAndBanner(string movedTo)
    {
        var g1 = await GroupAsync("G1");
        await GroupAsync("G2");
        var g3 = await GroupAsync("G3");
        await ItemAsync(g1.Id, "InG1");
        await ItemAsync(g3.Id, "InG3a");
        await ItemAsync(g3.Id, "InG3b");
        await ItemAsync(g1.Id, "OnlyFavorite", favorite: true);
        await ActivateAsync();
        var repositoryGroup = _repository.Categories[2];
        await SelectGroupAsync("G1");
        await OpenFavoritesAsync();
        Select("OnlyFavorite");
        var groupsBefore = _owner.Groups.ToList();

        var gate = _store.HoldNextListGroups();
        var remove = _owner.RemoveFromFavoritesCommand.ExecuteAsync();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));     // Favorites came back empty; the group query is held

        if (movedTo == "OwnerGroup")
            await SelectGroupAsync("G3");
        else
            _navigation.SelectedCategory = repositoryGroup;
        Assert.Null(_owner.BannerMessage);                          // the new screen shows no banner
        var selectedBefore = _owner.SelectedGroup;
        var itemsBefore = _owner.Items;
        var selectedItemBefore = _owner.SelectedItem;

        gate.Fail(new InvalidOperationException("database is locked (test)"));
        await remove.WaitAsync(TimeSpan.FromSeconds(10));
        await _owner.CurrentLoad.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(_owner.BannerMessage);                          // the stale error stays off the new screen
        Assert.False(_owner.BannerCanEdit);
        Assert.Same(selectedBefore, _owner.SelectedGroup);
        Assert.Same(itemsBefore, _owner.Items);
        Assert.Same(selectedItemBefore, _owner.SelectedItem);
        Assert.Equal(groupsBefore, _owner.Groups);
        Assert.False(_owner.IsLoading);
        if (movedTo == "OwnerGroup")
        {
            Assert.Equal(g3.Id, _owner.SelectedGroup!.Id);
            Assert.Equal(["InG3a", "InG3b"], _owner.Items.Select(i => i.Name));
            Assert.True(_navigation.IsOwnerActive);
        }
        else
        {
            Assert.Null(_owner.SelectedGroup);
            Assert.Empty(_owner.Items);
            Assert.True(_navigation.IsRepositoryActive);
            Assert.Same(repositoryGroup, _navigation.SelectedCategory);
        }
    }

    [Fact]
    public async Task LeaveEmptyFavorites_FailsWhileStillCurrent_TheErrorIsShownAsBefore()
    {
        var g1 = await GroupAsync("G1");
        await ItemAsync(g1.Id, "OnlyFavorite", favorite: true);
        await ActivateAsync();
        await OpenFavoritesAsync();
        Select("OnlyFavorite");

        var gate = _store.HoldNextListGroups();
        var remove = _owner.RemoveFromFavoritesCommand.ExecuteAsync();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        gate.Fail(new InvalidOperationException("database is locked (test)"));
        await remove.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Launchers could not be loaded.", _owner.BannerMessage); // the current load's error is still reported
        Assert.False(_owner.IsLoading);
    }

    /// <summary>Delegates to the real store; <see cref="HoldNextListGroups"/> holds one ListGroupsAsync call (ignoring
    /// cancellation, so the result really arrives late) until the test releases it.</summary>
    private sealed class GatedOwnerStore(IOwnerStore inner) : IOwnerStore
    {
        private Gate? _armed;

        public sealed class Gate
        {
            private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource EnteredSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => EnteredSource.Task;

            public Task Released => _released.Task;

            public void Release() => _released.TrySetResult();

            /// <summary>Lets the held query fail with <paramref name="error"/> (an ordinary exception, not a cancellation).</summary>
            public void Fail(Exception error) => _released.TrySetException(error);
        }

        public Gate HoldNextListGroups()
        {
            var gate = new Gate();
            Volatile.Write(ref _armed, gate);
            return gate;
        }

        public async Task<IReadOnlyList<OwnerGroup>> ListGroupsAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, null) is { } gate)
            {
                gate.EnteredSource.TrySetResult();
                await gate.Released.WaitAsync(TimeSpan.FromSeconds(30));
                return await inner.ListGroupsAsync(CancellationToken.None);
            }

            return await inner.ListGroupsAsync(cancellationToken);
        }

        public Task<OwnerGroup> CreateGroupAsync(string name, CancellationToken cancellationToken = default) => inner.CreateGroupAsync(name, cancellationToken);

        public Task<bool> RenameGroupAsync(long groupId, string name, CancellationToken cancellationToken = default) => inner.RenameGroupAsync(groupId, name, cancellationToken);

        public Task<bool> DeleteGroupAsync(long groupId, CancellationToken cancellationToken = default) => inner.DeleteGroupAsync(groupId, cancellationToken);

        public Task<int> CountItemsAsync(long groupId, CancellationToken cancellationToken = default) => inner.CountItemsAsync(groupId, cancellationToken);

        public Task<bool> MoveGroupAsync(long groupId, int offset, CancellationToken cancellationToken = default) => inner.MoveGroupAsync(groupId, offset, cancellationToken);

        public Task<IReadOnlyList<OwnerItemListItem>> ListItemsAsync(long groupId, CancellationToken cancellationToken = default) => inner.ListItemsAsync(groupId, cancellationToken);

        public Task<OwnerItemDetails?> GetItemAsync(long itemId, CancellationToken cancellationToken = default) => inner.GetItemAsync(itemId, cancellationToken);

        public Task<long> CreateItemAsync(long groupId, OwnerItemDraft draft, CancellationToken cancellationToken = default) => inner.CreateItemAsync(groupId, draft, cancellationToken);

        public Task<bool> UpdateItemAsync(long itemId, OwnerItemDraft draft, CancellationToken cancellationToken = default) => inner.UpdateItemAsync(itemId, draft, cancellationToken);

        public Task<bool> DeleteItemAsync(long itemId, CancellationToken cancellationToken = default) => inner.DeleteItemAsync(itemId, cancellationToken);

        public Task<bool> MoveItemToGroupAsync(long itemId, long targetGroupId, CancellationToken cancellationToken = default) => inner.MoveItemToGroupAsync(itemId, targetGroupId, cancellationToken);

        public Task<bool> MoveItemAsync(long itemId, int offset, CancellationToken cancellationToken = default) => inner.MoveItemAsync(itemId, offset, cancellationToken);

        public Task<OwnerFavoriteResult> SetFavoriteAsync(long itemId, bool isFavorite, CancellationToken cancellationToken = default) => inner.SetFavoriteAsync(itemId, isFavorite, cancellationToken);

        public Task<IReadOnlyList<OwnerItemListItem>> ListFavoritesAsync(CancellationToken cancellationToken = default) => inner.ListFavoritesAsync(cancellationToken);
    }
}
