using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DifferentialBackup.Utilities;

public static class DiskSpaceUtility
{
    public static void EnsureCapacity(string path, long requiredBytes, string label)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        var directory = Path.GetFullPath(path);
        ulong availableBytes;
        if (OperatingSystem.IsWindows())
        {
            // DriveInfo rejects UNC roots. Query the actual directory so network
            // shares, mapped drives and mounted volumes use the caller's quota.
            if (!Path.EndsInDirectorySeparator(directory))
            {
                directory += Path.DirectorySeparatorChar;
            }
            if (!GetDiskFreeSpaceEx(directory, out availableBytes, out _, out _))
            {
                var error = new Win32Exception(Marshal.GetLastPInvokeError());
                throw new IOException($"Could not query free space for '{directory}': {error.Message}", error);
            }
        }
        else
        {
            var drive = new DriveInfo(directory);
            availableBytes = checked((ulong)drive.AvailableFreeSpace);
        }

        if (availableBytes < (ulong)requiredBytes)
        {
            throw new IOException(
                $"Not enough free space in {label}. " +
                $"Required: {requiredBytes / 1024d / 1024d:0.0} MB; " +
                $"available: {availableBytes / 1024d / 1024d:0.0} MB.");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directory,
        out ulong freeBytesAvailable,
        out ulong totalBytes,
        out ulong totalFreeBytes);
}
