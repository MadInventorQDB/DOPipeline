using Xunit;
using DifferentialBackup.Pipeline;
using DOPipeline.Storage;
using DOPipeline.Entities;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DifferentialBackup.Components;
using System.Reflection;

namespace DifferentialBackup.Test.Pipeline
{
    public class BackupPipelineBuilderTests
    {
        [Fact]
        public void BuildBackupPipeline_CreatesPipeline()
        {
            // Arrange
            var sourceDirectory = Path.Combine(Path.GetTempPath(), "Source");
            Directory.CreateDirectory(sourceDirectory);
            var backupDestination = Path.Combine(Path.GetTempPath(), "Backup");
            var fileHashes = new Dictionary<string, string>();
            var backupDates = new HashSet<System.DateTime>();

            // Act
            var pipeline = BackupPipelineBuilder.BuildBackupPipeline(
                sourceDirectory,
                backupDestination,
                fileHashes,
                backupDates);

            // Assert
            Assert.NotNull(pipeline);

            // Use reflection to access the private '_pipes' field
            var pipesField = typeof(DOPipeline.Pipeline.Pipeline).GetField("_pipes", BindingFlags.NonPublic | BindingFlags.Instance);
            var pipes = (List<DOPipeline.Pipeline.Pipe>)pipesField.GetValue(pipeline);
            Assert.Equal(4, pipes.Count);

            // Clean up
            Directory.Delete(sourceDirectory, true);
        }

        [Fact]
        public void BackupPipeline_Execute_Success()
        {
            // Arrange
            var sourceDirectory = Path.Combine(Path.GetTempPath(), "Source");
            Directory.CreateDirectory(sourceDirectory);
            var sourceFile = Path.Combine(sourceDirectory, "file.txt");
            File.WriteAllText(sourceFile, "Test Content");

            var backupDestination = Path.Combine(Path.GetTempPath(), "Backup");
            Directory.CreateDirectory(backupDestination);

            var fileHashes = new Dictionary<string, string>();
            var backupDates = new HashSet<System.DateTime>();

            var pipeline = BackupPipelineBuilder.BuildBackupPipeline(
                sourceDirectory,
                backupDestination,
                fileHashes,
                backupDates);

            var storage = new ComponentStorage();
            var initialEntity = new Entity();

            // Do not set FilePathComponent on the initial entity
            // storage.SetComponent(initialEntity, new FilePathComponent { FilePath = sourceDirectory });

            var entities = new List<Entity> { initialEntity };

            // Act
            var result = pipeline.Execute(entities, storage);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotEmpty(fileHashes);
            Assert.NotEmpty(backupDates);

            var backupFolder = Directory.GetDirectories(backupDestination).FirstOrDefault();
            Assert.NotNull(backupFolder);
            var backedUpFile = Path.Combine(backupFolder, "file.txt");
            Assert.True(File.Exists(backedUpFile));

            // Clean up
            Directory.Delete(sourceDirectory, true);
            Directory.Delete(backupDestination, true);
        }
    }
}
