using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DifferentialBackup.Utilities;

/// <summary>Resolves ordinary aliases for validation; run identity is provided by tags.</summary>
public static class DirectoryAlias
{
    public static string Resolve(string path)
    {
        var full = DirectoryPath.Normalize(path);
        var suffix = new Stack<string>();
        while (!Directory.Exists(full))
        {
            if (File.Exists(full)) throw new IOException($"Expected a directory: '{full}'.");
            suffix.Push(Path.GetFileName(full));
            full = Path.GetDirectoryName(full) ?? throw new DirectoryNotFoundException(path);
        }
        string resolved;
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFile(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) throw Error(full);
            var buffer = new StringBuilder(32768);
            var count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (count == 0 || count >= buffer.Capacity) throw Error(full);
            resolved = buffer.ToString();
            if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) resolved = @"\\" + resolved[8..];
            else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        }
        else
        {
            var pointer = OperatingSystem.IsMacOS() ? MacRealPath(full, IntPtr.Zero) : UnixRealPath(full, IntPtr.Zero);
            if (pointer == IntPtr.Zero) throw Error(full);
            try { resolved = Marshal.PtrToStringUTF8(pointer) ?? throw new IOException("Unable to decode resolved directory."); }
            finally { if (OperatingSystem.IsMacOS()) MacFree(pointer); else UnixFree(pointer); }
        }
        foreach (var component in suffix) resolved = Path.Combine(resolved, component);
        return DirectoryPath.Normalize(resolved);
    }

    public static void RequireSeparate(string source, string destination)
    {
        var left = Resolve(source);
        var right = Resolve(destination);
        if (DirectoryPath.Contains(left, right) || DirectoryPath.Contains(right, left))
            throw new InvalidDataException("Source and backup destinations overlap.");
    }

    private static IOException Error(string path) => new($"Cannot resolve directory '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)] private static extern IntPtr UnixRealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "free")] private static extern void UnixFree(IntPtr buffer);
    [DllImport("libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)] private static extern IntPtr MacRealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
    [DllImport("libSystem.B.dylib", EntryPoint = "free")] private static extern void MacFree(IntPtr buffer);
}
