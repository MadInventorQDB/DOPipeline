using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using System;
using System.IO;

namespace DifferentialBackup.Systems
{
    public class BackupExecutionSystem : ISystem
    {
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;

        public BackupExecutionSystem(string sourceDirectory, string backupDestination)
        {
            _sourceDirectory = sourceDirectory;
            _backupDestination = backupDestination;
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            var backupDateComponent = storage.GetComponent<BackupDateComponent>(entity);
            var filePathComponent = storage.GetComponent<FilePathComponent>(entity);

            if (backupDateComponent == null)
            {
                // No backup needed for this entity
                return Result.Success();
            }

            if (filePathComponent == null)
                return Result.Fail("FilePathComponent missing.");

            try
            {
                var relativePath = Path.GetRelativePath(_sourceDirectory, filePathComponent.FilePath);
                var backupFolder = Path.Combine(_backupDestination, backupDateComponent.BackupDate.ToString("yyyyMMddHHmmss"));
                var backupPath = Path.Combine(backupFolder, relativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(filePathComponent.FilePath, backupPath, true);

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to store file version: {ex.Message}");
            }
        }
    }
}
