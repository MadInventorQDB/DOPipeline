
using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DifferentialBackup.Components;
using System.Collections.Generic;
using System.IO;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Test.Systems
{
    public class HashCalculationSystemTests
    {
        [Fact]
        public void Execute_ComputesHashAndUpdatesComponent()
        {
            var tempFile = Path.Combine(Path.GetTempPath(), "testfile.txt");
            File.WriteAllText(tempFile, "Test Content");

            var fileHashes = new Dictionary<string, string>();
            var system = new HashCalculationSystem(fileHashes);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new FilePathComponent { FilePath = tempFile });

            var result = system.Execute(entity, storage);

            Assert.True(result.IsSuccess);
            var fileHashComponent = storage.GetComponent<FileHashComponent>(entity);
            Assert.NotNull(fileHashComponent);
            Assert.NotEmpty(fileHashComponent.CurrentHash);
            Assert.Null(fileHashComponent.PreviousHash);

            File.Delete(tempFile);
        }

        [Fact]
        public void Execute_FilePathComponentMissing_ReturnsFailure()
        {
            var fileHashes = new Dictionary<string, string>();
            var system = new HashCalculationSystem(fileHashes);

            var entity = new Entity();
            var storage = new ComponentStorage();

            var result = system.Execute(entity, storage);

            Assert.False(result.IsSuccess);
            Assert.Equal("FilePathComponent missing.", result.ErrorMessage);
        }
    }
}
