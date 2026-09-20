using System.Security.Cryptography;
using System.Text.Json;

namespace DifferentialBackup.Utilities;

/// <summary>Reads and validates referenced artifact bytes; makes no workflow decisions.</summary>
public static class BackupIndexReader
{
    public static BackupPartCheckpoint? Read(string directory, BackupManifestPart part)
    {
        // Published v2 and early v3 sets have no separate index reference.
        if (string.IsNullOrEmpty(part.IndexFileName)) return null;
        if (part.IndexFileName != $"part-{part.PartNumber:000000}.index.json")
            throw new InvalidDataException("Unsafe or inconsistent part index name.");
        var path = Path.Combine(directory, part.IndexFileName);
        if (!File.Exists(path) || new FileInfo(path).Length != part.IndexBytes)
            throw new InvalidDataException($"Backup index is missing or incomplete: '{path}'.");
        using var stream = File.OpenRead(path);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), part.IndexSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Backup index checksum is invalid: '{path}'.");
        var index = JsonSerializer.Deserialize<BackupPartCheckpoint>(File.ReadAllText(path));
        if (index == null || index.PartNumber != part.PartNumber || index.ArchiveFileName != part.ArchiveFileName ||
            index.FileCount != part.FileCount || index.SourceBytes != part.SourceBytes || index.ArchiveBytes != part.ArchiveBytes ||
            !string.Equals(index.ArchiveSha256, part.ArchiveSha256, StringComparison.OrdinalIgnoreCase) ||
            index.Files == null || index.Files.Count != index.FileCount || index.Files.Sum(file => file.Length) != index.SourceBytes ||
            index.Fingerprint != part.Fingerprint || BackupFingerprint.ForFiles(index.Files) != index.Fingerprint)
            throw new InvalidDataException($"Backup index descriptor mismatch: '{path}'.");
        return index;
    }
}
