using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using System.IO;

namespace DifferentialBackup.Systems
{
    public class FileDiscoverySystem : ISystem
    {
        private readonly string _sourceDirectory;

        public FileDiscoverySystem(string sourceDirectory)
        {
            _sourceDirectory = sourceDirectory;
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            // Discover files in the source directory
            var filePaths = Directory.GetFiles(_sourceDirectory, "*", SearchOption.AllDirectories);

            foreach (var filePath in filePaths)
            {
                var fileEntity = new Entity();
                storage.SetComponent(fileEntity, new FilePathComponent { FilePath = filePath });
            }

            return Result.Success();
        }
    }
}
