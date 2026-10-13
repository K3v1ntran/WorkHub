namespace WorkHub.Infrastructure.Backup;

/// <summary>
/// The file-system operations the backup code performs (Phase 9 §10.2 seam). Production uses <see cref="BackupFileOps"/>;
/// tests substitute failures (disk full, sharing violations, reparse points) and record every path touched.
/// SQLite itself opens the database files directly; this seam covers only WorkHub's own file operations.
/// </summary>
internal interface IBackupFileOps
{
    void CreateDirectory(string path);

    bool DirectoryExists(string path);

    /// <summary>File names (not paths) directly inside <paramref name="directory"/>; no recursion.</summary>
    IReadOnlyList<string> EnumerateFileNames(string directory);

    bool FileExists(string path);

    FileAttributes GetAttributes(string path);

    long GetLength(string path);

    /// <summary>Reads up to <paramref name="count"/> bytes from the start without blocking other readers or writers.</summary>
    byte[] ReadHeader(string path, int count);

    /// <summary>Renames within a volume. Never overwrites: throws <see cref="IOException"/> if the target exists.</summary>
    void Move(string source, string destination);

    void Delete(string path);

    /// <summary>Opens <paramref name="path"/> exclusively (created if missing). Throws <see cref="IOException"/> if held.</summary>
    IDisposable OpenExclusive(string path);
}

internal sealed class BackupFileOps : IBackupFileOps
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> EnumerateFileNames(string directory) =>
        Directory.EnumerateFiles(directory).Select(f => Path.GetFileName(f)).ToList();

    public bool FileExists(string path) => File.Exists(path);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public long GetLength(string path) => new FileInfo(path).Length;

    public byte[] ReadHeader(string path, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[count];
        var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return read == count ? buffer : buffer[..read];
    }

    public void Move(string source, string destination) => File.Move(source, destination, overwrite: false);

    public void Delete(string path) => File.Delete(path);

    public IDisposable OpenExclusive(string path) =>
        new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}

/// <summary>Classifies I/O failures without parsing messages.</summary>
internal static class IoErrors
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;

    /// <summary>Another handle holds the file (the daily-backup lock is taken).</summary>
    public static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    public static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull;
}
