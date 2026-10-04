using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DifferentialBackup.Utilities;

/// <summary>Ownership is represented by an open handle, never by lock-file existence.</summary>
public sealed class ExclusiveOperationLock : IDisposable
{
    private readonly FileStream _stream;

    private ExclusiveOperationLock(FileStream stream)
    {
        _stream = stream;
    }

    public static ExclusiveOperationLock Acquire(string path) => Acquire(path, null);

    internal static ExclusiveOperationLock Acquire(string path, Func<SafeFileHandle, int>? nativeLock)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("An operation lock anchor must be a regular file.");
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            try
            {
                if (!OperatingSystem.IsWindows() || nativeLock != null)
                {
                    var result = nativeLock != null ? nativeLock(stream.SafeFileHandle) : AcquireNative(stream.SafeFileHandle);
                    if (result != 0)
                        throw new IOException($"Exclusive locking failed for '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
                }
                return new ExclusiveOperationLock(stream);
            }
            catch { stream.Dispose(); throw; }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Cannot acquire exclusive operation ownership for '{path}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"Cannot acquire operation lock '{path}'.", ex);
        }
    }

    public void Dispose() => _stream.Dispose();

    private static int AcquireNative(SafeFileHandle handle)
    {
        var descriptor = checked((int)handle.DangerousGetHandle());
        return OperatingSystem.IsMacOS() ? MacFlock(descriptor, 2 | 4) : UnixFlock(descriptor, 2 | 4);
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int UnixFlock(int descriptor, int operation);
    [DllImport("libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)] private static extern int MacFlock(int descriptor, int operation);
}
