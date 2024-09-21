
using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DifferentialBackup.Components;
using System.Collections.Generic;
using System;

namespace DifferentialBackup.Test.Systems
{
    public class BackupDecisionSystemTests
    {
        [Fact]
        public void Execute_NewFile_AddsBackupDateComponent()
        {
            var fileHashes = new Dictionary<string, string>();
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

            Assert.Contains(@"C:\test\file.txt", fileHashes.Keys);
            Assert.Equal("newHash", fileHashes[@"C:\test\file.txt"]);
            Assert.Single(backupDates);
        }

        [Fact]
        public void Execute_FileUnchanged_NoBackupDateComponent()
        {
            var fileHashes = new Dictionary<string, string>
            {
                { @"C:\test\file.txt", "existingHash" }
            };
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
            var fileHashes = new Dictionary<string, string>();
            var backupDates = new HashSet<DateTime>();
            var system = new BackupDecisionSystem(fileHashes, backupDates);

            var entity = new Entity();
            var storage = new ComponentStorage();

            var result = system.Execute(entity, storage);

            Assert.False(result.IsSuccess);
            Assert.Equal("Required components missing.", result.ErrorMessage);
        }
    }
}
