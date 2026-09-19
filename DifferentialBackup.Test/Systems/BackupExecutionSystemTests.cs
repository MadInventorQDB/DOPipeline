using DifferentialBackup.Components;
using DifferentialBackup.Systems;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Storage;
using System.IO.Compression;
using Xunit;

namespace DifferentialBackup.Test.Systems
{
    public sealed class BackupExecutionSystemTests : IDisposable
    {
        private readonly string _basePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;
        private readonly BackupRunState _runState;
        private readonly BackupBatchOptions _options = new()
        {
            MaxSourceBytesPerPart = 1024,
            MaxFilesPerPart = 10,
            TransferQueueCapacity = 2,
            CopyBufferSize = 64 * 1024
        };

        public BackupExecutionSystemTests()
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
        public void Execute_CreatesEachArchiveOnceAndTransfersItToWorkingDirectory()
        {
            var sourceFile = Path.Combine(_sourceDirectory, "nested", "file.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            File.WriteAllText(sourceFile, "backup content");
            var fixture = CreateFixture(new[]
            {
                new BackupPartFile(sourceFile, "nested/file.txt", "hash-1", new FileInfo(sourceFile).Length)
            });

            var system = new BackupExecutionSystem(_runState, _options, NullLogger.Instance);
            var result = system.Execute(fixture.Storage.GetAllEntities(), fixture.Storage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(BackupPartState.Transferred, fixture.Status.State);
            Assert.True(File.Exists(fixture.Status.DestinationArchivePath));
            Assert.False(File.Exists(fixture.Status.LocalArchivePath));
            Assert.True(File.Exists(_runState.GetPartCheckpointPath(1)));

            using var archive = ZipFile.OpenRead(fixture.Status.DestinationArchivePath);
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("nested/file.txt", entry.FullName);
            using var reader = new StreamReader(entry.Open());
            Assert.Equal("backup content", reader.ReadToEnd());
        }

        [Fact]
        public void Execute_RebuildsOnlyTheInterruptedPartAndIncompleteCopy()
        {
            var sourceFile = Path.Combine(_sourceDirectory, "file.txt");
            File.WriteAllText(sourceFile, "complete content");
            var fixture = CreateFixture(new[]
            {
                new BackupPartFile(sourceFile, "file.txt", "hash-1", new FileInfo(sourceFile).Length)
            });
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Status.LocalArchivePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Status.DestinationArchivePath)!);
            File.WriteAllText(fixture.Status.LocalArchivePath + ".partial", "broken zip");
            File.WriteAllText(fixture.Status.DestinationArchivePath + ".copying", "partial copy");

            var system = new BackupExecutionSystem(_runState, _options, NullLogger.Instance);
            var result = system.Execute(fixture.Storage.GetAllEntities(), fixture.Storage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.False(File.Exists(fixture.Status.LocalArchivePath + ".partial"));
            Assert.False(File.Exists(fixture.Status.DestinationArchivePath + ".copying"));
            using var archive = ZipFile.OpenRead(fixture.Status.DestinationArchivePath);
            Assert.NotNull(archive.GetEntry("file.txt"));
        }

        [Fact]
        public void Execute_ReusesAValidatedDestinationPartWithoutReadingSourceAgain()
        {
            var missingSource = Path.Combine(_sourceDirectory, "no-longer-readable.txt");
            var fixture = CreateFixture(new[]
            {
                new BackupPartFile(missingSource, "file.txt", "hash-1", 7)
            });
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Status.DestinationArchivePath)!);
            using (var archive = ZipFile.Open(fixture.Status.DestinationArchivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("file.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("content");
            }

            var archiveBytes = new FileInfo(fixture.Status.DestinationArchivePath).Length;
            _runState.SavePartCheckpoint(new BackupPartCheckpoint
            {
                PartNumber = 1,
                ArchiveFileName = fixture.Part.ArchiveFileName,
                Fingerprint = fixture.Part.Fingerprint,
                FileCount = 1,
                SourceBytes = 7,
                ArchiveBytes = archiveBytes
            });

            var system = new BackupExecutionSystem(_runState, _options, NullLogger.Instance);
            var result = system.Execute(fixture.Storage.GetAllEntities(), fixture.Storage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(BackupPartState.Transferred, fixture.Status.State);
            Assert.Equal(archiveBytes, fixture.Status.ArchiveBytes);
        }

        [Fact]
        public void Execute_StoresPreCompressedFilesWithoutRecompressing()
        {
            var sourceFile = Path.Combine(_sourceDirectory, "photo.jpg");
            var bytes = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
            File.WriteAllBytes(sourceFile, bytes);
            var fixture = CreateFixture(new[]
            {
                new BackupPartFile(sourceFile, "photo.jpg", "hash-1", bytes.Length)
            });

            var system = new BackupExecutionSystem(_runState, _options, NullLogger.Instance);
            var result = system.Execute(fixture.Storage.GetAllEntities(), fixture.Storage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            using var archive = ZipFile.OpenRead(fixture.Status.DestinationArchivePath);
            var entry = Assert.Single(archive.Entries);
            Assert.Equal(entry.Length, entry.CompressedLength);
        }

        public void Dispose()
        {
            if (Directory.Exists(_basePath))
            {
                Directory.Delete(_basePath, true);
            }

            GC.SuppressFinalize(this);
        }

        private ExecutionFixture CreateFixture(IReadOnlyList<BackupPartFile> files)
        {
            var backupDate = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
            var part = new BackupPartComponent
            {
                PartNumber = 1,
                BackupDate = backupDate,
                ArchiveFileName = "part-000001.zip",
                Fingerprint = "part-fingerprint",
                SourceBytes = files.Sum(file => file.Length),
                Files = files
            };
            var workingDirectory = Path.Combine(_backupDestination, "20260820120000.backup.copying");
            var finalDirectory = Path.Combine(_backupDestination, "20260820120000.backup");
            var status = new BackupPartStatusComponent
            {
                State = BackupPartState.Planned,
                LocalArchivePath = Path.Combine(_runState.StagingDirectory, part.ArchiveFileName),
                DestinationArchivePath = Path.Combine(workingDirectory, part.ArchiveFileName)
            };
            var run = new BackupRunComponent
            {
                BackupDate = backupDate,
                SourceDirectory = _sourceDirectory,
                BackupDestination = _backupDestination,
                StagingDirectory = _runState.StagingDirectory,
                WorkingDirectory = workingDirectory,
                FinalDirectory = finalDirectory,
                PlanFingerprint = "plan-fingerprint",
                TotalParts = 1,
                TotalFiles = files.Count,
                SourceBytes = part.SourceBytes
            };

            _runState.CreateOrUpdateJob(
                backupDate,
                _options,
                run.PlanFingerprint,
                1,
                files.Count,
                part.SourceBytes);

            var storage = new ComponentStorage();
            var runEntity = new Entity();
            storage.SetComponent(runEntity, run);
            var partEntity = new Entity();
            storage.SetComponent(partEntity, part);
            storage.SetComponent(partEntity, status);
            return new ExecutionFixture(storage, part, status);
        }

        private sealed record ExecutionFixture(
            ComponentStorage Storage,
            BackupPartComponent Part,
            BackupPartStatusComponent Status);
    }
}
