using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Owner;
using WorkHub.Core.Repositories;
using WorkHub.Core.Workspace;
using WorkHub.Core.Workspace.Content;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;

namespace WorkHub.Tests.TestSupport;

/// <summary>A clock the test sets directly (UTC). Only GetUtcNow is used by the backup code.</summary>
internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    private long _ticks = now.UtcTicks;

    public DateTimeOffset Now
    {
        get => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _ticks, value.UtcTicks);
    }

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>A UTC instant given as Tokyo wall-clock time.</summary>
    public static DateTimeOffset Tokyo(int year, int month, int day, int hour = 9, int minute = 0, int second = 0, int ms = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, ms, TimeSpan.FromHours(9)).ToUniversalTime();
}

/// <summary>Collects log lines so tests can assert what was (and was not) logged.</summary>
internal sealed class RecordingLog : IAppLog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Info(string message) => Lines.Enqueue("INFO " + message);

    public void Warn(string message) => Lines.Enqueue("WARN " + message);

    public void Error(string message, Exception? exception = null) => Lines.Enqueue("ERROR " + message + (exception is null ? "" : " " + exception.GetType().Name));

    public string All => string.Join(Environment.NewLine, Lines);
}

/// <summary>
/// Real file operations with injectable failures, recording every path the backup code touches (§10.2 seam).
/// Each rule receives the path and may throw.
/// </summary>
internal sealed class FaultyBackupFileOps : IBackupFileOps
{
    private readonly BackupFileOps _real = new();

    public ConcurrentQueue<string> Touched { get; } = new();

    public ConcurrentQueue<string> Deleted { get; } = new();

    public Action<string>? OnCreateDirectory { get; set; }

    public Action<string, string>? OnMove { get; set; }

    public Action<string>? OnDelete { get; set; }

    public Action<string>? OnOpenExclusive { get; set; }

    public Action<string>? OnEnumerate { get; set; }

    public Action<string>? OnFileExists { get; set; }

    public Action<string>? OnGetAttributes { get; set; }

    public Action<string>? OnGetLength { get; set; }

    public Action<string>? OnReadHeader { get; set; }

