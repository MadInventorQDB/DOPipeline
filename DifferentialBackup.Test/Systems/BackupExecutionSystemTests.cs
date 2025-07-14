
using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DifferentialBackup.Components;
using System.IO;
using System.IO.Compression;

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
            var backupZipName = backupDate.ToString("yyyyMMddHHmmss") + ".zip";
            var backupZipPath = Path.Combine(backupDestination, backupZipName);
            Assert.True(File.Exists(backupZipPath));

            using (var archive = ZipFile.OpenRead(backupZipPath))
            {
                Assert.NotNull(archive.GetEntry("file.txt"));
            }

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

            // Check if any backup archives were created
            var backupZips = Directory.GetFiles(backupDestination, "*.zip");
            Assert.Empty(backupZips);

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

            var zips = Directory.GetFiles(backupDestination, "*.zip");
            Assert.Empty(zips);

            Directory.Delete(sourceDirectory, true);
            Directory.Delete(backupDestination, true);
        }
    }
}
