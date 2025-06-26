using Xunit;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Components;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DifferentialBackup.Test.Helpers;
using DOPipeline.Logging;
using System.IO;
using System.Collections.Generic;

namespace DifferentialBackup.Test.Pipeline
{
    public class DuplicateDetectionPipelineBuilderTests
    {
        private readonly IPipelineLogger _logger = NullLogger.Instance;

        [Fact]
        public void BuildDuplicateDetectionPipeline_CreatesPipeline()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DupPipeBuildTest");
            Directory.CreateDirectory(tempDir);
            var pipeline = DuplicateDetectionPipelineBuilder.BuildDuplicateDetectionPipeline(tempDir, _logger);
            Assert.NotNull(pipeline);
            Directory.Delete(tempDir);
        }

        [Fact]
        public void DuplicateDetectionPipeline_Execute_FindsDuplicates()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "DupPipeExecTest");
            Directory.CreateDirectory(tempDir);
            var logPath = Path.Combine(tempDir, "dup.log");
            var file1 = Path.Combine(tempDir, "a.txt");
            var file2 = Path.Combine(tempDir, "b.txt");
            var file3 = Path.Combine(tempDir, "c.txt");
            var file4 = Path.Combine(tempDir, "d.txt");
            using (var logger = new FileConsoleLogger(logPath))
            {
                File.WriteAllText(file1, "abc");
                File.WriteAllText(file2, "abc");
                File.WriteAllText(file3, "abc");
                File.WriteAllText(file4, "xyz");

                var pipeline = DuplicateDetectionPipelineBuilder.BuildDuplicateDetectionPipeline(tempDir, logger);
                var storage = new ComponentStorage();
                var root = new Entity();
                storage.SetComponent(root, new FilePathComponent { FilePath = tempDir });
                var entities = new List<Entity> { root };
                var result = pipeline.Execute(entities, storage);
                Assert.True(result.IsSuccess);
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
