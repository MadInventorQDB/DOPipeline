using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DifferentialBackup.Components;
using DOPipeline.Logging;
using DOPipeline.Utilities;
using System.Linq;
using System;

namespace DifferentialBackup.Systems
{
    public class DuplicateLoggingSystem : ISystem
    {
        private readonly IPipelineLogger _logger;
        private readonly object _lock = new();
        private bool _logged;

        public DuplicateLoggingSystem(IPipelineLogger logger)
        {
            _logger = logger;
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            var dupComponent = storage.GetComponent<DuplicateFilesComponent>(entity);
            if (dupComponent == null)
            {
                return Result.Success();
            }

            lock (_lock)
            {
                if (_logged)
                {
                    return Result.Success();
                }

                try
                {
                    if (dupComponent.Groups.Any())
                    {
                        foreach (var group in dupComponent.Groups)
                        {
                            _logger.Log($"Duplicate files: {string.Join(", ", group)}");
                        }
                    }
                    else
                    {
                        _logger.Log("No duplicate files found.");
                    }
                    _logged = true;
                    return Result.Success();
                }
                catch (Exception ex)
                {
                    return Result.Fail($"Failed to log duplicates: {ex.Message}");
                }
            }
        }
    }
}
