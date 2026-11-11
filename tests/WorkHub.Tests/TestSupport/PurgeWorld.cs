using Microsoft.Data.Sqlite;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Phase 14 synthetic world (the Revision 2 probe fixture, built through the real services in an isolated root):
/// <code>
/// WF (Workspace Folder) > P (Project under test)    WG > R (Project)    Q (Project, top level)
/// In P:  root link L1 → E_L1; root note N1 → E_N1
///        FA { FA1 { FA1a { deep note }, FA1 link }, FA note }                          → E_FA (6 rows)
///        FB { NB (trashed first → E_NB), FB1 { FB1x { link } (trashed first → E_FB1x) }, FB link }  → E_FB
///        FC { FC1 { NC (→ E_NC) } (→ E_FC1) }                                           → E_FC (3 levels)
///        active: FK { FK link, note X }, root link LK, root note NK
/// In Q:  note Q1 → E_Q1; folder QF { note } → E_QF; active folder QK { link }
/// In R:  note R1 → E_R1; active note RK
/// </code>
/// Link targets are synthetic strings; nothing is ever opened.
/// </summary>
internal sealed class PurgeWorld : IDisposable
{
    public PurgeWorld()
    {
        Root.CreateInitializer().Initialize();
        Connections = new SqliteConnectionFactory(Root.Paths.DatabaseFile);
        Workspace = new WorkspaceService(new SqliteWorkspaceStore(Connections));
        Purge = NewPurge();
        Content = NewService(Purge);
        Other = NewService(NewPurge());
    }

    public TempWorkHubRoot Root { get; } = new();

    public SqliteConnectionFactory Connections { get; }

    public WorkspaceService Workspace { get; }

    /// <summary>The purge under test (hooks, files and bound can be replaced through <see cref="NewPurge"/>).</summary>
    public ProjectTrashPurge Purge { get; }

    public ProjectContentService Content { get; private set; }

    /// <summary>An independent service: another window on the same data root.</summary>
    public ProjectContentService Other { get; }

    public RecordingProcessStarter Starter { get; } = new();

    public long WF, P, WG, R, Q;
    public long L1, N1, FA, FA1, FA1a, FA1aNote, FA1Link, FANote, FB, NB, FB1, FB1x, FB1xLink, FBLink, FC, FC1, NC, FK, FKLink, X, LK, NK;
    public long Q1, QF, QFNote, QK, QKLink, R1, RK;
    public long E_L1, E_N1, E_FA, E_NB, E_FB1x, E_FB, E_NC, E_FC1, E_FC, E_Q1, E_QF, E_R1;

    /// <param name="lockWaitSeconds">The PreDelete copy's source lock wait (tests use 1 s instead of the production 10 s).</param>
    public ProjectTrashPurge NewPurge(IBackupFileOps? files = null, int maxDepth = SqliteProjectContentStore.MaxDepth, SqliteConnectionFactory? connections = null,
        IAppLog? log = null, int lockWaitSeconds = SqliteSnapshotter.DefaultLockWaitSeconds) =>
        new(Root.Paths, connections ?? Connections, log ?? NullAppLog.Instance, TimeProvider.System, SchemaReference.Frozen, files ?? new BackupFileOps(), maxDepth,
            lockWaitSeconds: lockWaitSeconds);

    public ProjectContentService NewService(IProjectTrashPurge? purge) =>
        new(new SqliteProjectContentStore(Connections), new ProcessProjectLinkLauncher(Starter, _ => true, _ => true), purge);

    /// <summary>Replaces <see cref="Content"/>'s purge (for a purge with hooks or a lower bound).</summary>
    public ProjectContentService Use(ProjectTrashPurge purge) => Content = NewService(purge);

    public async Task<PurgeWorld> BuildAsync()
    {
        WF = (await Workspace.CreateFolderAsync(null, "WF")).Value!.Id;
        P = (await Workspace.CreateProjectAsync(WF, new ProjectDraft("Project P", ProjectStatus.Active))).Value!.Id;
        WG = (await Workspace.CreateFolderAsync(null, "WG")).Value!.Id;
        R = (await Workspace.CreateProjectAsync(WG, new ProjectDraft("Project R", ProjectStatus.Active))).Value!.Id;
        Q = (await Workspace.CreateProjectAsync(null, new ProjectDraft("Project Q", ProjectStatus.Active))).Value!.Id;

        L1 = await Link(P, null, "L1"); N1 = await Note(P, null, "N1");
        E_L1 = await Trash(ContentKind.Link, L1); E_N1 = await Trash(ContentKind.Note, N1);

        FA = await Folder(P, null, "FA"); FA1 = await Folder(P, FA, "FA1"); FA1a = await Folder(P, FA1, "FA1a");
        FA1aNote = await Note(P, FA1a, "deep note"); FA1Link = await Link(P, FA1, "FA1 link"); FANote = await Note(P, FA, "FA note");
        E_FA = await Trash(ContentKind.Folder, FA);

        FB = await Folder(P, null, "FB"); NB = await Note(P, FB, "NB"); FB1 = await Folder(P, FB, "FB1");
        FB1x = await Folder(P, FB1, "FB1x"); FB1xLink = await Link(P, FB1x, "FB1x link"); FBLink = await Link(P, FB, "FB link");
        E_NB = await Trash(ContentKind.Note, NB); E_FB1x = await Trash(ContentKind.Folder, FB1x); E_FB = await Trash(ContentKind.Folder, FB);

        FC = await Folder(P, null, "FC"); FC1 = await Folder(P, FC, "FC1"); NC = await Note(P, FC1, "NC");
        E_NC = await Trash(ContentKind.Note, NC); E_FC1 = await Trash(ContentKind.Folder, FC1); E_FC = await Trash(ContentKind.Folder, FC);

        FK = await Folder(P, null, "FK"); FKLink = await Link(P, FK, "FK link"); X = await Note(P, FK, "X");
        LK = await Link(P, null, "LK"); NK = await Note(P, null, "NK");

        Q1 = await Note(Q, null, "Q1"); E_Q1 = await Trash(ContentKind.Note, Q1);
        QF = await Folder(Q, null, "QF"); QFNote = await Note(Q, QF, "QF note"); E_QF = await Trash(ContentKind.Folder, QF);
        QK = await Folder(Q, null, "QK"); QKLink = await Link(Q, QK, "QK link");

        R1 = await Note(R, null, "R1"); E_R1 = await Trash(ContentKind.Note, R1); RK = await Note(R, null, "RK");
        return this;
    }