    /// <summary>Paths reported as reparse points (to fake symbolic links without needing the privilege).</summary>
    public HashSet<string> FakeReparsePoints { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void CreateDirectory(string path)
    {
        Touched.Enqueue(path);
        OnCreateDirectory?.Invoke(path);
        _real.CreateDirectory(path);
    }

    public bool DirectoryExists(string path)
    {
        Touched.Enqueue(path);
        return _real.DirectoryExists(path);
    }

    public IReadOnlyList<string> EnumerateFileNames(string directory)
    {
        Touched.Enqueue(directory);
        OnEnumerate?.Invoke(directory);
        return _real.EnumerateFileNames(directory);
    }

    public bool FileExists(string path)
    {
        Touched.Enqueue(path);
        OnFileExists?.Invoke(path);
        return _real.FileExists(path);
    }

    public FileAttributes GetAttributes(string path)
    {
        Touched.Enqueue(path);
        OnGetAttributes?.Invoke(path);
        var attributes = _real.GetAttributes(path);
        return FakeReparsePoints.Contains(path) ? attributes | FileAttributes.ReparsePoint : attributes;
    }

    public long GetLength(string path)
    {
        Touched.Enqueue(path);
        OnGetLength?.Invoke(path);
        return _real.GetLength(path);
    }

    public byte[] ReadHeader(string path, int count)
    {
        Touched.Enqueue(path);
        OnReadHeader?.Invoke(path);
        return _real.ReadHeader(path, count);
    }

    public void Move(string source, string destination)
    {
        Touched.Enqueue(source);
        Touched.Enqueue(destination);
        OnMove?.Invoke(source, destination);
        _real.Move(source, destination);
    }

    public void Delete(string path)
    {
        Touched.Enqueue(path);
        OnDelete?.Invoke(path);
        _real.Delete(path);
        Deleted.Enqueue(Path.GetFileName(path));
    }

    public IDisposable OpenExclusive(string path)
    {
        Touched.Enqueue(path);
        OnOpenExclusive?.Invoke(path);
        return _real.OpenExclusive(path);
    }

    public static IOException DiskFull() => new("There is not enough space on the disk.", unchecked((int)0x80070070));

    public static IOException SharingViolation() => new("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));
}

/// <summary>Helpers shared by the Phase 9 backup tests.</summary>
internal static class BackupKit
{
    public static readonly JstCalendar Tokyo = JstCalendar.Resolve();

    /// <summary>A v7 database created by the real initializer (with the initial Systems).</summary>
    public static void CreateDatabase(TempWorkHubRoot root)
    {
        new DatabaseInitializer(root.Paths, NullAppLog.Instance, InitialSystemsSeed.Apply).Initialize();
        new SqliteConnectionFactory(root.Paths.DatabaseFile).ClearPools();
    }

    public static DailyBackupService Service(
        TempWorkHubRoot root,
        TimeProvider clock,
        IBackupFileOps? files = null,
        Action<BackupStage>? hook = null,
        IAppLog? log = null,
        int lockWaitSeconds = 10,
        SchemaReference? references = null) =>
        new(root.Paths, log ?? NullAppLog.Instance, clock, Tokyo, references ?? SchemaReference.Frozen, files ?? new BackupFileOps(), hook, lockWaitSeconds);

    public static BackupValidator Validator() => new(SchemaReference.Frozen, new BackupFileOps());

    public static string Daily(TempWorkHubRoot root, string fileName) => Path.Combine(root.Paths.DailyBackupsDirectory, fileName);

    public static string[] DailyFiles(TempWorkHubRoot root) =>
        Directory.Exists(root.Paths.DailyBackupsDirectory)
            ? Directory.GetFiles(root.Paths.DailyBackupsDirectory).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray()
            : [];

    public static string[] PublishedBackups(TempWorkHubRoot root) =>
        DailyFiles(root).Where(f => DailyBackupPolicy.TryParse(f, out _)).ToArray();

    /// <summary>Name of a daily backup for a Tokyo day (stamp at 09:00:00.000 + <paramref name="ms"/> ms).</summary>
    public static string NameFor(DateOnly day, int ms = 0, int version = 7) =>
        DailyBackupPolicy.FileName(day, version, new DateTimeOffset(day.Year, day.Month, day.Day, 9, 0, 0, ms, TimeSpan.FromHours(9)));

    /// <summary>Places a standalone, fully valid copy of the current database under a daily name (SQLite backup API).</summary>
    public static string PlaceValidBackup(TempWorkHubRoot root, string fileName)
    {
        Directory.CreateDirectory(root.Paths.DailyBackupsDirectory);
        var target = Daily(root, fileName);
        SnapshotTo(root.Paths.DatabaseFile, target);
        return target;
    }

    /// <summary>Standalone copy of <paramref name="source"/> (rollback-journal header, no sidecars).</summary>
    public static void SnapshotTo(string source, string target)
    {
        using (var from = new SqliteConnection(Cs(source, SqliteOpenMode.ReadWrite)))
        using (var to = new SqliteConnection(Cs(target, SqliteOpenMode.ReadWriteCreate)))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);
            Exec(to, "PRAGMA journal_mode=DELETE;");
        }
    }

