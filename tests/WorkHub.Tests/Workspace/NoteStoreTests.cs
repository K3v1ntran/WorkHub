using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>Note use cases through the real SQLite store: round trips, validation, revisions, moves and recovery (Phase 5B §5–§6).</summary>
public sealed class NoteStoreTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly SqliteConnectionFactory _connections;
    private readonly ProjectContentService _content;
    private readonly long _alpha;
    private readonly long _beta;

    public NoteStoreTests()
    {
        _root.CreateInitializer().Initialize();
        _connections = new SqliteConnectionFactory(_root.Paths.DatabaseFile);
        _content = NewService();
        var workspace = new WorkspaceService(new SqliteWorkspaceStore(_connections));
        _alpha = workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active)).Result.Value!.Id;
        _beta = workspace.CreateProjectAsync(null, new ProjectDraft("Beta", ProjectStatus.Active)).Result.Value!.Id;
    }

    public void Dispose() => _root.Dispose();

    /// <summary>An independent service on the same database file: another window or process.</summary>
    private ProjectContentService NewService() =>
        new(new SqliteProjectContentStore(_connections), new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));

    private async Task<ProjectNote> NewNote(string title = "Kickoff", string content = "agenda", long? folder = null, long? project = null) =>
        (await _content.CreateNoteAsync(project ?? _alpha, folder, new ProjectNoteDraft(title, content))).Note!;

    private async Task<ProjectFolder> NewFolder(string name, long? project = null) =>
        (await _content.CreateFolderAsync(project ?? _alpha, null, name)).Value!;

    private string? Row(string sql, long id)
    {
        using var connection = _root.Open();
        return Db.ScalarString(connection, sql, ("$id", id));
    }

    public static TheoryData<string> Contents => new()
    {
        string.Empty,
        "  leading and trailing spaces  ",
        "tab\tseparated\tvalues",
        "windows\r\nline\r\nbreaks\r\n",
        "unix\nline\nbreaks\n\n",
        "日本語のメモ　全角スペース",
        "emoji 😀 surrogate pairs 👍🏽",
        new string('x', 100000),
    };

    [Theory]
    [MemberData(nameof(Contents))]
    public async Task Content_RoundTripsExactly(string content)
    {
        var created = await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("  Title  ", content));

        Assert.True(created.Succeeded, created.Error);
        Assert.Equal("Title", created.Note!.Title);
        Assert.Equal(1, created.Note.Revision);
        Assert.Equal(content, created.Note.Content);
        Assert.Equal(content, (await _content.GetNoteAsync(created.Note.Id))!.Content);
    }

    [Theory]
    [InlineData("", "x", ProjectNoteValidator.Fields.Title)]
    [InlineData("   ", "x", ProjectNoteValidator.Fields.Title)]
    [InlineData("tab\there", "x", ProjectNoteValidator.Fields.Title)]
    [InlineData("ok", "nul\0char", ProjectNoteValidator.Fields.Content)]
    public async Task Validation_RejectsWithAFieldError_AndWritesNothing(string title, string content, string field)
    {
        var result = await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft(title, content));

        Assert.Equal(NoteWriteStatus.Invalid, result.Status);
        Assert.Equal(field, result.Field);
        using var connection = _root.Open();
        Assert.Equal(0, Db.Count(connection, "ProjectNotes"));
    }

    [Fact]
    public async Task Validation_RejectsIllFormedUtf16()
    {
        // Built at run time: a lone surrogate in an attribute constant would be replaced with U+FFFD at compile time.
        var lone = "lone " + new string('\ud800', 1) + " surrogate";
        var reversed = "pair " + new string(['\udc00', '\ud800']);

        Assert.Equal(ProjectNoteValidator.Fields.Content, (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("ok", lone))).Field);
        Assert.Equal(ProjectNoteValidator.Fields.Content, (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("ok", reversed))).Field);
        Assert.Equal(ProjectNoteValidator.Fields.Title, (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft(lone, ""))).Field);
    }

    [Fact]
    public async Task Validation_LimitsContentTo100000Units_AndTitleTo200()
    {
        Assert.Equal(ProjectNoteValidator.Fields.Content, (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft("t", new string('x', 100001)))).Field);
        Assert.Equal(ProjectNoteValidator.Fields.Title, (await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft(new string('t', 201), ""))).Field);
        Assert.True((await _content.CreateNoteAsync(_alpha, null, new ProjectNoteDraft(new string('t', 200), ""))).Succeeded);
    }

    [Fact]
    public async Task Create_InAMissingOrForeignContainer_IsLocationMissing()
    {
        var betaFolder = await NewFolder("Other", _beta);

        Assert.Equal(NoteWriteStatus.LocationMissing, (await _content.CreateNoteAsync(9999, null, new ProjectNoteDraft("t", ""))).Status);
        Assert.Equal(NoteWriteStatus.LocationMissing, (await _content.CreateNoteAsync(_alpha, 9999, new ProjectNoteDraft("t", ""))).Status);
        Assert.Equal(NoteWriteStatus.LocationMissing, (await _content.CreateNoteAsync(_alpha, betaFolder.Id, new ProjectNoteDraft("t", ""))).Status);
    }

    [Fact]
    public async Task Listing_HasNoteSummariesAfterFoldersAndLinks_SortedByTitle_WithoutContent()
    {
        await NewFolder("Specs");
        await _content.CreateLinkAsync(_alpha, null, new ProjectLinkDraft("Out", ProjectLinkType.Folder, @"C:\Work\Out"));
        await NewNote("zeta");
        await NewNote("Alpha note");
        await NewNote("Inside", folder: (await NewFolder("Docs")).Id);

        var listing = (await _content.ListAsync(_alpha, null)).Listing!;

        Assert.Equal(["Alpha note", "zeta"], listing.Notes.Select(n => n.Title));
        Assert.Equal(["Docs", "Specs"], listing.Folders.Select(f => f.Name));
        Assert.Single(listing.Links);
        Assert.All(listing.Notes, n => Assert.Equal(1, n.Revision));
    }

    [Fact]
    public async Task Save_WritesTitleContentRevisionAndUpdatedAt_Only()
    {
        var folder = await NewFolder("Specs");
        var note = await NewNote(folder: folder.Id);
        var created = Row("SELECT CreatedAt FROM ProjectNotes WHERE Id = $id;", note.Id);

        var saved = await _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Renamed ", "new\r\ntext"));

        Assert.True(saved.Succeeded);
        Assert.Equal(2, saved.Note!.Revision);
        Assert.Equal("Renamed", saved.Note.Title);
        Assert.Equal("new\r\ntext", saved.Note.Content);
        Assert.Equal(folder.Id, saved.Note.FolderId);
        Assert.Equal(created, Row("SELECT CreatedAt FROM ProjectNotes WHERE Id = $id;", note.Id));
        Assert.Equal(_alpha.ToString(System.Globalization.CultureInfo.InvariantCulture), Row("SELECT CAST(ProjectNodeId AS TEXT) FROM ProjectNotes WHERE Id = $id;", note.Id));
    }

    [Fact]
    public async Task TwoServices_ConcurrentEdits_TheSecondSaveIsAConflict_AndWritesNothing()
    {
        var note = await NewNote(content: "base");
        var windowB = NewService();

        var a = await _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Kickoff", "A's text"));
        var b = await windowB.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Kickoff", "B's text"));

        Assert.True(a.Succeeded);
        Assert.Equal(NoteWriteStatus.Conflict, b.Status);
        Assert.Equal(2, b.CurrentRevision);
        Assert.True(b.OffersRecovery);
        Assert.Equal("A's text|2", Row("SELECT Content || '|' || Revision FROM ProjectNotes WHERE Id = $id;", note.Id));
    }

    [Fact]
    public async Task Save_OfARemovedNote_IsNotFound()
    {
        var note = await NewNote();
        using (var connection = _root.Open())
            Db.Exec(connection, "DELETE FROM ProjectNotes WHERE Id = $id;", ("$id", note.Id)); // external removal (no API deletes)

        var result = await _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("t", "x"));

        Assert.Equal(NoteWriteStatus.NotFound, result.Status);
        Assert.True(result.OffersRecovery);
    }

    [Fact]
    public async Task ConcurrentMoveThenSave_KeepsTheMovedFolder_AndMoveKeepsTheRevision()
    {
        var p = await NewFolder("P");
        var q = await NewFolder("Q");
        var note = await NewNote(folder: p.Id);

        Assert.Equal(ContentMoveOutcome.Moved, (await NewService().MoveNoteAsync(note.Id, p.Id, q.Id)).Outcome);
        Assert.Equal("1", Row("SELECT CAST(Revision AS TEXT) FROM ProjectNotes WHERE Id = $id;", note.Id));

        var saved = await _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("Kickoff", "edited after the move"));

        Assert.True(saved.Succeeded);
        Assert.Equal(q.Id, saved.Note!.FolderId);
        Assert.Equal(2, saved.Note.Revision);
        Assert.Equal(q.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), Row("SELECT CAST(FolderId AS TEXT) FROM ProjectNotes WHERE Id = $id;", note.Id));
    }

    [Fact]
    public async Task StaleMove_IsMovedMeanwhile_IncludingPrecedenceAndRootNull_AndAlreadyThereKeepsTheTimestamp()
    {
        var p = await NewFolder("P");
        var q = await NewFolder("Q");
        var r = await NewFolder("R");
        var note = await NewNote(folder: p.Id);
        await NewService().MoveNoteAsync(note.Id, p.Id, q.Id);
        var afterB = Row("SELECT FolderId || '|' || UpdatedAt FROM ProjectNotes WHERE Id = $id;", note.Id);

        Assert.Equal(ContentMoveOutcome.MovedMeanwhile, (await _content.MoveNoteAsync(note.Id, p.Id, r.Id)).Outcome);
        Assert.Equal(ContentMoveOutcome.MovedMeanwhile, (await _content.MoveNoteAsync(note.Id, p.Id, q.Id)).Outcome); // precedence
        Assert.Equal(afterB, Row("SELECT FolderId || '|' || UpdatedAt FROM ProjectNotes WHERE Id = $id;", note.Id));

        var rootNote = await NewNote("At root");
        await NewService().MoveNoteAsync(rootNote.Id, null, p.Id);
        Assert.Equal(ContentMoveOutcome.MovedMeanwhile, (await _content.MoveNoteAsync(rootNote.Id, null, r.Id)).Outcome);
        await NewService().MoveNoteAsync(rootNote.Id, p.Id, null);
        Assert.Equal(ContentMoveOutcome.MovedMeanwhile, (await _content.MoveNoteAsync(rootNote.Id, p.Id, r.Id)).Outcome);

        var before = Row("SELECT UpdatedAt FROM ProjectNotes WHERE Id = $id;", rootNote.Id);
        Assert.Equal(ContentMoveOutcome.AlreadyThere, (await _content.MoveNoteAsync(rootNote.Id, null, null)).Outcome);
        Assert.Equal(before, Row("SELECT UpdatedAt FROM ProjectNotes WHERE Id = $id;", rootNote.Id));
        var betaFolder = await NewFolder("Other", _beta);
        Assert.Equal(ContentMoveOutcome.WrongProject, (await _content.MoveNoteAsync(rootNote.Id, null, betaFolder.Id)).Outcome);
    }

    [Fact]
    public async Task SaveAsNew_OriginalExists_GoesNextToItsCurrentLocation_AndLeavesItUntouched()
    {
        var p = await NewFolder("P");
        var q = await NewFolder("Q");
        var original = await NewNote(content: "theirs", folder: p.Id);
        await NewService().MoveNoteAsync(original.Id, p.Id, q.Id);
        var before = Row("SELECT Title || '|' || Content || '|' || Revision || '|' || UpdatedAt FROM ProjectNotes WHERE Id = $id;", original.Id);

        var result = await _content.SaveNoteAsNewAsync(original.Id, _alpha, p.Id, new ProjectNoteDraft("Kickoff", "mine"));

        Assert.True(result.Succeeded);
        Assert.NotEqual(original.Id, result.Note!.Id);
        Assert.Equal(q.Id, result.Note.FolderId); // the original's CURRENT folder, not the editor's last-known one
        Assert.Equal("Kickoff", result.Note.Title);
        Assert.Equal(1, result.Note.Revision);
        Assert.Equal(before, Row("SELECT Title || '|' || Content || '|' || Revision || '|' || UpdatedAt FROM ProjectNotes WHERE Id = $id;", original.Id));
    }

    [Fact]
    public async Task SaveAsNew_OriginalGone_UsesTheLastKnownContainer_IncludingTheRoot()
    {
        var p = await NewFolder("P");
        var inFolder = await NewNote(folder: p.Id);
        var atRoot = await NewNote("Root note");
        using (var connection = _root.Open())
            Db.Exec(connection, "DELETE FROM ProjectNotes;");

        var a = await _content.SaveNoteAsNewAsync(inFolder.Id, _alpha, p.Id, new ProjectNoteDraft("Recovered", "x"));
        var b = await _content.SaveNoteAsNewAsync(atRoot.Id, _alpha, null, new ProjectNoteDraft("Recovered root", "y"));

        Assert.Equal(p.Id, a.Note!.FolderId);
        Assert.Null(b.Note!.FolderId); // the root was the valid last-known container
    }

    [Fact]
    public async Task SaveAsNew_NoValidLocation_IsRefused_WithoutARootFallback_AndWritesNothing()
    {
        var note = await NewNote();
        using (var connection = _root.Open())
            Db.Exec(connection, "DELETE FROM ProjectNotes;");

        var missingFolder = await _content.SaveNoteAsNewAsync(note.Id, _alpha, 9999, new ProjectNoteDraft("t", "x"));
        var betaFolder = await NewFolder("Other", _beta);
        var foreignFolder = await _content.SaveNoteAsNewAsync(note.Id, _alpha, betaFolder.Id, new ProjectNoteDraft("t", "x"));
        var missingProject = await _content.SaveNoteAsNewAsync(note.Id, 9999, null, new ProjectNoteDraft("t", "x"));

        Assert.All([missingFolder, foreignFolder, missingProject], r => Assert.Equal(NoteWriteStatus.LocationMissing, r.Status));
        Assert.Contains("Copy your text", missingFolder.Error);
        using var after = _root.Open();
        Assert.Equal(0, Db.Count(after, "ProjectNotes"));
    }

    [Fact]
    public async Task AFailedWriteInsideTheTransaction_LeavesNothingBehind()
    {
        var note = await NewNote(content: "v1");
        using (var connection = _root.Open())
        {
            Db.Exec(connection, "CREATE TRIGGER Test_FailInsert BEFORE INSERT ON ProjectNotes BEGIN SELECT RAISE(ABORT, 'injected'); END;");
            Db.Exec(connection, "CREATE TRIGGER Test_FailSave BEFORE UPDATE OF Content ON ProjectNotes BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        }

        await Assert.ThrowsAnyAsync<Exception>(() => _content.SaveNoteAsNewAsync(note.Id, _alpha, null, new ProjectNoteDraft("t", "x")));
        await Assert.ThrowsAnyAsync<Exception>(() => _content.SaveNoteAsync(note.Id, 1, new ProjectNoteDraft("t", "v2")));

        Assert.Equal("v1|1", Row("SELECT Content || '|' || Revision FROM ProjectNotes WHERE Id = $id;", note.Id));
        using var after = _root.Open();
        Assert.Equal(1, Db.Count(after, "ProjectNotes"));
    }

    [Fact]
    public async Task GetNotes_ReturnsTheLatestStateOfPresentIds_AndOmitsAbsentOnes_WithoutWriting()
    {
        var a = await NewNote("A");
        var b = await NewNote("B");
        await NewService().SaveNoteAsync(a.Id, 1, new ProjectNoteDraft("A2", "newer"));
        using (var connection = _root.Open())
            Db.Exec(connection, "DELETE FROM ProjectNotes WHERE Id = $id;", ("$id", b.Id));
        var before = Row("SELECT group_concat(Id || ':' || Revision || ':' || UpdatedAt) FROM ProjectNotes;", 0);

        var fresh = await _content.GetNotesAsync([a.Id, b.Id, a.Id]);

        var only = Assert.Single(fresh);
        Assert.Equal(("A2", "newer", 2L), (only.Title, only.Content, only.Revision));
        Assert.Equal(before, Row("SELECT group_concat(Id || ':' || Revision || ':' || UpdatedAt) FROM ProjectNotes;", 0));
        Assert.Empty(await _content.GetNotesAsync([]));
    }

    [Fact]
    public async Task ContainerPath_IsTheProjectNameAndFolderChain_OrNullForAMissingOrForeignContainer()
    {
        var specs = await NewFolder("Specs");
        var drafts = (await _content.CreateFolderAsync(_alpha, specs.Id, "Drafts")).Value!;
        var betaFolder = await NewFolder("Elsewhere", _beta);

        Assert.Equal("Alpha", (await _content.GetContainerPathAsync(_alpha, null))!.Display);
        Assert.Equal("Alpha › Specs › Drafts", (await _content.GetContainerPathAsync(_alpha, drafts.Id))!.Display);
        Assert.Null(await _content.GetContainerPathAsync(_alpha, betaFolder.Id)); // folder of another Project
        Assert.Null(await _content.GetContainerPathAsync(_alpha, 999_999));
        Assert.Null(await _content.GetContainerPathAsync(999_999, null));
    }
}
