namespace DifferentialBackup.Test.Helpers;

public sealed class CaseDistinctFilesystemFactAttribute : FactAttribute
{
    public CaseDistinctFilesystemFactAttribute()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CaseCapability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "File"), "probe");
            if (File.Exists(Path.Combine(directory, "file")))
                Skip = "The test filesystem does not support case-distinct names; a capable volume is required.";
        }
        finally { Directory.Delete(directory, true); }
    }
}
