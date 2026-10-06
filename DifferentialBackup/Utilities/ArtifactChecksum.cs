using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using DOPipeline.Logging;

namespace DifferentialBackup.Utilities;

internal static class ArtifactChecksum
{
    public static string ComputeSha256(
        string path,
        IPipelineLogger? logger = null,
        Action<string, long>? bytesRead = null)
    {
        const int bufferSize = 1024 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var archive = Path.GetFileName(path).Contains(".zip", StringComparison.OrdinalIgnoreCase);
        var timer = Stopwatch.StartNew();
        var nextProgress = TimeSpan.FromSeconds(10);
        long total = 0;
        if (archive) Log(logger, $"Backup verification: reading '{path}' ({stream.Length / 1024d / 1024d:0.0} MB).");
        try
        {
            int count;
            while ((count = stream.Read(buffer, 0, bufferSize)) > 0)
            {
                hash.AppendData(buffer, 0, count);
                total += count;
                bytesRead?.Invoke(path, count);
                if (archive && timer.Elapsed >= nextProgress)
                {
                    Log(logger, $"Backup verification: '{Path.GetFileName(path)}', {total / 1024d / 1024d:0.0} / {stream.Length / 1024d / 1024d:0.0} MB.");
                    nextProgress = timer.Elapsed + TimeSpan.FromSeconds(10);
                }
            }
            if (archive) Log(logger, $"Backup verification: read '{Path.GetFileName(path)}' in {timer.Elapsed.TotalSeconds:0.0}s.");
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static void Log(IPipelineLogger? logger, string message)
    {
        try { logger?.Log(message); }
        catch { /* Diagnostics must not invalidate successfully verified data. */ }
    }
}
