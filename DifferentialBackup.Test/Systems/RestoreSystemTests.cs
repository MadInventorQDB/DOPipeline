using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using System.IO;
using System;

namespace DifferentialBackup.Test.Systems
{
    public class RestoreSystemTests
    {
        [Fact]
        public void Execute_RestoresFilesFromBackup()
        {
            // Arrange
            var backupDestination = Path.Combine(Path.GetTempPath(), "Backup");
            var restoreDestination = Path.Combine(Path.GetTempPath(), "Restore");
            Directory.CreateDirectory(backupDestination);
            Directory.CreateDirectory(restoreDestination);

            var backupDate = DateTime.UtcNow;
            var backupFolderName = backupDate.ToString("yyyyMMddHHmmss");
            var backupFolderPath = Path.Combine(backupDestination, backupFolderName);
            Directory.CreateDirectory(backupFolderPath);

            var backupFile = Path.Combine(backupFolderPath, "file.txt");
            File.WriteAllText(backupFile, "Backup Content");

            var system = new RestoreSystem(backupDestination, backupDate, restoreDestination);

            var entity = new Entity();
            var storage = new ComponentStorage();

            // Act
            var result = system.Execute(entity, storage);

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

        [Fact]
        public void Execute_BackupDateNotFound_ReturnsFailure()
        {
            // Arrange
            var backupDestination = Path.Combine(Path.GetTempPath(), "Backup");
            var restoreDestination = Path.Combine(Path.GetTempPath(), "Restore");

            // Ensure directories are created
            Directory.CreateDirectory(backupDestination);
            Directory.CreateDirectory(restoreDestination);

            var backupDate = DateTime.UtcNow;

            // Do not create backup folder for the given date to simulate missing backup
            var system = new RestoreSystem(backupDestination, backupDate, restoreDestination);

            var entity = new Entity();
            var storage = new ComponentStorage();

            // Act
            var result = system.Execute(entity, storage);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal("Backup date not found.", result.ErrorMessage);

            // Clean up
            Directory.Delete(backupDestination, true);
            Directory.Delete(restoreDestination, true);
        }
    }
}
