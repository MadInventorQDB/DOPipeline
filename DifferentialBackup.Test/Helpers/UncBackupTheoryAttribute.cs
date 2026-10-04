using Xunit;

namespace DifferentialBackup.Test.Helpers;

public sealed class UncBackupTheoryAttribute : TheoryAttribute
{
    public UncBackupTheoryAttribute()
    {
        var root = Environment.GetEnvironmentVariable("DIFFERENTIALBACKUP_TEST_UNC_ROOT");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(root) ||
            !root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            Skip = "Set DIFFERENTIALBACKUP_TEST_UNC_ROOT to a writable Windows UNC directory to run network integration tests.";
        }
    }
}
