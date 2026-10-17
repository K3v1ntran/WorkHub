using System.Globalization;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Startup;
using WorkHub.Core.Restore;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Restore;

// Test-only child process (Phase 9 §10.2). It runs one scenario, prints "READY" when it reaches the requested point,
// and then waits forever: the test kills it there, so what remains on disk is what a real crash leaves behind.
//
//   stage <dataRoot> <utcInstant> <BackupStage>   run the real daily backup and stop at that stage
//   hot-journal <databaseFile>                    open a write transaction whose pages spill into the database file
//   wal-write <file>                              write into a WAL-mode file and keep the transaction open
//
// Phase 10 (restore; the real RestoreCoordinator / RestoreRequester, killed at a RestoreStage boundary):
//   restore <dataRoot> <op> <RestoreStage> [fail-publish]   act on the op as a token start; stop at the stage
//   putback <dataRoot> <op> <RestoreStage> [fail-publish]   restore to Published (or MovedAside with fail-publish), become
//                                                           the owner, request Put back; stop at the stage
//   cancel <dataRoot> <op> <RestoreStage>                   a start without token answers Cancel restore; stop at the stage
//   request <dataRoot> <dailyFileName> staged|confirmed     a running window requests a restore; stop there (R held / released)
//   hold <file>                                             open the file with an unpooled connection and keep it open
//   hold-pooled <file>                                      open and close a pooled connection (the pool keeps the file open)

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

    case "restore":
    case "putback":
    case "cancel":
    {
        var paths = new WorkHubPaths(args[1]);
        var stop = Enum.Parse<RestoreStage>(args[3]);
        var failPublish = args.Length > 4 && args[4] == "fail-publish";
        var coordinator = new RestoreCoordinator(
            paths, NullAppLog.Instance, args[0] == "cancel" ? null : args[2], TimeProvider.System, SchemaReference.Frozen, new VolumeProbe(), new ProcessStarter(),
            locks => new ProbeFileOps(new RestoreFileOps(paths, () => locks.HoldsRestoreLock), failPublish ? paths : null),
            stage =>
            {
                if (stage == stop)
                    Ready();
            },
            TimeSpan.FromMilliseconds(50));
        var ui = new ProbeUi(args[0] == "cancel" ? RestoreAskAnswer.Cancel : RestoreAskAnswer.Now);
        var entry = coordinator.Enter(ui);
        if (args[0] == "putback")
        {
            entry.Session?.InitializationFailed(new InvalidOperationException("simulated initializer failure"));
            coordinator.RequestPutBack();
        }
        else if (entry.Session is { } session)
        {
            session.InitializationSucceeded(new DatabaseInitializer(paths, NullAppLog.Instance, InitialSystemsSeed.Apply).Initialize());
        }

        Console.WriteLine($"FINISHED {entry.Kind}");
        return 1;
    }

    case "request":
    {
        var paths = new WorkHubPaths(args[1]);
        var point = args[3];
        var requester = new RestoreRequester(paths, NullAppLog.Instance, TimeProvider.System, SchemaReference.Frozen, new VolumeProbe(), null)
        {
            AfterStaging = point == "staged" ? Ready : null,
        };
        DailyBackupPolicy.TryParse(args[2], out var name);
        var entry = new BackupEntry(args[2], BackupFolder.Daily, BackupKind.Daily, new FileInfo(Path.Combine(paths.DailyBackupsDirectory, args[2])).Length, null, name.Version);
        var start = requester.BeginAsync(entry).GetAwaiter().GetResult();
        if (start.Request is null)
        {
            Console.Error.WriteLine(start.Refusal);
            return 3;
        }

        var op = start.Request.ConfirmAsync().GetAwaiter().GetResult();
        Console.WriteLine($"OP {op}");
        Ready();
        return 1;
    }

    case "wal-commit":
    {
        // Committed rows that stay in -wal (no checkpoint): killed here, the database keeps -wal and -shm on disk.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        Exec(connection, "PRAGMA journal_mode = WAL;");
        Exec(connection, "PRAGMA wal_autocheckpoint = 0;");
        for (var i = 0; i < 30; i++)
            Exec(connection, $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('WALC{i:D4}', 'Wal', 'x', 'x');");
        Ready();
        return 1;
    }

    case "hold":
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        Exec(connection, "SELECT count(*) FROM sqlite_schema;");
        Ready();
        return 1;
    }

    case "hold-pooled":
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1], Mode = SqliteOpenMode.ReadWrite, Pooling = true }.ToString()))
        {
            connection.Open();
            Exec(connection, "SELECT count(*) FROM sqlite_schema;");
        }

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

/// <summary>Answers every question the same way; never presses a waiting-screen button.</summary>
internal sealed class ProbeUi(RestoreAskAnswer answer) : IRestoreStartupUi
{
    public void ShowGateWait() { }

    public void ShowWaiting(RestoreWaitKind kind, RestoreOpSummary op) { }

    public RestoreWaitAction TakeWaitAction() => RestoreWaitAction.None;

    public void ShowNotice(string message) => Console.WriteLine("NOTICE " + message);

    public RestoreAskAnswer Ask(RestoreAskKind kind, RestoreOpSummary op) => kind == RestoreAskKind.PutBack && answer == RestoreAskAnswer.Cancel ? RestoreAskAnswer.NotNow : answer;

    public void ShowProgress(string text, bool closeBlocked) { }
}

/// <summary>Real restore file operations; with <c>failPublishFor</c> the X9 rename of the candidate fails (publication failure).</summary>
internal sealed class ProbeFileOps(IRestoreFileOps inner, WorkHubPaths? failPublishFor) : IRestoreFileOps
{
    public bool FileExists(string path) => inner.FileExists(path);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

    public long GetLength(string path) => inner.GetLength(path);

    public string FileId(string path) => inner.FileId(path);

    public IReadOnlyList<WorkHub.Core.Restore.FileFact> List(string directory) => inner.List(directory);

    public IReadOnlyList<string> DirectoryNames(string directory) => inner.DirectoryNames(directory);

    public string Sha256(string path) => inner.Sha256(path);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public void Move(string source, string destination)
    {
        if (failPublishFor is not null && Path.GetFileName(source) == RestoreNames.Candidate && string.Equals(destination, failPublishFor.DatabaseFile, StringComparison.OrdinalIgnoreCase))
            throw new IOException("simulated publication failure", unchecked((int)0x80070005));
        inner.Move(source, destination);
    }

    public void Copy(string source, string destination) => inner.Copy(source, destination);

    public void Delete(string path) => inner.Delete(path);

    public void DeleteEmptyDirectory(string path) => inner.DeleteEmptyDirectory(path);

    public void WriteThrough(string path, byte[] content) => inner.WriteThrough(path, content);

    public void Replace(string source, string destination) => inner.Replace(source, destination);

    public byte[] ReadShared(string path) => inner.ReadShared(path);
}
