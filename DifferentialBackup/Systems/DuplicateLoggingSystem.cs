using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;

namespace DifferentialBackup.Systems;

public sealed class DuplicateLoggingSystem : ISystem
{
    private readonly IPipelineLogger _logger;

    public DuplicateLoggingSystem(IPipelineLogger logger)
    {
        _logger = logger;
    }

    public Result Execute(Entity entity, IComponentStorage storage)
    {
        var duplicate = storage.GetComponent<DuplicateFilesComponent>(entity);
        if (duplicate == null)
        {
            return Result.Success();
        }

        try
        {
            if (duplicate.Groups.Any())
            {
                foreach (var group in duplicate.Groups)
                {
                    _logger.Log($"Duplicate files: {string.Join(", ", group)}");
                }
            }
            else
            {
                _logger.Log("No duplicate files found.");
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Fail($"Failed to log duplicates: {ex.Message}", ex);
        }
    }
}
