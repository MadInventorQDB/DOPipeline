using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace DifferentialBackup.Systems
{
    public class BackupCompressionSystem : ISystem
    {
        private readonly string _backupDestination;
        private int _executed;

        public BackupCompressionSystem(string backupDestination)
        {
            _backupDestination = backupDestination;
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            if (Interlocked.Exchange(ref _executed, 1) == 1)
            {
                return Result.Success();
            }

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
