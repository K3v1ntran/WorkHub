using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 21 R2a (proposal §3.3.4, §4.3 TE1-TE6): a Project editor open in window A while window B renames or merges a tag. Window A
/// = <see cref="ProjectEditorViewModel"/> on service A (a gated store that records every draft sent to UpdateProjectAsync); window
/// B = service B on its own store and connections, same real SQLite test database. A opens the editor BEFORE B commits. Each test
/// states the baseline, chips, the draft sent to the store, the outcome and the data after.
/// </summary>
public sealed class ProjectEditorTagBaselineTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly GatedWorkspaceStore _gated;
    private readonly WorkspaceService _a;
    private readonly WorkspaceService _b;
    private readonly long _p1;
    private readonly long _p8;

    public ProjectEditorTagBaselineTests()
    {
        _root.CreateInitializer().Initialize();
        _gated = new GatedWorkspaceStore(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _a = new WorkspaceService(_gated);
        _b = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _p1 = _b.CreateProjectAsync(null, new ProjectDraft("P1", ProjectStatus.Active, null, "O1", null, ["clientA"])).Result.Value!.Id;
        _p8 = _b.CreateProjectAsync(null, new ProjectDraft("P8", ProjectStatus.Active, null, "O8", null, ["Client A", "clientA"])).Result.Value!.Id;
    }

    public void Dispose()
    {
        _gated.ReleaseAll();
        _root.Dispose();
    }

    private static readonly string ClientA = ProjectTagName.Key("clientA");

    private async Task<(ProjectEditorViewModel Editor, List<bool> Closed)> OpenA(long project)
    {
        var details = (await _a.GetProjectAsync(project))!;
        var editor = ProjectEditorViewModel.ForEdit(_a, details, null);
        var closed = new List<bool>();
        editor.CloseRequested += (_, saved) => closed.Add(saved);
        return (editor, closed);
    }

    private async Task BChange(TagChangeRequest request)
    {
        var preview = (await _b.PrepareTagChangeAsync(request)).Value!;
        Assert.True((await _b.ApplyTagChangeAsync(request, preview.Token)).Committed);
    }

    private Task BRename(string from, string to) => BChange(new TagChangeRequest(TagChangeKind.Rename, ProjectTagName.Key(from), to));

    private Task BMerge(string from, string into) => BChange(new TagChangeRequest(TagChangeKind.MergeInto, ProjectTagName.Key(from), into));

    private async Task<ProjectDetails> Stored(long project) => (await _b.GetProjectAsync(project))!;

    private List<string> Data()
    {
        using var connection = _root.Open();
        return ProjectContentMigrationTests.DataSnapshot(connection);
    }

    private static string[] Names(IEnumerable<string> names) => names.Order(StringComparer.Ordinal).ToArray();

    private static string[] Pairs(IEnumerable<ProjectTag> tags) => tags.Select(t => $"{t.NameKey}|{t.Name}").Order(StringComparer.Ordinal).ToArray();

    private ProjectDraft LastDraft => _gated.UpdateDrafts.Last();

    private static void AddChip(ProjectEditorViewModel editor, string name)
    {
        editor.TagInput = name;
        Assert.True(editor.AddPendingTag());
    }

    private static void RemoveChip(ProjectEditorViewModel editor, string name) => editor.Tags.Single(t => t.Name == name).Command.Execute(null);

    /// <summary>TE3 steps 1-2: A opens P1 ({clientA}), B renames clientA -> ClientX, A edits Owner and adds "Extra", Save -> TagsChanged.</summary>
    private async Task<(ProjectEditorViewModel Editor, List<bool> Closed, List<string> AfterB)> Te3Refused()
    {
        var (editor, closed) = await OpenA(_p1);
        await BRename("clientA", "ClientX");
        var afterB = Data();
        editor.Owner = "O2";
        AddChip(editor, "Extra");
        await editor.SaveCommand.ExecuteAsync();
        return (editor, closed, afterB);
    }

    [Fact]
    public async Task TE1_OwnerOnlyAfterBRenames_SendsTagsNull_SavesOwner_KeepsBsRename()
    {
        var created = (await Stored(_p1)).Tags.Single().CreatedAt;
        var (editor, closed) = await OpenA(_p1);
        await BRename("clientA", "ClientX");
        editor.Owner = "O2";                                                       // baseline {clientA}, chips {clientA}

        await editor.SaveCommand.ExecuteAsync();

        Assert.Equal([true], closed);
        Assert.Null(LastDraft.Tags);
        Assert.Null(LastDraft.TagsBaseline);
        var stored = await Stored(_p1);
        Assert.Equal("O2", stored.Owner);
        Assert.Equal(["ClientX"], stored.Tags.Select(t => t.Name));
        Assert.Equal(created, stored.Tags.Single().CreatedAt);                    // the rename kept the source CreatedAt
    }

    [Fact]
    public async Task TE2_OwnerOnlyAfterBMerges_SendsTagsNull_KeepsBsMerge()
    {
        var target = (await Stored(_p8)).Tags.Single(t => t.Name == "Client A");
        var (editor, closed) = await OpenA(_p8);
        await BMerge("clientA", "Client A");
        editor.Owner = "O8b";

        await editor.SaveCommand.ExecuteAsync();

        Assert.Equal([true], closed);
        Assert.Null(LastDraft.Tags);
        var stored = await Stored(_p8);
        Assert.Equal("O8b", stored.Owner);
        Assert.Equal([target], stored.Tags);                                       // the target row unchanged
    }

    [Fact]
    public async Task TE3_TagEditAfterBRenames_IsRefused_DraftKept_NothingWritten_FreshTagsPresentedButNotAccepted()
    {
        var (editor, closed, afterB) = await Te3Refused();

        Assert.Empty(closed);
        Assert.Equal(WorkspaceFailure.TagsChanged, editor.Refusal);
        Assert.Equal(["clientA", "Extra"], Names(LastDraft.Tags!));               // draft sent: chips + baseline {clientA}
        Assert.Equal([$"{ClientA}|clientA"], Pairs(LastDraft.TagsBaseline!));
        Assert.Equal(afterB, Data());                                              // no field, tag or UpdatedAt written
        Assert.Equal("O2", editor.Owner);                                          // draft kept
        Assert.Equal(["clientA", "Extra"], Names(editor.Tags.Select(t => t.Name)));
        Assert.True(editor.IsTagConflictPending);
        Assert.Equal(["ClientX"], editor.PresentedTags!.Select(t => t.Name));
        Assert.Equal("Current tags: ClientX", editor.CurrentTagsText);
        Assert.Equal([$"{ClientA}|clientA"], Pairs(editor.TagBaseline));          // the read completing accepted nothing
        Assert.False(editor.SaveCommand.CanExecute(null));
        var calls = _gated.UpdateDrafts.Count;
        await editor.SaveCommand.ExecuteAsync();                                   // Save stays disabled until a choice
        Assert.Equal(calls, _gated.UpdateDrafts.Count);
    }

    [Fact]
    public async Task TE3b_Control_UseCurrentTags_ChipsAndBaselineBecomeClientX_OwnerOnlySaveSendsTagsNull()
    {
        var (editor, closed, _) = await Te3Refused();
        editor.UseCurrentTagsCommand.Execute(null);
        Assert.Equal(["ClientX"], editor.Tags.Select(t => t.Name));
        Assert.Equal(["ClientX"], editor.TagBaseline.Select(t => t.Name));
        Assert.False(editor.IsTagConflictPending);
        var stored = (await Stored(_p1)).Tags;

        await editor.SaveCommand.ExecuteAsync();

        Assert.Equal([true], closed);
        Assert.Null(LastDraft.Tags);                                               // chips = baseline
        Assert.Equal("O2", (await Stored(_p1)).Owner);
        Assert.Equal(stored, (await Stored(_p1)).Tags);                           // the current tag set kept
    }

    [Fact]
    public async Task TE3c_KeepMyTags_WithTheChipsBackAtTheFirstReadSet_SendsTheChipsWithTheNewBaseline()
    {
        var (editor, closed, _) = await Te3Refused();
        RemoveChip(editor, "Extra");                                               // chips {clientA} = the set read on open
        editor.KeepMyTagsCommand.Execute(null);
        Assert.Equal(["ClientX"], editor.TagBaseline.Select(t => t.Name));        // baseline := the presented set

        await editor.SaveCommand.ExecuteAsync();

        Assert.Equal([true], closed);
        Assert.NotNull(LastDraft.Tags);                                            // never Tags = null here
        Assert.Equal(["clientA"], Names(LastDraft.Tags!));
        Assert.Equal(["ClientX"], LastDraft.TagsBaseline!.Select(t => t.Name));
        var stored = await Stored(_p1);
        Assert.Equal("O2", stored.Owner);
        Assert.Equal(["clientA"], stored.Tags.Select(t => t.Name));               // A's explicit choice
    }

    [Fact]
    public async Task TE3d_KeepMyTags_ThenBChangesTheTagsAgain_ANewTagsChanged_NothingPartial()
    {
        var (editor, closed, _) = await Te3Refused();
        RemoveChip(editor, "Extra");
        editor.KeepMyTagsCommand.Execute(null);
        await BRename("ClientX", "ClientY");
        var afterB = Data();

        await editor.SaveCommand.ExecuteAsync();

        Assert.Empty(closed);
        Assert.Equal(WorkspaceFailure.TagsChanged, editor.Refusal);
        Assert.Equal(["clientA"], Names(LastDraft.Tags!));
        Assert.Equal(["ClientX"], LastDraft.TagsBaseline!.Select(t => t.Name));
        Assert.Equal(afterB, Data());
        Assert.Equal("O2", editor.Owner);
        Assert.Equal(["clientA"], editor.Tags.Select(t => t.Name));
        Assert.Equal(["ClientY"], editor.PresentedTags!.Select(t => t.Name));
        Assert.Equal(["ClientX"], editor.TagBaseline.Select(t => t.Name));
        Assert.False(editor.SaveCommand.CanExecute(null));
        var stored = await Stored(_p1);
        Assert.Equal("O1", stored.Owner);
        Assert.Equal(["ClientY"], stored.Tags.Select(t => t.Name));
    }

    [Fact]
    public async Task TE3e_KeepMyTags_WithAnExtraTag_SavesTheChips()
    {
        var (editor, closed, _) = await Te3Refused();
        editor.KeepMyTagsCommand.Execute(null);

        await editor.SaveCommand.ExecuteAsync();

        Assert.Equal([true], closed);
        Assert.Equal(["clientA", "Extra"], Names(LastDraft.Tags!));
        Assert.Equal(["ClientX"], LastDraft.TagsBaseline!.Select(t => t.Name));
        Assert.Equal(["clientA", "Extra"], Names((await Stored(_p1)).Tags.Select(t => t.Name)));
    }

    [Fact]
    public async Task TE4_TagEditAfterBMerges_IsRefused_DescriptionNotWritten()
    {
        var (editor, closed) = await OpenA(_p8);
        await BMerge("clientA", "Client A");
        var afterB = Data();
        RemoveChip(editor, "Client A");
        editor.Description = "edited";

        await editor.SaveCommand.ExecuteAsync();

        Assert.Empty(closed);
        Assert.Equal(WorkspaceFailure.TagsChanged, editor.Refusal);
        Assert.Equal(["clientA"], Names(LastDraft.Tags!));
        Assert.Equal(2, LastDraft.TagsBaseline!.Count);
        Assert.Equal(afterB, Data());
        Assert.Equal(["Client A"], editor.PresentedTags!.Select(t => t.Name));
    }

    [Fact]
    public async Task TE5_BChangesOnlyTheSpelling_SameKey_TheBaselineComparesNamesToo()
    {
        var (editor, closed) = await OpenA(_p1);
        await BRename("clientA", "ClientA");                                       // same NameKey, new spelling
        var afterB = Data();
        editor.Owner = "O2";
        AddChip(editor, "Extra");

        await editor.SaveCommand.ExecuteAsync();

        Assert.Empty(closed);
        Assert.Equal(WorkspaceFailure.TagsChanged, editor.Refusal);               // a NameKey-only comparison would let it through
        Assert.Equal(afterB, Data());
        var stored = await Stored(_p1);
        Assert.Equal("O1", stored.Owner);
        Assert.Equal(["ClientA"], stored.Tags.Select(t => t.Name));
    }

    /// <summary>
    /// Correction R1: after a TagsChanged review choice, a Save that fails Owner validation (no typed failure) must not reuse the
    /// earlier Refusal to re-read and re-present the tags. Only the Owner error shows; chips, baseline and data stay; once Owner is
    /// fixed Save works without choosing again.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TE7_AfterAReviewChoice_AnOwnerValidationError_DoesNotReopenTheTagReview(bool useCurrentTags)
    {
        var (editor, closed, afterB) = await Te3Refused();
        if (useCurrentTags)
            editor.UseCurrentTagsCommand.Execute(null);                           // chips {ClientX}, baseline {ClientX}
        else
            editor.KeepMyTagsCommand.Execute(null);                               // chips {clientA, Extra}, baseline {ClientX}
        var chips = Names(editor.Tags.Select(t => t.Name));
        var baseline = Pairs(editor.TagBaseline);
        var reads = _gated.Calls($"details:{_p1}");
        var updates = _gated.UpdateDrafts.Count;
        editor.Owner = new string('o', ProjectDraftValidator.MaxPersonLength + 1);

        await editor.SaveCommand.ExecuteAsync();

        Assert.Empty(closed);
        Assert.NotNull(editor.OwnerError);                                         // only the Owner error
        Assert.Null(editor.NameError);
        Assert.Null(editor.StatusError);
        Assert.Null(editor.DescriptionError);
        Assert.Null(editor.ProjectManagerError);
        Assert.Null(editor.TagError);
        Assert.Null(editor.GeneralError);
        Assert.Equal(reads, _gated.Calls($"details:{_p1}"));                     // the tags were not read again
        Assert.False(editor.IsTagConflictPending);                                 // the review was not reopened
        Assert.Null(editor.PresentedTags);
        Assert.Null(editor.CurrentTagsText);
        Assert.Equal(chips, Names(editor.Tags.Select(t => t.Name)));
        Assert.Equal(baseline, Pairs(editor.TagBaseline));
        Assert.Equal(updates, _gated.UpdateDrafts.Count);                          // refused before the store
        Assert.Equal(afterB, Data());
        Assert.Equal(WorkspaceFailure.TagsChanged, editor.Refusal);                // the real earlier refusal is kept for the area
        Assert.True(editor.SaveCommand.CanExecute(null));

        editor.Owner = "O2";
        await editor.SaveCommand.ExecuteAsync();                                   // no new review choice needed

        Assert.Equal([true], closed);
        Assert.Null(editor.OwnerError);
        var stored = await Stored(_p1);
        Assert.Equal("O2", stored.Owner);
        if (useCurrentTags)
        {
            Assert.Null(LastDraft.Tags);
            Assert.Equal(["ClientX"], stored.Tags.Select(t => t.Name));
        }
        else
        {
            Assert.Equal(["ClientX"], LastDraft.TagsBaseline!.Select(t => t.Name));
            Assert.Equal(["clientA", "Extra"], Names(stored.Tags.Select(t => t.Name)));
        }
    }

    /// <summary>
    /// Correction R2: the fresh read after TagsChanged throws. The read error shows; draft, chips and baseline stay; no NodeNotFound and
    /// no tag set is presented; Save (and the review buttons) do nothing while there is no set to review.
    /// </summary>
    [Fact]
    public async Task TE8_ReadAfterTagsChangedThrows_ShowsTheReadError_KeepsTheDraft_NoNodeNotFound_SaveDoesNotProceed()
    {
        var (editor, closed) = await OpenA(_p1);
        await BRename("clientA", "ClientX");
        var afterB = Data();
        editor.Owner = "O2";
        AddChip(editor, "Extra");
        _gated.ScriptProject[_p1] = () => throw new IOException("disk read failed");

        await editor.SaveCommand.ExecuteAsync();

        Assert.Empty(closed);
        Assert.Equal(WorkspaceFailure.TagsChanged, editor.Refusal);               // not NodeNotFound
        Assert.NotNull(editor.GeneralError);
        Assert.Contains("Could not read", editor.GeneralError);
        Assert.Contains("disk read failed", editor.GeneralError);
        Assert.Null(editor.PresentedTags);                                         // no unread set presented
        Assert.Null(editor.CurrentTagsText);
        Assert.Equal("O2", editor.Owner);
        Assert.Equal(["clientA", "Extra"], Names(editor.Tags.Select(t => t.Name)));
        Assert.Equal([$"{ClientA}|clientA"], Pairs(editor.TagBaseline));
        Assert.Equal(afterB, Data());
        Assert.True(editor.IsTagConflictPending);
        Assert.False(editor.SaveCommand.CanExecute(null));

        var updates = _gated.UpdateDrafts.Count;
        await editor.SaveCommand.ExecuteAsync();
        editor.UseCurrentTagsCommand.Execute(null);                                // nothing presented: no-op
        editor.KeepMyTagsCommand.Execute(null);

        Assert.Equal(updates, _gated.UpdateDrafts.Count);
        Assert.True(editor.IsTagConflictPending);
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal(["clientA", "Extra"], Names(editor.Tags.Select(t => t.Name)));
        Assert.Equal([$"{ClientA}|clientA"], Pairs(editor.TagBaseline));
        Assert.Equal(afterB, Data());
        Assert.Empty(closed);
    }

    /// <summary>Control for R2: the fresh read completes and returns null → NodeNotFound, Save stays disabled.</summary>
    [Fact]
    public async Task TE8b_Control_ReadAfterTagsChangedReturnsNull_SetsNodeNotFound()
    {
        var (editor, closed) = await OpenA(_p1);
        await BRename("clientA", "ClientX");
        var afterB = Data();
        AddChip(editor, "Extra");
        _gated.ScriptProject[_p1] = () => Task.FromResult<ProjectDetails?>(null);

        await editor.SaveCommand.ExecuteAsync();

        Assert.Empty(closed);
        Assert.Equal(WorkspaceFailure.NodeNotFound, editor.Refusal);
        Assert.Equal("This project no longer exists. Nothing was saved.", editor.GeneralError);
        Assert.Null(editor.PresentedTags);
        Assert.Equal(["clientA", "Extra"], Names(editor.Tags.Select(t => t.Name)));
        Assert.Equal([$"{ClientA}|clientA"], Pairs(editor.TagBaseline));
        Assert.False(editor.SaveCommand.CanExecute(null));
        Assert.Equal(afterB, Data());
    }

    [Fact]
    public async Task TE6_NoWindowB_AddSendsTheBaseline_OwnerOnlyAndAddThenRemoveSendTagsNull()
    {
        var (a, closedA) = await OpenA(_p1);
        AddChip(a, "Extra");
        await a.SaveCommand.ExecuteAsync();
        Assert.Equal([true], closedA);
        Assert.Equal([$"{ClientA}|clientA"], Pairs(LastDraft.TagsBaseline!));
        Assert.Equal(["clientA", "Extra"], Names((await Stored(_p1)).Tags.Select(t => t.Name)));

        var (b, closedB) = await OpenA(_p1);
        b.Owner = "O3";
        await b.SaveCommand.ExecuteAsync();
        Assert.Equal([true], closedB);
        Assert.Null(LastDraft.Tags);

        var (c, closedC) = await OpenA(_p1);
        AddChip(c, "Temp");
        RemoveChip(c, "Temp");
        c.Owner = "O4";
        await c.SaveCommand.ExecuteAsync();
        Assert.Equal([true], closedC);
        Assert.Null(LastDraft.Tags);
        var stored = await Stored(_p1);
        Assert.Equal("O4", stored.Owner);
        Assert.Equal(["clientA", "Extra"], Names(stored.Tags.Select(t => t.Name)));
    }
}
