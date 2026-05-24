
using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DifferentialBackup.Components;
using System.Collections.Generic;
using System;
using System.Collections.Concurrent;

namespace DifferentialBackup.Test.Systems
{
    public class BackupDecisionSystemTests
    {
        [Fact]
        public void Execute_NewFile_AddsBackupDateComponent()
        {
            var fileHashes = new ConcurrentDictionary<string, string>();
            var backupDates = new HashSet<DateTime>();
            var system = new BackupDecisionSystem(fileHashes, backupDates);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new FileHashComponent
            {
                CurrentHash = "newHash",
                PreviousHash = null
            });
            storage.SetComponent(entity, new FilePathComponent
            {
                FilePath = @"C:\test\file.txt"
            });

            var result = system.Execute(entity, storage);

            Assert.True(result.IsSuccess);
            var backupDateComponent = storage.GetComponent<BackupDateComponent>(entity);
            Assert.NotNull(backupDateComponent);
            Assert.True((DateTime.UtcNow - backupDateComponent.BackupDate).TotalSeconds < 5);

            Assert.DoesNotContain(@"C:\test\file.txt", fileHashes.Keys);
            Assert.Empty(backupDates);
        }

        [Fact]
        public void Execute_MultipleChangedFilesInExecution_UsesOneBackupDate()
        {
            var fileHashes = new ConcurrentDictionary<string, string>();
            var backupDates = new HashSet<DateTime>();
            var system = new BackupDecisionSystem(fileHashes, backupDates);
            var storage = new ComponentStorage();

            var entity1 = CreateChangedFileEntity(storage, @"C:\test\file1.txt", "hash1");
            var entity2 = CreateChangedFileEntity(storage, @"C:\test\file2.txt", "hash2");

            var result1 = system.Execute(entity1, storage);
            var result2 = system.Execute(entity2, storage);

            Assert.True(result1.IsSuccess);
            Assert.True(result2.IsSuccess);
            Assert.Empty(backupDates);
            Assert.Equal(
                storage.GetComponent<BackupDateComponent>(entity1)!.BackupDate,
                storage.GetComponent<BackupDateComponent>(entity2)!.BackupDate);
        }

        [Fact]
        public void Execute_FileUnchanged_NoBackupDateComponent()
        {
            var fileHashes = new ConcurrentDictionary<string, string>(new Dictionary<string, string>
            {
                { @"C:\test\file.txt", "existingHash" }
            });

            var backupDates = new HashSet<DateTime>();
            var system = new BackupDecisionSystem(fileHashes, backupDates);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new FileHashComponent
            {
                CurrentHash = "existingHash",
                PreviousHash = "existingHash"
            });
            storage.SetComponent(entity, new FilePathComponent
            {
                FilePath = @"C:\test\file.txt"
            });

            var result = system.Execute(entity, storage);

            Assert.True(result.IsSuccess);
            var backupDateComponent = storage.GetComponent<BackupDateComponent>(entity);
            Assert.Null(backupDateComponent);

            Assert.Equal("existingHash", fileHashes[@"C:\test\file.txt"]);
            Assert.Empty(backupDates);
        }

        [Fact]
        public void Execute_MissingComponents_ReturnsFailure()
        {
            var fileHashes = new ConcurrentDictionary<string, string>();
            var backupDates = new HashSet<DateTime>();
            var system = new BackupDecisionSystem(fileHashes, backupDates);

            var entity = new Entity();
            var storage = new ComponentStorage();

            var result = system.Execute(entity, storage);

            Assert.False(result.IsSuccess);
            Assert.Equal("Required components missing.", result.ErrorMessage);
        }

        private static Entity CreateChangedFileEntity(ComponentStorage storage, string path, string hash)
        {
            var entity = new Entity();
            storage.SetComponent(entity, new FileHashComponent
            {
                CurrentHash = hash,
                PreviousHash = null
            });
            storage.SetComponent(entity, new FilePathComponent
            {
                FilePath = path
            });

            return entity;
        }
    }
}
