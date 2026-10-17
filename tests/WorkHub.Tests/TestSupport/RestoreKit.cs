using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using WorkHub.Core.Backup;
using WorkHub.Core.Diagnostics;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;
using WorkHub.Infrastructure.FileSystem;
using WorkHub.Infrastructure.Launching;
using WorkHub.Infrastructure.Persistence;
using WorkHub.Infrastructure.Restore;
using WorkHub.Infrastructure.Settings;

namespace WorkHub.Tests.TestSupport;

/// <summary>
/// Plays the startup screen (§13.3). Waiting-screen buttons are queued in <see cref="Actions"/>; questions are answered by
/// <see cref="OnAsk"/>. Everything shown is recorded.
/// </summary>
internal sealed class ScriptedRestoreUi : IRestoreStartupUi
{
    public ConcurrentQueue<RestoreWaitAction> Actions { get; } = new();

    public ConcurrentQueue<string> Events { get; } = new();

    public ConcurrentQueue<(RestoreWaitKind Kind, RestoreOpSummary Op)> Waits { get; } = new();

    public ConcurrentQueue<(RestoreAskKind Kind, RestoreOpSummary Op)> Asks { get; } = new();

    public ConcurrentQueue<string> Notices { get; } = new();

    public ConcurrentQueue<(string Text, bool CloseBlocked)> Progress { get; } = new();

    public Func<RestoreAskKind, RestoreOpSummary, RestoreAskAnswer> OnAsk { get; set; } = (_, _) => RestoreAskAnswer.NotNow;

    /// <summary>Called on the startup thread each time a waiting screen is shown.</summary>
    public Action<RestoreWaitKind, RestoreOpSummary>? OnWaiting { get; set; }

    public SemaphoreSlim WaitingShown { get; } = new(0);

    public bool GateWaitShown { get; private set; }

    public void ShowGateWait()
    {
        GateWaitShown = true;
        Events.Enqueue("gate-wait");
    }

    public void ShowWaiting(RestoreWaitKind kind, RestoreOpSummary op)
    {
        Waits.Enqueue((kind, op));
        Events.Enqueue($"wait:{kind}:{op.State}:{op.Revision}");
        OnWaiting?.Invoke(kind, op);
        WaitingShown.Release();
    }

    public RestoreWaitAction TakeWaitAction() => Actions.TryDequeue(out var action) ? action : RestoreWaitAction.None;

    public void ShowNotice(string message)
    {
        Notices.Enqueue(message);
        Events.Enqueue("notice");
    }

    public RestoreAskAnswer Ask(RestoreAskKind kind, RestoreOpSummary op)
    {
        Asks.Enqueue((kind, op));
        Events.Enqueue($"ask:{kind}:{op.State}");
        return OnAsk(kind, op);
    }

    public void ShowProgress(string text, bool closeBlocked) => Progress.Enqueue((text, closeBlocked));
}

/// <summary>Real restore file operations with injectable failures; records moves.</summary>
internal sealed class FaultyRestoreFileOps(IRestoreFileOps inner) : IRestoreFileOps
{
    public Action<string, string>? OnMove { get; set; }

    public Action<string>? OnDelete { get; set; }

    public Action<string>? OnWrite { get; set; }

    public ConcurrentQueue<(string From, string To)> Moves { get; } = new();

    public bool FileExists(string path) => inner.FileExists(path);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public FileAttributes GetAttributes(string path) => inner.GetAttributes(path);

    public long GetLength(string path) => inner.GetLength(path);

    public string FileId(string path) => inner.FileId(path);

    public IReadOnlyList<FileFact> List(string directory) => inner.List(directory);

    public IReadOnlyList<string> DirectoryNames(string directory) => inner.DirectoryNames(directory);

    public string Sha256(string path) => inner.Sha256(path);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public void Move(string source, string destination)
    {
        OnMove?.Invoke(source, destination);
        inner.Move(source, destination);
        Moves.Enqueue((source, destination));
    }

    public void Copy(string source, string destination) => inner.Copy(source, destination);

    public void Delete(string path)
    {
        OnDelete?.Invoke(path);
        inner.Delete(path);
    }

    public void DeleteEmptyDirectory(string path) => inner.DeleteEmptyDirectory(path);

    public void WriteThrough(string path, byte[] content)
    {
        OnWrite?.Invoke(path);
        inner.WriteThrough(path, content);
    }

    public void Replace(string source, string destination) => inner.Replace(source, destination);

    public byte[] ReadShared(string path) => inner.ReadShared(path);

    public static IOException SharingViolation() => FaultyBackupFileOps.SharingViolation();
}

/// <summary>Describes volumes that cannot be created on the test machine (FAT32, removable, remote…).</summary>
internal sealed class FakeVolumes(Func<string, VolumeFacts> describe) : IVolumeProbe
{
    public VolumeFacts Describe(string path) => describe(path);

