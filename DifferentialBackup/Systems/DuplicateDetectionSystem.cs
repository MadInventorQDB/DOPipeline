using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;

namespace DifferentialBackup.Systems;

/// <summary>Builds duplicate groups from explicit file/hash component rows.</summary>
public sealed class DuplicateDetectionSystem : IEntitySetSystem
{
    public Result Execute(IReadOnlyList<Entity> entities, IComponentStorage storage)
    {
        var files = storage.Query<FilePathComponent, FileHashComponent>()
            .Select(entity => new
            {
                Path = storage.GetComponent<FilePathComponent>(entity)!.FilePath,
                Hash = storage.GetComponent<FileHashComponent>(entity)!.CurrentHash
            })
            .Where(value => !string.IsNullOrWhiteSpace(value.Path) && !string.IsNullOrWhiteSpace(value.Hash))
            .ToList();

        var groups = files
            .GroupBy(value => value.Hash, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Select(value => value.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();

        var roots = storage.Query<DirectoryWorkComponent>()
            .Where(entity => storage.GetComponent<DirectoryWorkComponent>(entity)?.IsRoot == true)
            .ToList();
        foreach (var root in roots)
        {
            storage.SetComponent(root, new DuplicateFilesComponent { Groups = groups });
        }

        return Result.Success();
    }

    public Result Execute(Entity entity, IComponentStorage storage) =>
        Execute(storage.GetAllEntities(), storage);
}
