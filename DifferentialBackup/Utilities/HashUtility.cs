using System;
using System.IO;
using System.Security.Cryptography;

namespace DifferentialBackup.Utilities
{
    public static class HashUtility
    {
        public static string? ComputeSHA256(string filePath)
        {
            try
            {
                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
            catch
            {
                return null;
            }
        }
    }
}
