using WorkHub.Core.Workspace;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Phase 24 fixture (proposal §4.1), built by "window B" (<paramref name="b"/>) on the test database: P1 live {clientA, Infra}; P2
/// live in folder F1 {ClientA}; P3 archived {clientA}; P4 live in archived folder A1 {clientA}; P5 directly in the Trash {clientA};
/// P6 live in folder F2, F2 trashed {clientA}; P7 live {Infra}; F3 live and empty (a move destination). Tags are inserted while
/// every Project is still writable (raw rows with fixed CreatedAt text); then the lifecycle changes. Fake names only.
/// </summary>
internal sealed record TaggedProjectsFixture(long F1, long F2, long F3, long A1, long P1, long P2, long P3, long P4, long P5, long P6, long P7)
{
    public static readonly string ClientA = ProjectTagName.Key("clientA");
    public static readonly string Infra = ProjectTagName.Key("Infra");

    public static async Task<TaggedProjectsFixture> BuildAsync(TempWorkHubRoot root, WorkspaceService b)
    {
        async Task<long> Folder(string name) => (await b.CreateFolderAsync(null, name)).Value!.Id;
        async Task<long> Project(string name, long? parent = null) =>
            (await b.CreateProjectAsync(parent, new ProjectDraft(name, ProjectStatus.Active))).Value!.Id;

        var f1 = await Folder("F1");
        var f2 = await Folder("F2");
        var f3 = await Folder("F3");
        var a1 = await Folder("A1");
        var p1 = await Project("P1");
        var p2 = await Project("P2", f1);
        var p3 = await Project("P3");
        var p4 = await Project("P4", a1);
        var p5 = await Project("P5");
        var p6 = await Project("P6", f2);
        var p7 = await Project("P7");
        InsertTag(root, p1, "clientA", 1);
        InsertTag(root, p1, "Infra", 11);
        InsertTag(root, p2, "ClientA", 2);
        InsertTag(root, p3, "clientA", 3);
        InsertTag(root, p4, "clientA", 4);
        InsertTag(root, p5, "clientA", 5);
        InsertTag(root, p6, "clientA", 6);
        InsertTag(root, p7, "Infra", 17);
        Assert.Equal(ProjectArchiveOutcome.Archived, (await b.ArchiveProjectAsync(p3)).Outcome);
        await MarkFolder(b, a1, FolderLifecycleKind.Archive);
        Assert.Equal(ProjectTrashOutcome.Trashed, (await b.TrashProjectAsync(p5)).Outcome);
        await MarkFolder(b, f2, FolderLifecycleKind.Trash);
        return new TaggedProjectsFixture(f1, f2, f3, a1, p1, p2, p3, p4, p5, p6, p7);
    }

    /// <summary>Window B adds a tag row as a raw insert (the Project must be writable: live or archived rules of V010 apply).</summary>
    public static void InsertTag(TempWorkHubRoot root, long project, string name, int day)
    {
        using var connection = root.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ProjectTags (ProjectNodeId, NameKey, Name, CreatedAt) VALUES ($p, $k, $n, $c);";
        command.Parameters.AddWithValue("$p", project);
        command.Parameters.AddWithValue("$k", ProjectTagName.Key(name));
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$c", $"2026-01-{day:00}T00:00:00.000Z");
        command.ExecuteNonQuery();
    }

    private static async Task MarkFolder(WorkspaceService b, long folder, FolderLifecycleKind kind)
    {
        var token = (await b.PrepareFolderLifecycleAsync(folder, kind)).Confirmation!.Token;
        Assert.Equal(FolderLifecycleOutcome.Committed, (await b.MarkFolderAsync(folder, kind, token)).Outcome);
    }
}
