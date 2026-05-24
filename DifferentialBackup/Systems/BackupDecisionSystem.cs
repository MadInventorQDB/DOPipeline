using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace DifferentialBackup.Systems
{
    public class BackupDecisionSystem : ISystem, IExecutionScopedSystem
    {
        private readonly ConcurrentDictionary<string, string> _fileHashes;
        private readonly HashSet<DateTime> _backupDates;
        private readonly BackupRunState? _backupRunState;
        private readonly object _backupDateLock = new object();
        private DateTime? _currentBackupDate;

        public BackupDecisionSystem(
            ConcurrentDictionary<string, string> fileHashes,
            HashSet<DateTime> backupDates,
            BackupRunState? backupRunState = null)
        {
            _fileHashes = fileHashes;
            _backupDates = backupDates;
            _backupRunState = backupRunState;
        }

        public Result BeginExecution(IEnumerable<Entity> entities, IComponentStorage storage)
        {
            lock (_backupDateLock)
            {
                _backupRunState?.BeginRun();
                _currentBackupDate = null;
            }

            return Result.Success();
        }

        public Result EndExecution(IEnumerable<Entity> entities, IComponentStorage storage)
        {
            return Result.Success();
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
                var backupDate = GetBackupDateForCurrentExecution();
                storage.SetComponent(entity, new BackupDateComponent { BackupDate = backupDate });

                // Update the hash in the fileHashes
                // Hashes are updated after a file is successfully written to the backup archive.
            }

            return Result.Success();
        }

        private DateTime GetBackupDateForCurrentExecution()
        {
            lock (_backupDateLock)
            {
                if (_currentBackupDate == null)
                {
                    _currentBackupDate = _backupRunState?.GetOrCreateBackupDate() ?? TruncateToSecond(DateTime.UtcNow);
                }

                return _currentBackupDate.Value;
            }
        }

        private static DateTime TruncateToSecond(DateTime value)
        {
            return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
        }
    }
}
