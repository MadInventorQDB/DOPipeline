using Xunit;
using DifferentialBackup.Pipeline;
using DOPipeline.Storage;
using DOPipeline.Entities;
using System.IO;
using System;
using System.Reflection;

namespace DifferentialBackup.Test.Pipeline
{
    public class RestorePipelineBuilderTests
    {
        [Fact]
        public void BuildRestorePipeline_CreatesPipeline()
        {
            // Arrange
            var backupDestination = Path.Combine(Path.GetTempPath(), "Backup");
            var backupDate = DateTime.UtcNow;
            var restoreDestination = Path.Combine(Path.GetTempPath(), "Restore");

            // Act
            var pipeline = RestorePipelineBuilder.BuildRestorePipeline(
                backupDestination,
                backupDate,
                restoreDestination);

            // Assert
            Assert.NotNull(pipeline);

            var pipesField = typeof(DOPipeline.Pipeline.Pipeline).GetField("_pipes", BindingFlags.NonPublic | BindingFlags.Instance);
            var pipes = (List<DOPipeline.Pipeline.Pipe>)pipesField.GetValue(pipeline);
            Assert.Single(pipes);
        }

        [Fact]
        public void RestorePipeline_Execute_Success_PipelineTest()
        {
            // Arrange
            var backupDestination = Path.Combine(Path.GetTempPath(), "Backup");
            Directory.CreateDirectory(backupDestination);

            var backupDate = DateTime.UtcNow;
            var backupFolderName = backupDate.ToString("yyyyMMddHHmmss");
            var backupFolderPath = Path.Combine(backupDestination, backupFolderName);
            Directory.CreateDirectory(backupFolderPath);

            var backupFile = Path.Combine(backupFolderPath, "file.txt");
            File.WriteAllText(backupFile, "Backup Content");

            var restoreDestination = Path.Combine(Path.GetTempPath(), "Restore");
            Directory.CreateDirectory(restoreDestination);

            var pipeline = RestorePipelineBuilder.BuildRestorePipeline(
                backupDestination,
                backupDate,
                restoreDestination);

            var storage = new ComponentStorage();
            var initialEntity = new Entity();
            var entities = new List<Entity> { initialEntity };

            // Act
            var result = pipeline.Execute(entities, storage);

            // Assert
            Assert.True(result.IsSuccess);
            var restoredFile = Path.Combine(restoreDestination, "file.txt");
            Assert.True(File.Exists(restoredFile));
            var content = File.ReadAllText(restoredFile);
            Assert.Equal("Backup Content", content);

            // Clean up
            Directory.Delete(backupDestination, true);
            Directory.Delete(restoreDestination, true);
        }
    }
}
