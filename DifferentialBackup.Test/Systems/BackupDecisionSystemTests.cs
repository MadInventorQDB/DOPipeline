
using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Components;
using DOPipeline.Pipeline;
using DOPipeline.Storage;
using DifferentialBackup.Components;
using System.Collections.Generic;
using System.IO;
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

        [Fact]
        public void Execute_DiscoveredDirectoryRemovedBeforeDecision_DoesNotFailPipe()
        {
            var source = Path.Combine(Path.GetTempPath(), "BackupDecision_" + Guid.NewGuid().ToString("N"));
            var child = Path.Combine(source, "temporary");
            Directory.CreateDirectory(child);

            try
            {
                var filePath = Path.Combine(source, "file.txt");
                File.WriteAllText(filePath, "content");
                var storage = new ComponentStorage();
                var root = new Entity();
                storage.SetComponent(root, new OperationComponent
                {
                    SourceDirectory = source,
                    Phase = OperationPhase.InitialPass
                });
                storage.SetComponent(root, new FilePathComponent { FilePath = source });
                Assert.True(new FileDiscoverySystem(source).Execute(root, storage).IsSuccess);

                var directory = Assert.Single(storage.Query<DirectoryWorkComponent>(),
                    entity => storage.GetComponent<DirectoryWorkComponent>(entity)!.NormalizedPath == child);
                Directory.Delete(child);
                var entities = storage.GetAllEntities();
                var fileHashes = new ConcurrentDictionary<string, string>();
                Assert.True(new Pipe("Hash Calculation")
                    .AddSystem(new HashCalculationSystem(fileHashes))
                    .Execute(entities, storage).IsSuccess);

                var result = new Pipe("Backup Decision")
                    .AddSystem(new BackupDecisionSystem(fileHashes, new HashSet<DateTime>()))
                    .Execute(entities, storage);

                Assert.True(result.IsSuccess, result.ErrorMessage);
                Assert.Null(storage.GetComponent<ErrorComponent>(directory));
                Assert.Null(storage.GetComponent<BackupDateComponent>(directory));
                Assert.Equal(DirectoryWorkState.Enumerated,
                    storage.GetComponent<DirectoryWorkComponent>(directory)!.State);
                var file = Assert.Single(storage.Query<FileWorkComponent>());
                Assert.NotNull(storage.GetComponent<BackupDateComponent>(file));
            }
            finally
            {
                Directory.Delete(source, recursive: true);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Execute_MissingDeferredDirectory_PreservesRetryIssue(bool hasPath)
        {
            var storage = new ComponentStorage();
            var operationEntity = new Entity();
            var operation = new OperationComponent { Phase = OperationPhase.InitialPass };
            storage.SetComponent(operationEntity, operation);
            var directoryEntity = new Entity();
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var directory = new DirectoryWorkComponent
            {
                RunId = operation.RunId,
                StableKey = path,
                NormalizedPath = path,
                State = DirectoryWorkState.Deferred
            };
            var issue = new BackupIssueComponent
            {
                RunId = operation.RunId,
                StableKey = path,
                Path = path,
                Stage = "Discovery",
                Category = "SourceRead",
                Retryable = true,
                AttemptCount = 1
            };
            storage.SetComponent(directoryEntity, directory);
            storage.SetComponent(directoryEntity, issue);
            if (hasPath)
            {
                storage.SetComponent(directoryEntity, new FilePathComponent { FilePath = path });
            }

            var result = new Pipe("Backup Decision")
                .AddSystem(new BackupDecisionSystem(new ConcurrentDictionary<string, string>(), new HashSet<DateTime>()))
                .Execute(new[] { directoryEntity }, storage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Same(directory, storage.GetComponent<DirectoryWorkComponent>(directoryEntity));
            Assert.Equal(DirectoryWorkState.Deferred, directory.State);
            Assert.Same(issue, storage.GetComponent<BackupIssueComponent>(directoryEntity));
            Assert.False(issue.Resolved);
            Assert.Equal(1, issue.AttemptCount);
            Assert.Null(storage.GetComponent<BackupDateComponent>(directoryEntity));
            Assert.True(new PassCompletionSystem().Execute(storage.GetAllEntities(), storage).IsSuccess);
            Assert.Equal(OperationPhase.RetryWaiting, operation.Phase);
            Assert.NotNull(storage.GetComponent<OperationWaitComponent>(operationEntity));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Execute_ReadyFileWithoutHash_ReturnsFailure(bool alsoHasDirectoryWork)
        {
            var storage = new ComponentStorage();
            var entity = new Entity();
            storage.SetComponent(entity, new FilePathComponent
            {
                FilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
            });
            storage.SetComponent(entity, new FileWorkComponent { State = FileWorkState.ReadyToCapture });
            if (alsoHasDirectoryWork)
            {
                storage.SetComponent(entity, new DirectoryWorkComponent { State = DirectoryWorkState.Deferred });
            }

            var result = new BackupDecisionSystem(new ConcurrentDictionary<string, string>(), new HashSet<DateTime>())
                .Execute(entity, storage);

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