    public async Task<long> Folder(long project, long? parent, string name) => (await Other.CreateFolderAsync(project, parent, name)).Value!.Id;

    public async Task<long> Link(long project, long? folder, string name) =>
        (await Other.CreateLinkAsync(project, folder, new ProjectLinkDraft(name, ProjectLinkType.Web, $"https://example.test/{Uri.EscapeDataString(name)}"))).Value!.Id;

    public async Task<long> Note(long project, long? folder, string title, string content = "synthetic text") =>
        (await Other.CreateNoteAsync(project, folder, new ProjectNoteDraft(title, content))).Note!.Id;

    public async Task<long> Trash(ContentKind kind, long id)
    {
        var result = await Other.TrashAsync(new ContentRef(kind, id));
        Assert.Equal(TrashOutcome.Trashed, result.Outcome);
        return result.Entry!.Id;
    }

    /// <summary>Every row of every user table, keyed by table, as "col=value|…" ordered by all columns.</summary>
    public Dictionary<string, List<string>> Dump(string? file = null)
    {
        using var connection = file is null ? Root.Open() : BackupKit.Open(file, SqliteOpenMode.ReadOnly);
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
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
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY {string.Join(", ", Enumerable.Range(1, columns))};"; // also WITHOUT ROWID tables
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetName(i)}={(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))}")));
            dump[table] = rows;
        }

        if (file is not null)
            BackupKit.ClearPools(file);
        return dump;
    }

    /// <summary>Exactly the rows (table, Id) listed are gone; every other row of every table is byte-identical.</summary>
    public static void AssertOnlyRemoved(Dictionary<string, List<string>> before, Dictionary<string, List<string>> after, params (string Table, long Id)[] removed)
    {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var table in before.Keys)
        {
            var gone = removed.Where(r => r.Table == table).Select(r => $"Id={r.Id}|").ToHashSet();
            var expected = before[table].Where(row => !gone.Any(prefix => row.StartsWith(prefix, StringComparison.Ordinal))).ToList();
            Assert.True(expected.SequenceEqual(after[table]), $"{table}: expected {expected.Count} rows, found {after[table].Count}");
            Assert.Equal(before[table].Count - removed.Count(r => r.Table == table), after[table].Count);
        }
    }

    /// <summary>foreign_key_check empty, integrity_check ok, no dangling marker and no rootless entry.</summary>
    public void AssertIntegrity()
    {
        using var connection = Root.Open();
        Assert.Equal(0L, Db.ScalarLong(connection, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal("ok", Db.ScalarString(connection, "PRAGMA integrity_check;"));
        Assert.Equal(0L, Db.ScalarLong(connection, """
            SELECT (SELECT count(*) FROM ProjectFolders f WHERE f.TrashEntryId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ProjectTrashEntries e WHERE e.Id = f.TrashEntryId))
                 + (SELECT count(*) FROM ProjectLinks l WHERE l.TrashEntryId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ProjectTrashEntries e WHERE e.Id = l.TrashEntryId))
                 + (SELECT count(*) FROM ProjectNotes n WHERE n.TrashEntryId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ProjectTrashEntries e WHERE e.Id = n.TrashEntryId))
                 + (SELECT count(*) FROM ProjectTrashEntries e WHERE NOT EXISTS (
                        SELECT 1 FROM ProjectFolders WHERE e.RootKind = 'Folder' AND Id = e.RootId AND TrashEntryId = e.Id
                        UNION ALL SELECT 1 FROM ProjectLinks WHERE e.RootKind = 'Link' AND Id = e.RootId AND TrashEntryId = e.Id
                        UNION ALL SELECT 1 FROM ProjectNotes WHERE e.RootKind = 'Note' AND Id = e.RootId AND TrashEntryId = e.Id));
            """));
    }

    public string[] PreDeleteFiles() =>
        Directory.Exists(Root.Paths.PreDeleteBackupsDirectory)
            ? Directory.GetFiles(Root.Paths.PreDeleteBackupsDirectory).Select(f => Path.GetFileName(f)!).Where(n => n != "predelete.lock").Order().ToArray()
            : [];

    public void Exec(string sql)
    {
        using var connection = Root.Open();
        Db.Exec(connection, sql);
    }

    public void Dispose()
    {
        BackupKit.ClearPoolsUnder(Root.Paths.Root);
        Root.Dispose();
    }
}
