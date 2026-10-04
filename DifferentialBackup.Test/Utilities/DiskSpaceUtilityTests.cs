using DifferentialBackup.Utilities;
using Xunit;

namespace DifferentialBackup.Test.Utilities;

public sealed class DiskSpaceUtilityTests
{
    [Fact]
    public void FailedQueryIsAnOutputFailure()
    {
        var missing = Path.Combine(Path.GetTempPath(), "missing-volume-" + Guid.NewGuid().ToString("N"));
        Assert.ThrowsAny<IOException>(() => DiskSpaceUtility.EnsureCapacity(missing, 1, "backup destination"));
    }

    [Fact]
    public void MountedVolumeCapacityUsesTheActualDirectory()
    {
        var root = Environment.GetEnvironmentVariable("DIFFERENTIALBACKUP_TEST_CAPACITY_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        var available = new DriveInfo(root).AvailableFreeSpace;
        var directory = Path.Combine(root, "capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            DiskSpaceUtility.EnsureCapacity(directory, 1, "mounted volume");
            Assert.Throws<IOException>(() => DiskSpaceUtility.EnsureCapacity(directory, available + 1024 * 1024, "mounted volume"));
        }
        finally { Directory.Delete(directory); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureCapacity_LocalDirectoryAcceptsTrailingSeparator(bool trailingSeparator)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        if (trailingSeparator)
        {
            directory += Path.DirectorySeparatorChar;
        }

        DiskSpaceUtility.EnsureCapacity(directory, 1, "local staging");
    }

    [Fact]
    public void EnsureCapacity_InsufficientSpaceRemainsAnOutputFailure()
    {
        var error = Assert.Throws<IOException>(() =>
            DiskSpaceUtility.EnsureCapacity(Path.GetTempPath(), long.MaxValue, "backup destination"));

        Assert.Contains("Not enough free space in backup destination.", error.Message);
        Assert.Contains("Required:", error.Message);
        Assert.Contains("available:", error.Message);
    }
}
