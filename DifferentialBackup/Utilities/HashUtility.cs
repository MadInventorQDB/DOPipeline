using System;
using System.IO;
using System.Security.Cryptography;

namespace DifferentialBackup.Utilities
{
    public sealed record HashAttempt(
        bool Succeeded,
        string? Hash,
        long? Length,
        Exception? Exception)
    {
        public static HashAttempt Success(string hash, long length) => new(true, hash, length, null);
        public static HashAttempt Failure(Exception exception) => new(false, null, null, exception);
    }

    public static class HashUtility
    {
        public static HashAttempt TryComputeSHA256(string filePath)
        {
            try
            {
                using var sha256 = SHA256.Create();
                using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 128,
                    FileOptions.SequentialScan);
                var hash = sha256.ComputeHash(stream);
                return HashAttempt.Success(
                    BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(),
                    stream.Length);
            }
            catch (Exception ex)
            {
                return HashAttempt.Failure(ex);
            }
        }

        public static string? ComputeSHA256(string filePath)
        {
            return TryComputeSHA256(filePath).Hash;
        }
    }
}