    public static string Cs(string file, SqliteOpenMode mode, int? timeout = null)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = file, Mode = mode, Pooling = false, ForeignKeys = true };
        if (timeout is { } t)
            builder.DefaultTimeout = t;
        return builder.ToString();
    }

    public static SqliteConnection Open(string file, SqliteOpenMode mode = SqliteOpenMode.ReadWrite, int? timeout = null)
    {
        var connection = new SqliteConnection(Cs(file, mode, timeout));
        connection.Open();
        return connection;
    }

    public static void Exec(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.NextResult())
        {
        }
    }

    public static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>Flips bytes in the middle of a page that belongs to a table b-tree (damage quick_check sees).</summary>
    public static void CorruptPage(string file, int page = 3)
    {
        // In place and sharing read/write, so it also works while SQLite still has the file open.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var header = new byte[18];
        stream.ReadExactly(header);
        var pageSize = (header[16] << 8) | header[17];
        if (pageSize == 1)
            pageSize = 65536;
        var block = new byte[64];
        stream.Position = (long)pageSize * (page - 1);
        stream.ReadExactly(block);
        for (var i = 0; i < block.Length; i++)
            block[i] ^= 0xFF;
        stream.Position = (long)pageSize * (page - 1);
        stream.Write(block);
    }

    /// <summary>Header bytes 18/19 (file format write/read version: 1 = rollback journal, 2 = WAL).</summary>
    public static (byte Write, byte Read) Header(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[20];
        stream.ReadExactly(header);
        return (header[18], header[19]);
    }

    public static string[] Sidecars(string file) =>
        new[] { "-journal", "-wal", "-shm" }.Where(s => File.Exists(file + s)).ToArray();

    public static string Hash(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>Every row of every user table, ordered by rowid/primary key (for byte-level data preservation checks).</summary>
    public static string Dump(string file)
    {
        using var connection = Open(file, SqliteOpenMode.ReadOnly);
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        var text = new System.Text.StringBuilder();
        foreach (var table in tables)
        {
            text.Append("## ").Append(table).Append('\n');
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1, 2;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    text.Append(reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Applies the frozen migrations 1..<paramref name="toVersion"/> (each in its own transaction) to a file.</summary>
    public static void BuildSchema(string file, int toVersion)
    {
        using var connection = Open(file, SqliteOpenMode.ReadWriteCreate);
        foreach (var migration in WorkHub.Infrastructure.Persistence.Migrations.SchemaMigrations.All.Where(m => m.Version <= toVersion))
        {
            using var transaction = connection.BeginTransaction();
            migration.Apply(connection, transaction);
            Exec(connection, $"PRAGMA user_version = {migration.Version};", transaction);
            transaction.Commit();
        }
    }
}

/// <summary>
/// A populated v7 database with every WorkHub data kind (§10.2 B11): Systems with enabled/disabled memberships and a
/// custom target, Owner groups/items of every action type, a 3-level Workspace tree, active Projects with details, a
/// Project in the Workspace Trash with its own content, nested Project folders, links, notes with Revision &gt; 1, an
/// independent Project Trash entry and a nested one (a trashed folder holding a note and a link).
/// </summary>
internal static class PopulatedV7
{
    public sealed record Ids(long TrashedProject, long ActiveProject, long EditedNote, long NestedTrashEntry, long IndependentTrashEntry);

    public static async Task<Ids> CreateAsync(TempWorkHubRoot root)
    {
        BackupKit.CreateDatabase(root);
        var connections = new SqliteConnectionFactory(root.Paths.DatabaseFile);
        var starter = new RecordingProcessStarter();

        var systems = new SystemService(new SqliteSystemStore(connections));
        var created = await systems.CreateAsync(new SystemDraft("PH9SYS01", "Phase 9 System", "backup fixture", true,
        [
            new MembershipDraft(RepositoryType.SvnSystem, true),
            new MembershipDraft(RepositoryType.GitSystem, false),
            new MembershipDraft(RepositoryType.Release, true, "http://example.test/custom/target"),
        ]));
        Assert.True(created.Succeeded, string.Join(", ", created.Errors.Select(e => e.Message)));
        await systems.CreateAsync(new SystemDraft("PH9ORA01", "Phase 9 Oracle", null, false, [new MembershipDraft(RepositoryType.SvnOracle, true)]));

        var owners = new OwnerService(new SqliteOwnerStore(connections), new ProcessOwnerLauncher(starter));
        var group = (await owners.CreateGroupAsync("Phase 9 tools")).Group!;
        var second = (await owners.CreateGroupAsync("Phase 9 second")).Group!;
        foreach (var (type, target) in new[] { (OwnerActionType.Web, "https://example.test/"), (OwnerActionType.File, @"C:\Work\a.txt"), (OwnerActionType.Folder, @"C:\Work"), (OwnerActionType.Executable, @"C:\Tools\tool.exe") })
            Assert.True((await owners.CreateItemAsync(group.Id, new OwnerItemDraft($"Item {type}", type, target, Note: "note"))).Succeeded);
        Assert.True((await owners.CreateItemAsync(second.Id, new OwnerItemDraft("Other", OwnerActionType.Web, "https://example.test/x"))).Succeeded);

        var workspace = new WorkspaceService(new SqliteWorkspaceStore(connections));
        var level1 = (await workspace.CreateFolderAsync(null, "Clients")).Value!;
        var level2 = (await workspace.CreateFolderAsync(level1.Id, "Tokyo")).Value!;
        var level3 = (await workspace.CreateFolderAsync(level2.Id, "Sales")).Value!;
        var active = (await workspace.CreateProjectAsync(level3.Id, new ProjectDraft("Active Portal", ProjectStatus.Maintenance, "desc", "Owner A", "PM B"))).Value!;
        var trashed = (await workspace.CreateProjectAsync(level2.Id, new ProjectDraft("Old Portal", ProjectStatus.Completed, "old"))).Value!;
        await workspace.CreateProjectAsync(null, new ProjectDraft("Top Level", ProjectStatus.Planning));

        var content = new ProjectContentService(new SqliteProjectContentStore(connections), new ProcessProjectLinkLauncher(starter, _ => true, _ => true));
        var docs = (await content.CreateFolderAsync(active.Id, null, "Docs")).Value!;
        var specs = (await content.CreateFolderAsync(active.Id, docs.Id, "Specs")).Value!;
        await content.CreateLinkAsync(active.Id, specs.Id, new ProjectLinkDraft("Spec share", ProjectLinkType.Folder, @"\\server\share\spec"));
        await content.CreateLinkAsync(active.Id, null, new ProjectLinkDraft("Site", ProjectLinkType.Web, "https://example.test/site"));
        var note = (await content.CreateNoteAsync(active.Id, docs.Id, new ProjectNoteDraft("Plan", "v1"))).Note!;
        note = (await content.SaveNoteAsync(note.Id, note.Revision, new ProjectNoteDraft("Plan", "v2 edited"))).Note!;
        note = (await content.SaveNoteAsync(note.Id, note.Revision, new ProjectNoteDraft("Plan", "v3 edited again"))).Note!;
        Assert.True(note.Revision >= 3);

        // Nested trash entry: a folder holding a note and a link.
        var archive = (await content.CreateFolderAsync(active.Id, null, "Archive")).Value!;
        await content.CreateNoteAsync(active.Id, archive.Id, new ProjectNoteDraft("Old note", "inside"));
        await content.CreateLinkAsync(active.Id, archive.Id, new ProjectLinkDraft("Old file", ProjectLinkType.File, @"C:\Work\old.txt"));
        var nested = await content.TrashAsync(new ContentRef(ContentKind.Folder, archive.Id));
        Assert.True(nested.Committed);

        // Independent trash entry: a single link.
        var loose = (await content.CreateLinkAsync(active.Id, null, new ProjectLinkDraft("Loose", ProjectLinkType.Web, "https://example.test/loose"))).Value!;
        var independent = await content.TrashAsync(new ContentRef(ContentKind.Link, loose.Id));
        Assert.True(independent.Committed);

        // The trashed Project keeps its own content.
        await content.CreateNoteAsync(trashed.Id, null, new ProjectNoteDraft("Kept", "in trashed project"));
        var trash = await workspace.TrashProjectAsync(trashed.Id);
        Assert.Equal(ProjectTrashOutcome.Trashed, trash.Outcome);

        connections.ClearPools();
        return new Ids(trashed.Id, active.Id, note.Id, nested.Entry!.Id, independent.Entry!.Id);
    }
}
