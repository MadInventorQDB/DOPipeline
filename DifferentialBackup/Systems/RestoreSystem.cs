using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Utilities;
using System.IO.Compression;
using System.Text.Json;

namespace DifferentialBackup.Systems
{
    public sealed class RestoreSystem : ISystem
    {
        private readonly string _backupDestination;
        private readonly DateTime _backupDate;
        private readonly string _restoreDestination;

        public RestoreSystem(string backupDestination, DateTime backupDate, string restoreDestination)
        {
            _backupDestination = backupDestination;
            _backupDate = backupDate;
            _restoreDestination = restoreDestination;
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            try
            {
                var timestamp = _backupDate.ToString("yyyyMMddHHmmss");
                var backupSetPath = Path.Combine(_backupDestination, timestamp + ".backup");
                if (Directory.Exists(backupSetPath))
                {
                    RestoreBackupSet(backupSetPath);
                    return Result.Success();
                }

                var legacyArchivePath = Path.Combine(_backupDestination, timestamp + ".zip");
                if (!File.Exists(legacyArchivePath))
                {
                    return Result.Fail("Backup date not found.");
                }

                ExtractArchive(legacyArchivePath);
                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to restore files: {ex.Message}");
            }
        }

        private void RestoreBackupSet(string backupSetPath)
        {
            var manifestPath = Path.Combine(backupSetPath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException("Backup manifest is missing.");
            }

            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.FormatVersion != BackupManifest.CurrentFormatVersion)
            {
                throw new InvalidDataException("Backup manifest format is not supported.");
            }

            foreach (var part in manifest.Parts.OrderBy(part => part.PartNumber))
            {
                if (!string.Equals(
                        Path.GetFileName(part.ArchiveFileName),
                        part.ArchiveFileName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Backup manifest contains an invalid part name.");
                }

                var archivePath = Path.Combine(backupSetPath, part.ArchiveFileName);
                if (!File.Exists(archivePath) || new FileInfo(archivePath).Length != part.ArchiveBytes)
                {
                    throw new InvalidDataException($"Backup part is missing or incomplete: '{part.ArchiveFileName}'.");
                }

                ExtractArchive(archivePath);
            }
        }

        private void ExtractArchive(string archivePath)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var restorePath = GetSafeRestorePath(entry.FullName);
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(restorePath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);
                entry.ExtractToFile(restorePath, overwrite: true);
            }
        }

        private string GetSafeRestorePath(string entryName)
        {
            var restoreRoot = Path.GetFullPath(_restoreDestination)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var relativePath = entryName.Replace('/', Path.DirectorySeparatorChar);
            var restorePath = Path.GetFullPath(Path.Combine(restoreRoot, relativePath));
            if (!restorePath.StartsWith(restoreRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup entry escapes the restore directory: '{entryName}'.");
            }

            return restorePath;
        }
    }
}
