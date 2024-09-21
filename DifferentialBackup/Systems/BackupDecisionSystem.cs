using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using System;
using System.Collections.Generic;

namespace DifferentialBackup.Systems
{
    public class BackupDecisionSystem : ISystem
    {
        private readonly Dictionary<string, string> _fileHashes;
        private readonly HashSet<DateTime> _backupDates;

        public BackupDecisionSystem(Dictionary<string, string> fileHashes, HashSet<DateTime> backupDates)
        {
            _fileHashes = fileHashes;
            _backupDates = backupDates;
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            var fileHashComponent = storage.GetComponent<FileHashComponent>(entity);
            var filePathComponent = storage.GetComponent<FilePathComponent>(entity);

            if (fileHashComponent == null || filePathComponent == null)
                return Result.Fail("Required components missing.");

            if (fileHashComponent.PreviousHash != fileHashComponent.CurrentHash)
            {
                // Mark the entity for backup by adding a BackupDateComponent
                var backupDate = DateTime.UtcNow;
                storage.SetComponent(entity, new BackupDateComponent { BackupDate = backupDate });

                // Update the hash in the fileHashes
                _fileHashes[filePathComponent.FilePath] = fileHashComponent.CurrentHash;

                // Add the backup date
                _backupDates.Add(backupDate);
            }

            return Result.Success();
        }
    }
}
