using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Presentation.ViewModels;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.ViewModels;

/// <summary>
/// Phase 17 Project editor tags (proposal R2b §4.1–§4.3, §4.2.1; R2a-3; tests V1, V1a–V1e, two editors): chips, duplicate
/// notes, invalid text and the 21st tag keep the dialog open with nothing written; Save commits valid pending text first;
/// a box with only white space is no pending tag; suggestions.
/// </summary>
public sealed class ProjectTagsEditorViewModelTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly GatedWorkspaceStore _store;
    private readonly WorkspaceService _workspace;
    private readonly long _p;

    public ProjectTagsEditorViewModelTests()
    {
        _root.CreateInitializer().Initialize();
        _store = new GatedWorkspaceStore(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
        _workspace = new WorkspaceService(_store);
        _p = _workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active, null, "Alice", null, ["Infra"])).Result.Value!.Id;
    }

    public void Dispose()
    {
        _store.ReleaseAll();
        _root.Dispose();
    }

    private async Task<ProjectEditorViewModel> Edit()
    {
        var details = await _workspace.GetProjectAsync(_p);
        return ProjectEditorViewModel.ForEdit(_workspace, details!, null);
    }

    private static bool Closed(ProjectEditorViewModel editor, out Func<bool> closed)
    {
        var result = false;
        editor.CloseRequested += (_, saved) => result = saved;
        closed = () => result;
        return true;
    }

    private async Task<ProjectDetails> Stored() => (await _workspace.GetProjectAsync(_p))!;

    [Fact]
    public async Task V1_AddDuplicateInvalid21stBackspaceAndCancel()
    {
        var editor = await Edit();
        Assert.Equal(["Infra"], editor.Tags.Select(t => t.Name));

        editor.TagInput = " Ops ";
        Assert.True(editor.AddPendingTag());
        Assert.Equal(["Infra", "Ops"], editor.Tags.Select(t => t.Name));
        Assert.Equal(string.Empty, editor.TagInput);

        editor.TagInput = "ＩＮＦＲＡ";
        Assert.True(editor.AddPendingTag()); // not an error
        Assert.Equal("Already tagged “Infra”.", editor.TagNote);
        Assert.Null(editor.TagError);
        Assert.Equal(2, editor.Tags.Count);

        editor.TagInput = new string('x', 41);
        Assert.False(editor.AddPendingTag());
        Assert.Equal("A tag must be at most 40 characters.", editor.TagError);
        Assert.Equal(new string('x', 41), editor.TagInput); // the text stays

        editor.TagInput = string.Empty;
        editor.RemoveLastTag();
        Assert.Equal(["Infra"], editor.Tags.Select(t => t.Name));
        editor.Tags[0].Command.Execute(null); // ✕ on the chip
        Assert.Empty(editor.Tags);

        for (var i = 1; i <= 20; i++)
        {
            editor.TagInput = $"T{i}";
            Assert.True(editor.AddPendingTag());
        }

        Assert.True(editor.IsTagLimitReached);
        Assert.False(editor.CanAddTag);
        Assert.False(editor.AddTagCommand.CanExecute(null));
        editor.TagInput = "T21";
        Assert.False(editor.AddPendingTag()); // a direct call is still refused
        Assert.Equal("At most 20 tags.", editor.TagError);

        editor.CancelCommand.Execute(null);
        Assert.Equal(0, _store.Calls("update"));
        Assert.Equal(["Infra"], (await Stored()).Tags.Select(t => t.Name));
    }

    [Fact]
    public async Task V1a_SaveWithValidPendingText_AddsItThenSaves_Once()
    {
        var editor = await Edit();
        Closed(editor, out var closed);
        editor.TagInput = "Ops";

        await editor.SaveCommand.ExecuteAsync();

        Assert.True(closed());
        Assert.Equal(1, _store.Calls("update"));
        Assert.Equal(["Infra", "Ops"], (await Stored()).Tags.Select(t => t.Name));
    }

    [Theory]
    [InlineData("forty-one")]
    [InlineData("bell")]
    public async Task V1b_SaveWithInvalidPendingText_KeepsTheDialogOpen_NothingWritten(string kind)
    {
        var editor = await Edit();
        Closed(editor, out var closed);
        var text = kind == "bell" ? "a\u0007b" : new string('x', 41);
        editor.Owner = "Bob";
        editor.TagInput = text;
        var before = await Stored();

        await editor.SaveCommand.ExecuteAsync();

        Assert.False(closed());
        Assert.NotNull(editor.TagError);
        Assert.Equal(text, editor.TagInput);
        Assert.Equal(0, _store.Calls("update"));
        var after = await Stored();
        Assert.Equal((before.Owner, before.UpdatedAt), (after.Owner, after.UpdatedAt));
        Assert.Equal(before.Tags, after.Tags);
    }

    [Fact]
    public async Task V1c_SaveWith20ChipsAndPendingText_KeepsTheDialogOpen_NothingWritten()
    {
        var editor = await Edit();
        Closed(editor, out var closed);
        for (var i = 1; i <= 19; i++)
        {
            editor.TagInput = $"T{i}";
            editor.AddPendingTag();
        }

        editor.TagInput = "New";
        await editor.SaveCommand.ExecuteAsync();

        Assert.False(closed());
        Assert.Equal("At most 20 tags.", editor.TagError);
        Assert.Equal(0, _store.Calls("update"));
        Assert.Equal(["Infra"], (await Stored()).Tags.Select(t => t.Name));
    }

    [Fact]
    public async Task V1d_SaveWithADuplicatePendingText_SavesTheUnchangedSet_WithANote()
    {
        var editor = await Edit();
        Closed(editor, out var closed);
        editor.TagInput = "infra";

        await editor.SaveCommand.ExecuteAsync();

        Assert.True(closed());
        Assert.Equal("Already tagged “Infra”.", editor.TagNote);
        Assert.Equal(1, _store.Calls("update"));
        Assert.Equal(["Infra"], (await Stored()).Tags.Select(t => t.Name));
    }

    /// <summary>White space of each kind R2b names (spaces, U+3000, tab) and a mix; code points as data, built in the test.</summary>
    public static TheoryData<string> WhiteSpaceKinds() => ["spaces", "ideographic", "tab", "mixed"];

    private static string WhiteSpace(string kind) => kind switch
    {
        "spaces" => "   ",
        "ideographic" => new string((char)0x3000, 2),
        "tab" => "\t",
        _ => "  " + (char)0x3000 + "\t",
    };

    /// <summary>R2a-3 / V1e: white space only is no pending tag; the details change is saved with exactly the current chips.</summary>
    [Theory]
    [MemberData(nameof(WhiteSpaceKinds))]
    public async Task V1e_SaveWithOnlyWhiteSpaceInTheTagBox_SavesTheDetailsChange_WithTheCurrentChips(string kind)
    {
        var editor = await Edit();
        Closed(editor, out var closed);
        editor.TagInput = WhiteSpace(kind);
        editor.Owner = "Bob";
        var infraCreated = (await Stored()).Tags.Single().CreatedAt;

        await editor.SaveCommand.ExecuteAsync();

        Assert.True(closed());
        Assert.Equal(1, _store.Calls("update"));
        Assert.Single(editor.Tags);
        Assert.Null(editor.TagError);
        Assert.Null(editor.TagNote);
        var stored = await Stored();
        Assert.Equal("Bob", stored.Owner);
        Assert.Equal(["Infra"], stored.Tags.Select(t => t.Name));
        Assert.Equal(infraCreated, stored.Tags.Single().CreatedAt);
    }

    [Theory]
    [MemberData(nameof(WhiteSpaceKinds))]
    public async Task V1e_Variant_InvalidOwner_DialogOpen_OwnerError_TagBoxTextAndChipsKept_NoStoreCall(string kind)
    {
        var editor = await Edit();
        Closed(editor, out var closed);
        var text = WhiteSpace(kind);
        editor.TagInput = text;
        editor.Owner = new string('o', 101);

        await editor.SaveCommand.ExecuteAsync();

        Assert.False(closed());
        Assert.NotNull(editor.OwnerError);
        Assert.Null(editor.TagError);
        Assert.Equal(0, _store.Calls("update"));
        Assert.Equal(["Infra"], editor.Tags.Select(t => t.Name));
        Assert.Equal(text, editor.TagInput); // the box text stays as typed
    }

    // ---- Fix 1: suggestions are safe with malformed UTF-16 ----------------------------------------------------------------

    [Fact]
    public async Task Suggestions_WithALoneSurrogateInTheBox_AreEmpty_AndAddOrSaveReportTheValidationError_NothingWritten()
    {
        await _workspace.CreateProjectAsync(null, new ProjectDraft("Beta", ProjectStatus.Active, null, null, null, ["Infrastructure"]));
        var editor = await Edit();
        await editor.LoadSuggestionsAsync();
        editor.TagInput = "inf";
        Assert.Equal(["Infrastructure"], editor.FilteredSuggestions.Select(s => s.Name)); // the normalization path is live

        foreach (var text in new[] { "inf" + (char)0xD800, ((char)0xDC00).ToString(), "a" + (char)0xD800 + "b" })
        {
            editor.TagInput = text;
            Assert.Empty(editor.FilteredSuggestions); // no exception from NFKC on a lone surrogate
            Assert.False(editor.AddPendingTag());
            Assert.Equal("A tag contains invalid characters.", editor.TagError);
        }

        Closed(editor, out var closed);
        await editor.SaveCommand.ExecuteAsync();
        Assert.False(closed());
        Assert.Equal("A tag contains invalid characters.", editor.TagError);
        Assert.Equal(0, _store.Calls("update"));
        Assert.Equal(["Infra"], (await Stored()).Tags.Select(t => t.Name));
    }

    // ---- Fix 2: the edited Project is not a source of its own suggestions --------------------------------------------------

    [Fact]
    public async Task Suggestions_LeaveOutTheEditedProject_ATagOnlyItHasIsNotSuggested_ANewProjectSeesIt()
    {
        await _workspace.CreateProjectAsync(null, new ProjectDraft("Beta", ProjectStatus.Active, null, null, null, ["Ops"]));
        var edit = await Edit();
        await edit.LoadSuggestionsAsync();
        edit.Tags[0].Command.Execute(null); // remove Infra from the chips, so only the source rule can hide it
        edit.TagInput = "inf";
        Assert.Empty(edit.FilteredSuggestions); // "Infra" exists only on the edited Project
        Assert.Equal(1, _store.Calls("listTagSuggestions"));

        var create = ProjectEditorViewModel.ForCreate(_workspace, null, null);
        await create.LoadSuggestionsAsync();
        create.TagInput = "inf";
        Assert.Equal(["Infra"], create.FilteredSuggestions.Select(s => s.Name)); // every eligible Project for a new one
    }

    // ---- Fix 4: the 20-tag limit in the UI ---------------------------------------------------------------------------------

    [Fact]
    public async Task Limit_19To20To19_DisablesAndReenablesAdding_WithTheLimitText_AndNotifies()
    {
        var editor = await Edit();
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var commandChanges = 0;
        editor.AddTagCommand.CanExecuteChanged += (_, _) => commandChanges++;
        for (var i = 2; i <= 19; i++)
        {
            editor.TagInput = $"T{i}";
            editor.AddPendingTag();
        }

        Assert.Equal(19, editor.Tags.Count);
        Assert.True(editor.CanAddTag);
        Assert.Null(editor.TagLimitText);

        changed.Clear();
        editor.TagInput = "T20";
        Assert.True(editor.AddPendingTag());
        Assert.Equal(20, editor.Tags.Count);
        Assert.False(editor.CanAddTag);
        Assert.False(editor.AddTagCommand.CanExecute(null));
        Assert.Equal("At most 20 tags.", editor.TagLimitText);
        Assert.Contains(nameof(ProjectEditorViewModel.CanAddTag), changed);
        Assert.Contains(nameof(ProjectEditorViewModel.TagLimitText), changed);

        changed.Clear();
        editor.Tags[5].Command.Execute(null); // a chip can still be removed at 20
        Assert.Equal(19, editor.Tags.Count);
        Assert.True(editor.CanAddTag);
        Assert.True(editor.AddTagCommand.CanExecute(null));
        Assert.Null(editor.TagLimitText);
        Assert.Contains(nameof(ProjectEditorViewModel.CanAddTag), changed);
        Assert.True(commandChanges >= 2);
    }

    [Fact]
    public async Task Limit_AValidSetOf20_CanBeSaved()
    {
        var editor = await Edit();
        Closed(editor, out var closed);
        for (var i = 2; i <= 20; i++)
        {
            editor.TagInput = $"T{i:D2}";
            editor.AddPendingTag();
        }

        Assert.False(editor.CanAddTag);
        await editor.SaveCommand.ExecuteAsync();

        Assert.True(closed());
        Assert.Equal(20, (await Stored()).Tags.Count);
    }

    [Fact]
    public async Task Limit_SuggestionsAreHiddenAt20()
    {
        await _workspace.CreateProjectAsync(null, new ProjectDraft("Beta", ProjectStatus.Active, null, null, null, ["Ops"]));
        var editor = await Edit();
        await editor.LoadSuggestionsAsync();
        for (var i = 2; i <= 20; i++)
        {
            editor.TagInput = $"T{i:D2}";
            editor.AddPendingTag();
        }

        editor.TagInput = "o";
        Assert.Empty(editor.FilteredSuggestions);
    }

    [Fact]
    public async Task TwoEditors_SavedOneAfterTheOther_TheLastSaveDecidesTheWholeSet()
    {
        var first = await Edit();
        var second = await Edit();
        first.TagInput = "Client";
        await first.SaveCommand.ExecuteAsync();
        second.TagInput = "Web";
        second.Tags[0].Command.Execute(null); // removes Infra in the second editor
        await second.SaveCommand.ExecuteAsync();

        Assert.Equal(["Web"], (await Stored()).Tags.Select(t => t.Name));
    }

    [Fact]
    public async Task Suggestions_ComeFromOtherProjects_FilterByTheTypedKey_AndExcludeExistingChips()
    {
        await _workspace.CreateProjectAsync(null, new ProjectDraft("Beta", ProjectStatus.Active, null, null, null, ["Infrastructure", "Ops", "infra"]));
        var editor = await Edit();
        await editor.LoadSuggestionsAsync();

        editor.TagInput = "inf";
        Assert.Equal(["Infrastructure"], editor.FilteredSuggestions.Select(s => s.Name)); // INFRA is already a chip
        editor.FilteredSuggestions[0].Command.Execute(null);
        Assert.Equal(["Infra", "Infrastructure"], editor.Tags.Select(t => t.Name));
        editor.TagInput = "o";
        Assert.Equal(["Ops"], editor.FilteredSuggestions.Select(s => s.Name));
    }

    [Fact]
    public async Task NewProject_SavesItsChips()
    {
        var editor = ProjectEditorViewModel.ForCreate(_workspace, null, null);
        editor.Name = "Gamma";
        editor.TagInput = "Ops";
        editor.AddPendingTag();
        editor.TagInput = "Client A";

        await editor.SaveCommand.ExecuteAsync();

        var created = (await _workspace.GetProjectAsync(editor.CreatedNode!.Id))!;
        Assert.Equal(["Client A", "Ops"], created.Tags.Select(t => t.Name));
    }
}
