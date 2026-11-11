using Microsoft.Data.Sqlite;
using WorkHub.Core.Workspace;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>
/// Phase 14 (proposal Revision 2 §5.7, §7.3 P2/P3/P5): what the RELEASED v9 schema itself does with raw SQL, kept apart from
/// the store's purge policy. These tests document the schema; they are not the store's guarantees. Every raw write runs in a
/// transaction that is rolled back, so each test leaves the database as it found it.
/// </summary>
public sealed class PurgeSchemaBehaviorTests : IDisposable
{
    private readonly PurgeWorld _w = new();

    public void Dispose() => _w.Dispose();

    private static string? Error(SqliteConnection connection, string sql)
    {
        try
        {
            Db.Exec(connection, sql);
            return null;
        }
        catch (SqliteException ex)
        {
            return ex.Message;
        }
    }

    [Fact]
    public async Task P2_TheProjectItselfInTheWorkspaceTrash_V007RefusesEveryContentAndEntryDelete()
    {
        await _w.BuildAsync();
        await _w.Workspace.TrashProjectAsync(_w.P);
        var before = _w.Dump();
        using var connection = _w.Root.Open();
        Db.Exec(connection, "BEGIN IMMEDIATE;");

        Assert.Contains("WH_PROJECT_TRASHED", Error(connection, $"DELETE FROM ProjectNotes WHERE Id = {_w.N1};"));
        Assert.Contains("WH_PROJECT_TRASHED", Error(connection, $"DELETE FROM ProjectLinks WHERE Id = {_w.L1};"));
        Assert.Contains("WH_PROJECT_TRASHED", Error(connection, $"DELETE FROM ProjectFolders WHERE Id = {_w.FA1a};"));
        Assert.Contains("WH_PROJECT_TRASHED", Error(connection, $"DELETE FROM ProjectNotes WHERE Id = {_w.NK};")); // active content too
        Assert.Contains("WH_PROJECT_TRASHED", Error(connection, $"DELETE FROM ProjectTrashEntries WHERE Id = {_w.E_L1};")); // before the FK

        Db.Exec(connection, "ROLLBACK;");
        Assert.Equal(before, _w.Dump());
    }

    [Fact]
    public async Task P3_OnlyAnAncestorWorkspaceFolderTrashed_TheSchemaAllowsRawDeletes_ProtectionIsTheStoresAlone()
    {
        await _w.BuildAsync();
        await _w.Workspace.MarkFolderAsync(_w.WF, FolderLifecycleKind.Trash, (await _w.Workspace.PrepareFolderLifecycleAsync(_w.WF, FolderLifecycleKind.Trash)).Confirmation!.Token);
        var before = _w.Dump();
        using var connection = _w.Root.Open();
        Assert.Null(Db.ScalarString(connection, $"SELECT TrashedAt FROM WorkspaceNodes WHERE Id = {_w.P};")); // the Project's own marker is clear
        Db.Exec(connection, "BEGIN IMMEDIATE;");

        Assert.Null(Error(connection, $"DELETE FROM ProjectNotes WHERE Id = {_w.N1};"));
        Assert.Null(Error(connection, $"DELETE FROM ProjectLinks WHERE Id = {_w.L1};"));
        Assert.Null(Error(connection, $"DELETE FROM ProjectTrashEntries WHERE Id IN ({_w.E_L1}, {_w.E_N1});"));
        Assert.Null(Error(connection, $"DELETE FROM ProjectNotes WHERE Id = {_w.NK};"));

        Db.Exec(connection, "ROLLBACK;");
        Assert.Equal(before, _w.Dump());
    }

