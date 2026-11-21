using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Search;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 22 store acceptance (proposal Revision 2 + R2a §3.3, §4.1) on real SQLite: Delete a tag everywhere. Window A uses store A;
/// the interleaved change of every concurrency case is committed by window B (its own store and connections, or a raw connection)
/// between A's preview and A's apply.
/// <para>
/// Fixture (§4): P1 live {clientA, Infra}; P2 live {ClientA}; P3 archived {clientA}; P4 directly in the Trash {clientA}; P5 live in
/// folder F, F trashed {clientA}; P6 live {Infra}. Key "clienta" = rows on P1 (clientA), P2 (ClientA), P3, P4, P5.
/// </para>
/// </summary>
public sealed class TagDeleteStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteWorkspaceStore _storeA;
    private readonly WorkspaceService _a;
    private readonly WorkspaceService _b;
    private long _f;
    private readonly long[] _p = new long[7];

    private static readonly string ClientA = ProjectTagName.Key("clientA");

    public TagDeleteStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _storeA = new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile));
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

    private void DeleteTagRow(long project, string name) =>
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

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private static List<string> NonTagData(List<string> data) => data.Where(r => !r.StartsWith("ProjectTags|", StringComparison.Ordinal)).ToList();

    private async Task BuildAsync()
    {
        async Task<long> Project(string name, long? parent) => (await _a.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;
        _f = (await _a.CreateFolderAsync(null, "F")).Value!.Id;
        for (var i = 1; i <= 6; i++)
            _p[i] = await Project($"P{i}", i == 5 ? _f : null);
        InsertTag(_p[1], "clientA", Created(1));
        InsertTag(_p[1], "Infra", Created(11));
        InsertTag(_p[2], "ClientA", Created(2));
        InsertTag(_p[3], "clientA", Created(3));
        InsertTag(_p[4], "clientA", Created(4));
        InsertTag(_p[5], "clientA", Created(5));
        InsertTag(_p[6], "Infra", Created(16));
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _a.ArchiveProjectAsync(_p[3])).Outcome);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _a.TrashProjectAsync(_p[4])).Outcome);
        var token = (await _b.PrepareFolderLifecycleAsync(_f, FolderLifecycleKind.Trash)).Confirmation!.Token;
        Assert.Equal(FolderLifecycleOutcome.Committed, (await _b.MarkFolderAsync(_f, FolderLifecycleKind.Trash, token)).Outcome);
    }

    private async Task<TagDeletePreview> Prepare(string key, WorkspaceService? service = null) =>
        (await (service ?? _a).PrepareTagDeleteAsync(key)).Value!;

    /// <summary>A Stale apply of <paramref name="old"/>: fresh preview with another token, data = <paramref name="expected"/>, never replayed.</summary>
    private async Task<TagDeletePreview> AssertStale(string key, TagDeletePreview old, List<string> expected)
    {
        var result = await _a.ApplyTagDeleteAsync(key, old.Token);
        Assert.Equal(TagDeleteOutcome.Stale, result.Outcome);
        Assert.False(result.Committed);
        Assert.NotNull(result.Preview);
        Assert.NotEqual(old.Token, result.Preview!.Token);
        Assert.Equal(expected, Data());
        Assert.Equal(TagDeleteOutcome.Stale, (await _a.ApplyTagDeleteAsync(key, old.Token)).Outcome); // never replayed
        Assert.Equal(expected, Data());
        return result.Preview;
    }

    // ---- 4.1 preview and apply --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TD1_Preview_CountsLiveAndArchivedAsDeleted_TrashDirectAndThroughAFolderAsKept_WritesNothing()
    {
        await BuildAsync();
        var before = Data();

        var preview = await Prepare(ClientA);

        Assert.Equal(ClientA, preview.FromKey);
        Assert.Equal(3, preview.Deleted);   // P1, P2, P3
        Assert.Equal(2, preview.Trashed);   // P4, P5
        Assert.True(preview.IsConfirmable);
        Assert.Equal("clientA", preview.Spelling);
        Assert.Matches("^[0-9a-f]{64}$", preview.Token);
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task TD2_TD3_TD4_Apply_DeletesExactlyTheEligibleRows_ArchivedIncluded_TrashKept_NothingElseWritten()
    {
        await BuildAsync();
        var before = Data();
        var p4 = Tags(_p[4]);
        var p5 = Tags(_p[5]);
        var p1Infra = Tags(_p[1]).Single(t => t.Key == ProjectTagName.Key("Infra"));
        var p6 = Tags(_p[6]);
        var preview = await Prepare(ClientA);

        var result = await _a.ApplyTagDeleteAsync(ClientA, preview.Token);

        Assert.Equal(TagDeleteOutcome.Deleted, result.Outcome);
        Assert.True(result.Committed);
        Assert.Equal(preview, result.Preview);
        Assert.Equal([p1Infra], Tags(_p[1]));                  // P1 keeps Infra byte-equal
        Assert.Empty(Tags(_p[2]));
        Assert.Empty(Tags(_p[3]));                              // TD3: archived Project lost it
        Assert.Equal(p4, Tags(_p[4]));                          // directly in the Trash: kept byte-equal
        Assert.Equal(p5, Tags(_p[5]));                          // TD4: trashed through folder F: kept byte-equal (plan, not trigger)
        Assert.Equal(p6, Tags(_p[6]));
        Assert.Equal(NonTagData(before), NonTagData(Data()));  // Projects / WorkspaceNodes / content byte-equal (UpdatedAt, ArchiveSerial kept)
        Assert.Equal(ProjectStatus.Archived, (await _a.GetProjectAsync(_p[3]))!.Status);
    }

    // ---- 4.1 concurrency (A previews; B commits; A confirms) ----------------------------------------------------------------------

    [Fact]
    public async Task TD5_BAddsTheTagToP6_Stale_ThenTheFreshTokenCoversP6()
    {
        await BuildAsync();
        var preview = await Prepare(ClientA);
        Assert.True((await _b.UpdateProjectAsync(_p[6], new ProjectDraft("P6", ProjectStatus.Active, null, null, null, ["Infra", "clientA"]))).Succeeded);
        var afterB = Data();

        var fresh = await AssertStale(ClientA, preview, afterB);

        Assert.Equal(4, fresh.Deleted);
        Assert.True((await _a.ApplyTagDeleteAsync(ClientA, fresh.Token)).Committed);
        Assert.Equal(["Infra"], Tags(_p[6]).Select(t => t.Name));
    }

    [Fact]
    public async Task TD6_BRemovesTheTagFromP2_Stale()
    {
        await BuildAsync();
        var preview = await Prepare(ClientA);
        DeleteTagRow(_p[2], "ClientA");
        var afterB = Data();

        var fresh = await AssertStale(ClientA, preview, afterB);

        Assert.Equal(2, fresh.Deleted);
    }

    [Fact]
    public async Task TD7_BChangesTheSpelling_Stale()
    {
        await BuildAsync();
        var preview = await Prepare(ClientA);
        var rename = new TagChangeRequest(TagChangeKind.Rename, ClientA, "CLIENTA");
        var bPreview = (await _b.PrepareTagChangeAsync(rename)).Value!;
        Assert.True((await _b.ApplyTagChangeAsync(rename, bPreview.Token)).Committed);
        var afterB = Data();

        var fresh = await AssertStale(ClientA, preview, afterB);

        Assert.Equal(3, fresh.Deleted);
        Assert.Equal("CLIENTA", fresh.Spelling);
    }

    [Fact]
    public async Task TD8a_P1EntersTheTrash_Stale_FreshCountsExcludeIt()
    {
        await BuildAsync();
        var preview = await Prepare(ClientA);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _b.TrashProjectAsync(_p[1])).Outcome);
        var afterB = Data();

        var fresh = await AssertStale(ClientA, preview, afterB);

        Assert.Equal(2, fresh.Deleted);
        Assert.Equal(3, fresh.Trashed);
    }

    [Fact]
    public async Task TD8b_FolderFIsRestored_Stale_FreshCountsIncludeP5()
    {
        await BuildAsync();
        var preview = await Prepare(ClientA);
        Assert.Equal(FolderClearOutcome.Cleared, (await _b.ClearFolderAsync(_f, FolderLifecycleKind.Trash)).Outcome);
        var afterB = Data();

        var fresh = await AssertStale(ClientA, preview, afterB);

        Assert.Equal(4, fresh.Deleted);
        Assert.Equal(1, fresh.Trashed);
    }

    [Fact]
    public async Task TD9_ArchivingP2_DoesNotStale_TheDeleteProceeds_P2StaysArchived()
    {
        await BuildAsync();
        var preview = await Prepare(ClientA);
        Assert.Equal(ProjectArchiveOutcome.Archived, (await _b.ArchiveProjectAsync(_p[2])).Outcome);

        var result = await _a.ApplyTagDeleteAsync(ClientA, preview.Token);

        Assert.Equal(TagDeleteOutcome.Deleted, result.Outcome);
        Assert.Empty(Tags(_p[2]));
        Assert.Equal(ProjectStatus.Archived, (await _a.GetProjectAsync(_p[2]))!.Status);
    }

    [Fact]
    public async Task TD10_TheLastEligibleRowsGoAfterThePreview_Stale_NotNothingToDelete_FreshNotConfirmable()
    {
        await BuildAsync();
        var preview = await Prepare(ClientA);
        DeleteTagRow(_p[1], "clientA");
        DeleteTagRow(_p[2], "ClientA");
        DeleteTagRow(_p[3], "clientA");
        var afterB = Data();

        var fresh = await AssertStale(ClientA, preview, afterB);

        Assert.Equal(0, fresh.Deleted);
        Assert.False(fresh.IsConfirmable);
    }

    [Fact]
    public async Task TD10b_AKeyCarriedOnlyByTrashedProjects_ItsOwnToken_NothingToDelete()
    {
        await BuildAsync();
        // "Gone" only on a Project in the Trash: P5 (through folder F) accepts a raw insert (V010 blocks only a directly trashed Project).
        InsertTag(_p[5], "Gone", Created(25));
        var before = Data();
        var key = ProjectTagName.Key("Gone");
        var preview = await Prepare(key);
        Assert.Equal(0, preview.Deleted);
        Assert.Equal(1, preview.Trashed);

        var result = await _a.ApplyTagDeleteAsync(key, preview.Token);

        Assert.Equal(TagDeleteOutcome.NothingToDelete, result.Outcome);
        Assert.False(result.Committed);
        Assert.Equal(before, Data());
    }

    // ---- TD11: keys nobody carries (Revision 2 point 3) -------------------------------------------------------------------------

    [Fact]
    public async Task TD11a_ANeverExistingKey_PreviewIsAnEmptyPlan_NotConfirmable_WritesNothing()
    {
        await BuildAsync();
        var before = Data();

        var preview = await Prepare(ProjectTagName.Key("nosuchtag"));

        Assert.Equal(0, preview.Deleted);
        Assert.Equal(0, preview.Trashed);
        Assert.False(preview.IsConfirmable);
        Assert.Matches("^[0-9a-f]{64}$", preview.Token);
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task TD11b_ANeverExistingKey_WithItsOwnEmptyPlanToken_NothingToDelete()
    {
        await BuildAsync();
        var key = ProjectTagName.Key("nosuchtag");
        var preview = await Prepare(key);
        var before = Data();

        var result = await _a.ApplyTagDeleteAsync(key, preview.Token);

        Assert.Equal(TagDeleteOutcome.NothingToDelete, result.Outcome);
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task TD11c_AnOlderTokenOfTheSameKey_OnANowEmptyPlan_IsStale_BecauseTheTokenIsCheckedFirst()
    {
        await BuildAsync();
        InsertTag(_p[6], "temp", Created(26));
        var key = ProjectTagName.Key("temp");
        var t1 = await Prepare(key);
        Assert.Equal(1, t1.Deleted);
        DeleteTagRow(_p[6], "temp");                             // now nobody carries "temp"
        var afterRemoval = Data();

        var fresh = await AssertStale(key, t1, afterRemoval);

        Assert.Equal(0, fresh.Deleted);
        Assert.Equal(0, fresh.Trashed);
        Assert.False(fresh.IsConfirmable);
    }

    [Fact]
    public async Task TD11d_AnotherKeysToken_OnAnEmptyPlan_IsStale_NoRowTouched()
    {
        await BuildAsync();
        var clientA = await Prepare(ClientA);
        var key = ProjectTagName.Key("nosuchtag");
        var before = Data();

        var fresh = await AssertStale(key, clientA, before);

        Assert.Equal(0, fresh.Deleted);
        Assert.False(fresh.IsConfirmable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TD11e_AnEmptyKey_IsRefusedByTheService_BeforeTheStore(string key)
    {
        await BuildAsync();
        var gated = new GatedWorkspaceStore(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        var service = new WorkspaceService(gated);
        var before = Data();

        var prepare = await service.PrepareTagDeleteAsync(key);
        var apply = await service.ApplyTagDeleteAsync(key, new string('0', 64));

        Assert.False(prepare.Succeeded);
        Assert.Equal(WorkspaceService.NoTagSelected, prepare.Error);
        Assert.Equal(TagDeleteOutcome.InvalidKey, apply.Outcome);
        Assert.Equal(0, gated.Calls("prepareTagDelete"));
        Assert.Empty(gated.ApplyTagDeleteCalls);
        Assert.Equal(before, Data());
    }

    // ---- write failures and backstop --------------------------------------------------------------------------------------------

    [Fact]
    public async Task TD12_AFailureAfterTheFirstDelete_RollsEverythingBack()
    {
        await BuildAsync();
        var before = Data();
        var preview = await Prepare(ClientA);
        var leftInside = -1;
        _storeA.AfterTagDeleteRows = (connection, transaction) =>
        {
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT count(*) FROM ProjectTags WHERE NameKey = $k;";
            count.Parameters.AddWithValue("$k", ClientA);
            leftInside = Convert.ToInt32(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            throw new InvalidOperationException("injected after the first delete (test)");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _a.ApplyTagDeleteAsync(ClientA, preview.Token));

        Assert.Equal(4, leftInside);                              // one row was deleted inside the transaction
        Assert.Equal(before, Data());                             // nothing partial
    }

    [Fact]
    public async Task TD13_V010Backstop_ADirectDeleteOfATagOfAProjectDirectlyInTheTrash_IsRefused_RowKeptIntact()
    {
        // V010's TR_ProjectTags_Delete is the database backstop below the store's eligibility rule: a raw DELETE on the test
        // database (no store, no plan) of P4's row (P4 directly in the Trash) must be refused and leave the row exactly as it was.
        await BuildAsync();
        var before = Data();
        var p4Before = Tags(_p[4]);
        Assert.Equal(new[] { (ClientA, "clientA", Created(4)) }, p4Before);

        var error = Assert.Throws<SqliteException>(() =>
            Sql("DELETE FROM ProjectTags WHERE ProjectNodeId = $p AND NameKey = $k;", ("$p", _p[4]), ("$k", ClientA)));

        Assert.Contains("WH_PROJECT_TRASHED", error.Message, StringComparison.Ordinal);
        Assert.Equal(p4Before, Tags(_p[4]));                      // key, stored Name and raw CreatedAt unchanged
        Assert.Equal(before, Data());                             // nothing else touched
    }

    [Fact]
    public async Task TD14_RenameAndDeleteTokens_AreNeverInterchangeable()
    {
        await BuildAsync();
        var before = Data();
        var rename = new TagChangeRequest(TagChangeKind.Rename, ClientA, "ClientX");
        var renameToken = (await _a.PrepareTagChangeAsync(rename)).Value!.Token;
        var deleteToken = (await Prepare(ClientA)).Token;

        Assert.Equal(TagDeleteOutcome.Stale, (await _a.ApplyTagDeleteAsync(ClientA, renameToken)).Outcome);
        Assert.Equal(TagChangeOutcome.Stale, (await _a.ApplyTagChangeAsync(rename, deleteToken)).Outcome);
        Assert.Equal(before, Data());
    }

    [Fact]
    public async Task TD15_TheTokenIsStableForTheSameData_AndChangesWithAnyRowField()
    {
        await BuildAsync();
        var first = await Prepare(ClientA);
        var second = await Prepare(ClientA, _b);
        Assert.Equal(first.Token, second.Token);

        DeleteTagRow(_p[2], "ClientA");
        InsertTag(_p[2], "ClientA", Created(29));                 // same key and spelling, other CreatedAt
        Assert.NotEqual(first.Token, (await Prepare(ClientA)).Token);
    }

    [Fact]
    public async Task TD16_ANewSearch_NoLongerFindsLiveOrArchivedProjectsByTheDeletedTag()
    {
        await BuildAsync();
        var search = new WorkspaceSearchService(new SqliteWorkspaceSearchStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        async Task<long[]> Hits(string text) => (await search.SearchAsync(WorkspaceSearchService.Parse(text)!)).Hits
            .Where(h => h.Item.Kind == SearchResultKind.Project).Select(h => h.Item.Id).Order().ToArray();
        Assert.Equal(new[] { 1, 2, 3 }.Select(i => _p[i]).Order().ToArray(), await Hits("clientA"));
        var preview = await Prepare(ClientA);

        Assert.True((await _a.ApplyTagDeleteAsync(ClientA, preview.Token)).Committed);

        Assert.Empty(await Hits("clientA"));                      // P4 / P5 are in the Trash: not searched, as before
        Assert.Equal(new[] { 1, 6 }.Select(i => _p[i]).Order().ToArray(), await Hits("Infra"));
    }
}
