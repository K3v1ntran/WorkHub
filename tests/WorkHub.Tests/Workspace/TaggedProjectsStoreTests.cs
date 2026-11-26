using System.Globalization;
using System.Text;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 24 store acceptance (proposal Revision 2 + R2a §3.2, §3.3, §4.1, §4.2: TG1–TG8) on real SQLite: the read-only "Projects
/// with this tag" list. Window A reads through store A; window B (its own store and connections, or a raw connection) commits the
/// interleaved changes. Fake names only.
/// <para>
/// Fixture (§4.1): P1 live {clientA, Infra}; P2 live in folder F1 {ClientA}; P3 archived {clientA}; P4 live in archived folder A1
/// {clientA}; P5 directly in the Trash {clientA}; P6 live in folder F2, F2 trashed {clientA}; P7 live {Infra}. Key "clienta" → P1–P6
/// (EligibleTotal 4, TrashedTotal 2). Folder F3 (live, empty) is the TG6 / TG7 move destination.
/// </para>
/// <para>
/// Comparer and cap fixture K (TG5a–c, TG8; key "bulk"): eligible top level Äb, Az; folder Gen G001…G476 (G516 in TG5c); Ölfeld/P;
/// Ost/P; Trash group in live folder T, built in order, each created, tagged and trashed before the next: Dup (d1), Dup (d2 &gt; d1),
/// Kanri, Ｋａｎｒｉ (full width, same NameKey), X01…X16 (TG5c: only Dup d1, Dup d2, Kanri).
/// </para>
/// </summary>
public sealed class TaggedProjectsStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteWorkspaceStore _storeA;
    private readonly WorkspaceService _a;
    private readonly WorkspaceService _b;
    private TaggedProjectsFixture _x = null!;

    private const int Limit = WorkspaceService.TaggedProjectsLimit;
    private static readonly string ClientA = TaggedProjectsFixture.ClientA;
    private static readonly string Bulk = ProjectTagName.Key("bulk");

    public TaggedProjectsStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _storeA = new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile));
        _a = new WorkspaceService(_storeA);
        _b = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose() => _root.Dispose();

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private async Task<long> Folder(string name, long? parent = null) => (await _a.CreateFolderAsync(parent, name)).Value!.Id;

    private async Task<long> Project(string name, long? parent = null, params string[] tags) =>
        (await _a.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active, Tags: tags))).Value!.Id;

    private async Task BuildAsync() => _x = await TaggedProjectsFixture.BuildAsync(_root, _b);

    private Task<TaggedProjectsResult> List(string key, int limit = Limit) => _storeA.ListProjectsWithTagAsync(key, limit);

    private static (long Id, string Path, string Name, string Spelling, TaggedProjectState State) Row(TaggedProject r) =>
        (r.ProjectNodeId, r.Path, r.Name, r.StoredSpelling, r.State);

    // ---- TG1 – TG4 --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TG1_Listing_EveryProjectOfTheKeyOnce_InTheApprovedOrder_WithStatesPathsAndSpellings()
    {
        await BuildAsync();

        var result = await List(ClientA);

        Assert.Equal<(long, string, string, string, TaggedProjectState)>(new[]
        {
            (_x.P1, "", "P1", "clientA", TaggedProjectState.Live),       // top level ("Workspace"): before every folder
            (_x.P3, "", "P3", "clientA", TaggedProjectState.Archived),
            (_x.P4, "A1", "P4", "clientA", TaggedProjectState.Archived), // inside an archived folder
            (_x.P2, "F1", "P2", "ClientA", TaggedProjectState.Live),     // its own stored spelling
            (_x.P5, "", "P5", "clientA", TaggedProjectState.Trashed),    // the Trash group last
            (_x.P6, "F2", "P6", "clientA", TaggedProjectState.Trashed),  // through its trashed folder
        }, result.Rows.Select(Row).ToList());
        Assert.DoesNotContain(result.Rows, r => r.ProjectNodeId == _x.P7);
        Assert.Equal((4, 2, 0), (result.EligibleTotal, result.TrashedTotal, result.Hidden));
    }

    [Fact]
    public async Task TG2_TrashedIsExactlyTheKeepsTheTagRule_OfTheDeleteAndChangePreviews()
    {
        await BuildAsync();

        var result = await List(ClientA);
        var delete = (await _a.PrepareTagDeleteAsync(ClientA)).Value!;
        var change = (await _a.PrepareTagChangeAsync(new TagChangeRequest(TagChangeKind.Rename, ClientA, "ClientX"))).Value!;

        Assert.Equal(delete.Trashed, result.TrashedTotal);
        Assert.Equal(delete.Deleted, result.EligibleTotal);
        Assert.Equal(change.Trashed, result.TrashedTotal);
        Assert.Equal(change.Replaced + change.Merged + change.AlreadySpelled, result.EligibleTotal);

        // The identical SETS: after the delete is applied, exactly the listed Trashed projects still carry the key.
        var trashed = result.Rows.Where(r => r.State == TaggedProjectState.Trashed).Select(r => r.ProjectNodeId).Order().ToList();
        Assert.Equal(TagDeleteOutcome.Deleted, (await _a.ApplyTagDeleteAsync(ClientA, delete.Token)).Outcome);
        var after = await List(ClientA);
        Assert.Equal(trashed, after.Rows.Select(r => r.ProjectNodeId).Order().ToList());
        Assert.All(after.Rows, r => Assert.Equal(TaggedProjectState.Trashed, r.State));
        Assert.Equal((0, trashed.Count), (after.EligibleTotal, after.TrashedTotal));
    }

    [Fact]
    public async Task TG3_ReadOnly_DataUnchanged()
    {
        await BuildAsync();
        var before = Data();

        await List(ClientA);
        await List(ProjectTagName.Key("Infra"));
        await List("unknown");

        Assert.Equal(before, Data()); // the deferred-transaction-only rule is contract TG-C2 (Phase24ContractTests)
    }

    [Fact]
    public async Task TG4_UnknownKey_IsEmpty_EmptyKey_IsRefusedByTheServiceBeforeTheStore()
    {
        await BuildAsync();

        var unknown = await List(ProjectTagName.Key("nobody"));
        Assert.Empty(unknown.Rows);
        Assert.Equal((0, 0, 0), (unknown.EligibleTotal, unknown.TrashedTotal, unknown.Hidden));

        var gated = new GatedWorkspaceStore(_storeA);
        var service = new WorkspaceService(gated);
        foreach (var key in new[] { "", "   " })
        {
            var refused = await service.ListProjectsWithTagAsync(key);
            Assert.Null(refused.Value);
            Assert.Equal(WorkspaceService.NoTagSelected, refused.Error);
        }

        Assert.Equal(0, gated.Calls("taggedProjects"));
        Assert.True((await service.ListProjectsWithTagAsync(ClientA)).Value is { EligibleTotal: 4, TrashedTotal: 2 });
        Assert.Equal([ClientA], gated.TaggedProjectsCalls);
    }

    // ---- TG5a – TG5c, TG8: fixture K ----------------------------------------------------------------------------------------------

    private sealed record KFixture(long D1, long D2);

    /// <summary>Builds fixture K: <paramref name="generated"/> × Gen/G…, the full or short Trash group, optionally the extra top-level Aa.</summary>
    private async Task<KFixture> BuildKAsync(int generated, bool fullTrash, bool withAa = false)
    {
        await Project("Äb", null, "bulk");
        await Project("Az", null, "bulk");
        if (withAa)
            await Project("Aa", null, "bulk");
        var gen = await Folder("Gen");
        for (var i = 1; i <= generated; i++)
            await Project(string.Create(CultureInfo.InvariantCulture, $"G{i:000}"), gen, "bulk");
        await Project("P", await Folder("Ölfeld"), "bulk");
        await Project("P", await Folder("Ost"), "bulk");

        var t = await Folder("T");
        async Task<long> Trashed(string name)
        {
            var id = await Project(name, t, "bulk");                     // created and tagged while writable
            Assert.Equal(ProjectTrashOutcome.Trashed, (await _a.TrashProjectAsync(id)).Outcome); // finished before the next step
            return id;
        }

        var d1 = await Trashed("Dup");
        var d2 = await Trashed("Dup");
        Assert.True(d2 > d1);
        await Trashed("Kanri");
        if (fullTrash)
        {
            await Trashed("Ｋａｎｒｉ");
            for (var i = 1; i <= 16; i++)
                await Trashed(string.Create(CultureInfo.InvariantCulture, $"X{i:00}"));
        }

        return new KFixture(d1, d2);
    }

    private static string Display(TaggedProject r) => r.Path.Length == 0 ? r.Name : r.Path + "/" + r.Name;

    private static List<string> Gen(int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(i => string.Create(CultureInfo.InvariantCulture, $"Gen/G{i:000}")).ToList();

    private static List<string> FullTrashGroup() =>
        ["T/Dup", "T/Dup", "T/Kanri", "T/Ｋａｎｒｉ", .. Enumerable.Range(1, 16).Select(i => string.Create(CultureInfo.InvariantCulture, $"T/X{i:00}"))];

    [Fact]
    public async Task TG5a_Exactly500_AllRowsInOrder_BothDupRowsInIdOrder()
    {
        var k = await BuildKAsync(476, fullTrash: true);

        var result = await List(Bulk);

        Assert.Equal(500, result.Rows.Count);
        Assert.Equal((480, 20, 0), (result.EligibleTotal, result.TrashedTotal, result.Hidden));
        List<string> expected = ["Äb", "Az", .. Gen(1, 476), "Ölfeld/P", "Ost/P", .. FullTrashGroup()];
        Assert.Equal(expected, result.Rows.Select(Display).ToList());
        Assert.Equal([k.D1, k.D2], result.Rows.Where(r => r.Name == "Dup").Select(r => r.ProjectNodeId).ToList());
    }

    [Fact]
    public async Task TG5b_501_AaSortsFirst_TheLastTrashRowIsTheOneHidden()
    {
        var k = await BuildKAsync(476, fullTrash: true, withAa: true);

        var result = await List(Bulk);

        Assert.Equal(500, result.Rows.Count);
        Assert.Equal((481, 20, 1), (result.EligibleTotal, result.TrashedTotal, result.Hidden));
        List<string> full = ["Aa", "Äb", "Az", .. Gen(1, 476), "Ölfeld/P", "Ost/P", .. FullTrashGroup()];
        Assert.Equal("T/X16", full[^1]);
        Assert.Equal(full.Take(500).ToList(), result.Rows.Select(Display).ToList()); // up to T/X15
        Assert.Equal([k.D1, k.D2], result.Rows.Where(r => r.Name == "Dup").Select(r => r.ProjectNodeId).ToList());
    }

    [Fact]
    public async Task TG5c_CapInsideTheEligibleGroup_NoTrashRow_TotalsBeforeTheCap()
    {
        await BuildKAsync(516, fullTrash: false);

        var result = await List(Bulk);

        Assert.Equal(500, result.Rows.Count);
        Assert.Equal((520, 3, 23), (result.EligibleTotal, result.TrashedTotal, result.Hidden));
        List<string> expected = ["Äb", "Az", .. Gen(1, 498)];
        Assert.Equal(expected, result.Rows.Select(Display).ToList());
        Assert.DoesNotContain(result.Rows, r => r.State == TaggedProjectState.Trashed);
    }

    /// <summary>The §3.3 order written out independently (culture ignoring case and width, then ordinal, per segment; then id).</summary>
    private static int Culture(string a, string b)
    {
        var c = CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth);
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    private static int Ordered(TaggedProject x, TaggedProject y, Func<string, string, int> names)
    {
        var g = (x.State == TaggedProjectState.Trashed).CompareTo(y.State == TaggedProjectState.Trashed);
        if (g != 0)
            return g;
        var xs = x.Path.Length == 0 ? [] : x.Path.Split(" › ");
        var ys = y.Path.Length == 0 ? [] : y.Path.Split(" › ");
        for (var i = 0; i < Math.Min(xs.Length, ys.Length); i++)
        {
            var s = names(xs[i], ys[i]);
            if (s != 0)
                return s;
        }

        var d = xs.Length.CompareTo(ys.Length);
        if (d != 0)
            return d;
        var n = names(x.Name, y.Name);
        return n != 0 ? n : x.ProjectNodeId.CompareTo(y.ProjectNodeId);
    }

    /// <summary>The rejected order: "NFKC + upper case, then ordinal" (how NameKey is built for uniqueness).</summary>
    private static int NfkcUpperOrdinal(string a, string b) =>
        string.CompareOrdinal(a.Normalize(NormalizationForm.FormKC).ToUpperInvariant(), b.Normalize(NormalizationForm.FormKC).ToUpperInvariant());

    [Fact]
    public async Task TG8_Comparer_IsTheTreeNameOrder_NotNfkcUpperOrdinal()
    {
        var k = await BuildKAsync(476, fullTrash: true);

        var rows = (await List(Bulk)).Rows.ToList();

        var approved = rows.ToList();
        approved.Sort((x, y) => Ordered(x, y, Culture));
        var rejected = rows.ToList();
        rejected.Sort((x, y) => Ordered(x, y, NfkcUpperOrdinal));
        Assert.Equal(approved.Select(r => r.ProjectNodeId), rows.Select(r => r.ProjectNodeId));
        Assert.NotEqual(rejected.Select(r => r.ProjectNodeId), rows.Select(r => r.ProjectNodeId));

        var order = rows.Select(Display).ToList();
        Assert.True(order.IndexOf("Äb") < order.IndexOf("Az"));
        Assert.True(order.IndexOf("Ölfeld/P") < order.IndexOf("Ost/P"));
        Assert.True(order.IndexOf("T/Kanri") < order.IndexOf("T/Ｋａｎｒｉ"));
        Assert.Equal([k.D1, k.D2], rows.Where(r => r.Name == "Dup").Select(r => r.ProjectNodeId).ToList());
        var bad = rejected.Select(Display).ToList();
        Assert.True(bad.IndexOf("Az") < bad.IndexOf("Äb"));         // what TG8 rejects
        Assert.True(bad.IndexOf("Ost/P") < bad.IndexOf("Ölfeld/P"));
    }

    // ---- TG6, TG7: consistency and totals from the list read ----------------------------------------------------------------------

    [Fact]
    public async Task TG6_EachReadIsOneSnapshot_TheSecondReadShowsTheNewPath_SameTotals()
    {
        await BuildAsync();

        var first = await List(ClientA);
        Assert.True((await _b.MoveAsync(_x.P2, _x.F3)).Value);                    // window B
        var second = await List(ClientA);

        Assert.Equal("F1", first.Rows.Single(r => r.ProjectNodeId == _x.P2).Path);
        Assert.Equal("F3", second.Rows.Single(r => r.ProjectNodeId == _x.P2).Path);
        Assert.Equal((first.EligibleTotal, first.TrashedTotal), (second.EligibleTotal, second.TrashedTotal));
        Assert.Equal(first.Rows.Count, second.Rows.Count);
    }

    [Fact]
    public async Task TG7_CountsComeFromTheListRead_NotFromTheOlderTagRow()
    {
        await BuildAsync();
        var row = (await _a.ListTagsInUseAsync()).Single(t => t.NameKey == ClientA);
        Assert.Equal((4, 2), (row.ChangeableProjects, row.TrashedProjects));

        TaggedProjectsFixture.InsertTag(_root, _x.P7, "clientA", 27);                                // window B adds the key to P7 …
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _b.TrashProjectAsync(_x.P1)).Outcome); // … and trashes P1
        var result = await List(ClientA);

        Assert.Equal((4, 3), (result.EligibleTotal, result.TrashedTotal));
        Assert.Equal(new[] { _x.P2, _x.P3, _x.P4, _x.P7 }.Order(),
            result.Rows.Where(r => r.State != TaggedProjectState.Trashed).Select(r => r.ProjectNodeId).Order());
        Assert.Equal(new[] { _x.P1, _x.P5, _x.P6 }.Order(),
            result.Rows.Where(r => r.State == TaggedProjectState.Trashed).Select(r => r.ProjectNodeId).Order());
    }
}
