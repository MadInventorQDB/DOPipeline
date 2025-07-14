using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using System;
using System.IO;
using System.IO.Compression;

namespace DifferentialBackup.Systems
{
    public class RestoreSystem : ISystem
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
                var zipName = _backupDate.ToString("yyyyMMddHHmmss") + ".zip";
                var zipPath = Path.Combine(_backupDestination, zipName);
                if (!File.Exists(zipPath))
                    return Result.Fail("Backup date not found.");

                using var archive = ZipFile.OpenRead(zipPath);
                foreach (var entry in archive.Entries)
                {
                    var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var restorePath = Path.Combine(_restoreDestination, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);
                    entry.ExtractToFile(restorePath, true);
                }

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to restore files: {ex.Message}");
            }
        }
    }
}
