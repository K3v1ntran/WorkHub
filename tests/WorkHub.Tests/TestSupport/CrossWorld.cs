using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Phase 15 synthetic world (the Revision 2 probe fixture, built through the real services in an isolated root):
/// <code>
/// WS1 'Clients' > A 'Alpha' (source)      B 'Beta'      WS2 'Shelf' > C 'Gamma'      D 'Delta' (archived)
/// WS3 'Archived shelf' (archived) > E 'Epsilon'
/// In A: root link LA; root note NA (Revision 3; CR/LF, quotes, CJK); root note NA2; K 'Keep' { KN }
///       S 'Specs' { link SL1; note SN1; S1 'Drafts' { note S1N; S1a 'Deep' { link S1aL }; note S1T (trashed first) };
///                   link SL2 (trashed); S2 'Old' { note S2N } (trashed) }
///       active subtree of S = 3 folders, 2 links, 2 notes; skipped Trash below S = 4 rows in 3 entries
/// In B: Inbox; a folder named with five full-width letters ＳＰＥＣＳ (NameKey SPECS); root link 'Build server'
/// In C: Incoming.  In D: D-in.  In E: E-in.
/// </code>
/// Link targets are synthetic strings; nothing is ever opened.
/// </summary>
internal sealed class CrossWorld : IDisposable
{
    public CrossWorld(int maxDepth = SqliteProjectContentStore.MaxDepth)
    {
        Root.CreateInitializer().Initialize();
        Connections = new SqliteConnectionFactory(Root.Paths.DatabaseFile);
        Workspace = new WorkspaceService(new SqliteWorkspaceStore(Connections));
        Store = new SqliteProjectContentStore(Connections, null, maxDepth);
        Content = Service(Store);
        Other = Service(new SqliteProjectContentStore(Connections, null, maxDepth));
    }

    public TempWorkHubRoot Root { get; } = new();

    public SqliteConnectionFactory Connections { get; }

    public WorkspaceService Workspace { get; }

    /// <summary>The store under test (its <c>OnWriteStep</c> hook injects failures).</summary>
    public SqliteProjectContentStore Store { get; }

    public ProjectContentService Content { get; }

    /// <summary>An independent service: another window on the same data root.</summary>
    public ProjectContentService Other { get; }

