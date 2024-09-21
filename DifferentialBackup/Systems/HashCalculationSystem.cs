using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;
using System.Collections.Generic;

namespace DifferentialBackup.Systems
{
    public class HashCalculationSystem : ISystem
    {
        private readonly Dictionary<string, string> _fileHashes;

        public HashCalculationSystem(Dictionary<string, string> fileHashes)
        {
            _fileHashes = fileHashes;
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            var filePathComponent = storage.GetComponent<FilePathComponent>(entity);
            if (filePathComponent == null)
                return Result.Fail("FilePathComponent missing.");

            var filePath = filePathComponent.FilePath;

            var currentHash = HashUtility.ComputeSHA256(filePath);
            if (currentHash == null)
                return Result.Fail("Failed to compute hash.");

            _fileHashes.TryGetValue(filePath, out var previousHash);

            var fileHashComponent = new FileHashComponent
            {
                CurrentHash = currentHash,
                PreviousHash = previousHash
            };

            storage.SetComponent(entity, fileHashComponent);

            return Result.Success();
        }
    }
}
