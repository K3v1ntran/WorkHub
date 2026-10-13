using WorkHub.Core.Backup;

namespace WorkHub.Infrastructure.Backup;

/// <summary>
/// Pre-open gates (Phase 9 §4.3). Run on the file system only, BEFORE any SQLite open, so a file that would make SQLite
/// create <c>-wal</c>/<c>-shm</c> files (a WAL-mode header) or that carries a journal/WAL is never opened.
/// </summary>
internal static class StandaloneGate
{
    public static readonly IReadOnlyList<string> SidecarSuffixes = ["-journal", "-wal", "-shm"];

    private const int HeaderLength = 100;
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    /// <summary>Sidecar files currently next to <paramref name="path"/> (suffixes only).</summary>
    public static IReadOnlyList<string> Sidecars(IBackupFileOps files, string path) =>
        SidecarSuffixes.Where(suffix => files.FileExists(path + suffix)).ToList();

    /// <returns>Null when the file may be opened read-only; otherwise the reason it must not be.</returns>
    public static BackupCheckResult? Check(IBackupFileOps files, string path)
    {
        if (!files.FileExists(path))
            return new BackupCheckResult(BackupStatus.Unreadable, Detail: "file not found");

        var attributes = files.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            return new BackupCheckResult(BackupStatus.Unreadable, Detail: "not a regular file");

        var sidecars = Sidecars(files, path);
        if (sidecars.Count > 0)
            return new BackupCheckResult(BackupStatus.NotStandalone, Detail: $"has {string.Join(", ", sidecars)} file");

        var length = files.GetLength(path);
        if (length < 512 || length % 512 != 0)
            return new BackupCheckResult(BackupStatus.Unreadable, Detail: "incomplete or not a SQLite database");

        var header = files.ReadHeader(path, HeaderLength);
        if (header.Length < HeaderLength || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return new BackupCheckResult(BackupStatus.Unreadable, Detail: "not a SQLite database");

        // Bytes 18/19: file format write/read version. 1 = rollback journal (standalone), 2 = WAL.
        if (header[18] != 1 || header[19] != 1)
            return new BackupCheckResult(BackupStatus.NotStandalone, Detail: header[18] == 2 || header[19] == 2 ? "WAL-mode header" : "unsupported header");

        return null;
    }
}
