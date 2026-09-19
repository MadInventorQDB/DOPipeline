using DifferentialBackup.Pipeline;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Pipeline;
using DOPipeline.Storage;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace DifferentialBackup.Test.Pipeline
{
    public sealed class BackupPipelineBuilderTests : IDisposable
    {
        private readonly string _basePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;
        private readonly string _stagingRoot;

        public BackupPipelineBuilderTests()
        {
            _sourceDirectory = Path.Combine(_basePath, "Source");
            _backupDestination = Path.Combine(_basePath, "Backup");
            _stagingRoot = Path.Combine(_basePath, "Staging");
            Directory.CreateDirectory(_sourceDirectory);
            Directory.CreateDirectory(_backupDestination);
        }

        [Fact]
        public void BuildBackupPipeline_CreatesSixDataStagesWithoutLegacyCompressionPipe()
        {
            var pipeline = BuildPipeline(
                new ConcurrentDictionary<string, string>(),
                new HashSet<DateTime>(),
                CreateRunState());
            var pipesField = typeof(DOPipeline.Pipeline.Pipeline)
                .GetField("_pipes", BindingFlags.NonPublic | BindingFlags.Instance);
            var pipes = Assert.IsType<List<Pipe>>(pipesField!.GetValue(pipeline));

            Assert.Equal(6, pipes.Count);
            Assert.Equal(
                new[]
                {
                    "File Discovery",
                    "Hash Calculation",
                    "Backup Decision",
                    "Backup Part Planning",
                    "Backup Part Execution",
                    "Backup Publication"
                },
                pipes.Select(pipe => pipe.Name));
        }

        [Fact]
        public void BackupPipeline_ChangedFiles_CreateNumberedPartsAndCompactManifest()
        {
            File.WriteAllText(Path.Combine(_sourceDirectory, "a.txt"), "first");
            Directory.CreateDirectory(Path.Combine(_sourceDirectory, "Nested"));
            File.WriteAllText(Path.Combine(_sourceDirectory, "Nested", "b.txt"), "second");
            var fileHashes = new ConcurrentDictionary<string, string>();
            var backupDates = new HashSet<DateTime>();
            var runState = CreateRunState();
            var options = new BackupBatchOptions
            {
                MaxSourceBytesPerPart = 1024,
                MaxFilesPerPart = 1,
                TransferQueueCapacity = 2,
                CopyBufferSize = 64 * 1024
            };
            var pipeline = BuildPipeline(fileHashes, backupDates, runState, options);

            var result = pipeline.Execute(new[] { new Entity() }, new ComponentStorage());

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(2, fileHashes.Count);
            Assert.Single(backupDates);
            var backupSet = Assert.Single(Directory.GetDirectories(_backupDestination, "*.backup"));
            Assert.False(Directory.Exists(backupSet + ".copying"));
            Assert.Equal(2, Directory.GetFiles(backupSet, "part-*.zip").Length);
            Assert.Empty(Directory.GetFiles(_backupDestination, "*.zip"));

            var manifest = JsonSerializer.Deserialize<BackupManifest>(
                File.ReadAllText(Path.Combine(backupSet, "manifest.json")));
            Assert.NotNull(manifest);
            Assert.Equal(2, manifest.FileCount);
            Assert.Equal(2, manifest.Parts.Count);
            Assert.All(manifest.Parts, part => Assert.Equal(1, part.FileCount));

            using var firstArchive = ZipFile.OpenRead(Path.Combine(backupSet, "part-000001.zip"));
            using var secondArchive = ZipFile.OpenRead(Path.Combine(backupSet, "part-000002.zip"));
            var entryNames = firstArchive.Entries.Concat(secondArchive.Entries)
                .Select(entry => entry.FullName)
                .OrderBy(name => name)
                .ToList();
            Assert.Equal(new[] { "a.txt", "Nested/b.txt" }.OrderBy(name => name), entryNames);
        }

        [Fact]
        public void BackupPipeline_ModifiedFileCreatesAnotherDifferentialBackupSet()
        {
            var sourceFile = Path.Combine(_sourceDirectory, "file.txt");
            File.WriteAllText(sourceFile, "initial");
            var fileHashes = new ConcurrentDictionary<string, string>();
            var backupDates = new HashSet<DateTime>();
            var firstRunState = CreateRunState();

            var firstResult = BuildPipeline(fileHashes, backupDates, firstRunState)
                .Execute(new[] { new Entity() }, new ComponentStorage());
            Assert.True(firstResult.IsSuccess, firstResult.ErrorMessage);
            firstRunState.ClearRun();

            File.WriteAllText(sourceFile, "modified");
            var secondRunState = CreateRunState();
            var secondResult = BuildPipeline(fileHashes, backupDates, secondRunState)
                .Execute(new[] { new Entity() }, new ComponentStorage());

            Assert.True(secondResult.IsSuccess, secondResult.ErrorMessage);
            Assert.Equal(2, backupDates.Count);
            var backupSets = Directory.GetDirectories(_backupDestination, "*.backup")
                .OrderBy(path => path)
                .ToList();
            Assert.Equal(2, backupSets.Count);
            var latestPart = Path.Combine(backupSets[^1], "part-000001.zip");
            using var archive = ZipFile.OpenRead(latestPart);
            using var reader = new StreamReader(archive.GetEntry("file.txt")!.Open());
            Assert.Equal("modified", reader.ReadToEnd());
        }

        [Fact]
        public void BackupPipeline_UnchangedFilesDoNotCreateAnotherBackupSet()
        {
            var sourceFile = Path.Combine(_sourceDirectory, "file.txt");
            File.WriteAllText(sourceFile, "stable");
            var fileHashes = new ConcurrentDictionary<string, string>();
            var backupDates = new HashSet<DateTime>();
            var firstRunState = CreateRunState();
            var firstResult = BuildPipeline(fileHashes, backupDates, firstRunState)
                .Execute(new[] { new Entity() }, new ComponentStorage());
            Assert.True(firstResult.IsSuccess, firstResult.ErrorMessage);
            firstRunState.ClearRun();

            var secondRunState = CreateRunState();
            var secondResult = BuildPipeline(fileHashes, backupDates, secondRunState)
                .Execute(new[] { new Entity() }, new ComponentStorage());

            Assert.True(secondResult.IsSuccess, secondResult.ErrorMessage);
            Assert.Single(backupDates);
            Assert.Single(Directory.GetDirectories(_backupDestination, "*.backup"));
        }

        [Fact]
        public void BackupPipeline_ReusesPublishedSetWhenPersistentStateWasNotSaved()
        {
            var sourceFile = Path.Combine(_sourceDirectory, "file.txt");
            File.WriteAllText(sourceFile, "published content");
            var firstHashes = new ConcurrentDictionary<string, string>();
            var firstDates = new HashSet<DateTime>();
            var firstRunState = CreateRunState();
            var firstResult = BuildPipeline(firstHashes, firstDates, firstRunState)
                .Execute(new[] { new Entity() }, new ComponentStorage());
            Assert.True(firstResult.IsSuccess, firstResult.ErrorMessage);

            var recoveredHashes = new ConcurrentDictionary<string, string>();
            var recoveredDates = new HashSet<DateTime>();
            var recoveredRunState = CreateRunState();
            var recoveredResult = BuildPipeline(recoveredHashes, recoveredDates, recoveredRunState)
                .Execute(new[] { new Entity() }, new ComponentStorage());

            Assert.True(recoveredResult.IsSuccess, recoveredResult.ErrorMessage);
            Assert.Equal(firstHashes[sourceFile], recoveredHashes[sourceFile]);
            Assert.Single(recoveredDates);
            Assert.Single(Directory.GetDirectories(_backupDestination, "*.backup"));
            Assert.Empty(Directory.GetDirectories(_backupDestination, "*.backup.copying"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_basePath))
            {
                Directory.Delete(_basePath, true);
            }

            GC.SuppressFinalize(this);
        }

        private BackupRunState CreateRunState()
        {
            return new BackupRunState(_sourceDirectory, _backupDestination, _stagingRoot);
        }

        private DOPipeline.Pipeline.Pipeline BuildPipeline(
            ConcurrentDictionary<string, string> fileHashes,
            HashSet<DateTime> backupDates,
            BackupRunState runState,
            BackupBatchOptions? options = null)
        {
            return BackupPipelineBuilder.BuildBackupPipeline(
                _sourceDirectory,
                _backupDestination,
                fileHashes,
                backupDates,
                NullLogger.Instance,
                runState,
                options);
        }
    }
}
