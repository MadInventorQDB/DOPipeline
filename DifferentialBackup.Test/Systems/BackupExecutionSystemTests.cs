
using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DifferentialBackup.Components;
using System.IO;

namespace DifferentialBackup.Test.Systems
{
    public class BackupExecutionSystemTests
    {
        [Fact]
        public void Execute_BackupsFileWhenBackupDateComponentExists()
        {
            var sourceDirectory = Path.Combine(Path.GetTempPath(), "Source");
            var backupDestination = Path.Combine(Path.GetTempPath(), "Backup");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(backupDestination);

            var sourceFile = Path.Combine(sourceDirectory, "file.txt");
            File.WriteAllText(sourceFile, "Test Content");

            var backupDate = System.DateTime.UtcNow;

            var system = new BackupExecutionSystem(sourceDirectory, backupDestination);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new FilePathComponent { FilePath = sourceFile });
            storage.SetComponent(entity, new BackupDateComponent { BackupDate = backupDate });

            var result = system.Execute(entity, storage);

            Assert.True(result.IsSuccess);
            var backupFolderName = backupDate.ToString("yyyyMMddHHmmss");
            var backupFilePath = Path.Combine(backupDestination, backupFolderName, "file.txt");
            Assert.True(File.Exists(backupFilePath));

            Directory.Delete(sourceDirectory, true);
            Directory.Delete(backupDestination, true);
        }

        [Fact]
        public void Execute_SkipsBackupWhenNoBackupDateComponent()
        {
            // Arrange
            var sourceDirectory = Path.Combine(Path.GetTempPath(), "Source");
            var backupDestination = Path.Combine(Path.GetTempPath(), "Backup");

            // Ensure directories are clean
            if (Directory.Exists(sourceDirectory))
                Directory.Delete(sourceDirectory, true);
            if (Directory.Exists(backupDestination))
                Directory.Delete(backupDestination, true);

            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(backupDestination);

            var sourceFile = Path.Combine(sourceDirectory, "file.txt");
            File.WriteAllText(sourceFile, "Test Content");

            var system = new BackupExecutionSystem(sourceDirectory, backupDestination);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new FilePathComponent { FilePath = sourceFile });

            // Act
            var result = system.Execute(entity, storage);

            // Assert
            Assert.True(result.IsSuccess);

            // Check if any backup folders were created
            var backupFolders = Directory.GetDirectories(backupDestination);
            Assert.Empty(backupFolders);

            // Clean up
            Directory.Delete(sourceDirectory, true);
            Directory.Delete(backupDestination, true);
        }

        [Fact]
        public void Execute_MissingFilePathComponent_ReturnsFailure()
        {
            var sourceDirectory = Path.Combine(Path.GetTempPath(), "Source");
            var backupDestination = Path.Combine(Path.GetTempPath(), "Backup");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(backupDestination);

            var backupDate = System.DateTime.UtcNow;

            var system = new BackupExecutionSystem(sourceDirectory, backupDestination);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new BackupDateComponent { BackupDate = backupDate });

            var result = system.Execute(entity, storage);

            Assert.False(result.IsSuccess);
            Assert.Equal("FilePathComponent missing.", result.ErrorMessage);

            Directory.Delete(sourceDirectory, true);
            Directory.Delete(backupDestination, true);
        }
    }
}