    public static FakeVolumes Ntfs(string serial = "4A78BED1") => new(_ => new VolumeFacts("NTFS", serial, serial, 3, false));
}

internal static class RestoreKit
{
    public static readonly string ProbePath = typeof(RestoreKit).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CrashProbePath").Value!;

    public static RestoreCoordinator Coordinator(
        TempWorkHubRoot root,
        string? token = null,
        Action<RestoreStage>? hook = null,
        Func<InstanceLocks, IRestoreFileOps>? files = null,
        IVolumeProbe? volumes = null,
        IAppLog? log = null,
        int pollMs = 20) =>
        new(root.Paths, log ?? NullAppLog.Instance, token, TimeProvider.System, SchemaReference.Frozen, volumes ?? new VolumeProbe(),
            new RecordingProcessStarter(), files, hook, TimeSpan.FromMilliseconds(pollMs));

    public static Func<InstanceLocks, IRestoreFileOps> Faulty(TempWorkHubRoot root, Action<FaultyRestoreFileOps> configure) => locks =>
    {
        var ops = new FaultyRestoreFileOps(new RestoreFileOps(root.Paths, () => locks.HoldsRestoreLock));
        configure(ops);
        return ops;
    };

    public static RestoreRequester Requester(TempWorkHubRoot root, IVolumeProbe? volumes = null, Action? afterStaging = null, IAppLog? log = null) =>
        new(root.Paths, log ?? NullAppLog.Instance, TimeProvider.System, SchemaReference.Frozen, volumes ?? new VolumeProbe(), null) { AfterStaging = afterStaging };

    /// <summary>The application's startup with the restore protocol in front of the real initializer (seeding on a new database).</summary>
    public static AppInitializer Initializer(TempWorkHubRoot root, IRestoreStartup restore, IAppLog? log = null, IDailyBackup? daily = null) =>
        new(root.Paths, new LocalStorage(root.Paths), new JsonSettingsStore(root.Paths.SettingsFile),
            new DatabaseInitializer(root.Paths, log ?? NullAppLog.Instance, InitialSystemsSeed.Apply), log ?? NullAppLog.Instance, daily, restore);

    /// <summary>Starts WorkHub the way the app does (on a worker thread) and returns the result or the restore stop.</summary>
    public static (InitializationResult? Result, RestoreStartupException? Stop, Exception? Error) Start(TempWorkHubRoot root, RestoreCoordinator coordinator, IRestoreStartupUi ui, IAppLog? log = null)
    {
        try
        {
            return (Initializer(root, coordinator, log).Initialize(ui), null, null);
        }
        catch (RestoreStartupException ex)
        {
            return (null, ex, null);
        }
        catch (Exception ex)
        {
            return (null, null, ex);
        }
    }

    public static BackupEntry DailyEntry(TempWorkHubRoot root, string fileName)
    {
        Assert.True(DailyBackupPolicy.TryParse(fileName, out var name), fileName);
        return new BackupEntry(fileName, BackupFolder.Daily, BackupKind.Daily, new FileInfo(BackupKit.Daily(root, fileName)).Length, null, name.Version);
    }

    /// <summary>Places a valid daily backup of the CURRENT database and returns its entry.</summary>
    public static BackupEntry PlaceDaily(TempWorkHubRoot root, int dayOffset = 0, int ms = 0)
    {
        var name = BackupKit.NameFor(new DateOnly(2026, 10, 1).AddDays(dayOffset), ms);
        BackupKit.PlaceValidBackup(root, name);
        return DailyEntry(root, name);
    }

