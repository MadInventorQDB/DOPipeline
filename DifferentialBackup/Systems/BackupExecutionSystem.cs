using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using System;
using System.IO;
using System.IO.Compression;

namespace DifferentialBackup.Systems
{
    public class BackupExecutionSystem : ISystem
    {
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;

        private static readonly object _zipLock = new();

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
                var zipName = backupDateComponent.BackupDate.ToString("yyyyMMddHHmmss") + ".zip";
                var zipPath = Path.Combine(_backupDestination, zipName);

                lock (_zipLock)
                {
                    Directory.CreateDirectory(_backupDestination);
                    using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);

                    var entryName = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                    var existingEntry = archive.GetEntry(entryName);
                    existingEntry?.Delete();
                    archive.CreateEntryFromFile(filePathComponent.FilePath, entryName, CompressionLevel.Optimal);
                }

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to store file version: {ex.Message}");
            }
        }
    }
}
