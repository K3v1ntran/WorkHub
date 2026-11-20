using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 21 store acceptance (proposal Revision 2 + R2a §3.3, §4.1, §4.2) on real SQLite. Window A uses store A; the interleaved
/// change of every concurrency case is committed by window B (its own store and connections, or a raw connection) between A's
/// preview and A's apply, in that fixed order. Common assertions S1-S4 for Stale: fresh preview with another token (S1), data
/// byte-equal to the state right after B's change (S2), the old token stays Stale (S3), fresh counts as stated (S4).
/// <para>
/// Fixture: live P1-P4 with "clientA" (P4 also "Client-B"); P5 archived with "clientA"; P6 directly trashed with "clientA"; P7 in
/// trashed folder F7 with "clientA"; P8 live with "Client A" and "clientA"; P9 live with 20 tags incl. "clientA"; P10 live without
/// "clientA"; P11 live with "Client A"; P2 inside live folder F2. Each "clientA" row of Pn has CreatedAt 2026-01-0n / 2026-01-nn.
/// </para>
/// </summary>
public sealed class TagRenameStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteWorkspaceStore _storeA;
    private readonly WorkspaceService _a;
    private readonly WorkspaceService _b;
    private long _f2, _f7;
    private readonly long[] _p = new long[12];

    private static readonly string ClientA = ProjectTagName.Key("clientA");
    private static readonly string ClientSpaceA = ProjectTagName.Key("Client A");

    public TagRenameStoreTests()
    {
        _root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _storeA = new SqliteWorkspaceStore(connections);
        _a = new WorkspaceService(_storeA);
        _b = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose() => _root.Dispose();

    private static string Created(int n) => $"2026-01-{n:00}T00:00:00.000Z";

    private void Sql(string text, params (string Name, object Value)[] args)
    {
        using var connection = _root.Open();
        using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private void InsertTag(long project, string name, string createdAt) =>
        Sql("INSERT INTO ProjectTags (ProjectNodeId, NameKey, Name, CreatedAt) VALUES ($p, $k, $n, $c);",
            ("$p", project), ("$k", ProjectTagName.Key(name)), ("$n", name), ("$c", createdAt));

    private void DeleteTag(long project, string name) =>
        Sql("DELETE FROM ProjectTags WHERE ProjectNodeId = $p AND NameKey = $k;", ("$p", project), ("$k", ProjectTagName.Key(name)));

    private List<(string Key, string Name, string CreatedAt)> Tags(long project)
    {
        using var connection = _root.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NameKey, Name, CreatedAt FROM ProjectTags WHERE ProjectNodeId = $p ORDER BY NameKey;";
        command.Parameters.AddWithValue("$p", project);
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private (string Key, string Name, string CreatedAt)? Tag(long project, string key) =>
        Tags(project).Where(t => t.Key == key).Select(t => ((string, string, string)?)t).FirstOrDefault();

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private static List<string> NonTagData(List<string> data) => data.Where(r => !r.StartsWith("ProjectTags|", StringComparison.Ordinal)).ToList();

    private async Task BuildAsync()
    {
        async Task<long> Project(string name, long? parent) => (await _a.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;
        _f2 = (await _a.CreateFolderAsync(null, "F2")).Value!.Id;
        _f7 = (await _a.CreateFolderAsync(null, "F7")).Value!.Id;
        for (var i = 1; i <= 11; i++)
            _p[i] = await Project($"P{i}", i == 2 ? _f2 : i == 7 ? _f7 : null);
        foreach (var i in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })
            InsertTag(_p[i], "clientA", Created(i));
        InsertTag(_p[4], "Client-B", "2026-02-04T00:00:00.000Z");
        InsertTag(_p[8], "Client A", "2026-03-08T00:00:00.000Z");
        InsertTag(_p[11], "Client A", "2026-03-11T00:00:00.000Z");
        for (var t = 1; t <= 19; t++)
            InsertTag(_p[9], $"T{t:00}", "2026-04-01T00:00:00.000Z");
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _a.ArchiveProjectAsync(_p[5])).Outcome);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _a.TrashProjectAsync(_p[6])).Outcome);
        await TrashFolderAsync(_b, _f7);
    }

    private static async Task TrashFolderAsync(WorkspaceService service, long folder)
    {
        var token = (await service.PrepareFolderLifecycleAsync(folder, FolderLifecycleKind.Trash)).Confirmation!.Token;
        Assert.Equal(FolderLifecycleOutcome.Committed, (await service.MarkFolderAsync(folder, FolderLifecycleKind.Trash, token)).Outcome);
    }

    private static TagChangeRequest Rename(string newName) => new(TagChangeKind.Rename, ClientA, newName);

    private static TagChangeRequest Merge(string target) => new(TagChangeKind.MergeInto, ClientA, target);

    private async Task<TagChangePreview> Prepare(TagChangeRequest request, WorkspaceService? service = null) =>
        (await (service ?? _a).PrepareTagChangeAsync(request)).Value!;

    /// <summary>S1-S3 for a Stale apply of <paramref name="old"/>; returns the fresh preview for the S4 checks.</summary>
    private async Task<TagChangePreview> AssertStale(TagChangeRequest request, TagChangePreview old, List<string> afterB)
    {
        var result = await _a.ApplyTagChangeAsync(request, old.Token);
        Assert.Equal(TagChangeOutcome.Stale, result.Outcome);
        Assert.NotNull(result.Preview);
        Assert.NotEqual(old.Token, result.Preview!.Token);                                  // S1
        Assert.Equal(afterB, Data());                                                         // S2
        Assert.Equal(TagChangeOutcome.Stale, (await _a.ApplyTagChangeAsync(request, old.Token)).Outcome); // S3: never replayed
        Assert.Equal(afterB, Data());
        return result.Preview;
    }

    // ---- 4.1 main flow ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TR1_TheListShowsOneRowPerKey_MostUsedSpelling_ChangeableAndTrashCounts_ByKey()
    {
        await BuildAsync();
        InsertTag(_p[10], "CLIENTA", "2026-05-10T00:00:00.000Z"); // another spelling of the key, used once
        var list = await _a.ListTagsInUseAsync();

        Assert.Equal(list.Select(t => t.NameKey).Order(StringComparer.Ordinal), list.Select(t => t.NameKey));
        var row = list.Single(t => t.NameKey == ClientA);
        Assert.Equal("clientA", row.Spelling);
        Assert.Equal(8, row.ChangeableProjects);                 // P1-P5, P8, P9 (archived P5 included) + P10
        Assert.Equal(2, row.TrashedProjects);                    // P6 directly, P7 through F7
        Assert.Equal(new TagInUse(ClientSpaceA, "Client A", 2, 0), list.Single(t => t.NameKey == ClientSpaceA));
    }

    [Fact]
    public async Task TR2_RenameToANewKey_KeepsEachReplacedRowsCreatedAtText_TrashedProjectsKeepTheirTag()
    {
        await BuildAsync();
        var preview = await Prepare(Rename("ClientA-2026"));
        Assert.Equal(TagChangeOperation.Rename, preview.Operation);
        Assert.Equal((7, 0, 0, 2), (preview.Replaced, preview.Merged, preview.AlreadySpelled, preview.Trashed));
        var p6 = Tag(_p[6], ClientA); var p7 = Tag(_p[7], ClientA);

        var result = await _a.ApplyTagChangeAsync(Rename("ClientA-2026"), preview.Token);

        Assert.Equal(TagChangeOutcome.Renamed, result.Outcome);
        var newKey = ProjectTagName.Key("ClientA-2026");
        foreach (var i in new[] { 1, 2, 3, 4, 5, 8, 9 })
        {
            Assert.Null(Tag(_p[i], ClientA));
            Assert.Equal((newKey, "ClientA-2026", Created(i)), Tag(_p[i], newKey));
        }

        Assert.Equal(p6, Tag(_p[6], ClientA));
        Assert.Equal(p7, Tag(_p[7], ClientA));
    }

    [Fact]
    public async Task TR3_SpellingChange_KeepsCreatedAt_LeavesAlreadySpelledRowsUntouched()
    {
        await BuildAsync();
        DeleteTag(_p[3], "clientA");
        InsertTag(_p[3], "ClientA", Created(3));                 // P3 already spelled as confirmed
        var p3 = Tag(_p[3], ClientA);
        var preview = await Prepare(Rename("ClientA"));
        Assert.Equal(TagChangeOperation.SpellingChange, preview.Operation);
        Assert.Equal((6, 0, 1, 2), (preview.Replaced, preview.Merged, preview.AlreadySpelled, preview.Trashed));

        var result = await _a.ApplyTagChangeAsync(Rename("ClientA"), preview.Token);

        Assert.Equal(TagChangeOutcome.SpellingChanged, result.Outcome);
        foreach (var i in new[] { 1, 2, 4, 5, 8, 9 })
            Assert.Equal((ClientA, "ClientA", Created(i)), Tag(_p[i], ClientA));
        Assert.Equal(p3, Tag(_p[3], ClientA));
        Assert.Equal((ClientA, "clientA", Created(6)), Tag(_p[6], ClientA));
    }

    [Fact]
    public async Task TR4_MergeIntoClientA_TheExistingTargetRowStaysByteEqual_OthersGetTheTargetWithTheirCreatedAt()
    {
        await BuildAsync();
        var p8Target = Tag(_p[8], ClientSpaceA); var p11 = Tags(_p[11]);
        var preview = await Prepare(Merge("Client A"));
        Assert.Equal(TagChangeOperation.Merge, preview.Operation);
        Assert.Equal((6, 1, 0, 2), (preview.Replaced, preview.Merged, preview.AlreadySpelled, preview.Trashed));

        var result = await _a.ApplyTagChangeAsync(Merge("Client A"), preview.Token);

        Assert.Equal(TagChangeOutcome.Merged, result.Outcome);
        Assert.Null(Tag(_p[8], ClientA));
        Assert.Equal(p8Target, Tag(_p[8], ClientSpaceA));
        foreach (var i in new[] { 1, 2, 3, 4, 5, 9 })
            Assert.Equal((ClientSpaceA, "Client A", Created(i)), Tag(_p[i], ClientSpaceA));
        Assert.Equal(p11, Tags(_p[11]));
    }

    [Fact]
    public async Task TR5_TheProjectWith20Tags_NeverExceeds20_OnRenameAndMerge()
    {
        await BuildAsync();
        Assert.Equal(20, Tags(_p[9]).Count);
        var rename = await Prepare(Rename("Renamed"));
        Assert.True((await _a.ApplyTagChangeAsync(Rename("Renamed"), rename.Token)).Committed);
        Assert.Equal(20, Tags(_p[9]).Count);
        var back = new TagChangeRequest(TagChangeKind.MergeInto, ProjectTagName.Key("Renamed"), "Client A");
        var merge = await Prepare(back);
        Assert.Equal(TagChangeOutcome.Merged, (await _a.ApplyTagChangeAsync(back, merge.Token)).Outcome);
        Assert.Equal(20, Tags(_p[9]).Count);
    }

    [Fact]
    public async Task TR6_ProjectsAndWorkspaceNodesAreByteEqual_UpdatedAtUnchanged()
    {
        await BuildAsync();
        var before = NonTagData(Data());
        var preview = await Prepare(Merge("Client A"));
        Assert.True((await _a.ApplyTagChangeAsync(Merge("Client A"), preview.Token)).Committed);
        Assert.Equal(before, NonTagData(Data()));
    }

    [Fact]
    public async Task TR8_ANewSearch_FindsTheRenamedLiveAndArchivedProjects_ByTheNewTag_NotByTheOld()
    {
        await BuildAsync();
        var search = new WorkspaceSearchService(new SqliteWorkspaceSearchStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        async Task<long[]> Hits(string text) => (await search.SearchAsync(WorkspaceSearchService.Parse(text)!)).Hits
            .Where(h => h.Item.Kind == SearchResultKind.Project).Select(h => h.Item.Id).Order().ToArray();
        var preview = await Prepare(Rename("Zephyrtag"));
        Assert.True((await _a.ApplyTagChangeAsync(Rename("Zephyrtag"), preview.Token)).Committed);

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 8, 9 }.Select(i => _p[i]).Order().ToArray(), await Hits("Zephyrtag")); // archived P5 included
        Assert.DoesNotContain(_p[1], await Hits("clientA"));
        Assert.DoesNotContain(_p[6], await Hits("clientA"));                  // Trash: not searched, as today
        Assert.DoesNotContain(_p[7], await Hits("clientA"));
    }

    // ---- 4.2 refusals and concurrency (A previews; B commits; A confirms) ---------------------------------------------------------

    [Fact]
    public async Task TR9_BAddsTheSourceTagToP10_Stale_ThenANewConfirmCoversIt()
    {
        await BuildAsync();
        var preview = await Prepare(Rename("ClientX"));
        InsertTag(_p[10], "clientA", Created(10));               // B
        var fresh = await AssertStale(Rename("ClientX"), preview, Data());
        Assert.Equal(8, fresh.Replaced);                          // S4

        var result = await _a.ApplyTagChangeAsync(Rename("ClientX"), fresh.Token); // a NEW confirmation of the fresh preview
        Assert.Equal(TagChangeOutcome.Renamed, result.Outcome);
        var x = ProjectTagName.Key("ClientX");
        foreach (var i in new[] { 1, 2, 3, 4, 5, 8, 9, 10 })
            Assert.Equal((x, "ClientX", Created(i)), Tag(_p[i], x));
    }

    [Fact]
    public async Task TR10_TheLastEligibleSourceRowDisappears_IsStaleNotNothingToRename_TheFreshPreviewIsNotConfirmable()
    {
        await BuildAsync();
        foreach (var i in new[] { 2, 3, 4, 5, 8, 9 })
            DeleteTag(_p[i], "clientA");                          // variant: only P1 (live), P6 and P7 (Trash) carry it
        var preview = await Prepare(Rename("ClientX"));
        Assert.Equal((1, 2), (preview.Replaced, preview.Trashed));
        DeleteTag(_p[1], "clientA");                              // B removes the last eligible row
        var fresh = await AssertStale(Rename("ClientX"), preview, Data());
        Assert.Equal((0, 0, 0, 2), (fresh.Replaced, fresh.Merged, fresh.AlreadySpelled, fresh.Trashed));
        Assert.False(fresh.IsConfirmable);

        // TR10b: a direct apply with the empty plan's own token is NothingToRename, nothing written.
        var before = Data();
        Assert.Equal(TagChangeOutcome.NothingToRename, (await _a.ApplyTagChangeAsync(Rename("ClientX"), fresh.Token)).Outcome);
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task TR11_AnInvalidNewName_IsRefusedBeforeTheStore()
    {
        await BuildAsync();
        var before = Data();
        foreach (var name in new[] { "   ", new string('x', 41), "bad\u0001name" })
        {
            Assert.NotNull((await _a.PrepareTagChangeAsync(Rename(name))).Error);
            Assert.Equal(TagChangeOutcome.InvalidName, (await _a.ApplyTagChangeAsync(Rename(name), "token")).Outcome);
        }

        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task TR12_AFailureAfterTheDeletes_RollsEverythingBack()
    {
        await BuildAsync();
        var before = Data();
        var preview = await Prepare(Rename("ClientX"));
        var deletesSeen = 0;
        _storeA.AfterTagChangeDeletes = (connection, transaction) =>
        {
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT count(*) FROM ProjectTags WHERE NameKey = $k;";
            count.Parameters.AddWithValue("$k", ClientA);
            deletesSeen = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            throw new InvalidOperationException("injected after the deletes (test)");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _a.ApplyTagChangeAsync(Rename("ClientX"), preview.Token));

        Assert.Equal(2, deletesSeen);                             // inside the transaction only the trashed rows were left
        Assert.Equal(before, Data());                             // nothing partial
    }

    [Fact]
    public async Task TR15_V010IsUnchanged_UpdateRefused_InsertIntoATrashedProjectRefused()
    {
        await BuildAsync();
        var update = Assert.Throws<SqliteException>(() => Sql("UPDATE ProjectTags SET Name = 'x' WHERE ProjectNodeId = $p;", ("$p", _p[1])));
        Assert.Contains("WH_TAG_IMMUTABLE", update.Message, StringComparison.Ordinal);
        var insert = Assert.Throws<SqliteException>(() => InsertTag(_p[6], "New", Created(6)));
        Assert.Contains("WH_PROJECT_TRASHED", insert.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TR16_P1MovesDirectlyToTheTrash_AfterThePreview_Stale()
    {
        await BuildAsync();
        var preview = await Prepare(Rename("ClientX"));
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _b.TrashProjectAsync(_p[1])).Outcome);
        var fresh = await AssertStale(Rename("ClientX"), preview, Data());
        Assert.Equal((6, 3), (fresh.Replaced, fresh.Trashed));
        Assert.Equal((ClientA, "clientA", Created(1)), Tag(_p[1], ClientA));
    }

    [Fact]
    public async Task TR17_P6IsRestoredFromTheTrash_AfterThePreview_Stale()
    {
        await BuildAsync();
        var preview = await Prepare(Rename("ClientX"));
        Assert.Equal(ProjectRestoreOutcome.Restored, (await _b.RestoreProjectAsync(_p[6])).Outcome);
        var fresh = await AssertStale(Rename("ClientX"), preview, Data());
        Assert.Equal((8, 1), (fresh.Replaced, fresh.Trashed));
    }

    [Fact]
    public async Task TR18a_FolderF7IsRestored_P7BecomesEligible_Stale()
    {
        await BuildAsync();
        var preview = await Prepare(Rename("ClientX"));
        Assert.Equal(FolderClearOutcome.Cleared, (await _b.ClearFolderAsync(_f7, FolderLifecycleKind.Trash)).Outcome);
        var fresh = await AssertStale(Rename("ClientX"), preview, Data());
        Assert.Equal((8, 1), (fresh.Replaced, fresh.Trashed));
    }

    [Fact]
    public async Task TR18b_FolderF2MovesToTheTrash_P2IsExcluded_AndANewConfirmNeverWritesP2()
    {
        await BuildAsync();
        var preview = await Prepare(Rename("ClientX"));
        await TrashFolderAsync(_b, _f2);
        var fresh = await AssertStale(Rename("ClientX"), preview, Data());
        Assert.Equal((6, 3), (fresh.Replaced, fresh.Trashed));
        var p2 = Tag(_p[2], ClientA);

        Assert.Equal(TagChangeOutcome.Renamed, (await _a.ApplyTagChangeAsync(Rename("ClientX"), fresh.Token)).Outcome);
        Assert.Equal(p2, Tag(_p[2], ClientA));                   // V010 would allow it; the store's effective-state rule does not
    }

    [Fact]
    public async Task TR19_TheTargetKeyIsAddedToASourceProject_Stale()
    {
        await BuildAsync();
        var preview = await Prepare(Merge("Client A"));
        InsertTag(_p[1], "Client A", "2026-06-01T00:00:00.000Z");
        var fresh = await AssertStale(Merge("Client A"), preview, Data());
        Assert.Equal((5, 2), (fresh.Replaced, fresh.Merged));
    }

    [Fact]
    public async Task TR20_TheTargetKeyIsRemovedFromP8_Stale()
    {
        await BuildAsync();
        var preview = await Prepare(Merge("Client A"));
        DeleteTag(_p[8], "Client A");
        var fresh = await AssertStale(Merge("Client A"), preview, Data());
        Assert.Equal((7, 0), (fresh.Replaced, fresh.Merged));
    }

    [Fact]
    public async Task TR21a_Control_TheTargetAddedToANonSourceProject_DoesNotForceAReview()
    {
        await BuildAsync();
        var preview = await Prepare(Merge("Client A"));
        InsertTag(_p[10], "Client A", "2026-06-10T00:00:00.000Z");
        var p10 = Tags(_p[10]);
        var result = await _a.ApplyTagChangeAsync(Merge("Client A"), preview.Token);
        Assert.Equal(TagChangeOutcome.Merged, result.Outcome);
        Assert.Equal((6, 1), (result.Preview!.Replaced, result.Preview.Merged));
        Assert.Equal(p10, Tags(_p[10]));
    }

    [Fact]
    public async Task TR21b_ARenameBecomesAMerge_WhenTheNewKeyAppearsAnywhere_Stale()
    {
        await BuildAsync();
        var preview = await Prepare(Rename("Brand-New"));
        Assert.Equal(TagChangeOperation.Rename, preview.Operation);
        InsertTag(_p[10], "Brand-New", "2026-06-10T00:00:00.000Z");
        var fresh = await AssertStale(Rename("Brand-New"), preview, Data());
        Assert.Equal(TagChangeOperation.Merge, fresh.Operation);
        Assert.Equal((7, 0), (fresh.Replaced, fresh.Merged));
    }

    [Fact]
    public async Task TR21c_AMergeBecomesARename_WhenTheTargetKeyVanishesEverywhere_Stale()
    {
        await BuildAsync();
        var preview = await Prepare(Merge("Client A"));
        DeleteTag(_p[8], "Client A");
        DeleteTag(_p[11], "Client A");
        var fresh = await AssertStale(Merge("Client A"), preview, Data());
        Assert.Equal(TagChangeOperation.Rename, fresh.Operation);
    }

    [Fact]
    public async Task TR22_TheSourceRowsSpellingOrCreatedAtChanges_Stale()
    {
        await BuildAsync();
        var preview = await Prepare(Rename("ClientX"));
        var spelling = await Prepare(Rename("ClientA"), _b);     // (i) B changes the spelling everywhere (same key)
        Assert.True((await _b.ApplyTagChangeAsync(Rename("ClientA"), spelling.Token)).Committed);
        await AssertStale(Rename("ClientX"), preview, Data());

        var again = await Prepare(Rename("ClientX"));
        DeleteTag(_p[2], "ClientA");                              // (ii) removed and added back: same Name, new CreatedAt
        InsertTag(_p[2], "ClientA", "2026-07-02T00:00:00.000Z");
        await AssertStale(Rename("ClientX"), again, Data());
    }

    [Fact]
    public async Task TR23_TheTokenIsStable_ForOneSnapshot_AndBindsTheConfirmedSpelling()
    {
        await BuildAsync();
        var one = await Prepare(Rename("ClientX"));
        var two = await Prepare(Rename("ClientX"));
        Assert.Equal(one.Token, two.Token);
        Assert.Matches("^[0-9a-f]{64}$", one.Token);
        Assert.NotEqual(one.Token, (await Prepare(Rename("clientx"))).Token); // same key, other spelling
        InsertTag(_p[4], "Client-C", "2026-06-04T00:00:00.000Z");            // an unrelated key
        Assert.Equal(one.Token, (await Prepare(Rename("ClientX"))).Token);
        Assert.Equal(TagChangeOutcome.Renamed, (await _a.ApplyTagChangeAsync(Rename("ClientX"), one.Token)).Outcome); // recomputed under the lock = the preview token
    }

    [Fact]
    public async Task TR24a_EveryRowAlreadySpelled_PreviewNotConfirmable_ApplyIsUnchanged_NothingWritten()
    {
        await BuildAsync();
        var first = await Prepare(Rename("ClientA"));
        Assert.True((await _a.ApplyTagChangeAsync(Rename("ClientA"), first.Token)).Committed);
        var preview = await Prepare(Rename("ClientA"));
        Assert.Equal((0, 0, 7), (preview.Replaced, preview.Merged, preview.AlreadySpelled));
        Assert.False(preview.IsConfirmable);
        var before = Data();

        Assert.Equal(TagChangeOutcome.Unchanged, (await _a.ApplyTagChangeAsync(Rename("ClientA"), preview.Token)).Outcome);
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task TR24b_BAppliesTheSameSpellingFirst_AsConfirmIsStale_TheFreshPreviewIsAlreadySpelled()
    {
        await BuildAsync();
        var preview = await Prepare(Rename("ClientA"));
        var theirs = await Prepare(Rename("ClientA"), _b);
        Assert.True((await _b.ApplyTagChangeAsync(Rename("ClientA"), theirs.Token)).Committed);
        var fresh = await AssertStale(Rename("ClientA"), preview, Data());
        Assert.Equal((0, 7), (fresh.Replaced, fresh.AlreadySpelled));
        Assert.False(fresh.IsConfirmable);
        foreach (var i in new[] { 1, 2, 3, 4, 5, 8, 9 })
            Assert.Equal((ClientA, "ClientA", Created(i)), Tag(_p[i], ClientA));
    }

    // ---- R2a store side ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task R2a_ABaselineThatDiffersOnlyInSpelling_RefusesTheWholeSave_ANullBaselineKeepsPhase17Behaviour()
    {
        await BuildAsync();
        var details = (await _a.GetProjectAsync(_p[1]))!;
        var before = Data();
        var stale = new[] { new ProjectTag("ClientA", ClientA, DateTimeOffset.UnixEpoch) };  // stored spelling is "clientA"
        var refused = await _a.UpdateProjectAsync(_p[1], new ProjectDraft(details.Name, details.Status, null, "O2", null, ["clientA", "Extra"], stale));
        Assert.Equal(WorkspaceFailure.TagsChanged, refused.Failure);
        Assert.Equal(before, Data());

        var saved = await _a.UpdateProjectAsync(_p[1], new ProjectDraft(details.Name, details.Status, null, "O2", null, ["clientA", "Extra"]));
        Assert.True(saved.Succeeded, saved.Error);
        Assert.Equal(["clientA", "Extra"], saved.Value!.Tags.Select(t => t.Name).Order(StringComparer.Ordinal));
    }
}
