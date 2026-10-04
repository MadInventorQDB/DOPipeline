using Xunit;

namespace DifferentialBackup.Test.Helpers;

/// <summary>Open handles do not block replacement on every filesystem.</summary>
public sealed class OpenHandleBlocksReplacementFactAttribute : FactAttribute
{
    public OpenHandleBlocksReplacementFactAttribute()
    {
        var root = Path.Combine(Path.GetTempPath(), "replace-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "target");
        var candidate = Path.Combine(root, "candidate");
        try
        {
            File.WriteAllText(target, "old");
            File.WriteAllText(candidate, "new");
            using var held = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                File.Move(candidate, target, true);
                Skip = "This filesystem permits replacing an open target; open-handle restore retry behavior is not applicable.";
            }
            catch (IOException) { }
        }
        finally { Directory.Delete(root, true); }
    }
}
