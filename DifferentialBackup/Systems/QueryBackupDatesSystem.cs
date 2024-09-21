using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DifferentialBackup.Systems
{
    public class QueryBackupDatesSystem : ISystem
    {
        private readonly HashSet<DateTime> _backupDates;

        public QueryBackupDatesSystem(HashSet<DateTime> backupDates)
        {
            _backupDates = backupDates;
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            try
            {
                var backupDates = _backupDates
                    .OrderByDescending(d => d)
                    .ToList();

                storage.SetComponent(entity, new BackupDatesComponent { Dates = backupDates });

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to query backup dates: {ex.Message}");
            }
        }
    }
}
