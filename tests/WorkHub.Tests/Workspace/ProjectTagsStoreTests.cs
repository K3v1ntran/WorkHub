using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Phase 17 Project tags through the real SQLite store (proposal R2b §4.2, §5.3, §5.4, §6.1 R/T/X): rules, one transaction for
/// details and tags, the diff by composite key at Save time, delete-before-insert at 20 tags, the last Save deciding the whole
/// set, state refusals (also inherited from Folders), rollback after the deletes, suggestions, and the Phase 14/15 regressions.
/// </summary>
public sealed class ProjectTagsStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly WorkspaceService _workspace;
    private readonly WorkspaceService _other;
    private readonly long _folder;
    private readonly long _p;

    public ProjectTagsStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(_connections));
        _other = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _folder = _workspace.CreateFolderAsync(null, "Clients").Result.Value!.Id;
        _p = CreateProject(_folder, "Alpha", "Infra", "Ops");
    }

    public void Dispose() => _root.Dispose();

    private long CreateProject(long? parent, string name, params string[] tags) =>
        _workspace.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active, "desc", "Alice", null, tags)).Result.Value!.Id;

    private static ProjectDraft Draft(ProjectDetails d, string? owner = null, params string[] tags) =>
        new(d.Name, d.Status, d.Description, owner ?? d.Owner, d.ProjectManager, tags);

    private async Task<ProjectDetails> Get(long id) => (await _workspace.GetProjectAsync(id))!;

    /// <summary>(NameKey → Name|CreatedAt) of one Project, read with raw SQL.</summary>
    private Dictionary<string, string> Rows(long project)
    {
        using var c = _root.Open();
        using var command = c.CreateCommand();
        command.CommandText = "SELECT NameKey, Name || '|' || CreatedAt FROM ProjectTags WHERE ProjectNodeId = $p;";
        command.Parameters.AddWithValue("$p", project);
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            rows[reader.GetString(0)] = reader.GetString(1);
        return rows;
    }

    private (string Name, string Owner, string Updated) Details(long project)
    {
        using var c = _root.Open();
        var text = Db.ScalarString(c, "SELECT n.Name || '|' || coalesce(p.Owner, '') || '|' || n.UpdatedAt FROM WorkspaceNodes n JOIN Projects p ON p.NodeId = n.Id WHERE n.Id = $id;", ("$id", project))!.Split('|');
        return (text[0], text[1], text[2]);
    }

    /// <summary>Name, Owner, UpdatedAt and every tag row (key order) of one Project, as one comparable string.</summary>
    private string State(long project) =>
        $"{Details(project)}#{string.Join(";", Rows(project).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => $"{r.Key}={r.Value}"))}";

    // ---- R: rules -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("  Infra  ", "Infra")]
    [InlineData("\u3000会議\u3000", "会議")]
    [InlineData("a\u2028b", "a\u2028b")]
    public void R1_ValidNames_AreTrimmedWithAllUnicodeWhiteSpace(string raw, string expected)
    {
        Assert.True(ProjectTagName.TryNormalize(raw, out var name, out var error), error);
        Assert.Equal(expected, name);
    }

    public static TheoryData<int> ControlCodePoints() => [.. Enumerable.Range(0, 32).Append(127).Concat(Enumerable.Range(128, 32))];

    [Theory]
    [MemberData(nameof(ControlCodePoints))]
    public void R1_EveryControlCharacter_IsRefused(int codePoint)
    {
        Assert.False(ProjectTagName.TryNormalize($"a{(char)codePoint}b", out _, out var error));
        Assert.Equal("A tag must not contain control characters.", error);
    }

    [Fact]
    public void R1_LengthAndUtf16Rules()
    {
        Assert.True(ProjectTagName.TryNormalize(new string('a', 40), out _, out _));
        Assert.False(ProjectTagName.TryNormalize(new string('a', 41), out _, out var tooLong));
        Assert.Equal("A tag must be at most 40 characters.", tooLong);
        Assert.False(ProjectTagName.TryNormalize("   ", out _, out var empty));
        Assert.Equal("A tag needs a name.", empty);
        Assert.False(ProjectTagName.TryNormalize("a\uD800b", out _, out var lone));
        Assert.Equal("A tag contains invalid characters.", lone);
        Assert.True(ProjectTagName.TryNormalize(string.Concat(Enumerable.Repeat("😀", 20)), out _, out _)); // 40 UTF-16 units
        Assert.False(ProjectTagName.TryNormalize(string.Concat(Enumerable.Repeat("😀", 20)) + "a", out _, out _));
    }

    [Fact]
    public void R2_TheApplicationKey_IsTheWorkspaceNameKey()
    {
        Assert.Equal("INFRA", ProjectTagName.Key("Infra"));
        Assert.Equal(ProjectTagName.Key("Infra"), ProjectTagName.Key("INFRA"));
        Assert.Equal(ProjectTagName.Key("Infra"), ProjectTagName.Key("ＩＮＦＲＡ"));
        Assert.NotEqual(ProjectTagName.Key("かな"), ProjectTagName.Key("カナ"));
        Assert.Equal(WorkspaceName.Key("ＩＮＦＲＡ"), ProjectTagName.Key("ＩＮＦＲＡ"));
    }

    [Fact]
    public async Task R3_TheValidator_RefusesADuplicateKeyOr21Tags_BeforeAnySql()
    {
        var gated = new GatedWorkspaceStore(new SqliteWorkspaceStore(_connections));
        var service = new WorkspaceService(gated);
        var d = await Get(_p);
        var duplicate = await service.UpdateProjectAsync(_p, Draft(d, null, "Infra", "ＩＮＦＲＡ"));
        Assert.Equal(ProjectDraftValidator.Fields.Tags, duplicate.Field);
        var many = await service.UpdateProjectAsync(_p, Draft(d, null, Enumerable.Range(1, 21).Select(i => $"T{i}").ToArray()));
        Assert.Equal((ProjectDraftValidator.Fields.Tags, ProjectTagRules.TooManyMessage), (many.Field, many.Error));
        Assert.Equal(0, gated.Calls("update"));
    }

    // ---- T: store ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task T1_T14_CreateWithTags_OneTransaction_ReadBackInDisplayOrder_ByCompositeKey()
    {
        var id = CreateProject(null, "Beta", "web", "Ａｐｉ", "infra");
        var tags = (await Get(id)).Tags;
        Assert.Equal(["Ａｐｉ", "infra", "web"], tags.Select(t => t.Name)); // key order: API < INFRA < WEB
        Assert.Equal(["API", "INFRA", "WEB"], tags.Select(t => t.NameKey));
        Assert.All(tags, t => Assert.Equal(tags[0].CreatedAt, t.CreatedAt));
        Assert.Equal(typeof(ProjectTag).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal), ["CreatedAt", "Name", "NameKey"]); // no tag id
    }

    [Fact]
    public async Task T2_KeptKeys_KeepTheirStoredNameAndCreatedAt_RemovedGo_NewAreInserted()
    {
        var before = Rows(_p);
        await Task.Delay(5);
        var saved = await _workspace.UpdateProjectAsync(_p, Draft(await Get(_p), null, "infra", "New"));
        Assert.True(saved.Succeeded, saved.Error);
        var after = Rows(_p);
        Assert.Equal(before["INFRA"], after["INFRA"]); // still "Infra", original CreatedAt
        Assert.False(after.ContainsKey("OPS"));
        Assert.StartsWith("New|", after["NEW"], StringComparison.Ordinal);
        Assert.Equal(["Infra", "New"], saved.Value!.Tags.Select(t => t.Name));
    }

    [Fact]
    public async Task T3_AnIdenticalSet_WritesNoTagRow_ButUpdatedAtChangesAsForEverySave()
    {
        var before = Rows(_p);
        var updated = Details(_p).Updated;
        await Task.Delay(5);
        Assert.True((await _workspace.UpdateProjectAsync(_p, Draft(await Get(_p), null, "Ops", "Infra"))).Succeeded);
        Assert.Equal(before, Rows(_p));
        Assert.NotEqual(updated, Details(_p).Updated); // D6: unchanged editor rule
    }

    [Fact]
    public async Task T3b_NullTags_LeaveTheStoredSetUnchanged()
    {
        var d = await Get(_p);
        var before = Rows(_p);
        Assert.True((await _workspace.UpdateProjectAsync(_p, new ProjectDraft(d.Name, d.Status, d.Description, "Bob", d.ProjectManager))).Succeeded);
        Assert.Equal(before, Rows(_p));
    }

    [Fact]
    public async Task T4_T4a_T5_T5a_ArchivedOrTrashed_DirectlyOrThroughAFolder_AreRefused_NothingChanges()
    {
        var archived = CreateProject(null, "Archived", "Infra");
        var draftArchived = Draft(await Get(archived), null, "Other");
        await _workspace.ArchiveProjectAsync(archived);
        var trashed = CreateProject(null, "Trashed", "Infra");
        var draftTrashed = Draft(await Get(trashed), null, "Other");
        await _workspace.TrashProjectAsync(trashed);
        var draftP = Draft(await Get(_p), null, "Other");

        var before = State(archived) + "#" + State(trashed) + "#" + State(_p);
        Assert.Equal(WorkspaceFailure.ProjectArchived, (await _workspace.UpdateProjectAsync(archived, draftArchived)).Failure);
        Assert.Equal(WorkspaceFailure.ProjectTrashed, (await _workspace.UpdateProjectAsync(trashed, draftTrashed)).Failure);

        var archive = await _workspace.PrepareFolderLifecycleAsync(_folder, FolderLifecycleKind.Archive);
        Assert.Equal(FolderLifecycleOutcome.Committed, (await _workspace.MarkFolderAsync(_folder, FolderLifecycleKind.Archive, archive.Confirmation!.Token)).Outcome);
        Assert.Equal(WorkspaceFailure.AncestorNotLive, (await _workspace.UpdateProjectAsync(_p, draftP)).Failure); // T4a
        Assert.Equal(FolderClearOutcome.Cleared, (await _workspace.ClearFolderAsync(_folder, FolderLifecycleKind.Archive)).Outcome);
        var trash = await _workspace.PrepareFolderLifecycleAsync(_folder, FolderLifecycleKind.Trash);
        Assert.Equal(FolderLifecycleOutcome.Committed, (await _workspace.MarkFolderAsync(_folder, FolderLifecycleKind.Trash, trash.Confirmation!.Token)).Outcome);
        Assert.Equal(WorkspaceFailure.AncestorNotLive, (await _workspace.UpdateProjectAsync(_p, draftP)).Failure); // T5a

        Assert.Equal(before, State(archived) + "#" + State(trashed) + "#" + State(_p));
    }

    [Fact]
    public async Task T6_Suggestions_DistinctKeys_MostUsedSpelling_TrashedExcluded_ArchivedIncluded()
    {
        CreateProject(null, "B1", "infra", "Zeta");
        var b2 = CreateProject(null, "B2", "infra", "Only archived");
        var trashed = CreateProject(null, "B3", "Only trashed", "INFRA");
        await _workspace.ArchiveProjectAsync(b2);
        await _workspace.TrashProjectAsync(trashed);

        var suggestions = await _workspace.ListTagSuggestionsAsync();

        // INFRA: "infra" ×2 (B1, B2) beats "Infra" ×1 (Alpha); the trashed Project's "INFRA" and "Only trashed" do not count.
        Assert.Equal(["infra", "Only archived", "Ops", "Zeta"], suggestions);
    }

    [Fact]
    public async Task T6b_TheEditedProject_IsLeftOutBeforeSpellingsAreCountedAndTheLimitApplied()
    {
        var store = new SqliteWorkspaceStore(_connections);
        // Key OPS: "Ops" on Alpha (_p) and R; "ops" on Edited and Q. Key "AAA ONLY HERE" exists only on Edited.
        var edited = CreateProject(null, "Edited", "ops", "AAA only here");
        CreateProject(null, "Q", "ops", "BBB");
        CreateProject(null, "R", "Ops");

        var withEdited = await store.ListTagSuggestionsAsync(null);      // Ops 2 : ops 2 → ordinal → "Ops"
        var withoutEdited = await store.ListTagSuggestionsAsync(edited); // Ops 2 : ops 1 → "Ops"; Edited's own key is gone

        Assert.Contains("AAA only here", withEdited);
        Assert.DoesNotContain("AAA only here", withoutEdited);
        Assert.Equal("Ops", withoutEdited.Single(s => ProjectTagName.Key(s) == "OPS"));

        // Editing Alpha: its "Ops" no longer counts, so another Project's spelling is chosen (ops 2 : Ops 1), unlike with no exclusion.
        Assert.Equal("ops", (await store.ListTagSuggestionsAsync(_p)).Single(s => ProjectTagName.Key(s) == "OPS"));
        Assert.Equal("Ops", withEdited.Single(s => ProjectTagName.Key(s) == "OPS"));

        // The limit is applied after the exclusion: with limit 1, the edited Project's first key does not take the only slot.
        Assert.Equal(["AAA only here"], await store.ListTagSuggestionsAsync(null, 1));
        Assert.Equal(["BBB"], await store.ListTagSuggestionsAsync(edited, 1));
    }

    [Fact]
    public async Task T6c_WithAnEditedProject_TrashedProjectsAndThoseInATrashedFolderStayOut_ArchivedStayIn()
    {
        var store = new SqliteWorkspaceStore(_connections);
        var archived = CreateProject(null, "Archived", "From archived");
        await _workspace.ArchiveProjectAsync(archived);
        var trashed = CreateProject(null, "Trashed", "From trashed");
        await _workspace.TrashProjectAsync(trashed);
        CreateProject(_folder, "In folder", "From trashed folder");
        var trash = await _workspace.PrepareFolderLifecycleAsync(_folder, FolderLifecycleKind.Trash);
        await _workspace.MarkFolderAsync(_folder, FolderLifecycleKind.Trash, trash.Confirmation!.Token);
        var edited = CreateProject(null, "Edited", "Own");

        var suggestions = await store.ListTagSuggestionsAsync(edited);

        Assert.Contains("From archived", suggestions);
        Assert.DoesNotContain("From trashed", suggestions);
        Assert.DoesNotContain("From trashed folder", suggestions);
        Assert.DoesNotContain("Infra", suggestions); // Alpha is inside the trashed folder too
        Assert.DoesNotContain("Own", suggestions);
    }

    [Fact]
    public async Task T7_ArchiveUnarchiveUndo_TrashRestore_FolderLifecycle_ChangeNoTagRow()
    {
        var before = Rows(_p);
        var archived = await _workspace.ArchiveProjectAsync(_p);
        Assert.Equal(before, Rows(_p));
        Assert.Equal(ArchiveUndoOutcome.Undone, (await _workspace.UndoArchiveAsync(archived.Event ?? throw new InvalidOperationException("no event"))).Outcome);
        await _workspace.ArchiveProjectAsync(_p);
        await _workspace.UnarchiveProjectAsync(_p, ProjectStatus.Active);
        await _workspace.TrashProjectAsync(_p);
        Assert.Equal(before, Rows(_p));
        await _workspace.RestoreProjectAsync(_p);
        var trash = await _workspace.PrepareFolderLifecycleAsync(_folder, FolderLifecycleKind.Trash);
        await _workspace.MarkFolderAsync(_folder, FolderLifecycleKind.Trash, trash.Confirmation!.Token);
        Assert.Equal(FolderClearOutcome.Cleared, (await _workspace.ClearFolderAsync(_folder, FolderLifecycleKind.Trash)).Outcome);
        Assert.Equal(before, Rows(_p));
    }

    [Fact]
    public async Task T8_AFailureBeforeTheTagDeletes_RollsBackNameDetailsAndTags()
    {
        var store = new SqliteWorkspaceStore(_connections) { AfterTagDeletes = (_, _) => { } };
        var before = State(_p);
        var d = await Get(_p);
        // An invalid set reaching the store (bypassing the validator) is refused before any tag SQL; the rename rolls back.
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateProjectAsync(_p, new ProjectDraft("Renamed", d.Status, d.Description, "Bob", null, ["a", "A"])));
        Assert.Equal(before, State(_p));
    }

    [Fact]
    public async Task T9_TwoEditors_TheLastSaveDecidesTheWholeSet_DiffAgainstTheDatabaseAtSaveTime()
    {
        var loadedByA = await _workspace.GetProjectAsync(_p);
        var loadedByB = await _other.GetProjectAsync(_p);
        var infra = Rows(_p)["INFRA"];

        Assert.True((await _workspace.UpdateProjectAsync(_p, Draft(loadedByA!, "Owner A", "Infra", "Ops", "Client"))).Succeeded);
        Assert.True((await _other.UpdateProjectAsync(_p, Draft(loadedByB!, "Owner B", "Infra", "Web"))).Succeeded);

        var rows = Rows(_p);
        Assert.Equal(["INFRA", "WEB"], rows.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(infra, rows["INFRA"]);
        Assert.Equal("Owner B", Details(_p).Owner);
    }

    [Fact]
    public async Task T10_At20Tags_ReplacingOne_Succeeds_TheOther19Unchanged()
    {
        var names = Enumerable.Range(1, 20).Select(i => $"T{i:D2}").ToArray();
        Assert.True((await _workspace.UpdateProjectAsync(_p, Draft(await Get(_p), null, names))).Succeeded);
        var before = Rows(_p);
        Assert.Equal(20, before.Count);

        var saved = await _workspace.UpdateProjectAsync(_p, Draft(await Get(_p), null, [.. names.Where(n => n != "T07"), "New"]));

        Assert.True(saved.Succeeded, saved.Error);
        var after = Rows(_p);
        Assert.Equal(20, after.Count);
        Assert.False(after.ContainsKey("T07"));
        Assert.True(after.ContainsKey("NEW"));
        Assert.All(before.Where(r => r.Key != "T07"), r => Assert.Equal(r.Value, after[r.Key]));
    }

    [Fact]
    public async Task T11_At20Tags_AddingOne_IsRefusedByTheValidator_NothingChanges()
    {
        var names = Enumerable.Range(1, 20).Select(i => $"T{i:D2}").ToArray();
        await _workspace.UpdateProjectAsync(_p, Draft(await Get(_p), null, names));
        var before = State(_p);
        var result = await _workspace.UpdateProjectAsync(_p, Draft(await Get(_p), "Bob", [.. names, "T21"]));
        Assert.Equal(ProjectTagRules.TooManyMessage, result.Error);
        Assert.Equal(before, State(_p));
    }

    [Fact]
    public async Task T12_AFailureAfterTheDeletes_RollsBackDetailsAndTheWholeSet()
    {
        var before = State(_p);
        var throwing = new WorkspaceService(new SqliteWorkspaceStore(_connections) { AfterTagDeletes = (_, _) => throw new InvalidOperationException("injected after the deletes") });
        var d = await Get(_p);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => throwing.UpdateProjectAsync(_p, Draft(d, "Bob", "New"))); // removes Infra, Ops
        Assert.Equal("injected after the deletes", ex.Message);
        Assert.Equal(before, State(_p));

        // Variant: the seam fills the Project to 20 inside the same transaction, so the store's INSERT hits the real WH_TAG_LIMIT.
        var filling = new WorkspaceService(new SqliteWorkspaceStore(_connections)
        {
            AfterTagDeletes = (connection, transaction) =>
            {
                for (var i = 1; i <= 20; i++)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "INSERT INTO ProjectTags (ProjectNodeId, NameKey, Name, CreatedAt) VALUES ($p, $k, $k, '2026-01-01T00:00:00.000Z');";
                    command.Parameters.AddWithValue("$p", _p);
                    command.Parameters.AddWithValue("$k", $"X{i:D2}");
                    command.ExecuteNonQuery();
                }
            },
        });
        var limit = await Assert.ThrowsAsync<SqliteException>(() => filling.UpdateProjectAsync(_p, Draft(d, "Bob", "New")));
        Assert.Contains("WH_TAG_LIMIT", limit.Message, StringComparison.Ordinal);
        Assert.Equal(before, State(_p));
    }

    [Fact]
    public void T13_TheStoreNeverReplacesUpsertsOrUpdatesTagRows()
    {
        var repo = FindRepoRoot();
        foreach (var file in new[] { "SqliteWorkspaceStore.cs", "SqliteWorkspaceSearchStore.cs" })
        {
            var source = File.ReadAllText(Path.Combine(repo, "src", "WorkHub.Infrastructure", "Persistence", file));
            Assert.DoesNotContain("OR REPLACE", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ON CONFLICT", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE ProjectTags", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- X: Phase 14 / Phase 15 regressions on tagged Projects ----------------------------------------------------------------

    [Fact]
    public async Task X1_Phase14_PurgingTheTrashOfATaggedProject_KeepsItsTags_AndThePreDeleteCopyHoldsThem()
    {
        using var w = new PurgeWorld();
        await w.BuildAsync();
        var d = (await w.Workspace.GetProjectAsync(w.P))!;
        Assert.True((await w.Workspace.UpdateProjectAsync(w.P, new ProjectDraft(d.Name, d.Status, d.Description, d.Owner, d.ProjectManager, ["Infra", "Ops"]))).Succeeded);
        var before = w.Dump();

        var request = PurgeRequest.Selected(w.P, [w.E_L1]);
        var preview = await w.Content.PreparePurgeAsync(request);
        Assert.True(preview.IsReady);
        var result = await w.Content.PurgeAsync(request, preview.Preview!.Token, null, CancellationToken.None);

        Assert.Equal(PurgeOutcome.Purged, result.Outcome);
        var after = w.Dump();
        Assert.Equal(before["ProjectTags"], after["ProjectTags"]);
        var copy = Assert.Single(w.PreDeleteFiles());
        Assert.Equal(before["ProjectTags"], w.Dump(Path.Combine(w.Root.Paths.PreDeleteBackupsDirectory, copy))["ProjectTags"]);
        w.AssertIntegrity();
    }

    [Fact]
    public async Task X2_Phase15_CopyAndMoveIntoAndOutOfTaggedProjects_LeaveBothSetsUnchanged()
    {
        using var w = new CrossWorld();
        await w.BuildAsync();
        foreach (var (project, tags) in new[] { (w.A, new[] { "Source" }), (w.C, new[] { "Target", "Shared" }) })
        {
            var d = (await w.Workspace.GetProjectAsync(project))!;
            Assert.True((await w.Workspace.UpdateProjectAsync(project, new ProjectDraft(d.Name, d.Status, d.Description, d.Owner, d.ProjectManager, tags))).Succeeded);
        }

        var before = w.Dump()["ProjectTags"];
        var copy = await w.CommitAsync(CrossWorld.Req(CrossProjectMode.Copy, ContentKind.Note, w.NA, w.C, null));
        Assert.True(copy.Result.Committed);
        var move = await w.CommitAsync(CrossWorld.Req(CrossProjectMode.Move, ContentKind.Link, w.LA, w.C, null));
        Assert.True(move.Result.Committed);

        Assert.Equal(before, w.Dump()["ProjectTags"]);
        w.AssertIntegrity();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkHub.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WorkHub.sln not found above the test output folder.");
    }
}
