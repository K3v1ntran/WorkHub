using Microsoft.Data.Sqlite;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Tests.TestSupport;

namespace WorkHub.Tests.Persistence;

/// <summary>
/// Pooled connections from <see cref="SqliteConnectionFactory"/> are owned by exactly one caller while open. Microsoft.Data.Sqlite
/// 10.0.12 could hand a connection that was still being activated to a second, concurrent open (dotnet/efcore#39008, fixed in
/// 10.0.13): two live connections then shared one native handle and the second BEGIN failed. Public behaviour only; no private
/// members of the library are inspected.
/// </summary>
public sealed class PooledConnectionTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private readonly TempWorkHubRoot _root = new();

    public PooledConnectionTests() => _root.CreateInitializer().Initialize();

    public void Dispose() => _root.Dispose();

    private SqliteConnectionFactory Factory => new(_root.Paths.DatabaseFile);

    private static long Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void ConnectionsOpenedConcurrentlyAndHeldTogether_HaveDistinctNativeHandles_AndIndependentTransactions()
    {
        const int width = 8;
        var factory = Factory;
        for (var round = 0; round < 25; round++)
        {
            var handles = new IntPtr[width];
            var failures = new List<Exception>();
            using var opened = new Barrier(width);
            using var begun = new Barrier(width);
            var threads = Enumerable.Range(0, width).Select(i => new Thread(() =>
            {
                try
                {
                    Assert.True(opened.SignalAndWait(Bound)); // the opens race each other (warm pool, then new connections)
                    using var connection = factory.OpenExisting();
                    handles[i] = connection.Handle!.DangerousGetHandle();
                    using var transaction = connection.BeginTransaction(deferred: true);
                    Assert.True(begun.SignalAndWait(Bound)); // every connection is open, inside its own transaction, at once
                    Assert.Equal(0, Scalar(connection, transaction, "SELECT count(*) FROM WorkspaceNodes;"));
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    lock (failures)
                        failures.Add(ex);
                    opened.RemoveParticipant();
                    begun.RemoveParticipant();
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => Assert.True(t.Join(Bound)));

            Assert.Empty(failures);
            Assert.Equal(width, handles.Distinct().Count());
        }
    }

    [Fact]
    public void AnOpenWhileEveryPooledConnectionIsLeased_NeverReceivesALiveOne()
    {
        // With every pooled connection leased and an even count, the pool looks for leaked connections before creating one: a live,
        // open connection must never be taken for leaked.
        var factory = Factory;
        using (factory.OpenExisting())
        using (factory.OpenExisting())
        {
        }

        using var first = factory.OpenExisting();
        using var second = factory.OpenExisting();
        using var third = factory.OpenExisting();

        Assert.Equal(3, new[] { first, second, third }.Select(c => c.Handle!.DangerousGetHandle()).Distinct().Count());
        using var a = first.BeginTransaction(deferred: true);
        using var b = second.BeginTransaction(deferred: true);
        using var c = third.BeginTransaction(deferred: true);
    }

    [Fact]
    public void AWriteTransactionOnOneHeldConnection_IsNotSeenOrEndedThroughAnother()
    {
        var factory = Factory;
        using var writer = factory.OpenExisting();
        using var reader = factory.OpenExisting();
        Assert.NotEqual(writer.Handle!.DangerousGetHandle(), reader.Handle!.DangerousGetHandle());

        using (var write = writer.BeginTransaction(deferred: false))
        {
            using (var insert = writer.CreateCommand())
            {
                insert.Transaction = write;
                insert.CommandText = $"""
                    INSERT INTO WorkspaceNodes (ParentId, NodeType, Name, NameKey, CreatedAt, UpdatedAt)
                    VALUES (NULL, 'Folder', 'Pending', 'PENDING', '{Db.Now}', '{Db.Now}');
                    """;
                insert.ExecuteNonQuery();
            }

            // The other connection starts and ends its own transactions while the write is open, and never sees it.
            for (var i = 0; i < 2; i++)
            {
                using var read = reader.BeginTransaction(deferred: true);
                Assert.Equal(0, Scalar(reader, read, "SELECT count(*) FROM WorkspaceNodes;"));
                read.Commit();
            }

            Assert.Equal(1, Scalar(writer, write, "SELECT count(*) FROM WorkspaceNodes;")); // still open, still the writer's
            write.Rollback();
        }

        using var after = reader.BeginTransaction(deferred: true);
        Assert.Equal(0, Scalar(reader, after, "SELECT count(*) FROM WorkspaceNodes;"));
    }
}
