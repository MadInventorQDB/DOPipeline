using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using System;
using System.Linq;
using System.Collections.Generic;

namespace DifferentialBackup.Systems
{
    public class DuplicateDetectionSystem : ISystem
    {
        private List<List<string>>? _cachedGroups;
        private bool _processed;

        public DuplicateDetectionSystem()
        {
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            // This system should only operate once on the root entity. Other entities
            // represent individual files and are ignored here. We identify the root by
            // checking that its FilePath points to a directory rather than a file.
            var pathComponent = storage.GetComponent<FilePathComponent>(entity);
            if (pathComponent == null || File.Exists(pathComponent.FilePath))
            {
                return Result.Success();
            }

            try
            {
                if (!_processed)
                {
                    var files = storage.GetAllEntities()
                        .Where(e => storage.HasComponent<FileHashComponent>(e))
                        .Select(e => new
                        {
                            Path = storage.GetComponent<FilePathComponent>(e)?.FilePath,
                            Hash = storage.GetComponent<FileHashComponent>(e)?.CurrentHash
                        })
                        .Where(x => x.Path != null && x.Hash != null)
                        .ToList();

                    _cachedGroups = files
                        .GroupBy(f => f.Hash)
                        .Where(g => g.Count() > 1)
                        .Select(g => g.Select(f => f.Path!).ToList())
                        .ToList();

                    _processed = true;
                }

                storage.SetComponent(entity, new DuplicateFilesComponent { Groups = _cachedGroups ?? new List<List<string>>() });
                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to detect duplicates: {ex.Message}");
            }
        }
    }
}
