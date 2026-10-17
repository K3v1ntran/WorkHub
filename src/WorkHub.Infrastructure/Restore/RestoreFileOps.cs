using System.Security.Cryptography;
using WorkHub.Core.Restore;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Backup;

namespace WorkHub.Infrastructure.Restore;

/// <summary>
/// Every file operation of Restore and Put back (Phase 10 §17 seam). Production uses <see cref="RestoreFileOps"/>; tests
/// wrap it to inject failures (sharing violations, moves that fail) and to record what was touched.
/// </summary>
internal interface IRestoreFileOps
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    FileAttributes GetAttributes(string path);

    long GetLength(string path);

    string FileId(string path);

    /// <summary>Entries directly inside <paramref name="directory"/> (files with IDs; directories as <see cref="FileFact.DirectoryId"/>).</summary>
    IReadOnlyList<FileFact> List(string directory);

    IReadOnlyList<string> DirectoryNames(string directory);

    string Sha256(string path);

    void CreateDirectory(string path);

    /// <summary>Same-volume rename. Never overwrites.</summary>
    void Move(string source, string destination);

    /// <summary>Byte copy to a new file (never overwrites).</summary>
    void Copy(string source, string destination);

    void Delete(string path);

    /// <summary>Deletes an empty directory only.</summary>
    void DeleteEmptyDirectory(string path);

    /// <summary>Writes a new file through to disk (journal temp file).</summary>
    void WriteThrough(string path, byte[] content);

    /// <summary>Replaces <paramref name="destination"/> by renaming over it (journal publication).</summary>
    void Replace(string source, string destination);

    byte[] ReadShared(string path);
}

/// <summary>
/// Real file operations. The metadata rule (§5.1) is enforced here: every create, write, rename or delete under
/// <c>Data\Restore\</c> requires the caller to hold the Restore lock; otherwise <see cref="RestoreLockNotHeldException"/>.
/// </summary>
internal sealed class RestoreFileOps(WorkHubPaths paths, Func<bool> holdsRestoreLock) : IRestoreFileOps
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public long GetLength(string path) => new FileInfo(path).Length;

    public string FileId(string path) => NativeFiles.FileId(path);

    public IReadOnlyList<FileFact> List(string directory)
    {
        var facts = new List<FileFact>();
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                facts.Add(new FileFact(entry.Name, 0, FileFact.DirectoryId));
            else
                facts.Add(new FileFact(entry.Name, ((FileInfo)entry).Length, NativeFiles.FileId(entry.FullName)));
        }

        return facts;
    }

    public IReadOnlyList<string> DirectoryNames(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateDirectories(directory).Select(d => Path.GetFileName(d)).ToList() : [];

    public string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void CreateDirectory(string path)
    {
        Guard(path);
        Directory.CreateDirectory(path);
    }

    public void Move(string source, string destination)
    {
        Guard(source);
        Guard(destination);
        File.Move(source, destination, overwrite: false);
    }

    public void Copy(string source, string destination)
    {
        Guard(destination);
        File.Copy(source, destination, overwrite: false);
    }

    public void Delete(string path)
    {
        Guard(path);
        File.Delete(path);
    }

    public void DeleteEmptyDirectory(string path)
    {
        Guard(path);
        Directory.Delete(path, recursive: false);
    }

    public void WriteThrough(string path, byte[] content)
    {
        Guard(path);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    public void Replace(string source, string destination)
    {
        Guard(source);
        Guard(destination);
        File.Move(source, destination, overwrite: true);
    }

    public byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private void Guard(string path)
    {
        if (IsUnderRestoreDirectory(path) && !holdsRestoreLock())
            throw new RestoreLockNotHeldException(Path.GetFileName(path));
    }

    private bool IsUnderRestoreDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(paths.RestoreDirectory);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A write under <c>Data\Restore</c> was attempted without the Restore lock (a programming error; never caught).</summary>
internal sealed class RestoreLockNotHeldException(string name)
    : InvalidOperationException($"Restore metadata '{name}' may only be changed while the Restore lock is held.");

/// <summary>Classification of file-operation failures used by the restore code.</summary>
internal static class RestoreIo
{
    public static bool IsSharingViolation(Exception ex) => ex is IOException io && IoErrors.IsSharingViolation(io);

    /// <summary>Runs <paramref name="action"/>; a sharing violation is retried (5 × 200 ms by default). Other failures propagate.</summary>
    public static void WithRetries(Action action, int attempts = 5, int delayMs = 200)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < attempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }
}
