using Xunit;
using DifferentialBackup.Systems;
using DifferentialBackup.Components;
using DOPipeline.Entities;
using DOPipeline.Storage;
using System.IO;
using System.Collections.Concurrent;
using DOPipeline.Logging;
using DifferentialBackup.Test.Helpers;

namespace DifferentialBackup.Test.Systems
{
    public class DuplicateDetectionSystemTests
    {
        [Fact]
        public void Execute_FindsDuplicateFiles()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DupDetectTest");
            Directory.CreateDirectory(tempDir);
            var file1 = Path.Combine(tempDir, "a.txt");
            var file2 = Path.Combine(tempDir, "b.txt");
            var file3 = Path.Combine(tempDir, "c.txt");
            var file4 = Path.Combine(tempDir, "d.txt");
            var logPath = Path.Combine(tempDir, "dup.log");
            using (var logger = new FileConsoleLogger(logPath))
            {
                File.WriteAllText(file1, "same");
                File.WriteAllText(file2, "same");
                File.WriteAllText(file3, "same");
                File.WriteAllText(file4, "other");

            var storage = new ComponentStorage();
            var discovery = new FileDiscoverySystem(tempDir);
            var fileHashes = new ConcurrentDictionary<string, string>();
            var hashSystem = new HashCalculationSystem(fileHashes);
            var root = new Entity();
            storage.SetComponent(root, new FilePathComponent { FilePath = tempDir });
            discovery.Execute(root, storage);

            foreach (var entity in storage.GetAllEntities())
            {
                if (storage.HasComponent<FilePathComponent>(entity) && entity != root)
                {
                    hashSystem.Execute(entity, storage);
                }
            }

                var dupSystem = new DuplicateDetectionSystem();
                var result = dupSystem.Execute(root, storage);
                Assert.True(result.IsSuccess);
                var logSystem = new DuplicateLoggingSystem(logger);
                var logResult = logSystem.Execute(root, storage);
                Assert.True(logResult.IsSuccess);
                var dupComponent = storage.GetComponent<DuplicateFilesComponent>(root);
                Assert.NotNull(dupComponent);
                Assert.Single(dupComponent.Groups);
                var group = dupComponent.Groups[0];
                Assert.Contains(file1, group);
                Assert.Contains(file2, group);
                Assert.Contains(file3, group);
                Assert.DoesNotContain(file4, group);

                var logContents = TestFileHelpers.ReadAllTextShared(logPath);
                Assert.Contains(file1, logContents);
                Assert.Contains(file2, logContents);
                Assert.Contains(file3, logContents);
                Assert.DoesNotContain(file4, logContents);

                foreach (var entity in storage.GetAllEntities())
                {
                    if (entity != root && storage.HasComponent<FilePathComponent>(entity))
                    {
                        Assert.Null(storage.GetComponent<DuplicateFilesComponent>(entity));
                    }
                }
            }

            File.Delete(file1); File.Delete(file2); File.Delete(file3); File.Delete(file4); File.Delete(logPath); Directory.Delete(tempDir);
        }
    }
}
