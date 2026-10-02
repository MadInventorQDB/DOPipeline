using DifferentialBackup.Components;
using DifferentialBackup.Systems;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Components;
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

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Execute_CaptureBookkeepingScalesLinearlyAndPreservesRunAndPathMatching(bool legacyRun)
        {
            var files = Enumerable.Range(0, 64).Select(index =>
            {
                var path = Path.Combine(_sourceDirectory, $"file-{index}.txt");
                File.WriteAllText(path, "content");
                return new BackupPartFile(path, $"file-{index}.txt", "hash", 7);
            }).ToArray();
            var fixture = CreateFixture(files);
            var runId = legacyRun ? Guid.Empty : Guid.NewGuid();
            fixture.Run.RunId = runId;
            fixture.Part.RunId = runId;
            fixture.Status.RunId = runId;
            var otherRunId = Guid.NewGuid();
            var capturedRows = new List<Entity>();
            var untouchedRows = new List<Entity>();
            foreach (var file in files)
            {
                var row = new Entity();
                fixture.Storage.SetComponent(row, new FileWorkComponent
                {
                    RunId = runId,
                    SourcePath = file.SourcePath.ToUpperInvariant(),
                    State = FileWorkState.ReadyToCapture
                });
                fixture.Storage.SetComponent(row, new BackupIssueComponent { RunId = runId });
                capturedRows.Add(row);

                var otherRow = new Entity();
                fixture.Storage.SetComponent(otherRow, new FileWorkComponent
                {
                    RunId = otherRunId,
                    SourcePath = file.SourcePath,
                    State = FileWorkState.ReadyToCapture
                });
                fixture.Storage.SetComponent(otherRow, new BackupIssueComponent { RunId = otherRunId });
                untouchedRows.Add(otherRow);
            }

            for (var index = 0; index < 256; index++)
            {
                var row = new Entity();
                fixture.Storage.SetComponent(row, new FileWorkComponent
                {
                    RunId = runId,
                    SourcePath = Path.Combine(_sourceDirectory, $"unchanged-{index}.txt"),
                    State = FileWorkState.Unchanged
                });
            }

            var totalRows = fixture.Storage.Query<FileWorkComponent>().Count;
            var countingStorage = new FileWorkCountingStorage(fixture.Storage);
            var system = new BackupExecutionSystem(_runState, _options, NullLogger.Instance);
            var result = system.Execute(fixture.Storage.GetAllEntities(), countingStorage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(BackupPartState.Transferred, fixture.Status.State);
            Assert.All(capturedRows, row =>
            {
                Assert.Equal(FileWorkState.Captured, fixture.Storage.GetComponent<FileWorkComponent>(row).State);
                Assert.True(fixture.Storage.GetComponent<BackupIssueComponent>(row).Resolved);
            });
            Assert.All(untouchedRows, row =>
            {
                Assert.Equal(FileWorkState.ReadyToCapture, fixture.Storage.GetComponent<FileWorkComponent>(row).State);
                Assert.False(fixture.Storage.GetComponent<BackupIssueComponent>(row).Resolved);
            });
            // Bound component reads instead of elapsed time, so the performance
            // regression is detected reliably on both fast and slow machines.
            Assert.InRange(countingStorage.FileWorkReads, files.Length, totalRows * 2);
        }

        [Fact]
        public void Execute_DefersTheMatchingFileWithoutChangingAnotherRun()
        {
            var missingFile = Path.Combine(_sourceDirectory, "missing.txt");
            var fixture = CreateFixture(new[] { new BackupPartFile(missingFile, "missing.txt", "hash", 7) });
            var row = new Entity();
            fixture.Storage.SetComponent(row, new FileWorkComponent
            {
                SourcePath = missingFile.ToUpperInvariant(),
                StableKey = "existing-key",
                State = FileWorkState.ReadyToCapture,
                PartNumber = 1
            });
            var issue = new BackupIssueComponent { AttemptCount = 2, OriginalMessage = "original failure" };
            fixture.Storage.SetComponent(row, issue);
            var otherRow = new Entity();
            fixture.Storage.SetComponent(otherRow, new FileWorkComponent
            {
                RunId = Guid.NewGuid(),
                SourcePath = missingFile,
                State = FileWorkState.ReadyToCapture,
                PartNumber = 1
            });

            var result = new BackupExecutionSystem(_runState, _options, NullLogger.Instance)
                .Execute(fixture.Storage.GetAllEntities(), fixture.Storage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(BackupPartState.Omitted, fixture.Status.State);
            var work = fixture.Storage.GetComponent<FileWorkComponent>(row);
            Assert.Equal(FileWorkState.Deferred, work.State);
            Assert.Null(work.PartNumber);
            Assert.Equal("existing-key", work.StableKey);
            Assert.Equal(3, issue.AttemptCount);
            Assert.Equal("original failure", issue.OriginalMessage);
            Assert.False(issue.Resolved);
            Assert.Equal(FileWorkState.ReadyToCapture, fixture.Storage.GetComponent<FileWorkComponent>(otherRow).State);
            Assert.Equal(2, fixture.Storage.Query<FileWorkComponent>().Count);
        }

        [Fact]
        public void Execute_UsesCurrentFileRowsWhenTheSystemExecutesAgain()
        {
            var sourceFile = Path.Combine(_sourceDirectory, "first.txt");
            File.WriteAllText(sourceFile, "first");
            var first = CreateFixture(new[] { new BackupPartFile(sourceFile, "first.txt", "hash", 5) });
            var firstRow = new Entity();
            first.Storage.SetComponent(firstRow, new FileWorkComponent
            {
                SourcePath = sourceFile,
                State = FileWorkState.ReadyToCapture
            });
            var system = new BackupExecutionSystem(_runState, _options, NullLogger.Instance);
            var firstResult = system.Execute(first.Storage.GetAllEntities(), first.Storage);
            Assert.True(firstResult.IsSuccess, firstResult.ErrorMessage);
            Assert.Equal(FileWorkState.Captured, first.Storage.GetComponent<FileWorkComponent>(firstRow).State);

            var laterFile = Path.Combine(_sourceDirectory, "later.txt");
            File.WriteAllText(laterFile, "later");
            var later = CreateFixture(new[] { new BackupPartFile(laterFile, "later.txt", "hash", 5) }, partNumber: 2);
            var laterRow = new Entity();
            later.Storage.SetComponent(laterRow, new FileWorkComponent
            {
                SourcePath = laterFile,
                State = FileWorkState.ReadyToCapture
            });

            var result = system.Execute(later.Storage.GetAllEntities(), later.Storage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(FileWorkState.Captured, later.Storage.GetComponent<FileWorkComponent>(laterRow).State);
            using var archive = ZipFile.OpenRead(later.Status.DestinationArchivePath);
            Assert.Equal("later.txt", Assert.Single(archive.Entries).FullName);
        }

        public void Dispose()
        {
            if (Directory.Exists(_basePath))
            {
                Directory.Delete(_basePath, true);
            }

            GC.SuppressFinalize(this);
        }

        private ExecutionFixture CreateFixture(IReadOnlyList<BackupPartFile> files, int partNumber = 1)
        {
            var backupDate = new DateTime(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);
            var part = new BackupPartComponent
            {
                PartNumber = partNumber,
                BackupDate = backupDate,
                ArchiveFileName = $"part-{partNumber:000000}.zip",
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
            return new ExecutionFixture(storage, part, status, run);
        }

        private sealed class FileWorkCountingStorage(IComponentStorage inner) : IComponentStorage
        {
            private int _fileWorkReads;
            public int FileWorkReads => _fileWorkReads;

            public T GetComponent<T>(Entity entity) where T : class, IComponent
            {
                if (typeof(T) == typeof(FileWorkComponent))
                {
                    Interlocked.Increment(ref _fileWorkReads);
                }
                return inner.GetComponent<T>(entity);
            }

            public void SetComponent<T>(Entity entity, T component) where T : class, IComponent =>
                inner.SetComponent(entity, component);
            public bool HasComponent<T>(Entity entity) where T : class, IComponent => inner.HasComponent<T>(entity);
            public List<Entity> GetAllEntities() => inner.GetAllEntities();
            public IReadOnlyList<Entity> Query<T>() where T : class, IComponent => inner.Query<T>();
            public IReadOnlyList<Entity> Query<T1, T2>()
                where T1 : class, IComponent where T2 : class, IComponent => inner.Query<T1, T2>();
            public IReadOnlyList<Entity> Query<T1, T2, T3>()
                where T1 : class, IComponent where T2 : class, IComponent where T3 : class, IComponent =>
                inner.Query<T1, T2, T3>();
        }

        private sealed record ExecutionFixture(
            ComponentStorage Storage,
            BackupPartComponent Part,
            BackupPartStatusComponent Status,
            BackupRunComponent Run);
    }
}
