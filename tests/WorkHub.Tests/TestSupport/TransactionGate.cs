using Microsoft.Data.Sqlite;
using WorkHub.Infrastructure.Persistence;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Deterministic overlapping-transaction gate (Phase 8 D14). Connections from <see cref="Factory"/> carry a blocking SQL
/// function and a TEMP trigger <c>AFTER {operation} ON main.{table}</c>, so the FIRST matching write through a store built
/// on this factory stops INSIDE its write transaction — after its precondition reads and its write, before commit, while
/// it holds the write lock — until <see cref="Release"/>. Later matching writes pass straight through.
/// Disposing always releases, so a failed assertion never leaves a store thread or connection blocked.
/// </summary>
internal sealed class TransactionGate : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly ManualResetEventSlim _release = new();
    private int _armed = 1;

    public TransactionGate(string databaseFile, string table, string operation)
    {
        Factory = new SqliteConnectionFactory(databaseFile, defaultTimeoutSeconds: null, opened: connection =>
        {
            connection.CreateFunction("wh_test_gate", () =>
            {
                if (Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    Entered.Set();
                    if (!_release.Wait(Timeout))
                        throw new TimeoutException("The transaction gate was never released.");
                }

                return 0L;
            });
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TEMP TRIGGER wh_test_gate_trigger AFTER {operation} ON main.{table} BEGIN SELECT wh_test_gate(); END;";
            command.ExecuteNonQuery();
        });
    }

    /// <summary>Unpooled connections with the gate installed; give it to the store under test.</summary>
    public SqliteConnectionFactory Factory { get; }

    /// <summary>Set when the gated write is paused inside its transaction.</summary>
    public ManualResetEventSlim Entered { get; } = new();

    public void WaitEntered() => Assert.True(Entered.Wait(TimeSpan.FromSeconds(10)), "the gated write never reached the gate");

    public void Release() => _release.Set();

    public void Dispose() => _release.Set();
}

/// <summary>
/// A second writer's connections signal when they open, so a test knows the second operation has started (it then
/// waits on the write lock in <c>BEGIN IMMEDIATE</c>) before releasing the first.
/// </summary>
internal sealed class OpenSignal
{
    public OpenSignal(string databaseFile) =>
        Factory = new SqliteConnectionFactory(databaseFile, defaultTimeoutSeconds: null, opened: _ => Opened.Set());

    public SqliteConnectionFactory Factory { get; }

    public ManualResetEventSlim Opened { get; } = new();

    public void WaitOpened() => Assert.True(Opened.Wait(TimeSpan.FromSeconds(10)), "the second operation never opened its connection");
}
