using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Startup;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Restore;

/// <summary>
/// Phase 14 B9: a PreDelete copy is listed by the catalog and restorable through the existing whole-database Restore (request,
/// confirmation, restart-time execution with its PreRestore safety copy). Restoring it returns EVERYTHING to the moment the copy
/// was taken: the purged items come back and later edits are reverted. It is not an item-level Undo.
/// </summary>
public sealed class PreDeleteRestoreTests : RestoreTestBase
{
    [Fact]
    public async Task B9_RestoringAPreDeleteCopy_BringsThePurgedItemsBack_AndRevertsLaterEdits()
    {
        Root.CreateInitializer().Initialize();
        var connections = new SqliteConnectionFactory(Root.Paths.DatabaseFile);
        var workspace = new WorkspaceService(new SqliteWorkspaceStore(connections));
        var content = new ProjectContentService(new SqliteProjectContentStore(connections),
            new ProcessProjectLinkLauncher(new RecordingProcessStarter(), _ => true, _ => true), new ProjectTrashPurge(Root.Paths, connections, NullAppLog.Instance));
        var project = (await workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active))).Value!.Id;
        var kept = (await content.CreateNoteAsync(project, null, new ProjectNoteDraft("Kept", "before the purge"))).Note!;
        var gone = (await content.CreateNoteAsync(project, null, new ProjectNoteDraft("Gone", "purged text"))).Note!;
        var entry = (await content.TrashAsync(new ContentRef(ContentKind.Note, gone.Id))).Entry!.Id;

        var request = PurgeRequest.Selected(project, [entry]);
        var purged = await content.PurgeAsync(request, (await content.PreparePurgeAsync(request)).Preview!.Token);
        Assert.Equal(PurgeOutcome.Purged, purged.Outcome);
        await content.SaveNoteAsync(kept.Id, kept.Revision, new ProjectNoteDraft("Kept", "edited after the purge"));

        var copy = Path.Combine(Root.Paths.PreDeleteBackupsDirectory, purged.BackupFileName!);
        var copyDump = BackupKit.Dump(copy);
        Assert.Contains("purged text", copyDump);
        Assert.DoesNotContain("purged text", BackupKit.Dump(Root.Paths.DatabaseFile));

        var listed = Assert.Single(await new BackupCatalog(Root.Paths).ListAsync(), e => e.Kind == BackupKind.PreDelete);
        RestoreKit.ClearPools(Root);
        var op = await RestoreKit.RequestAndConfirmAsync(Root, listed);
        var (result, stop, error) = RestoreKit.Start(Root, Coordinator(token: op), new ScriptedRestoreUi());

        Assert.Null(error);
        Assert.Null(stop);
        Assert.Contains(listed.FileName, result!.RestoreNotice);
        RestoreKit.ClearPools(Root);
        var restored = BackupKit.Dump(Root.Paths.DatabaseFile);
        Assert.Equal(copyDump, restored);                       // the whole database as it was when the copy was taken
        Assert.Contains("purged text", restored);               // the purged note is back (in the Trash, as it was)
        Assert.DoesNotContain("edited after the purge", restored); // and the later edit is gone: not an item-level Undo
        Assert.NotEmpty(Directory.GetFiles(Root.Paths.PreRestoreBackupsDirectory)); // the existing PreRestore safety copy
        Assert.True(File.Exists(copy));                           // the PreDelete copy itself is kept
    }

    [Fact]
    public void PreDeleteNames_RoundTrip_AndOwnCandidatesAreRecognized()
    {
        var at = new DateTimeOffset(2026, 10, 23, 1, 2, 3, 456, TimeSpan.Zero);
        var name = PreDeleteNames.Validated(9, at);

        Assert.Equal("workhub_predelete_v9_20261023-010203456.db", name);
        Assert.True(PreDeleteNames.TryParse(name, out var version, out var taken));
        Assert.Equal((9, at), (version, taken));
        Assert.True(PreDeleteNames.IsOwnCandidate(PreDeleteNames.CandidateName(name, Guid.NewGuid())));
        Assert.True(PreDeleteNames.IsOwnCandidate(PreDeleteNames.CandidateName(name, Guid.NewGuid()) + "-journal"));
        Assert.False(PreDeleteNames.IsOwnCandidate(name));
        Assert.False(PreDeleteNames.TryParse("predelete.lock", out _, out _));
        Assert.Equal(Path.Combine(new WorkHubPaths(@"C:\WorkHubTest\x").BackupsDirectory, "PreDelete"), new WorkHubPaths(@"C:\WorkHubTest\x").PreDeleteBackupsDirectory);
    }
}
