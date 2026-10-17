using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WorkHub.Infrastructure.Restore;

/// <summary>
/// Win32 file identity and volume facts by handle (Phase 10 §4, §11). Handles are opened for attribute reads only, with
/// full sharing and <c>FILE_FLAG_BACKUP_SEMANTICS</c> (directories too), so they never block SQLite or other readers.
/// </summary>
internal static class NativeFiles
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    public const uint DriveFixed = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        // FILETIME is two DWORDs (4-byte aligned); a long here would be padded to offset 8 and shift every field.
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, StringBuilder? volumeName, uint volumeNameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystemName, uint fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string rootPath);

    /// <summary>Opens a file or directory for attribute reads. The link itself is opened when it is a reparse point.</summary>
    public static SafeFileHandle OpenForAttributes(string path)
    {
        var handle = CreateFileW(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw error is 2 or 3
                ? new FileNotFoundException("The file or folder does not exist.", Path.GetFileName(path))
                : new IOException($"The file or folder could not be opened (Win32 error {error}).", unchecked((int)0x80070000) | error);
        }

        return handle;
    }

    /// <summary><c>{volume serial:X8}-{file index:X16}</c>.</summary>
    public static string FileId(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("File information could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        return string.Create(CultureInfo.InvariantCulture, $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}");
    }

    public static string FileId(string path)
    {
        using var handle = OpenForAttributes(path);
        return FileId(handle);
    }

    public static (string FileSystem, string Serial) Volume(SafeFileHandle handle)
    {
        var fileSystem = new StringBuilder(64);
        if (!GetVolumeInformationByHandleW(handle, null, 0, out var serial, out _, out _, fileSystem, (uint)fileSystem.Capacity))
            throw new IOException("Volume information could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        return (fileSystem.ToString(), serial.ToString("X8", CultureInfo.InvariantCulture));
    }

    public static string FileInformationSerial(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("File information could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        return info.VolumeSerialNumber.ToString("X8", CultureInfo.InvariantCulture);
    }

    /// <summary>The drive type of the root of the handle's final (resolved) path.</summary>
    public static uint FinalRootDriveType(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new IOException("The final path could not be read.", new Win32Exception(Marshal.GetLastWin32Error()));
        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return 4; // DRIVE_REMOTE
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? 0 : GetDriveTypeW(root);
    }
}
