
using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DifferentialBackup.Components;
using System.IO;
using System.IO.Compression;
using System.Collections.Concurrent;
using System.Collections.Generic;

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
            var fileHashes = new ConcurrentDictionary<string, string>();
            var backupDates = new HashSet<System.DateTime>();

            var system = new BackupExecutionSystem(sourceDirectory, backupDestination, fileHashes, backupDates);
            var compressionSystem = new BackupCompressionSystem(backupDestination);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new FilePathComponent { FilePath = sourceFile });
            storage.SetComponent(entity, new FileHashComponent { CurrentHash = "hash", PreviousHash = null });
            storage.SetComponent(entity, new BackupDateComponent { BackupDate = backupDate });

            var beginResult = system.BeginExecution(new[] { entity }, storage);
            var result = system.Execute(entity, storage);
            var endResult = system.EndExecution(new[] { entity }, storage);
            compressionSystem.Execute(entity, storage);
            Assert.True(beginResult.IsSuccess);
            Assert.True(endResult.IsSuccess);
            var backupZipPath = Assert.Single(Directory.GetFiles(backupDestination, "*.zip"));
            Assert.True(File.Exists(backupZipPath));
            Assert.Equal("hash", fileHashes[sourceFile]);
            Assert.Single(backupDates);

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
            var compressionSystem = new BackupCompressionSystem(backupDestination);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new FilePathComponent { FilePath = sourceFile });

            // Act
            var result = system.Execute(entity, storage);
            compressionSystem.Execute(entity, storage);

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
            var compressionSystem = new BackupCompressionSystem(backupDestination);
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new BackupDateComponent { BackupDate = backupDate });

            var result = system.Execute(entity, storage);
            compressionSystem.Execute(entity, storage);

            Assert.False(result.IsSuccess);
            Assert.Equal("FilePathComponent missing.", result.ErrorMessage);

            var zips = Directory.GetFiles(backupDestination, "*.zip");
            Assert.Empty(zips);

            Directory.Delete(sourceDirectory, true);
            Directory.Delete(backupDestination, true);
        }

        [Fact]
        public void Compression_MergesFolderWhenZipAlreadyExists()
        {
            var backupDestination = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var backupFolder = Path.Combine(backupDestination, "20250101010101");
            var backupZipPath = backupFolder + ".zip";

            Directory.CreateDirectory(backupFolder);
            File.WriteAllText(Path.Combine(backupFolder, "first.txt"), "First");

            var compressionSystem = new BackupCompressionSystem(backupDestination);
            var storage = new ComponentStorage();
            var entity = new Entity();

            var firstResult = compressionSystem.Execute(entity, storage);
            Assert.True(firstResult.IsSuccess);
            Assert.True(File.Exists(backupZipPath));
            Assert.False(Directory.Exists(backupFolder));

            Directory.CreateDirectory(backupFolder);
            File.WriteAllText(Path.Combine(backupFolder, "second.txt"), "Second");

            var secondResult = compressionSystem.Execute(entity, storage);

            Assert.True(secondResult.IsSuccess);
            Assert.False(Directory.Exists(backupFolder));

            using (var archive = ZipFile.OpenRead(backupZipPath))
            {
                Assert.NotNull(archive.GetEntry("first.txt"));
                Assert.NotNull(archive.GetEntry("second.txt"));
            }

            Directory.Delete(backupDestination, true);
        }

        [Fact]
        public void Execute_StoresPreCompressedFileWithoutRecompressing()
        {
            var sourceDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            var backupDestination = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(backupDestination);

            var sourceFile = Path.Combine(sourceDirectory, "photo.jpg");
            var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
            File.WriteAllBytes(sourceFile, bytes);

            var fileHashes = new ConcurrentDictionary<string, string>();
            var backupDates = new HashSet<System.DateTime>();
            var system = new BackupExecutionSystem(sourceDirectory, backupDestination, fileHashes, backupDates);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new FilePathComponent { FilePath = sourceFile });
            storage.SetComponent(entity, new FileHashComponent { CurrentHash = "hash", PreviousHash = null });
            storage.SetComponent(entity, new BackupDateComponent { BackupDate = System.DateTime.UtcNow });

            var beginResult = system.BeginExecution(new[] { entity }, storage);
            var result = system.Execute(entity, storage);
            var endResult = system.EndExecution(new[] { entity }, storage);

            Assert.True(beginResult.IsSuccess);
            Assert.True(result.IsSuccess);
            Assert.True(endResult.IsSuccess);

            var backupZipPath = Assert.Single(Directory.GetFiles(backupDestination, "*.zip"));
            using (var archive = ZipFile.OpenRead(backupZipPath))
            {
                var entry = archive.GetEntry("photo.jpg");
                Assert.NotNull(entry);
                Assert.Equal(bytes.Length, entry.Length);
                Assert.Equal(entry.Length, entry.CompressedLength);
            }

            Directory.Delete(sourceDirectory, true);
            Directory.Delete(backupDestination, true);
        }
    }
}
