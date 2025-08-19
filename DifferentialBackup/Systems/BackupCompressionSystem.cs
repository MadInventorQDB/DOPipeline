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
                        if (!File.Exists(zipPath))
                        {
                            ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Optimal, false);
                            Directory.Delete(folder, true);
                        }
                    }

                    return Result.Success();
                }
                catch (Exception ex)
                {
                    return Result.Fail($"Failed to compress backups: {ex.Message}");
                }
            }
        }
    }
}
