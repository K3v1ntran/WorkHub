using WorkHub.Core.Owner;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Owner;

/// <summary>Owner groups and items through the application service, against a real SQLite file.</summary>
public sealed class OwnerServiceTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly OwnerService _owners;
    private readonly RecordingOwnerLauncher _launcher = new();

    public OwnerServiceTests()
    {
        _root.CreateInitializer().Initialize();
        _owners = new OwnerService(new SqliteOwnerStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)), _launcher);
    }

    public void Dispose() => _root.Dispose();

    private async Task<OwnerGroup> GroupAsync(string name)
    {
        var (group, error) = await _owners.CreateGroupAsync(name);
        Assert.True(group is not null, error);
        return group!;
    }

    private async Task<long> ItemAsync(long groupId, OwnerItemDraft draft)
    {
        var result = await _owners.CreateItemAsync(groupId, draft);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Id!.Value;
    }

    private async Task<List<string>> GroupNamesAsync() => (await _owners.ListGroupsAsync()).Select(g => g.Name).ToList();

    private async Task<List<string>> ItemNamesAsync(long groupId) => (await _owners.ListItemsAsync(groupId)).Select(i => i.Name).ToList();

    // ---- Groups -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FreshDatabase_HasNoOwnerGroups()
    {
        Assert.Empty(await _owners.ListGroupsAsync());
    }

    [Fact]
    public async Task CreateGroup_TrimsAndAppendsInOrder_IncludingJapanese()
    {
        await GroupAsync("  Akamai ");
        await GroupAsync("Company Tools");
        await GroupAsync("社内ツール");

        Assert.Equal(["Akamai", "Company Tools", "社内ツール"], await GroupNamesAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateGroup_RequiresName(string name)
    {
        var (group, error) = await _owners.CreateGroupAsync(name);

        Assert.Null(group);
        Assert.Equal("Group name is required.", error);
    }

    [Theory]
    [InlineData("company tools")]
    [InlineData("COMPANY TOOLS")]
    [InlineData(" Company Tools ")]
    [InlineData("Ｃｏｍｐａｎｙ Ｔｏｏｌｓ")] // full-width variant
    public async Task CreateGroup_RejectsCaseAndWidthVariantDuplicates(string duplicate)
    {
        await GroupAsync("Company Tools");

        var (group, error) = await _owners.CreateGroupAsync(duplicate);

        Assert.Null(group);
        Assert.Contains("already exists", error);
        Assert.Single(await _owners.ListGroupsAsync());
    }

    [Fact]
    public async Task CreateGroup_RejectsJapaneseDuplicateIncludingHalfWidthKana()
    {
        await GroupAsync("ツール");

        Assert.NotNull((await _owners.CreateGroupAsync("ツール")).Error);
        Assert.NotNull((await _owners.CreateGroupAsync("ﾂｰﾙ")).Error);
    }

    [Fact]
    public async Task RenameGroup_KeepsIdAndItems()
    {
        var group = await GroupAsync("Tools");
        await ItemAsync(group.Id, new OwnerItemDraft("Portal", OwnerActionType.Web, "https://portal.example.test/"));

        Assert.Null(await _owners.RenameGroupAsync(group.Id, " 社内ツール "));

        var renamed = Assert.Single(await _owners.ListGroupsAsync());
        Assert.Equal(group.Id, renamed.Id);
        Assert.Equal("社内ツール", renamed.Name);
        Assert.Equal(["Portal"], await ItemNamesAsync(group.Id));
    }

    [Fact]
    public async Task RenameGroup_ToItsOwnNameWithDifferentCase_IsAllowed_ButNotToAnotherGroup()
    {
        var tools = await GroupAsync("Tools");
        await GroupAsync("Docs");

        Assert.Null(await _owners.RenameGroupAsync(tools.Id, "TOOLS"));
        Assert.Contains("already exists", await _owners.RenameGroupAsync(tools.Id, "docs"));
        Assert.Equal(["TOOLS", "Docs"], await GroupNamesAsync());
    }

    [Fact]
    public async Task MoveGroup_PersistsSortOrderOnly()
    {
        var a = await GroupAsync("A");
        var b = await GroupAsync("B");
        var c = await GroupAsync("C");

        Assert.True(await _owners.MoveGroupAsync(c.Id, -1));
        Assert.Equal(["A", "C", "B"], await GroupNamesAsync());
        Assert.True(await _owners.MoveGroupAsync(a.Id, +5)); // clamped to the end
        Assert.Equal(["C", "B", "A"], await GroupNamesAsync());
        Assert.False(await _owners.MoveGroupAsync(c.Id, -1)); // already first
        Assert.Equal([c.Id, b.Id, a.Id], (await _owners.ListGroupsAsync()).Select(g => g.Id));
    }

    [Fact]
    public async Task DeleteEmptyGroup_RemovesOnlyThatGroup()
    {
        var a = await GroupAsync("A");
        await GroupAsync("B");

        Assert.Equal(0, await _owners.CountItemsAsync(a.Id));
        Assert.True(await _owners.DeleteGroupAsync(a.Id));

        Assert.Equal(["B"], await GroupNamesAsync());
    }

    [Fact]
    public async Task DeleteNonEmptyGroup_RemovesItsEntriesAtomically_AndNothingElse()
    {
        var tools = await GroupAsync("Company Tools");
        var keep = await GroupAsync("Keep");
        await ItemAsync(tools.Id, new OwnerItemDraft("Portal", OwnerActionType.Web, "https://portal.example.test/"));
        await ItemAsync(tools.Id, new OwnerItemDraft("Docs", OwnerActionType.Folder, @"C:\Example"));
        await ItemAsync(keep.Id, new OwnerItemDraft("Kept", OwnerActionType.File, @"C:\Example\keep.txt"));

        Assert.Equal(2, await _owners.CountItemsAsync(tools.Id));
        Assert.True(await _owners.DeleteGroupAsync(tools.Id));

        using var connection = _root.Open();
        Assert.Equal(1, Db.Count(connection, "OwnerGroups"));
        Assert.Equal(1, Db.Count(connection, "OwnerItems"));
        Assert.Equal(["Kept"], await ItemNamesAsync(keep.Id));
        Assert.Empty(_launcher.Launched); // nothing external was touched
    }

    // ---- Items --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateItems_OfAllFourTypes_PersistAllFields()
    {
        var group = await GroupAsync("Tools");
        var web = await ItemAsync(group.Id, new OwnerItemDraft("Akamai Control", OwnerActionType.Web, "https://control.example.test/", Username: "dev.user", Note: "メモ: 管理画面"));
        var file = await ItemAsync(group.Id, new OwnerItemDraft("Config.xlsx", OwnerActionType.File, @"\\fileserver\share\Config.xlsx"));
        var folder = await ItemAsync(group.Id, new OwnerItemDraft("Release Folder", OwnerActionType.Folder, @"C:\Releases\2026"));
        var exe = await ItemAsync(group.Id, new OwnerItemDraft("Internal Utility", OwnerActionType.Executable, @"C:\Program Files\Tool Suite\tool.exe",
            Arguments: "--profile \"Team A\" --verbose", WorkingDirectory: @"C:\Work Dir"));

        Assert.Equal(["Akamai Control", "Config.xlsx", "Release Folder", "Internal Utility"], await ItemNamesAsync(group.Id));

        var w = (await _owners.GetItemAsync(web))!;
        Assert.Equal(OwnerActionType.Web, w.ActionType);
        Assert.Equal("dev.user", w.Username);
        Assert.Equal("メモ: 管理画面", w.Note);
        Assert.Null(w.Arguments);

        Assert.Equal(OwnerActionType.File, (await _owners.GetItemAsync(file))!.ActionType);
        Assert.Equal(OwnerActionType.Folder, (await _owners.GetItemAsync(folder))!.ActionType);

        var e = (await _owners.GetItemAsync(exe))!;
        Assert.Equal("--profile \"Team A\" --verbose", e.Arguments);
        Assert.Equal(@"C:\Work Dir", e.WorkingDirectory);

        foreach (var id in new[] { web, file, folder, exe })
        {
            var item = (await _owners.GetItemAsync(id))!;
            Assert.False(item.IsFavorite);
            Assert.Null(item.CredentialId);
        }
    }

    [Fact]
    public async Task NonExecutableItems_DropArgumentsAndWorkingDirectory()
    {
        var group = await GroupAsync("Tools");
        var id = await ItemAsync(group.Id, new OwnerItemDraft("Docs", OwnerActionType.Folder, @"C:\Docs", Arguments: "--x", WorkingDirectory: @"C:\"));

        var item = (await _owners.GetItemAsync(id))!;
        Assert.Null(item.Arguments);
        Assert.Null(item.WorkingDirectory);
    }

    [Fact]
    public async Task CreateItem_InvalidTarget_IsRejectedAndNothingStored()
    {
        var group = await GroupAsync("Tools");

        var result = await _owners.CreateItemAsync(group.Id, new OwnerItemDraft("Bad", OwnerActionType.Web, "javascript:alert(1)"));

        Assert.False(result.Succeeded);
        Assert.Equal(OwnerItemValidator.Fields.Target, Assert.Single(result.Errors).Field);
        Assert.Empty(await _owners.ListItemsAsync(group.Id));
    }

    [Fact]
    public async Task CreateItem_InMissingGroup_FailsCleanly()
    {
        var result = await _owners.CreateItemAsync(999, new OwnerItemDraft("X", OwnerActionType.Web, "https://x.example.test/"));

        Assert.False(result.Succeeded);
        using var connection = _root.Open();
        Assert.Equal(0, Db.Count(connection, "OwnerItems"));
    }

    [Fact]
    public async Task EditItem_ChangesFields_ButKeepsGroupOrderFavoriteAndCredential()
    {
        var group = await GroupAsync("Tools");
        var first = await ItemAsync(group.Id, new OwnerItemDraft("First", OwnerActionType.Web, "https://a.example.test/"));
        var id = await ItemAsync(group.Id, new OwnerItemDraft("Second", OwnerActionType.Web, "https://b.example.test/"));
        var before = (await _owners.GetItemAsync(id))!;

        var result = await _owners.UpdateItemAsync(id, new OwnerItemDraft("Tool", OwnerActionType.Executable, @"C:\Tools\tool.exe", "-a", @"C:\Tools", "svc", "note"));

        Assert.True(result.Succeeded);
        var after = (await _owners.GetItemAsync(id))!;
        Assert.Equal(("Tool", OwnerActionType.Executable, @"C:\Tools\tool.exe", "-a", @"C:\Tools", "svc", "note"),
            (after.Name, after.ActionType, after.Target, after.Arguments, after.WorkingDirectory, after.Username, after.Note));
        Assert.Equal((before.GroupId, before.SortOrder, before.IsFavorite, before.CredentialId, before.CreatedAt),
            (after.GroupId, after.SortOrder, after.IsFavorite, after.CredentialId, after.CreatedAt));
        Assert.Equal(["First", "Tool"], await ItemNamesAsync(group.Id));
        _ = first;
    }

    [Fact]
    public async Task DeleteItem_RemovesOnlyTheWorkHubEntry()
    {
        var group = await GroupAsync("Tools");
        var id = await ItemAsync(group.Id, new OwnerItemDraft("Doc", OwnerActionType.File, @"C:\Example\document.txt"));
        await ItemAsync(group.Id, new OwnerItemDraft("Other", OwnerActionType.File, @"C:\Example\other.txt"));

        Assert.True(await _owners.DeleteItemAsync(id));

        Assert.Equal(["Other"], await ItemNamesAsync(group.Id));
        Assert.Null(await _owners.GetItemAsync(id));
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task MoveItemToGroup_KeepsIdAndAppendsAtDestinationEnd()
    {
        var source = await GroupAsync("Source");
        var destination = await GroupAsync("Destination");
        var moving = await ItemAsync(source.Id, new OwnerItemDraft("Moving", OwnerActionType.Web, "https://m.example.test/"));
        await ItemAsync(source.Id, new OwnerItemDraft("Staying", OwnerActionType.Web, "https://s.example.test/"));
        await ItemAsync(destination.Id, new OwnerItemDraft("Existing", OwnerActionType.Web, "https://e.example.test/"));

        Assert.True(await _owners.MoveItemToGroupAsync(moving, destination.Id));

        Assert.Equal(["Staying"], await ItemNamesAsync(source.Id));
        Assert.Equal(["Existing", "Moving"], await ItemNamesAsync(destination.Id));
        var moved = (await _owners.GetItemAsync(moving))!;
        Assert.Equal(destination.Id, moved.GroupId);
        using var connection = _root.Open();
        Assert.Equal(3, Db.Count(connection, "OwnerItems")); // moved, not duplicated
    }

    [Fact]
    public async Task MoveItemToGroup_SameOrMissingGroup_ChangesNothing()
    {
        var group = await GroupAsync("Tools");
        var id = await ItemAsync(group.Id, new OwnerItemDraft("X", OwnerActionType.Web, "https://x.example.test/"));

        Assert.False(await _owners.MoveItemToGroupAsync(id, group.Id));
        Assert.False(await _owners.MoveItemToGroupAsync(id, 999));

        Assert.Equal(group.Id, (await _owners.GetItemAsync(id))!.GroupId);
    }

    [Fact]
    public async Task MoveItem_ReordersWithinGroupOnly()
    {
        var group = await GroupAsync("Tools");
        var other = await GroupAsync("Other");
        var a = await ItemAsync(group.Id, new OwnerItemDraft("A", OwnerActionType.Web, "https://a.example.test/"));
        await ItemAsync(group.Id, new OwnerItemDraft("B", OwnerActionType.Web, "https://b.example.test/"));
        var c = await ItemAsync(group.Id, new OwnerItemDraft("C", OwnerActionType.Web, "https://c.example.test/"));
        await ItemAsync(other.Id, new OwnerItemDraft("Z", OwnerActionType.Web, "https://z.example.test/"));

        Assert.True(await _owners.MoveItemAsync(c, -2));
        Assert.Equal(["C", "A", "B"], await ItemNamesAsync(group.Id));
        Assert.True(await _owners.MoveItemAsync(a, +1));
        Assert.Equal(["C", "B", "A"], await ItemNamesAsync(group.Id));
        Assert.False(await _owners.MoveItemAsync(c, -1));
        Assert.Equal(["Z"], await ItemNamesAsync(other.Id));
    }

    [Fact]
    public async Task Open_UsesTheStoredTargetDirectly()
    {
        var group = await GroupAsync("Tools");
        var id = await ItemAsync(group.Id, new OwnerItemDraft("Portal", OwnerActionType.Web, "https://portal.example.test/x"));

        var result = await _owners.OpenAsync(id);

        Assert.True(result.Succeeded);
        var launched = Assert.Single(_launcher.Launched);
        Assert.Equal("https://portal.example.test/x", launched.Target);
    }

    [Fact]
    public async Task Open_DeletedItem_ReportsNotFound()
    {
        var result = await _owners.OpenAsync(12345);

        Assert.Equal(OwnerLaunchProblem.ItemNotFound, result.Problem);
        Assert.Empty(_launcher.Launched);
    }

    internal sealed class RecordingOwnerLauncher : IOwnerLauncher
    {
        public List<OwnerItemDetails> Launched { get; } = [];

        public OwnerLaunchResult Result { get; set; } = OwnerLaunchResult.Ok;

        public OwnerLaunchResult Launch(OwnerItemDetails item)
        {
            Launched.Add(item);
            return Result;
        }
    }
}
