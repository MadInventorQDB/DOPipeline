using System.ComponentModel;
using System.Runtime.InteropServices;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Test.Utilities;

public sealed class ExclusiveOperationLockTests
{
    [Theory]
    [InlineData(95)] // Linux ENOTSUP
    [InlineData(45)] // macOS ENOTSUP
    [InlineData(13)] // EACCES
    [InlineData(11)] // EAGAIN
    public void NativeLockErrorsRejectOwnershipAndReleaseTheOpenHandle(int error)
    {
        var directory = Path.Combine(Path.GetTempPath(), "LockFailure-" + Guid.NewGuid().ToString("N"));
        var anchor = Path.Combine(directory, "anchor");
        try
        {
            var failure = Assert.Throws<InvalidOperationException>(() => ExclusiveOperationLock.Acquire(anchor, handle =>
            {
                Assert.False(handle.IsInvalid);
                Marshal.SetLastPInvokeError(error);
                return -1;
            }));
            var native = Assert.IsType<Win32Exception>(Assert.IsType<IOException>(failure.InnerException).InnerException);
            Assert.Equal(error, native.NativeErrorCode);
            using var acquiredAfterFailure = ExclusiveOperationLock.Acquire(anchor);
            Assert.True(File.Exists(anchor));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
