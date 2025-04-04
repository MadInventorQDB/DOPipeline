using Xunit;
using DifferentialBackup.Pipeline;
using DOPipeline.Storage;
using DOPipeline.Entities;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DifferentialBackup.Components;
using System.Reflection;
using DifferentialBackup.Test.Helpers;
using System;
using DOPipeline.Logging;
using DifferentialBackup.Utilities;
//using System.Threading; // --- REMOVED ---
using System.Globalization; // <-- Added for DateTime parsing

namespace DifferentialBackup.Test.Pipeline
{
    public class BackupPipelineBuilderTests : IDisposable
    {
        private readonly string _testBasePath;
        private readonly string _sourceDirectory;
        private readonly string _backupDestination;
        private readonly IPipelineLogger _logger;
        // Use the same format string as the production code for consistency
        private const string BackupFolderFormat = "yyyyMMddHHmmss";

        public BackupPipelineBuilderTests()
        {
            _logger = NullLogger.Instance;
            _testBasePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            _sourceDirectory = Path.Combine(_testBasePath, "Source");
            _backupDestination = Path.Combine(_testBasePath, "Backup");
            Directory.CreateDirectory(_sourceDirectory);
            Directory.CreateDirectory(_backupDestination);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_testBasePath)) { Directory.Delete(_testBasePath, true); } } catch (Exception ex) { Console.WriteLine($"WARN: Cleanup failed: {ex.Message}"); }
            GC.SuppressFinalize(this);
        }


        [Fact]
        public void BuildBackupPipeline_CreatesPipelineWithCorrectNumberOfPipes()
        {
            var fileHashes = new Dictionary<string, string>(); var backupDates = new HashSet<System.DateTime>();
            var pipeline = BackupPipelineBuilder.BuildBackupPipeline(_sourceDirectory, _backupDestination, fileHashes, backupDates, _logger);
            Assert.NotNull(pipeline); var pipesField = typeof(DOPipeline.Pipeline.Pipeline).GetField("_pipes", BindingFlags.NonPublic | BindingFlags.Instance); Assert.NotNull(pipesField); var pipes = pipesField.GetValue(pipeline) as List<DOPipeline.Pipeline.Pipe>; Assert.NotNull(pipes); Assert.Equal(4, pipes.Count);
        }

        [Fact]
        public void BackupPipeline_Execute_Success_WhenFilesChange()
        {
            // --- Arrange: Common Setup ---
            var sourceFile = Path.Combine(_sourceDirectory, "file1.txt");
            File.WriteAllText(sourceFile, "Initial Content");
            var fileHashes = new Dictionary<string, string>(); // Persisted state
            var backupDates = new HashSet<System.DateTime>(); // Persisted state
            var pipeline = BackupPipelineBuilder.BuildBackupPipeline(_sourceDirectory, _backupDestination, fileHashes, backupDates, _logger);

            // --- Run 1 ---
            var storage1 = new ComponentStorage();
            var initialEntity1 = new Entity();
            var entities1 = new List<Entity> { initialEntity1 };
            Console.WriteLine($"TEST: --- Executing Pipeline Run 1 ---");
            var result1 = pipeline.Execute(entities1, storage1);

            // --- Assert Run 1 & Get Time Info ---
            Console.WriteLine($"TEST: Asserting Pipeline Run 1 results...");
            Assert.True(result1.IsSuccess);
            Assert.Single(fileHashes);
            Assert.True(fileHashes.ContainsKey(sourceFile));
            Assert.Single(backupDates);
            var backupFolders1 = Directory.GetDirectories(_backupDestination);
            Assert.Single(backupFolders1); // Verify folder created
            var firstBackupFolderName = Path.GetFileName(backupFolders1[0]);
            // Parse the date used for the first folder name
            Assert.True(DateTime.TryParseExact(firstBackupFolderName, BackupFolderFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dateUsedInRun1), "Could not parse DateTime from first backup folder name.");
            var formatStringRun1 = dateUsedInRun1.ToString(BackupFolderFormat); // Get the exact string used
            Console.WriteLine($"TEST: Run 1 completed. Folder Name: {firstBackupFolderName}, Parsed Date Used: {dateUsedInRun1:o}, Format String: {formatStringRun1}");


            // --- Arrange Run 2: Modify file ---
            Console.WriteLine($"TEST: Modifying file: {sourceFile}");
            File.WriteAllText(sourceFile, "Modified Content");

            // --- Arrange Run 2: WAIT for next second ---
            Console.WriteLine($"TEST: Waiting for next second to ensure different folder name...");
            DateTime startTimeWait = DateTime.UtcNow;
            while (DateTime.UtcNow.ToString(BackupFolderFormat) == formatStringRun1)
            {
                // Spin briefly to avoid pegging CPU, but don't sleep long
                // Task.Delay is better than Thread.Sleep if async context allowed, but SpinWait is fine here.
                System.Threading.SpinWait.SpinUntil(() => false, 10); // Spin for ~10ms

                // Add a timeout safeguard
                if ((DateTime.UtcNow - startTimeWait).TotalSeconds > 5)
                {
                    Assert.Fail("Timeout waiting for DateTime format string to change.");
                }
            }
            var formatStringRun2Check = DateTime.UtcNow.ToString(BackupFolderFormat);
            Console.WriteLine($"TEST: Wait complete. Current format string: {formatStringRun2Check} (Different from {formatStringRun1})");
            Assert.NotEqual(formatStringRun1, formatStringRun2Check); // Verify the wait worked

            // --- Arrange Run 2: Reset *transient* state ONLY ---
            var storage2 = new ComponentStorage();
            var initialEntity2 = new Entity();
            var entities2 = new List<Entity> { initialEntity2 };

            // --- Run 2 ---
            Console.WriteLine($"TEST: --- Executing Pipeline Run 2 ---");
            var result2 = pipeline.Execute(entities2, storage2);

            // --- Assert Run 2 ---
            Console.WriteLine($"TEST: Asserting Pipeline Run 2 results...");
            Assert.True(result2.IsSuccess);
            Assert.Single(fileHashes); // File count still 1
            // Hash should be updated (verify read after write just in case)
            var hashReadAfterWrite = HashUtility.ComputeSHA256(sourceFile);
            Assert.Equal(hashReadAfterWrite, fileHashes[sourceFile]);
            Assert.Equal(2, backupDates.Count); // Date should have been added

            // Check folder count - THIS SHOULD NOW PASS RELIABLY
            var backupFolders2 = Directory.GetDirectories(_backupDestination).OrderBy(d => d).ToList();
            if (backupFolders2.Count != 2)
            {
                Console.WriteLine($"TEST_FAIL: Expected 2 backup folders, but found {backupFolders2.Count}. Folders: [{string.Join(", ", backupFolders2)}]");
            }
            Assert.Equal(2, backupFolders2.Count);

            // Verify content of the second backup folder
            var secondBackupFolderName = Path.GetFileName(backupFolders2[1]);
            Assert.NotEqual(firstBackupFolderName, secondBackupFolderName); // Ensure folder names are different
            var backedUpFile2 = Path.Combine(backupFolders2[1], "file1.txt");
            Assert.True(File.Exists(backedUpFile2), $"Backup file '{backedUpFile2}' should exist after second run.");
            Assert.Equal("Modified Content", File.ReadAllText(backedUpFile2));
            Console.WriteLine($"TEST: Run 2 completed assertions successfully.");
        }

        // Test 'BackupPipeline_Execute_NoBackup_WhenFilesUnchanged' remains the same
        [Fact]
        public void BackupPipeline_Execute_NoBackup_WhenFilesUnchanged()
        {
            // Arrange
            var sourceFile = Path.Combine(_sourceDirectory, "file_unchanged.txt");
            File.WriteAllText(sourceFile, "Stable Content");
            var fileHashes = new Dictionary<string, string>();
            var backupDates = new HashSet<System.DateTime>();
            var pipeline = BackupPipelineBuilder.BuildBackupPipeline(_sourceDirectory, _backupDestination, fileHashes, backupDates, _logger);
            var storage = new ComponentStorage();
            var entities = new List<Entity> { new Entity() };

            // Act: First execution
            pipeline.Execute(entities, storage);

            // Assert: Sanity check first run
            Assert.Single(fileHashes);
            Assert.Single(backupDates);
            Assert.Single(Directory.GetDirectories(_backupDestination));
            var initialHash = fileHashes[sourceFile];
            var initialBackupDateCount = backupDates.Count;

            // Arrange: Prepare for second run
            // No need to wait here, as no change should occur anyway
            entities = new List<Entity> { new Entity() };
            storage = new ComponentStorage(); // Reset storage

            // Act: Second execution
            var result2 = pipeline.Execute(entities, storage);

            // Assert: Second execution
            Assert.True(result2.IsSuccess);
            Assert.Single(fileHashes);
            Assert.Equal(initialHash, fileHashes[sourceFile]); // Hash unchanged
            Assert.Equal(initialBackupDateCount, backupDates.Count); // Date count unchanged
            Assert.Single(Directory.GetDirectories(_backupDestination)); // Folder count unchanged
        }
    }
}