    /// <summary>A valid daily backup at an older schema version (with a System row), as an Older candidate.</summary>
    public static BackupEntry PlaceOlderDaily(TempWorkHubRoot root, int version, string systemCode = "OLDV0001")
    {
        Directory.CreateDirectory(root.Paths.DailyBackupsDirectory);
        var name = BackupKit.NameFor(new DateOnly(2026, 9, 1), version: version);
        var file = BackupKit.Daily(root, name);
        BackupKit.BuildSchema(file, version);
        using (var connection = BackupKit.Open(file))
            BackupKit.Exec(connection, $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('{systemCode}', 'Older', '{Db.Now}', '{Db.Now}');");
        return DailyEntry(root, name);
    }

    public static async Task<string> RequestAndConfirmAsync(TempWorkHubRoot root, BackupEntry entry, IVolumeProbe? volumes = null)
    {
        var start = await Requester(root, volumes).BeginAsync(entry);
        Assert.True(start.Request is not null, start.Refusal);
        using var request = start.Request!;
        return await request.ConfirmAsync();
    }

    public static string OpDir(TempWorkHubRoot root, string op) => root.Paths.RestoreOperationDirectory(op);

    public static string Previous(TempWorkHubRoot root, string op) => Path.Combine(OpDir(root, op), RestoreNames.Previous);

    public static string Restored(TempWorkHubRoot root, string op) => Path.Combine(OpDir(root, op), RestoreNames.Restored);

    public static string Candidate(TempWorkHubRoot root, string op) => Path.Combine(OpDir(root, op), RestoreNames.Candidate);

    public static RestoreJournal Journal(TempWorkHubRoot root, string op)
    {
        var read = RestoreJournalStore.Parse(op, File.ReadAllBytes(Path.Combine(OpDir(root, op), RestoreNames.Journal)));
        Assert.True(read.IsValid, read.Problem);
        return read.Journal!;
    }

    /// <summary>Publishes a journal as an R holder would (test arrangement of a state).</summary>
    public static void WriteJournal(TempWorkHubRoot root, RestoreJournal journal)
    {
        using var locks = new InstanceLocks(root.Paths);
        Assert.True(locks.TryRestoreLock());
        new RestoreJournalStore(root.Paths, new RestoreFileOps(root.Paths, () => locks.HoldsRestoreLock)).Write(journal);
    }

    public static string[] Ops(TempWorkHubRoot root) =>
        Directory.Exists(root.Paths.RestoreDirectory) ? Directory.GetDirectories(root.Paths.RestoreDirectory).Select(d => Path.GetFileName(d)!).ToArray() : [];

    public static string[] DataSet(TempWorkHubRoot root) =>
        Directory.GetFiles(root.Paths.DataDirectory, "workhub.db*").Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    public static string FileId(string path) => NativeFiles.FileId(path);

    /// <summary>
    /// Clears only this root's pools (the initializer's and the search store's). Never <c>ClearAllPools</c>: other test
    /// classes run in parallel, and clearing their pools disposes connections they are using.
    /// </summary>
    public static void ClearPools(TempWorkHubRoot root)
    {
        new SqliteConnectionFactory(root.Paths.DatabaseFile).ClearPools();
        new SqliteConnectionFactory(root.Paths.DatabaseFile, SqliteWorkspaceSearchStore.BusyTimeoutSeconds).ClearPools();
    }

    public static void InsertSystem(string file, string code)
    {
        using var connection = BackupKit.Open(file);
        BackupKit.Exec(connection, $"INSERT INTO Systems (SystemCode, SystemName, CreatedAt, UpdatedAt) VALUES ('{code}', 'x', '{Db.Now}', '{Db.Now}');");
    }

    /// <summary>Starts the crash probe and returns once it printed READY (it then waits to be killed).</summary>
    public static ProbeChild StartProbe(params string[] args)
    {
        var info = new ProcessStartInfo(ProbePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        var lines = new ConcurrentQueue<string>();
        var ready = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                lines.Enqueue(line);
                if (line == "READY")
                    return true;
            }

            return false;
        });
        if (!ready.Wait(TimeSpan.FromSeconds(90)) || !ready.Result)
        {
            var error = process.HasExited ? process.StandardError.ReadToEnd() : "(still running)";
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"The crash probe did not reach its point ({string.Join(" | ", args)}): {string.Join(" / ", lines)} {error}");
        }

        return new ProbeChild(process, lines);
    }

    internal sealed class ProbeChild(Process process, ConcurrentQueue<string> lines) : IDisposable
    {
        public IReadOnlyCollection<string> Lines => lines;

        public void Kill()
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        public void Dispose()
        {
            Kill();
            process.Dispose();
        }
    }
}

/// <summary>Base for restore tests: one isolated data root; every coordinator made through it is disposed (locks released).</summary>
public abstract class RestoreTestBase : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    internal TempWorkHubRoot Root { get; } = new();

    internal RestoreCoordinator Coordinator(
        string? token = null,
        Action<RestoreStage>? hook = null,
        Func<InstanceLocks, IRestoreFileOps>? files = null,
        IVolumeProbe? volumes = null,
        IAppLog? log = null,
        int pollMs = 20)
    {
        var coordinator = RestoreKit.Coordinator(Root, token, hook, files, volumes, log, pollMs);
        lock (_owned)
            _owned.Add(coordinator);
        return coordinator;
    }

    internal T Own<T>(T disposable) where T : IDisposable
    {
        lock (_owned)
            _owned.Add(disposable);
        return disposable;
    }

    public void Dispose()
    {
        lock (_owned)
        {
            foreach (var owned in Enumerable.Reverse(_owned))
                owned.Dispose();
        }

        RestoreKit.ClearPools(Root);
        Root.Dispose();
    }
}

internal static class RestoreJournalText
{
    /// <summary>The journal exactly as published (records with lists do not compare structurally).</summary>
    public static string Of(TempWorkHubRoot root, string op) =>
        File.ReadAllText(Path.Combine(root.Paths.RestoreOperationDirectory(op), RestoreNames.Journal));
}