    [Fact]
    public async Task RestrictIsCheckedPerRow_OneDeleteOverAParentAndChildFolderFails_AndLeavesTheTransactionOpen()
    {
        await _w.BuildAsync();
        var before = _w.Dump();
        using var connection = _w.Root.Open();
        Db.Exec(connection, "BEGIN IMMEDIATE;");
        Db.Exec(connection, $"DELETE FROM ProjectNotes WHERE TrashEntryId = {_w.E_FA}; DELETE FROM ProjectLinks WHERE TrashEntryId = {_w.E_FA};");

        Assert.Contains("FOREIGN KEY", Error(connection, $"DELETE FROM ProjectFolders WHERE TrashEntryId = {_w.E_FA};"));
        Assert.Equal(3L, Db.ScalarLong(connection, $"SELECT count(*) FROM ProjectFolders WHERE TrashEntryId = {_w.E_FA};")); // the statement undid itself
        Assert.Contains("FOREIGN KEY", Error(connection, $"DELETE FROM ProjectTrashEntries WHERE Id = {_w.E_FA};")); // markers still reference it
        Assert.Contains("FOREIGN KEY", Error(connection, $"DELETE FROM ProjectFolders WHERE Id = {_w.FB};")); // a dependent (NB) still in it
        Assert.False(connection.AutoCommit(), "a failed statement must not end the transaction");

        Db.Exec(connection, "ROLLBACK;");
        Assert.Equal(before, _w.Dump());
    }

    [Fact]
    public async Task P5_TrashedRowsAndEntriesAreImmutable_NothingCanBeCreatedInsideATrashedFolder()
    {
        await _w.BuildAsync();
        var before = _w.Dump();
        using var connection = _w.Root.Open();

        Assert.Contains("WH_TRASHED", Error(connection, $"UPDATE ProjectNotes SET Content = 'x', Revision = Revision + 1 WHERE Id = {_w.N1};"));
        Assert.Contains("WH_TRASHED", Error(connection, $"UPDATE ProjectLinks SET Target = 'https://example.test/x' WHERE Id = {_w.L1};"));
        Assert.Contains("WH_TRASHED", Error(connection, $"UPDATE ProjectFolders SET Name = 'x', NameKey = 'X' WHERE Id = {_w.FA1};"));
        Assert.Contains("WH_TRASH_ENTRY_IMMUTABLE", Error(connection, $"UPDATE ProjectTrashEntries SET ItemCount = 9 WHERE Id = {_w.E_FA};"));
        Assert.Contains("WH_TRASH_MARKER", Error(connection, $"UPDATE ProjectNotes SET TrashEntryId = {_w.E_FA} WHERE Id = {_w.N1};"));
        Assert.Contains("WH_CONTAINER_TRASHED", Error(connection, $"INSERT INTO ProjectNotes (ProjectNodeId, FolderId, Title, CreatedAt, UpdatedAt) VALUES ({_w.P}, {_w.FA1}, 'new', '{Db.Now}', '{Db.Now}');"));
        Assert.Contains("WH_CONTENT_IMMUTABLE", Error(connection, $"UPDATE ProjectNotes SET ProjectNodeId = {_w.Q} WHERE Id = {_w.N1};"));
        Assert.Equal(before, _w.Dump());
    }

    [Fact]
    public async Task ClearingADependentsMarkerIsAllowedByTheSchema_RestoreRefusesItAsAStoreRule()
    {
        await _w.BuildAsync();
        using var connection = _w.Root.Open();
        Db.Exec(connection, "BEGIN IMMEDIATE;");
        Assert.Null(Error(connection, $"UPDATE ProjectNotes SET TrashEntryId = NULL WHERE Id = {_w.NB};"));
        Db.Exec(connection, "ROLLBACK;");

        Assert.Equal(WorkHub.Core.Workspace.Content.RestoreOutcome.ContainerTrashed, (await _w.Other.RestoreAsync(_w.E_NB)).Outcome);
    }
}

internal static class SqliteConnectionAutoCommit
{
    /// <summary>True when no transaction is open on the connection (SQLite's autocommit mode).</summary>
    public static bool AutoCommit(this SqliteConnection connection)
    {
        // A nested BEGIN fails only while a transaction is open.
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN; ROLLBACK;";
        try
        {
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
