using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using System;
using System.IO;
using System.IO.Compression;

namespace DifferentialBackup.Systems
{
    public class BackupCompressionSystem : ISystem
    {
        private readonly string _backupDestination;
        private static readonly object _lock = new();

        public BackupCompressionSystem(string backupDestination)
        {
            _backupDestination = backupDestination;
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            // Multiple entities may trigger this system concurrently due to the pipeline's
            // Parallel.ForEach execution model. To avoid racing or double-processing we
            // synchronize compression so that only one thread performs the work at a time.
            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(_backupDestination))
                        return Result.Success();

                    var folders = Directory.GetDirectories(_backupDestination);
                    foreach (var folder in folders)
                    {
                        var zipPath = folder + ".zip";
                        CompressFolder(folder, zipPath);
                        Directory.Delete(folder, true);
                    }

                    return Result.Success();
                }
                catch (Exception ex)
                {
                    return Result.Fail($"Failed to compress backups: {ex.Message}");
                }
            }
        }

        private static void CompressFolder(string folder, string zipPath)
        {
            if (!File.Exists(zipPath))
            {
                var tempZipPath = zipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

                try
                {
                    ZipFile.CreateFromDirectory(folder, tempZipPath, CompressionLevel.Optimal, false);
                    File.Move(tempZipPath, zipPath);
                }
                finally
                {
                    if (File.Exists(tempZipPath))
                    {
                        File.Delete(tempZipPath);
                    }
                }

                return;
            }

            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
            foreach (var filePath in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                var entryName = Path.GetRelativePath(folder, filePath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');

                archive.GetEntry(entryName)?.Delete();
                archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
            }
        }
    }
}
