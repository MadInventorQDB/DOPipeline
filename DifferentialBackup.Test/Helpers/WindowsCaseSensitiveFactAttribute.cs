using System.Diagnostics;

namespace DifferentialBackup.Test.Helpers;

public sealed class WindowsCaseSensitiveFactAttribute : FactAttribute
{
    public WindowsCaseSensitiveFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) { Skip = "Requires Windows per-directory case policies."; return; }
        var path = Path.Combine(Path.GetTempPath(), "CasePolicy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            if (!Enable(path)) Skip = "The test account/filesystem cannot enable per-directory case sensitivity.";
        }
        finally { Directory.Delete(path, false); }
    }

    public static bool Enable(string path)
    {
        var start = new ProcessStartInfo("fsutil.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "file", "setCaseSensitiveInfo", path, "enable" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); return false; }
        return process.ExitCode == 0;
    }
}
