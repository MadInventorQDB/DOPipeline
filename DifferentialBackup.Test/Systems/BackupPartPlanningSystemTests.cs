using DifferentialBackup.Components;
using DifferentialBackup.Systems;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Storage;
using Xunit;

namespace DifferentialBackup.Test.Systems
{
    public sealed class BackupPartPlanningSystemTests : IDisposable
    {
        private readonly string _basePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;
        private readonly BackupRunState _runState;

        public BackupPartPlanningSystemTests()
        {
            _sourceDirectory = Path.Combine(_basePath, "Source");
            _backupDestination = Path.Combine(_basePath, "Backup");
            Directory.CreateDirectory(_sourceDirectory);
            Directory.CreateDirectory(_backupDestination);
            _runState = new BackupRunState(
                _sourceDirectory,
                _backupDestination,
                Path.Combine(_basePath, "Staging"));
            _runState.BeginRun();
        }

        [Fact]
        public void Execute_SplitsPartsAtTheSourceByteLimit()
        {
            var storage = CreateChangedFiles(("a.txt", 6), ("b.txt", 5), ("c.txt", 2));
            var options = new BackupBatchOptions
            {
                MaxSourceBytesPerPart = 10,
                MaxFilesPerPart = 100,
                TransferQueueCapacity = 2
            };
            var system = new BackupPartPlanningSystem(
                _sourceDirectory,
                _backupDestination,
                _runState,
                options,
                NullLogger.Instance);

            var result = system.Execute(storage.GetAllEntities(), storage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            var parts = GetParts(storage);
            Assert.Equal(2, parts.Count);
            Assert.Equal(new[] { "a.txt" }, parts[0].Files.Select(file => file.EntryName));
            Assert.Equal(new[] { "b.txt", "c.txt" }, parts[1].Files.Select(file => file.EntryName));
        }

        [Fact]
        public void Execute_SplitsPartsAtTheFileCountLimitAndSortsDeterministically()
        {
            var storage = CreateChangedFiles(("c.txt", 1), ("a.txt", 1), ("b.txt", 1));
            var options = new BackupBatchOptions
            {
                MaxSourceBytesPerPart = 100,
                MaxFilesPerPart = 2,
                TransferQueueCapacity = 2
            };
            var system = new BackupPartPlanningSystem(
                _sourceDirectory,
                _backupDestination,
                _runState,
                options,
                NullLogger.Instance);

            var result = system.Execute(storage.GetAllEntities(), storage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            var parts = GetParts(storage);
            Assert.Equal(2, parts.Count);
            Assert.Equal(new[] { "a.txt", "b.txt" }, parts[0].Files.Select(file => file.EntryName));
            Assert.Equal(new[] { "c.txt" }, parts[1].Files.Select(file => file.EntryName));
            Assert.Equal("part-000001.zip", parts[0].ArchiveFileName);
            Assert.Equal("part-000002.zip", parts[1].ArchiveFileName);
        }

        [Fact]
        public void BeginRun_DoesNotDeleteAnIncompatibleCheckpoint()
        {
            Directory.CreateDirectory(_runState.StagingDirectory);
            var oldArchive = Path.Combine(_runState.StagingDirectory, "old.zip.partial");
            File.WriteAllText(Path.Combine(_runState.StagingDirectory, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(_runState.StagingDirectory, "completed.log"), "0\thash");
            File.WriteAllText(oldArchive, "obsolete");

            var exception = Assert.Throws<InvalidDataException>(() => _runState.BeginRun());

            Assert.Contains(_runState.StagingDirectory, exception.Message);
            Assert.True(Directory.Exists(_runState.StagingDirectory));
            Assert.True(File.Exists(oldArchive));
        }

        public void Dispose()
        {
            if (Directory.Exists(_basePath))
            {
                Directory.Delete(_basePath, true);
            }

            GC.SuppressFinalize(this);
        }

        private ComponentStorage CreateChangedFiles(params (string Name, int Length)[] definitions)
        {
            var storage = new ComponentStorage();
            var backupDate = new DateTime(2026, 8, 20, 13, 0, 0, DateTimeKind.Utc);
            foreach (var definition in definitions)
            {
                var path = Path.Combine(_sourceDirectory, definition.Name);
                File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', definition.Length).ToArray());
                var entity = new Entity();
                storage.SetComponent(entity, new FilePathComponent { FilePath = path });
                storage.SetComponent(entity, new FileHashComponent { CurrentHash = "hash-" + definition.Name });
                storage.SetComponent(entity, new BackupDateComponent { BackupDate = backupDate });
            }

            return storage;
        }

        private static List<BackupPartComponent> GetParts(ComponentStorage storage)
        {
            return storage.GetAllEntities()
                .Select(entity => storage.GetComponent<BackupPartComponent>(entity))
                .Where(component => component != null)
                .Cast<BackupPartComponent>()
                .OrderBy(component => component.PartNumber)
                .ToList();
        }
    }
}