    public static ProjectContentService Service(IProjectContentStore store) =>
        new(store, new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true));

    public long WS1, A, B, WS2, C, D, WS3, E;
    public long LA, NA, NA2, K, KN, S, SL1, SN1, S1, S1N, S1a, S1aL, S1T, SL2, S2, S2N, E_S1T, E_SL2, E_S2;
    public long Inbox, BWide, BLink, Incoming, Din, Ein;

    public const string WideSpecs = "ＳＰＥＣＳ";

    public async Task<CrossWorld> BuildAsync()
    {
        WS1 = (await Workspace.CreateFolderAsync(null, "Clients")).Value!.Id;
        A = await Project("Alpha", WS1);
        B = await Project("Beta", null);
        WS2 = (await Workspace.CreateFolderAsync(null, "Shelf")).Value!.Id;
        C = await Project("Gamma", WS2);
        D = await Project("Delta", null);
        WS3 = (await Workspace.CreateFolderAsync(null, "Archived shelf")).Value!.Id;
        E = await Project("Epsilon", WS3);

        LA = await Link(A, null, "Build server", ProjectLinkType.Web, "https://build.example.test/nightly");
        NA = await Note(A, null, "Alpha note", "first");
        await Edit(NA, 1, "second");
        await Edit(NA, 2, "alpha note body\r\nline 2 with 'quotes' and 日本");
        NA2 = await Note(A, null, "Unrelated note");
        S = await Folder(A, null, "Specs");
        SL1 = await Link(A, S, "Spec index", ProjectLinkType.File, @"C:\WorkHubTest11\synthetic\spec-index.txt");
        SN1 = await Note(A, S, "Spec note", "spec body");
        S1 = await Folder(A, S, "Drafts");
        S1N = await Note(A, S1, "Draft note", "draft body");
        S1a = await Folder(A, S1, "Deep");
        S1aL = await Link(A, S1a, "Deep link", ProjectLinkType.Folder, @"C:\WorkHubTest11\synthetic\deep");
        S1T = await Note(A, S1, "Trashed draft");
        SL2 = await Link(A, S, "Old link");
        S2 = await Folder(A, S, "Old");
        S2N = await Note(A, S2, "Old note");
        E_S1T = await Trash(ContentKind.Note, S1T);
        E_SL2 = await Trash(ContentKind.Link, SL2);
        E_S2 = await Trash(ContentKind.Folder, S2);
        K = await Folder(A, null, "Keep");
        KN = await Note(A, K, "Keep note");

        Inbox = await Folder(B, null, "Inbox");
        BWide = await Folder(B, null, WideSpecs);
        BLink = await Link(B, null, "Build server");
        Incoming = await Folder(C, null, "Incoming");
        Din = await Folder(D, null, "D-in");
        Ein = await Folder(E, null, "E-in");

        Assert.Equal(ProjectArchiveOutcome.Archived, (await Workspace.ArchiveProjectAsync(D)).Outcome);
        var token = (await Workspace.PrepareFolderLifecycleAsync(WS3, FolderLifecycleKind.Archive)).Confirmation!.Token;
        Assert.Equal(FolderLifecycleOutcome.Committed, (await Workspace.MarkFolderAsync(WS3, FolderLifecycleKind.Archive, token)).Outcome);
        return this;
    }

    public async Task<long> Project(string name, long? parent) => (await Workspace.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;

    public async Task<long> Folder(long project, long? parent, string name) => (await Other.CreateFolderAsync(project, parent, name)).Value!.Id;

    public async Task<long> Link(long project, long? folder, string name, ProjectLinkType type = ProjectLinkType.Web, string? target = null) =>
        (await Other.CreateLinkAsync(project, folder, new ProjectLinkDraft(name, type, target ?? $"https://example.test/{Uri.EscapeDataString(name)}"))).Value!.Id;

    public async Task<long> Note(long project, long? folder, string title, string content = "synthetic text") =>
        (await Other.CreateNoteAsync(project, folder, new ProjectNoteDraft(title, content))).Note!.Id;

    public async Task Edit(long note, long revision, string content)
    {
        var current = (await Other.GetNoteAsync(note))!;
        Assert.Equal(NoteWriteStatus.Saved, (await Other.SaveNoteAsync(note, revision, new ProjectNoteDraft(current.Title, content))).Status);
    }

    public async Task<long> Trash(ContentKind kind, long id)
    {
        var result = await Other.TrashAsync(new ContentRef(kind, id));
        Assert.Equal(TrashOutcome.Trashed, result.Outcome);
        return result.Entry!.Id;
    }

    public static CrossProjectRequest Req(CrossProjectMode mode, ContentKind kind, long id, long targetProject, long? targetFolder, string? name = null) =>
        new(mode, new ContentRef(kind, id), targetProject, targetFolder, name);

    /// <summary>Preview, assert Ready, commit with its token.</summary>
    public async Task<(CrossProjectPreview Preview, CrossProjectResult Result)> CommitAsync(CrossProjectRequest request, ProjectContentService? content = null)
    {
        var service = content ?? Content;
        var preview = await service.PrepareCrossProjectAsync(request);
        Assert.True(preview.IsReady, $"preview {preview.Status}");
        return (preview, await service.CrossProjectAsync(request, preview.Token!));
    }

    /// <summary>Every row of every user table plus sqlite_sequence, keyed by table, ordered by all columns.</summary>
    public Dictionary<string, List<string>> Dump()
    {
        using var connection = Root.Open();
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_stat%' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        var dump = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            int columns;
            using (var shape = connection.CreateCommand())
            {
                shape.CommandText = $"SELECT * FROM \"{table}\" LIMIT 0;";
                using var empty = shape.ExecuteReader();
                columns = empty.FieldCount;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY {string.Join(", ", Enumerable.Range(1, columns))};";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            dump[table] = rows;
        }

        return dump;
    }

    public long Scalar(string sql)
    {
        using var connection = Root.Open();
        return Db.ScalarLong(connection, sql);
    }

    public void Exec(string sql)
    {
        using var connection = Root.Open();
        Db.Exec(connection, sql);
    }

    /// <summary>Names, nesting, link fields, note contents and CreatedAt of the ACTIVE tree under a folder, by path.</summary>
    public List<string> Tree(long folder)
    {
        using var connection = Root.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE s (Id, P) AS (
                SELECT Id, '' FROM ProjectFolders WHERE Id = $root
                UNION ALL
                SELECT c.Id, s.P || '/' || c.Name FROM ProjectFolders AS c JOIN s ON c.ParentFolderId = s.Id WHERE c.TrashEntryId IS NULL
            )
            SELECT 'F:' || P || '|' || (SELECT CreatedAt FROM ProjectFolders WHERE Id = s.Id) FROM s
            UNION ALL SELECT 'L:' || s.P || '/' || l.Name || '|' || l.LinkType || '|' || l.Target || '|' || l.CreatedAt FROM ProjectLinks AS l JOIN s ON l.FolderId = s.Id WHERE l.TrashEntryId IS NULL
            UNION ALL SELECT 'N:' || s.P || '/' || n.Title || '|' || n.Content || '|' || n.CreatedAt FROM ProjectNotes AS n JOIN s ON n.FolderId = s.Id WHERE n.TrashEntryId IS NULL
            ORDER BY 1;
            """;
        command.Parameters.AddWithValue("$root", folder);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows;
    }

    public void AssertIntegrity()
    {
        using var connection = Root.Open();
        Assert.Equal(0L, Db.ScalarLong(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA integrity_check;"));
    }

    public void Dispose()
    {
        BackupKit.ClearPoolsUnder(Root.Paths.Root);
        Root.Dispose();
    }
}
