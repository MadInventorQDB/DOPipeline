using Xunit;

namespace DifferentialBackup.Test.Helpers;

public sealed class NasMappedBackupFactAttribute : FactAttribute
{
    public NasMappedBackupFactAttribute()
    {
        var unc = Environment.GetEnvironmentVariable("DIFFERENTIALBACKUP_TEST_UNC_ROOT");
        var mapped = Environment.GetEnvironmentVariable("DIFFERENTIALBACKUP_TEST_MAPPED_ROOT");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(unc) || !unc.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(mapped) || !Path.IsPathFullyQualified(mapped))
            Skip = "Set writable UNC and mapped-root integration directories referring to the same NAS directory.";
    }
}
