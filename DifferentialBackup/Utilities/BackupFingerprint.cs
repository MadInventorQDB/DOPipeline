using DifferentialBackup.Components;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DifferentialBackup.Utilities
{
    internal static class BackupFingerprint
    {
        public static string ForFiles(IEnumerable<BackupPartFile> files)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in files)
            {
                AppendString(hash, file.EntryName);
                AppendString(hash, file.Hash);
                AppendInt64(hash, file.Length);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        public static string ForParts(IEnumerable<BackupPartComponent> parts)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var part in parts.OrderBy(part => part.PartNumber))
            {
                AppendInt64(hash, part.PartNumber);
                AppendString(hash, part.Fingerprint);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static void AppendString(IncrementalHash hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            AppendInt64(hash, bytes.Length);
            hash.AppendData(bytes);
        }

        private static void AppendInt64(IncrementalHash hash, long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            hash.AppendData(bytes);
        }
    }
}
