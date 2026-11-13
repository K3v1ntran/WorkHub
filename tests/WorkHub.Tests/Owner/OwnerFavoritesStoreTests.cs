using System.Xml.Linq;
using WorkHub.Core.Owner;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Persistence.Migrations;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Owner;

/// <summary>Phase 16B Favorites in the store (proposal §5.2, acceptance F1–F4, F14), against a real SQLite file on a test root.</summary>
public sealed class OwnerFavoritesStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly OwnerService _owners;

    public OwnerFavoritesStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _owners = new OwnerService(new SqliteOwnerStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)), new OwnerServiceTests.RecordingOwnerLauncher());
    }

    public void Dispose() => _root.Dispose();

    private async Task<OwnerGroup> GroupAsync(string name) => (await _owners.CreateGroupAsync(name)).Group!;

    private async Task<long> ItemAsync(long groupId, string name) =>
        (await _owners.CreateItemAsync(groupId, new OwnerItemDraft(name, OwnerActionType.Web, $"https://{name.ToLowerInvariant()}.example.test/", Note: "n"))).Id!.Value;

    [Fact]
    public async Task F1_AddToFavorites_SetsOnlyTheMark_UpdatedAtAndEveryOtherColumnUnchanged()
    {
        var group = await GroupAsync("Tools");
        var id = await ItemAsync(group.Id, "Portal");
        var before = (await _owners.GetItemAsync(id))!;
        await Task.Delay(20); // a stray UpdatedAt = now would differ

        Assert.Equal(OwnerFavoriteResult.Saved, await _owners.SetFavoriteAsync(id, true));

        var after = (await _owners.GetItemAsync(id))!;
        Assert.True(after.IsFavorite);
        Assert.Equal(before with { IsFavorite = true }, after);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);

        Assert.Equal(OwnerFavoriteResult.Saved, await _owners.SetFavoriteAsync(id, false));
        Assert.Equal(before, await _owners.GetItemAsync(id));
    }

    [Fact]
    public async Task F2_AMissingLauncher_IsNotFound_AndNoRowChanges()
    {
        var group = await GroupAsync("Tools");
        var id = await ItemAsync(group.Id, "Portal");
        var before = await _owners.GetItemAsync(id);

        Assert.Equal(OwnerFavoriteResult.NotFound, await _owners.SetFavoriteAsync(id + 1000, true));

        Assert.Equal(before, await _owners.GetItemAsync(id));
        Assert.Empty(await _owners.ListFavoritesAsync());
    }

    [Fact]
    public async Task F3_FavoritesAreListedByGroupOrderThenItemOrder_WithTheirGroupNames()
    {
        var g1 = await GroupAsync("G1");                  // group order 0
        var g2 = await GroupAsync("G2");                  // group order 1
        var g2a = await ItemAsync(g2.Id, "Bravo");
        var g2b = await ItemAsync(g2.Id, "Alpha");
        var g1a = await ItemAsync(g1.Id, "Zulu");
        var g1b = await ItemAsync(g1.Id, "Yankee");
        await ItemAsync(g1.Id, "NotAFavorite");
        foreach (var id in new[] { g2b, g1b, g2a, g1a })
            await _owners.SetFavoriteAsync(id, true);

        var favorites = await _owners.ListFavoritesAsync();

        Assert.Equal(["Zulu", "Yankee", "Bravo", "Alpha"], favorites.Select(f => f.Name));
        Assert.Equal(["G1", "G1", "G2", "G2"], favorites.Select(f => f.GroupName));
        Assert.All(favorites, f => Assert.True(f.IsFavorite));

        Assert.True(await _owners.MoveGroupAsync(g2.Id, -1)); // G2 first now
        Assert.Equal(["Bravo", "Alpha", "Zulu", "Yankee"], (await _owners.ListFavoritesAsync()).Select(f => f.Name));
        Assert.True((await _owners.ListItemsAsync(g1.Id)).Single(i => i.Id == g1a).IsFavorite); // group lists carry the mark
        Assert.False((await _owners.ListItemsAsync(g1.Id)).Single(i => i.Name == "NotAFavorite").IsFavorite);
    }

    [Fact]
    public async Task F4_EditAndMoveKeepTheMark_DeletingTheItemOrItsGroupRemovesIt()
    {
        var g1 = await GroupAsync("G1");
        var g2 = await GroupAsync("G2");
        var edited = await ItemAsync(g1.Id, "Edited");
        var moved = await ItemAsync(g1.Id, "Moved");
        var deleted = await ItemAsync(g1.Id, "Deleted");
        var inGroup = await ItemAsync(g2.Id, "InDeletedGroup");
        foreach (var id in new[] { edited, moved, deleted, inGroup })
            await _owners.SetFavoriteAsync(id, true);

        Assert.True((await _owners.UpdateItemAsync(edited, new OwnerItemDraft("Edited 2", OwnerActionType.Web, "https://edited2.example.test/"))).Succeeded);
        var g3 = await GroupAsync("G3");
        Assert.True(await _owners.MoveItemToGroupAsync(moved, g3.Id));
        Assert.True(await _owners.DeleteItemAsync(deleted));
        Assert.True(await _owners.DeleteGroupAsync(g2.Id));

        Assert.True((await _owners.GetItemAsync(edited))!.IsFavorite);
        Assert.True((await _owners.GetItemAsync(moved))!.IsFavorite);
        Assert.Null(await _owners.GetItemAsync(deleted));
        Assert.Null(await _owners.GetItemAsync(inGroup));
        Assert.Equal(["Edited 2", "Moved"], (await _owners.ListFavoritesAsync()).Select(f => f.Name));
    }

    [Fact]
    public void F14_NoMigration_NoNewPackage_TheSchemaStaysV9()
    {
        Assert.Equal(9, SchemaMigrations.LatestVersion);
        Assert.Equal(Enumerable.Range(1, 9), SchemaMigrations.All.Select(m => m.Version));

        var repo = FindRepoRoot();
        var packages = Directory.GetFiles(Path.Combine(repo, "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference")
                .Select(r => $"{Path.GetFileNameWithoutExtension(p)}:{(string?)r.Attribute("Include")}@{(string?)r.Attribute("Version")}"))
            .Order().ToList();
        Assert.Equal(["WorkHub.Infrastructure:Microsoft.Data.Sqlite@10.0.13"], packages);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
