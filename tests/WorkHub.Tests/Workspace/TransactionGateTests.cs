using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Workspace;

/// <summary>
/// Proves the D14 test-only gate before any overlapping-write test relies on it: a gated store write pauses INSIDE its
/// write transaction holding the write lock, nothing is visible before release, a second writer cannot get the lock
/// meanwhile, and disposing the gate always lets the write finish. The app factory has no hook.
/// </summary>
public sealed class TransactionGateTests : IDisposable
{
    private readonly TempWorkHubRoot _root = new();
    private readonly WorkspaceService _workspace;

    public TransactionGateTests()
    {
        _root.CreateInitializer().Initialize();
        _workspace = new WorkspaceService(new SqliteWorkspaceStore(new SqliteConnectionFactory(_root.Paths.DatabaseFile)));
    }

    public void Dispose() => _root.Dispose();

    private string? TrashedAt(long id)
    {
        using var connection = _root.Open();
        return Db.ScalarString(connection, "SELECT TrashedAt FROM WorkspaceNodes WHERE Id = $id;", ("$id", id));
    }

    [Fact]
    public async Task GatedWrite_PausesInsideItsTransaction_HoldingTheWriteLock_UntilReleased()
    {
        var alpha = (await _workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active))).Value!.Id;
        using var gate = new TransactionGate(_root.Paths.DatabaseFile, "WorkspaceNodes", "UPDATE");
        var gated = new WorkspaceService(new SqliteWorkspaceStore(gate.Factory));

        var trash = gated.TrashProjectAsync(alpha);
        gate.WaitEntered();

        Assert.False(trash.IsCompleted);
        Assert.Null(TrashedAt(alpha)); // written inside the transaction, not committed
        using (var other = new SqliteConnectionFactory(_root.Paths.DatabaseFile, defaultTimeoutSeconds: 1).Open(SqliteOpenMode.ReadWrite, pooling: false))
        {
            var busy = Assert.Throws<SqliteException>(() => other.BeginTransaction(deferred: false));
            Assert.Equal(5, busy.SqliteErrorCode); // SQLITE_BUSY: the gated transaction holds the write lock
        }

        gate.Release();
        Assert.Equal(ProjectTrashOutcome.Trashed, (await trash).Outcome);
        Assert.NotNull(TrashedAt(alpha));
    }

    [Fact]
    public async Task OnlyTheFirstMatchingWrite_IsPaused()
    {
        var a = (await _workspace.CreateProjectAsync(null, new ProjectDraft("A", ProjectStatus.Active))).Value!.Id;
        var b = (await _workspace.CreateProjectAsync(null, new ProjectDraft("B", ProjectStatus.Active))).Value!.Id;
        using var gate = new TransactionGate(_root.Paths.DatabaseFile, "WorkspaceNodes", "UPDATE");
        var gated = new WorkspaceService(new SqliteWorkspaceStore(gate.Factory));

        var first = gated.TrashProjectAsync(a);
        gate.WaitEntered();
        gate.Release();
        await first;

        Assert.Equal(ProjectTrashOutcome.Trashed, (await gated.TrashProjectAsync(b)).Outcome); // not paused again
    }

    [Fact]
    public async Task DisposingTheGate_ReleasesAPausedWrite()
    {
        var alpha = (await _workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active))).Value!.Id;
        Task<ProjectTrashResult> trash;
        using (var gate = new TransactionGate(_root.Paths.DatabaseFile, "WorkspaceNodes", "UPDATE"))
        {
            trash = new WorkspaceService(new SqliteWorkspaceStore(gate.Factory)).TrashProjectAsync(alpha);
            gate.WaitEntered();
        } // e.g. an assertion failed here

        Assert.Equal(ProjectTrashOutcome.Trashed, (await trash).Outcome);
    }

    [Fact]
    public async Task TheAppFactory_HasNoHook()
    {
        var alpha = (await _workspace.CreateProjectAsync(null, new ProjectDraft("Alpha", ProjectStatus.Active))).Value!.Id;
        Assert.Equal(ProjectTrashOutcome.Trashed, (await _workspace.TrashProjectAsync(alpha)).Outcome);
        using var connection = new SqliteConnectionFactory(_root.Paths.DatabaseFile).OpenExisting();
        Assert.Equal(0, Db.ScalarLong(connection, "SELECT count(*) FROM temp.sqlite_schema;"));
    }
}
