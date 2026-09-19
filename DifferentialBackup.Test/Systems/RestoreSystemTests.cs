using DifferentialBackup.Systems;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Storage;
using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace DifferentialBackup.Test.Systems
{
    public sealed class RestoreSystemTests : IDisposable
    {
        private readonly string _basePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        private readonly string _backupDestination;
        private readonly string _restoreDestination;

        public RestoreSystemTests()
        {
            _backupDestination = Path.Combine(_basePath, "Backup");
            _restoreDestination = Path.Combine(_basePath, "Restore");
            Directory.CreateDirectory(_backupDestination);
            Directory.CreateDirectory(_restoreDestination);
        }

        [Fact]
        public void Execute_RestoresLegacySingleZipBackup()
        {
            var backupDate = new DateTime(2026, 8, 20, 15, 0, 0, DateTimeKind.Utc);
            var archivePath = Path.Combine(
                _backupDestination,
                backupDate.ToString("yyyyMMddHHmmss") + ".zip");
            CreateArchive(archivePath, ("file.txt", "legacy content"));

            var system = new RestoreSystem(_backupDestination, backupDate, _restoreDestination);
            var result = system.Execute(new Entity(), new ComponentStorage());

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal("legacy content", File.ReadAllText(Path.Combine(_restoreDestination, "file.txt")));
        }

        [Fact]
        public void Execute_RestoresAllPartsFromManifestInOrder()
        {
            var backupDate = new DateTime(2026, 8, 20, 15, 1, 0, DateTimeKind.Utc);
            var backupSet = Path.Combine(
                _backupDestination,
                backupDate.ToString("yyyyMMddHHmmss") + ".backup");
            Directory.CreateDirectory(backupSet);
            var firstPart = Path.Combine(backupSet, "part-000001.zip");
            var secondPart = Path.Combine(backupSet, "part-000002.zip");
            CreateArchive(firstPart, ("first.txt", "first"));
            CreateArchive(secondPart, ("nested/second.txt", "second"));
            var manifest = new BackupManifest
            {
                SourceDirectory = "C:\\Source",
                BackupDate = backupDate,
                PlanFingerprint = "plan",
                FileCount = 2,
                SourceBytes = 11,
                Parts =
                {
                    CreateManifestPart(1, firstPart, 1),
                    CreateManifestPart(2, secondPart, 1)
                }
            };
            File.WriteAllText(
                Path.Combine(backupSet, "manifest.json"),
                JsonSerializer.Serialize(manifest));

            var system = new RestoreSystem(_backupDestination, backupDate, _restoreDestination);
            var result = system.Execute(new Entity(), new ComponentStorage());

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal("first", File.ReadAllText(Path.Combine(_restoreDestination, "first.txt")));
            Assert.Equal("second", File.ReadAllText(Path.Combine(_restoreDestination, "nested", "second.txt")));
        }

        [Fact]
        public void Execute_BackupDateNotFound_ReturnsFailure()
        {
            var system = new RestoreSystem(
                _backupDestination,
                new DateTime(2026, 8, 20, 15, 2, 0, DateTimeKind.Utc),
                _restoreDestination);

            var result = system.Execute(new Entity(), new ComponentStorage());

            Assert.False(result.IsSuccess);
            Assert.Equal("Backup date not found.", result.ErrorMessage);
        }

        public void Dispose()
        {
            if (Directory.Exists(_basePath))
            {
                Directory.Delete(_basePath, true);
            }

            GC.SuppressFinalize(this);
        }

        private static void CreateArchive(string path, params (string Name, string Content)[] entries)
        {
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var definition in entries)
            {
                var entry = archive.CreateEntry(definition.Name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(definition.Content);
            }
        }

        private static BackupManifestPart CreateManifestPart(int partNumber, string path, int fileCount)
        {
            return new BackupManifestPart
            {
                PartNumber = partNumber,
                ArchiveFileName = Path.GetFileName(path),
                Fingerprint = "part-" + partNumber,
                FileCount = fileCount,
                SourceBytes = 5,
                ArchiveBytes = new FileInfo(path).Length
            };
        }
    }
}
