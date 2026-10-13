using System.ComponentModel;
using System.Diagnostics;
using WorkHub.Core.Backup;
using WorkHub.Core.Startup;
using WorkHub.Infrastructure.Launching;

namespace WorkHub.Infrastructure.Backup;

/// <summary>
/// Read-only discovery and checking of <c>Backups\Daily</c> and <c>Backups\Migrations</c> (Phase 9 §8.1). Listing never
/// opens a file; a check opens one file with an unpooled read-only connection after the pre-open gates and closes it
/// before returning. There is no operation that writes, replaces or deletes anything.
/// </summary>
public sealed class BackupCatalog : IBackupCatalog
{
    private readonly WorkHubPaths _paths;
    private readonly JstCalendar _calendar;
    private readonly IBackupFileOps _files;
    private readonly IProcessStarter _starter;
    private readonly BackupValidator _validator;

    public BackupCatalog(WorkHubPaths paths, IProcessStarter? starter = null)
        : this(paths, JstCalendar.Resolve(), SchemaReference.Frozen, new BackupFileOps(), starter ?? new ProcessStarter())
    {
    }

    internal BackupCatalog(WorkHubPaths paths, JstCalendar calendar, SchemaReference references, IBackupFileOps files, IProcessStarter starter)
    {
        _paths = paths;
        _calendar = calendar;
        _files = files;
        _starter = starter;
        _validator = new BackupValidator(references, files);
        LatestSchemaVersion = references.LatestVersion;
    }

    public int LatestSchemaVersion { get; }

    public Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<BackupEntry>>(() =>
        {
            var daily = List(BackupFolder.Daily, _paths.DailyBackupsDirectory);
            var migrations = List(BackupFolder.Migrations, _paths.MigrationBackupsDirectory);
            return
            [
                .. daily.Where(e => e.Kind == BackupKind.Daily).OrderByDescending(e => e.Taken).ThenByDescending(e => e.FileName, StringComparer.Ordinal),
                .. migrations.Where(e => e.Kind == BackupKind.PreMigration).OrderByDescending(e => e.Taken).ThenByDescending(e => e.FileName, StringComparer.Ordinal),
                .. daily.Concat(migrations).Where(e => e.Kind == BackupKind.Unrecognized).OrderBy(e => e.Folder).ThenBy(e => e.FileName, StringComparer.OrdinalIgnoreCase),
            ];
        }, cancellationToken);

    public Task<BackupCheckResult> CheckAsync(BackupEntry entry, BackupCheckLevel level, CancellationToken cancellationToken = default)
    {
        if (entry.Kind == BackupKind.Unrecognized)
            return Task.FromResult(new BackupCheckResult(BackupStatus.Unrecognized, Detail: "not a WorkHub backup name; not opened"));

        // Migration backups are checked against their own version (the name's "from" version).
        return Task.Run(() => _validator.Check(PathOf(entry), level, entry.NameVersion, ValidationRules.Daily), cancellationToken);
    }

    public string? OpenFolder()
    {
        var folder = _paths.BackupsDirectory;
        if (!_files.DirectoryExists(folder))
            return "The Backups folder does not exist yet.";

        try
        {
            _starter.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"The Backups folder could not be opened ({ex.Message}).";
        }
    }

    private string PathOf(BackupEntry entry) =>
        Path.Combine(entry.Folder == BackupFolder.Daily ? _paths.DailyBackupsDirectory : _paths.MigrationBackupsDirectory, entry.FileName);

    private List<BackupEntry> List(BackupFolder folder, string directory)
    {
        if (!_files.DirectoryExists(directory))
            return [];

        var entries = new List<BackupEntry>();
        foreach (var name in _files.EnumerateFileNames(directory))
        {
            var path = Path.Combine(directory, name);
            if (folder == BackupFolder.Daily && string.Equals(path, _paths.DailyBackupLockFile, StringComparison.OrdinalIgnoreCase))
                continue; // the feature's own lock file

            long size;
            try
            {
                size = _files.GetLength(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                size = -1;
            }

            if (folder == BackupFolder.Daily && DailyBackupPolicy.TryParse(name, out var daily))
            {
                var taken = new DateTimeOffset(daily.Stamp, JstCalendar.FixedOffset);
                entries.Add(new BackupEntry(name, folder, BackupKind.Daily, size, taken, daily.Version));
            }
            else if (folder == BackupFolder.Migrations && DailyBackupPolicy.TryParseMigration(name, out var from, out var to, out var takenUtc))
            {
                entries.Add(new BackupEntry(name, folder, BackupKind.PreMigration, size, _calendar.ToTokyo(takenUtc), from, to));
            }
            else
            {
                entries.Add(new BackupEntry(name, folder, BackupKind.Unrecognized, size, null, null));
            }
        }

        return entries;
    }
}
