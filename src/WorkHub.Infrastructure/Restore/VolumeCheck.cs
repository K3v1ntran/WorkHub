using WorkHub.Core.Restore;
using WorkHub.Core.Startup;

namespace WorkHub.Infrastructure.Restore;

/// <param name="FileSystem">From <c>GetVolumeInformationByHandleW</c>.</param>
/// <param name="VolumeSerial">From <c>GetVolumeInformationByHandleW</c>.</param>
/// <param name="FileSerial">From <c>GetFileInformationByHandle</c> (must equal <paramref name="VolumeSerial"/>).</param>
/// <param name="DriveType">Drive type of the root of the handle's final path.</param>
internal sealed record VolumeFacts(string FileSystem, string VolumeSerial, string FileSerial, uint DriveType, bool IsReparsePoint);

/// <summary>Seam for volume facts (tests describe FAT32, removable or remote volumes that cannot be created here).</summary>
internal interface IVolumeProbe
{
    VolumeFacts Describe(string path);
}

internal sealed class VolumeProbe : IVolumeProbe
{
    public VolumeFacts Describe(string path)
    {
        var reparse = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        using var handle = NativeFiles.OpenForAttributes(path);
        var (fileSystem, serial) = NativeFiles.Volume(handle);
        return new VolumeFacts(fileSystem, serial, NativeFiles.FileInformationSerial(handle), NativeFiles.FinalRootDriveType(handle), reparse);
    }
}

/// <param name="Message">User text (no absolute paths); null when supported.</param>
/// <param name="LogDetail">Raw values for the log.</param>
internal sealed record VolumeCheckResult(FileSystemFacts? Facts, string? Message, string? LogDetail)
{
    public bool IsSupported => Message is null;
}

/// <summary>
/// §11: Restore and Put back are supported only when the data folder, <c>Data\Restore</c>, the operation folder and every
/// database file involved are on one local, fixed NTFS volume, with no reparse point anywhere. Checked at Q2, X0 and U0.
/// </summary>
internal sealed class VolumeCheck(WorkHubPaths paths, IVolumeProbe probe)
{
    public const string RequiredFileSystem = "NTFS";

    public VolumeCheckResult Check(string? op)
    {
        var targets = new List<string> { paths.Root, paths.DataDirectory, paths.RestoreDirectory };
        if (op is not null)
        {
            var opDir = paths.RestoreOperationDirectory(op);
            targets.Add(opDir);
            foreach (var sub in new[] { RestoreNames.Previous, RestoreNames.Restored, RestoreNames.AsFound, RestoreNames.Candidate })
                targets.Add(Path.Combine(opDir, sub));
        }

        targets.Add(paths.DatabaseFile);
        targets.AddRange(RestoreNames.DatabaseSet.Skip(1).Select(n => Path.Combine(paths.DataDirectory, n)));

        string? serial = null;
        FileSystemFacts? facts = null;
        foreach (var target in targets.Where(t => File.Exists(t) || Directory.Exists(t)))
        {
            VolumeFacts volume;
            try
            {
                volume = probe.Describe(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refuse("Restore could not read the properties of the WorkHub data folder.", $"{Path.GetFileName(target)}: {ex.GetType().Name}");
            }

            if (volume.IsReparsePoint)
                return Refuse(
                    $"Restore needs the WorkHub data folder on a local NTFS drive. {Describe(target)} is a link to another folder.",
                    $"reparse point: {Path.GetFileName(target)}");
            if (!string.Equals(volume.FileSystem, RequiredFileSystem, StringComparison.Ordinal))
                return Refuse(
                    $"Restore needs the WorkHub data folder on a local NTFS drive. This folder is on {Name(volume.FileSystem)} ({DriveTypeName(volume.DriveType)}).",
                    $"file system '{volume.FileSystem}', drive type {volume.DriveType}");
            if (volume.DriveType != NativeFiles.DriveFixed)
                return Refuse(
                    $"Restore needs the WorkHub data folder on a local NTFS drive. This folder is on {Name(volume.FileSystem)} ({DriveTypeName(volume.DriveType)}).",
                    $"drive type {volume.DriveType}");
            if (!string.Equals(volume.VolumeSerial, volume.FileSerial, StringComparison.Ordinal) || (serial is not null && !string.Equals(serial, volume.VolumeSerial, StringComparison.Ordinal)))
                return Refuse(
                    "Restore needs the WorkHub data folder on a local NTFS drive. The restore files are on different volumes.",
                    "volume serials differ");

            serial ??= volume.VolumeSerial;
            facts ??= new FileSystemFacts(volume.FileSystem, volume.VolumeSerial);
        }

        return facts is null
            ? Refuse("Restore could not read the properties of the WorkHub data folder.", "no target exists")
            : new VolumeCheckResult(facts, null, $"fs {facts.Name}, fixed, one volume");
    }

    private string Describe(string target)
    {
        if (string.Equals(target, paths.DataDirectory, StringComparison.OrdinalIgnoreCase))
            return "Data";
        if (string.Equals(target, paths.RestoreDirectory, StringComparison.OrdinalIgnoreCase))
            return @"Data\Restore";
        if (string.Equals(target, paths.Root, StringComparison.OrdinalIgnoreCase))
            return "The data folder";
        return $"'{Path.GetFileName(target)}'";
    }

    private static string Name(string fileSystem) => string.IsNullOrEmpty(fileSystem) ? "an unknown file system" : fileSystem;

    private static string DriveTypeName(uint type) => type switch
    {
        2 => "removable drive",
        3 => "local drive",
        4 => "network drive",
        5 => "CD/DVD drive",
        6 => "RAM disk",
        _ => "unknown drive type",
    };

    private static VolumeCheckResult Refuse(string message, string detail) => new(null, message, detail);
}
