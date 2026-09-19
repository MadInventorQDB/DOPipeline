using DifferentialBackup.Components;
using DifferentialBackup.Systems;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Storage;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace DifferentialBackup.Test.Systems
{
    public sealed class BackupPublicationSystemTests : IDisposable
    {
        private readonly string _basePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;
        private readonly BackupRunState _runState;
        private readonly BackupBatchOptions _options = new();

        public BackupPublicationSystemTests()
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
        public void Execute_WritesManifestLastAndAtomicallyPublishesBackupSet()
        {
            var fixture = CreateFixture(createArchive: true);
            var fileHashes = new ConcurrentDictionary<string, string>();
            var backupDates = new HashSet<DateTime>();
            var system = new BackupPublicationSystem(
                fileHashes,
                backupDates,
                _runState,
                NullLogger.Instance);

            var result = system.Execute(fixture.Storage.GetAllEntities(), fixture.Storage);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.False(Directory.Exists(fixture.Run.WorkingDirectory));
            Assert.True(Directory.Exists(fixture.Run.FinalDirectory));
            Assert.True(File.Exists(Path.Combine(fixture.Run.FinalDirectory, "part-000001.zip")));
            var manifestPath = Path.Combine(fixture.Run.FinalDirectory, "manifest.json");
            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath));
            Assert.NotNull(manifest);
            Assert.Equal(BackupManifest.CurrentFormatVersion, manifest.FormatVersion);
            Assert.Single(manifest.Parts);
            Assert.Equal("hash-1", fileHashes[fixture.SourceFile]);
            Assert.Contains(fixture.Run.BackupDate, backupDates);
            Assert.True(_runState.Job!.Published);
            Assert.True(Directory.Exists(_runState.StagingDirectory));
        }

        [Fact]
        public void Execute_DoesNotPublishOrUpdateStateWhenAPartIsMissing()
        {
            var fixture = CreateFixture(createArchive: false);
            var fileHashes = new ConcurrentDictionary<string, string>();
            var backupDates = new HashSet<DateTime>();
            var system = new BackupPublicationSystem(
                fileHashes,
                backupDates,
                _runState,
                NullLogger.Instance);

            var result = system.Execute(fixture.Storage.GetAllEntities(), fixture.Storage);

            Assert.False(result.IsSuccess);
            Assert.False(Directory.Exists(fixture.Run.FinalDirectory));
            Assert.Empty(fileHashes);
            Assert.Empty(backupDates);
            Assert.False(_runState.Job!.Published);
        }

        public void Dispose()
        {
            if (Directory.Exists(_basePath))
            {
                Directory.Delete(_basePath, true);
            }

            GC.SuppressFinalize(this);
        }

        private PublicationFixture CreateFixture(bool createArchive)
        {
            var backupDate = new DateTime(2026, 8, 20, 14, 0, 0, DateTimeKind.Utc);
            var timestamp = backupDate.ToString("yyyyMMddHHmmss");
            var workingDirectory = Path.Combine(_backupDestination, timestamp + ".backup.copying");
            var finalDirectory = Path.Combine(_backupDestination, timestamp + ".backup");
            Directory.CreateDirectory(workingDirectory);
            var sourceFile = Path.Combine(_sourceDirectory, "file.txt");
            File.WriteAllText(sourceFile, "content");
            var archivePath = Path.Combine(workingDirectory, "part-000001.zip");
            if (createArchive)
            {
                using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
                var entry = archive.CreateEntry("file.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("content");
            }

            var archiveBytes = createArchive ? new FileInfo(archivePath).Length : 123;
            var part = new BackupPartComponent
            {
                PartNumber = 1,
                BackupDate = backupDate,
                ArchiveFileName = "part-000001.zip",
                Fingerprint = "part-fingerprint",
                SourceBytes = new FileInfo(sourceFile).Length,
                Files = new[]
                {
                    new BackupPartFile(sourceFile, "file.txt", "hash-1", new FileInfo(sourceFile).Length)
                }
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
                TotalFiles = 1,
                SourceBytes = part.SourceBytes
            };
            var status = new BackupPartStatusComponent
            {
                State = BackupPartState.Transferred,
                LocalArchivePath = Path.Combine(_runState.StagingDirectory, part.ArchiveFileName),
                DestinationArchivePath = archivePath,
                ArchiveBytes = archiveBytes
            };

            _runState.CreateOrUpdateJob(backupDate, _options, run.PlanFingerprint, 1, 1, part.SourceBytes);
            var storage = new ComponentStorage();
            var runEntity = new Entity();
            storage.SetComponent(runEntity, run);
            var partEntity = new Entity();
            storage.SetComponent(partEntity, part);
            storage.SetComponent(partEntity, status);
            return new PublicationFixture(storage, run, sourceFile);
        }

        private sealed record PublicationFixture(
            ComponentStorage Storage,
            BackupRunComponent Run,
            string SourceFile);
    }
}
