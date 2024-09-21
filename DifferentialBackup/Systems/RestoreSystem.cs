using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using System;
using System.IO;

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
                var backupFolder = Path.Combine(_backupDestination, _backupDate.ToString("yyyyMMddHHmmss"));
                if (!Directory.Exists(backupFolder))
                    return Result.Fail("Backup date not found.");

                var files = Directory.GetFiles(backupFolder, "*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(backupFolder, file);
                    var restorePath = Path.Combine(_restoreDestination, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);
                    File.Copy(file, restorePath, true);
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
