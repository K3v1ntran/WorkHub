using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;

// Test-only child process (Phase 9 §10.2). It runs one scenario, prints "READY" when it reaches the requested point,
// and then waits forever: the test kills it there, so what remains on disk is what a real crash leaves behind.
//
//   stage <dataRoot> <utcInstant> <BackupStage>   run the real daily backup and stop at that stage
//   hot-journal <databaseFile>                    open a write transaction whose pages spill into the database file
//   wal-write <file>                              write into a WAL-mode file and keep the transaction open

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: stage <root> <utc> <stage> | hot-journal <db> | wal-write <file>");
    return 2;
}

switch (args[0])
{
    case "stage":
    {
        var paths = new WorkHubPaths(args[1]);
        var clock = new FixedClock(DateTimeOffset.Parse(args[2], CultureInfo.InvariantCulture));
        var stop = Enum.Parse<BackupStage>(args[3]);
        var service = new DailyBackupService(paths, NullAppLog.Instance, clock, JstCalendar.Resolve(), SchemaReference.Frozen, new BackupFileOps(), stage =>
        {
            if (stage == stop)
                Ready();
        });
        service.Run();
        Console.WriteLine("FINISHED");
        return 1;
    }

    case "hot-journal":
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        Exec(connection, "PRAGMA cache_size = 2;"); // tiny cache: modified pages are written to the database before commit
        Exec(connection, "BEGIN IMMEDIATE;");
        for (var i = 0; i < 400; i++)
            Exec(connection, $"INSERT INTO Systems (SystemCode, SystemName, Description, CreatedAt, UpdatedAt) VALUES ('HOT{i:D5}', 'Hot', '{new string('h', 400)}', 'x', 'x');");
        Ready();
        return 1;
    }

    case "wal-write":
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        Exec(connection, "BEGIN IMMEDIATE;");
        Exec(connection, $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('WALW1', 'Wal', 'x', 'x');");
        Ready();
        return 1;
    }

    default:
        Console.Error.WriteLine($"unknown mode {args[0]}");
        return 2;
}

static void Ready()
{
    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
}

static void Exec(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
