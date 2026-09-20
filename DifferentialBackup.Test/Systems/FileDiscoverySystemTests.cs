
using Xunit;
using DifferentialBackup.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using System.IO;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;
using System.Linq;

namespace DifferentialBackup.Test.Systems
{
    public class FileDiscoverySystemTests
    {
        [Fact]
        public void Execute_FindsAllFilesInDirectory()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "FileDiscoveryTest");
            Directory.CreateDirectory(tempDir);

            var file1 = Path.Combine(tempDir, "file1.txt");
            var file2 = Path.Combine(tempDir, "file2.txt");
            File.WriteAllText(file1, "Content1");
            File.WriteAllText(file2, "Content2");

            var system = new FileDiscoverySystem(tempDir);
            var storage = new ComponentStorage();
            var entity = new Entity();

            var result = system.Execute(entity, storage);

            Assert.True(result.IsSuccess);

            var entities = storage.GetAllEntities();
            var filePaths = entities.Select(e => storage.GetComponent<FilePathComponent>(e)?.FilePath)
                                    .Where(fp => fp != null)
                                    .ToList();

            Assert.Contains(file1, filePaths);
            Assert.Contains(file2, filePaths);

            File.Delete(file1);
            File.Delete(file2);
            Directory.Delete(tempDir);
        }

        [Fact]
        public void Execute_LargeDirectory_CompletesLinearlyAndQuickly()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "FileDiscoveryScaleTest_" + System.Guid.NewGuid());
            Directory.CreateDirectory(tempDir);

            try
            {
                const int subDirCount = 10;
                const int filesPerDir = 200; // 2,000 files total
                for (var d = 0; d < subDirCount; d++)
                {
                    var sub = Path.Combine(tempDir, $"dir_{d:D2}");
                    Directory.CreateDirectory(sub);
                    for (var f = 0; f < filesPerDir; f++)
                    {
                        File.WriteAllText(Path.Combine(sub, $"file_{f:D4}.txt"), "test");
                    }
                }

                var system = new FileDiscoverySystem(tempDir);
                var storage = new ComponentStorage();
                var entity = new Entity();

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = system.Execute(entity, storage);
                stopwatch.Stop();

                Assert.True(result.IsSuccess);
                var discoveredFiles = storage.Query<FileWorkComponent>()
                    .Select(e => storage.GetComponent<FileWorkComponent>(e))
                    .Where(fw => fw != null)
                    .ToList();

                Assert.Equal(subDirCount * filesPerDir, discoveredFiles.Count);
                // Discovery of 2,000 files must easily complete in well under 3 seconds on any modern machine
                Assert.True(stopwatch.ElapsedMilliseconds < 3000,
                    $"Discovery took {stopwatch.ElapsedMilliseconds} ms, which indicates non-linear performance.");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Fact]
        public void Execute_LargeDirectory_PipelineHashAndDecision_CompletesQuickly()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "PipelineScaleTest_" + System.Guid.NewGuid());
            var backupDir = Path.Combine(Path.GetTempPath(), "PipelineScaleBackup_" + System.Guid.NewGuid());
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(backupDir);

            try
            {
                const int fileCount = 2000;
                for (var f = 0; f < fileCount; f++)
                {
                    File.WriteAllText(Path.Combine(tempDir, $"file_{f:D4}.txt"), "content_" + f);
                }

                var storage = new ComponentStorage();
                var opEntity = new Entity();
                var runId = System.Guid.NewGuid();
                storage.SetComponent(opEntity, new OperationComponent
                {
                    RunId = runId,
                    OperationType = "backup",
                    SourceDirectory = tempDir,
                    BackupDestination = backupDir,
                    Phase = OperationPhase.InitialPass,
                    ActivePass = 0
                });

                // Pipe 1: Discovery
                var discovery = new FileDiscoverySystem(tempDir);
                Assert.True(discovery.Execute(opEntity, storage).IsSuccess);

                var allEntities = storage.GetAllEntities();
                Assert.True(allEntities.Count >= fileCount);

                // Pipe 2: Hash Calculation
                var hashDict = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
                var hashSystem = new HashCalculationSystem(hashDict);
                var hashStopwatch = System.Diagnostics.Stopwatch.StartNew();

                hashSystem.BeginExecution(allEntities, storage);
                System.Threading.Tasks.Parallel.ForEach(allEntities, entity =>
                {
                    Assert.True(hashSystem.Execute(entity, storage).IsSuccess);
                });
                hashSystem.EndExecution(allEntities, storage);
                hashStopwatch.Stop();

                // 2,000 files must hash in under 5 seconds (previously took minutes)
                Assert.True(hashStopwatch.ElapsedMilliseconds < 5000,
                    $"HashCalculation took {hashStopwatch.ElapsedMilliseconds} ms, indicating bottleneck.");

                // Pipe 3: Backup Decision
                var backupDates = new System.Collections.Generic.HashSet<System.DateTime>();
                var decisionSystem = new BackupDecisionSystem(hashDict, backupDates);
                var decisionStopwatch = System.Diagnostics.Stopwatch.StartNew();

                decisionSystem.BeginExecution(allEntities, storage);
                System.Threading.Tasks.Parallel.ForEach(allEntities, entity =>
                {
                    Assert.True(decisionSystem.Execute(entity, storage).IsSuccess);
                });
                decisionSystem.EndExecution(allEntities, storage);
                decisionStopwatch.Stop();

                Assert.True(decisionStopwatch.ElapsedMilliseconds < 3000,
                    $"BackupDecision took {decisionStopwatch.ElapsedMilliseconds} ms, indicating bottleneck.");

                // Pipe 4: Part Planning
                var stagingDir = Path.Combine(tempDir, "staging");
                var runState = new DifferentialBackup.Utilities.BackupRunState(tempDir, backupDir, stagingDir);
                var planningSystem = new BackupPartPlanningSystem(
                    tempDir,
                    backupDir,
                    runState,
                    new BackupBatchOptions { MaxFilesPerPart = 100 });
                var planStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var planResult = planningSystem.Execute(allEntities, storage);
                planStopwatch.Stop();

                Assert.True(planResult.IsSuccess);
                Assert.True(planStopwatch.ElapsedMilliseconds < 3000,
                    $"PartPlanning took {planStopwatch.ElapsedMilliseconds} ms, indicating bottleneck.");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
                if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            }
        }
    }
}
