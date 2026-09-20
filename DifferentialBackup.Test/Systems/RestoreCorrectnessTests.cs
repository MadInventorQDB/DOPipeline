using System.IO.Compression;
using System.Text.Json;
using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Systems;
using DifferentialBackup.Utilities;
using DifferentialBackup.Test.Helpers;
using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using Xunit;

namespace DifferentialBackup.Test.Systems;

public sealed class RestoreCorrectnessTests : IDisposable
{
    private readonly string _tempRoot;

    public RestoreCorrectnessTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "RestoreCorrectness_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void RestoreIncompleteBackup_ReturnsWarningsAndExitCode1()
    {
        var backupDest = Path.Combine(_tempRoot, "Backup");
        var restoreDest = Path.Combine(_tempRoot, "Restore");
        Directory.CreateDirectory(backupDest);
        Directory.CreateDirectory(restoreDest);

        var date = DateTime.UtcNow;
        var timestamp = date.ToString("yyyyMMddHHmmss");
        var backupSetDir = Path.Combine(backupDest, timestamp + ".backup");
        Directory.CreateDirectory(backupSetDir);

        var archiveName = "part-000001.zip";
        var archivePath = Path.Combine(backupSetDir, archiveName);

        var sampleFile = Path.Combine(_tempRoot, "sample.txt");
        File.WriteAllText(sampleFile, "SAMPLE_CONTENT");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(sampleFile, "sample.txt");
        }

        var archiveHash = HashUtility.ComputeSHA256(archivePath);
        var archiveBytes = new FileInfo(archivePath).Length;

        var indexPath = Path.Combine(backupSetDir, "part-000001.index.json");
        File.WriteAllText(indexPath, JsonSerializer.Serialize(new
        {
            PartNumber = 1,
            Files = new[]
            {
                new { EntryName = "sample.txt", Length = new FileInfo(sampleFile).Length, Hash = HashUtility.ComputeSHA256(sampleFile) }
            }
        }));

        var indexHash = HashUtility.ComputeSHA256(indexPath);
        var indexBytes = new FileInfo(indexPath).Length;

        // Incomplete manifest: IsComplete = false
        var manifest = new BackupManifest
        {
            FormatVersion = 3,
            BackupDate = date,
            IsComplete = false,
            Parts = new List<BackupManifestPart>
            {
                new()
                {
                    PartNumber = 1,
                    ArchiveFileName = archiveName,
                    ArchiveSha256 = archiveHash,
                    ArchiveBytes = archiveBytes,
                    IndexFileName = null
                }
            }
        };
        File.WriteAllText(Path.Combine(backupSetDir, "manifest.json"), JsonSerializer.Serialize(manifest));

        var pipeline = RestorePipelineBuilder.BuildRestorePipeline(
            backupDest,
            date,
            restoreDest,
            NullLogger.Instance);

        var storage = new ComponentStorage();
        var opEntity = new Entity();
        var runId = Guid.NewGuid();
        storage.SetComponent(opEntity, new OperationComponent
        {
            RunId = runId,
            OperationType = "restore",
            BackupDestination = backupDest,
            RestoreDestination = restoreDest,
            BackupDate = date,
            Phase = OperationPhase.InitialPass,
            ActivePass = 0,
            InitializationSucceeded = true
        });
        storage.SetComponent(opEntity, new RetryScheduleComponent { RunId = runId });

        var result = pipeline.Execute(new[] { opEntity }, storage);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var outcome = storage.GetComponent<OperationOutcomeComponent>(opEntity);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcome.Warnings, outcome.Outcome);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Equal("Restored from an incomplete backup.", outcome.ErrorMessage);

        var restoredFile = Path.Combine(restoreDest, "sample.txt");
        Assert.True(File.Exists(restoredFile));
        Assert.Equal("SAMPLE_CONTENT", File.ReadAllText(restoredFile));
    }

    [Fact]
    public void RestoreDifferentArchive_DoesNotInheritPreviousArchiveRetryBudget()
    {
        var backupDest = Path.Combine(_tempRoot, "Backup");
        var restoreDest = Path.Combine(_tempRoot, "Restore");
        Directory.CreateDirectory(backupDest);
        Directory.CreateDirectory(restoreDest);

        // Pre-populate journal with an exhausted retry schedule for a foreign archive
        var foreignJournal = new RestoreJournal
        {
            ArchiveIdentity = "FOREIGN_ARCHIVE_IDENTITY_12345",
            RetrySchedule = new RestoreJournalRetrySchedule
            {
                CompletedRound = 7,
                ActiveRound = 7,
                RoundInProgress = false
            }
        };
        RestoreJournal.Save(restoreDest, foreignJournal);

        var date = DateTime.UtcNow;
        var timestamp = date.ToString("yyyyMMddHHmmss");
        var backupSetDir = Path.Combine(backupDest, timestamp + ".backup");
        Directory.CreateDirectory(backupSetDir);

        var archiveName = "part-000001.zip";
        var archivePath = Path.Combine(backupSetDir, archiveName);

        var sampleFile = Path.Combine(_tempRoot, "sample.txt");
        File.WriteAllText(sampleFile, "NEW_ARCHIVE_CONTENT");
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(sampleFile, "sample.txt");
        }

        var manifest = new BackupManifest
        {
            FormatVersion = 3,
            BackupDate = date,
            IsComplete = true,
            Parts = new List<BackupManifestPart>
            {
                new()
                {
                    PartNumber = 1,
                    ArchiveFileName = archiveName,
                    ArchiveSha256 = HashUtility.ComputeSHA256(archivePath),
                    ArchiveBytes = new FileInfo(archivePath).Length,
                    IndexFileName = "part-000001.index.json",
                    IndexSha256 = "hash",
                    IndexBytes = 10
                }
            }
        };
        File.WriteAllText(Path.Combine(backupSetDir, "manifest.json"), JsonSerializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(backupSetDir, "part-000001.index.json"), JsonSerializer.Serialize(new
        {
            PartNumber = 1,
            Files = new[]
            {
                new { EntryName = "sample.txt", Length = new FileInfo(sampleFile).Length, Hash = HashUtility.ComputeSHA256(sampleFile) }
            }
        }));

        var storage = new ComponentStorage();
        var opEntity = new Entity();
        var runId = Guid.NewGuid();
        var schedule = new RetryScheduleComponent { RunId = runId };
        storage.SetComponent(opEntity, new OperationComponent
        {
            RunId = runId,
            OperationType = "restore",
            BackupDestination = backupDest,
            RestoreDestination = restoreDest,
            BackupDate = date,
            Phase = OperationPhase.InitialPass,
            ActivePass = 0,
            InitializationSucceeded = true
        });
        storage.SetComponent(opEntity, schedule);

        var activationSystem = new RestoreRetryActivationSystem(backupDest, date, restoreDest);
        var result = activationSystem.Execute(new[] { opEntity }, storage);

        Assert.True(result.IsSuccess);
        // CompletedRound must remain 0 and NOT inherit 7 from the foreign archive journal
        Assert.Equal(0, schedule.CompletedRound);
        Assert.Equal(0, schedule.ActiveRound);
    }

    [Fact]
    public void BackupDecisionSystem_RecoveryDoesNotMutateUnrelatedRunSchedule()
    {
        var stagingDir = Path.Combine(_tempRoot, "Staging");
        Directory.CreateDirectory(stagingDir);

        var runId1 = Guid.NewGuid();
        var runId2 = Guid.NewGuid();

        var runState1 = new BackupRunState(
            Path.Combine(_tempRoot, "Source1"),
            Path.Combine(_tempRoot, "Dest1"),
            stagingDir);
        runState1.BeginRun();
        runState1.CreateOrUpdateJob(DateTime.UtcNow, new BackupBatchOptions(), "fp", 1, 1, 1024, runId1);
        runState1.SaveRetrySchedule(new RetryScheduleComponent
        {
            RunId = runId1,
            CompletedRound = 5,
            ActiveRound = 6,
            RoundInProgress = false
        });

        var storage = new ComponentStorage();

        var op1 = new Entity();
        var opComponent1 = new OperationComponent { RunId = runId1, Phase = OperationPhase.InitialPass };
        var schedule1 = new RetryScheduleComponent { RunId = runId1, CompletedRound = 0, ActiveRound = 0 };
        storage.SetComponent(op1, opComponent1);
        storage.SetComponent(op1, schedule1);

        var op2 = new Entity();
        var opComponent2 = new OperationComponent { RunId = runId2, Phase = OperationPhase.InitialPass };
        var schedule2 = new RetryScheduleComponent { RunId = runId2, CompletedRound = 0, ActiveRound = 0 };
        storage.SetComponent(op2, opComponent2);
        storage.SetComponent(op2, schedule2);

        var decisionSystem = new BackupDecisionSystem(
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(),
            new HashSet<DateTime>(),
            backupRunState: runState1);

        var entities = new List<Entity> { op1, op2 };
        var beginResult = decisionSystem.BeginExecution(entities, storage);

        Assert.True(beginResult.IsSuccess);
        // Run 1 should have its schedule recovered
        Assert.Equal(5, schedule1.CompletedRound);
        // Run 2 must remain completely untouched!
        Assert.Equal(0, schedule2.CompletedRound);
        Assert.Equal(0, schedule2.ActiveRound);
    }

    [Fact]
    public void RestoreSystem_MultipleEntries_PerformsLinearlyAndCompactsLog()
    {
        var backupDest = Path.Combine(_tempRoot, "ScaleBackup");
        var restoreDest = Path.Combine(_tempRoot, "ScaleRestore");
        Directory.CreateDirectory(backupDest);
        Directory.CreateDirectory(restoreDest);

        var date = DateTime.UtcNow;
        var timestamp = date.ToString("yyyyMMddHHmmss");
        var backupSetDir = Path.Combine(backupDest, timestamp + ".backup");
        Directory.CreateDirectory(backupSetDir);

        var archiveName = "part-000001.zip";
        var archivePath = Path.Combine(backupSetDir, archiveName);

        const int fileCount = 100;
        var filesList = new List<object>();

        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            for (var i = 0; i < fileCount; i++)
            {
                var entryName = $"file_{i:000}.txt";
                var tempFile = Path.Combine(_tempRoot, $"temp_{i}.txt");
                File.WriteAllText(tempFile, $"Content for file {i}");
                zip.CreateEntryFromFile(tempFile, entryName);
                filesList.Add(new
                {
                    EntryName = entryName,
                    Length = new FileInfo(tempFile).Length,
                    Hash = HashUtility.ComputeSHA256(tempFile)
                });
                File.Delete(tempFile);
            }
        }

        var indexPath = Path.Combine(backupSetDir, "part-000001.index.json");
        File.WriteAllText(indexPath, JsonSerializer.Serialize(new
        {
            PartNumber = 1,
            Files = filesList
        }));

        var manifest = new BackupManifest
        {
            FormatVersion = 3,
            BackupDate = date,
            IsComplete = true,
            Parts = new List<BackupManifestPart>
            {
                new()
                {
                    PartNumber = 1,
                    ArchiveFileName = archiveName,
                    ArchiveSha256 = HashUtility.ComputeSHA256(archivePath),
                    ArchiveBytes = new FileInfo(archivePath).Length,
                    IndexFileName = null
                }
            }
        };
        File.WriteAllText(Path.Combine(backupSetDir, "manifest.json"), JsonSerializer.Serialize(manifest));

        var storage = new ComponentStorage();
        var opEntity = new Entity();
        var runId = Guid.NewGuid();
        storage.SetComponent(opEntity, new OperationComponent
        {
            RunId = runId,
            OperationType = "restore",
            BackupDestination = backupDest,
            RestoreDestination = restoreDest,
            BackupDate = date,
            Phase = OperationPhase.InitialPass,
            ActivePass = 0,
            InitializationSucceeded = true
        });

        var restoreSystem = new RestoreSystem(backupDest, date, restoreDest);
        var result = restoreSystem.Execute(opEntity, storage);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        // Verify all 100 files are restored
        for (var i = 0; i < fileCount; i++)
        {
            var restored = Path.Combine(restoreDest, $"file_{i:000}.txt");
            Assert.True(File.Exists(restored), $"File {restored} must exist");
        }

        // Verify journal exists and log file is compacted away
        Assert.True(File.Exists(RestoreJournal.GetJournalPath(restoreDest)));
        Assert.False(File.Exists(RestoreJournal.GetLogPath(restoreDest)));

        var journal = RestoreJournal.Load(restoreDest);
        Assert.Equal(fileCount, journal.Successes.Count);
    }

    [Fact]
    public void RestoreJournal_InterruptedWriteAtTail_RecoversPriorRecordsAndTruncatesIncompleteTail()
    {
        var restoreDest = Path.Combine(_tempRoot, "RestoreInterrupted_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restoreDest);

        var logPath = RestoreJournal.GetLogPath(restoreDest);
        var success1 = new RestoreJournalSuccess(100, "hash1");
        RestoreJournal.AppendSuccess(restoreDest, "archive-1", "file1.txt", success1);

        var initialContent = File.ReadAllText(logPath);
        Assert.EndsWith("\n", initialContent);

        // Simulate a crash mid-write of record 2 (unterminated JSON without newline)
        File.AppendAllText(logPath, "{\"Sequence\":2,\"ArchiveIdentity\":\"archive-1\",\"EntryIdentity\":\"file2.txt\",\"Length\":200");

        var journal = RestoreJournal.Load(restoreDest, "archive-1");
        Assert.True(journal.Successes.ContainsKey("file1.txt"));
        Assert.False(journal.Successes.ContainsKey("file2.txt"));
        Assert.Equal(1, journal.LastLogSequence);

        var recoveredContent = File.ReadAllText(logPath);
        Assert.DoesNotContain("file2.txt", recoveredContent);
        Assert.EndsWith("\n", recoveredContent);

        // Subsequent append should succeed and continue with sequence 2
        var success2 = new RestoreJournalSuccess(200, "hash2");
        RestoreJournal.AppendSuccess(restoreDest, "archive-1", "file2.txt", success2, journal);
        Assert.Equal(2, journal.LastLogSequence);

        var reloaded = RestoreJournal.Load(restoreDest, "archive-1");
        Assert.Equal(2, reloaded.Successes.Count);
        Assert.True(reloaded.Successes.ContainsKey("file1.txt"));
        Assert.True(reloaded.Successes.ContainsKey("file2.txt"));
        Assert.Equal(2, reloaded.LastLogSequence);
    }

    [Fact]
    public void RestoreJournal_CompleteRecordCorruption_ThrowsAndPreservesFile()
    {
        var restoreDest = Path.Combine(_tempRoot, "RestoreCorruption_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restoreDest);

        var logPath = RestoreJournal.GetLogPath(restoreDest);
        var success1 = new RestoreJournalSuccess(4, "hash1");
        RestoreJournal.AppendSuccess(restoreDest, "archive-1", "file1.txt", success1);

        var originalText = File.ReadAllText(logPath);
        Assert.Contains("\"Length\":4", originalText);

        // Tamper with record length from 4 to 9 without updating checksum
        var corruptedText = originalText.Replace("\"Length\":4", "\"Length\":9");
        File.WriteAllText(logPath, corruptedText);

        var ex = Assert.Throws<InvalidDataException>(() => RestoreJournal.Load(restoreDest, "archive-1"));
        Assert.Contains("invalid checksum or sequence", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Verify corrupted file was preserved for forensic evidence and not truncated
        Assert.True(File.Exists(logPath));
        Assert.Equal(corruptedText, File.ReadAllText(logPath));
    }

    [Fact]
    public void RestoreJournal_SequenceGapOrTampering_ThrowsAndPreservesFile()
    {
        var restoreDest = Path.Combine(_tempRoot, "RestoreSeqGap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restoreDest);

        var logPath = RestoreJournal.GetLogPath(restoreDest);
        var record = new RestoreJournalEntryRecord
        {
            Sequence = 2, // Unexpected sequence gap (expected 1)
            ArchiveIdentity = "archive-1",
            EntryIdentity = "file1.txt",
            Length = 10,
            Hash = "hash1"
        };
        record.Checksum = RestoreJournal.ComputeRecordChecksum(record);
        var serialized = JsonSerializer.Serialize(record) + "\n";
        File.WriteAllText(logPath, serialized);

        var ex = Assert.Throws<InvalidDataException>(() => RestoreJournal.Load(restoreDest, "archive-1"));
        Assert.Contains("invalid checksum or sequence", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Verify preserved
        Assert.True(File.Exists(logPath));
        Assert.Equal(serialized, File.ReadAllText(logPath));
    }

    [Fact]
    public void RestoreSystem_MultiEntryArchive_UsesSingleReaderAndRestoresAllFiles()
    {
        var backupDest = Path.Combine(_tempRoot, "BackupMulti");
        var restoreDest = Path.Combine(_tempRoot, "RestoreMulti");
        Directory.CreateDirectory(backupDest);
        Directory.CreateDirectory(restoreDest);

        var date = DateTime.UtcNow;
        var timestamp = date.ToString("yyyyMMddHHmmss");
        var backupSetDir = Path.Combine(backupDest, timestamp + ".backup");
        Directory.CreateDirectory(backupSetDir);

        var archiveName = "part-000001.zip";
        var archivePath = Path.Combine(backupSetDir, archiveName);

        const int fileCount = 5;
        var filesList = new List<object>();

        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            for (var i = 0; i < fileCount; i++)
            {
                var entryName = $"doc_{i}.txt";
                var tempFile = Path.Combine(_tempRoot, $"temp_multi_{i}.txt");
                File.WriteAllText(tempFile, $"Multi content {i}");
                zip.CreateEntryFromFile(tempFile, entryName);
                filesList.Add(new
                {
                    EntryName = entryName,
                    Length = new FileInfo(tempFile).Length,
                    Hash = HashUtility.ComputeSHA256(tempFile)
                });
                File.Delete(tempFile);
            }
        }

        var indexPath = Path.Combine(backupSetDir, "part-000001.index.json");
        File.WriteAllText(indexPath, JsonSerializer.Serialize(new
        {
            PartNumber = 1,
            Files = filesList
        }));

        var manifest = new BackupManifest
        {
            FormatVersion = 3,
            BackupDate = date,
            IsComplete = true,
            Parts = new List<BackupManifestPart>
            {
                new()
                {
                    PartNumber = 1,
                    ArchiveFileName = archiveName,
                    ArchiveSha256 = HashUtility.ComputeSHA256(archivePath)!,
                    ArchiveBytes = new FileInfo(archivePath).Length,
                    IndexFileName = string.Empty
                }
            }
        };
        File.WriteAllText(Path.Combine(backupSetDir, "manifest.json"), JsonSerializer.Serialize(manifest));

        var storage = new ComponentStorage();
        var opEntity = new Entity();
        var runId = Guid.NewGuid();
        storage.SetComponent(opEntity, new OperationComponent
        {
            RunId = runId,
            OperationType = "restore",
            BackupDestination = backupDest,
            RestoreDestination = restoreDest,
            BackupDate = date,
            Phase = OperationPhase.InitialPass,
            ActivePass = 0,
            InitializationSucceeded = true
        });

        var restoreSystem = new RestoreSystem(backupDest, date, restoreDest);
        var result = restoreSystem.Execute(opEntity, storage);

        Assert.True(result.IsSuccess, result.ErrorMessage);

        for (var i = 0; i < fileCount; i++)
        {
            var restored = Path.Combine(restoreDest, $"doc_{i}.txt");
            Assert.True(File.Exists(restored), $"File {restored} must exist");
            Assert.Equal($"Multi content {i}", File.ReadAllText(restored));
        }

        var journal = RestoreJournal.Load(restoreDest);
        Assert.Equal(fileCount, journal.Successes.Count);
    }

    [Fact]
    public void RestoreDestination_WhenJunctionOrReparsePoint_IsRejected()
    {
        var backupDest = Path.Combine(_tempRoot, "BackupJunction");
        var realTarget = Path.Combine(_tempRoot, "RealTarget");
        var junctionDest = Path.Combine(_tempRoot, "JunctionDest");
        Directory.CreateDirectory(backupDest);
        Directory.CreateDirectory(realTarget);

        if (!TryCreateJunction(junctionDest, realTarget))
        {
            // If the platform/filesystem does not support junctions, skip.
            return;
        }

        try
        {
            var date = DateTime.UtcNow;
            var timestamp = date.ToString("yyyyMMddHHmmss");
            var backupSetDir = Path.Combine(backupDest, timestamp + ".backup");
            Directory.CreateDirectory(backupSetDir);

            var archiveName = "part-000001.zip";
            var archivePath = Path.Combine(backupSetDir, archiveName);

            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var tempFile = Path.Combine(_tempRoot, "temp_junc.txt");
                File.WriteAllText(tempFile, "Junction test content");
                zip.CreateEntryFromFile(tempFile, "secret.txt");
                File.Delete(tempFile);
            }

            var manifest = new BackupManifest
            {
                FormatVersion = 3,
                BackupDate = date,
                IsComplete = true,
                Parts = new List<BackupManifestPart>
                {
                    new()
                    {
                        PartNumber = 1,
                        ArchiveFileName = archiveName,
                        ArchiveSha256 = HashUtility.ComputeSHA256(archivePath)!,
                        ArchiveBytes = new FileInfo(archivePath).Length,
                        IndexFileName = string.Empty
                    }
                }
            };
            File.WriteAllText(Path.Combine(backupSetDir, "manifest.json"), JsonSerializer.Serialize(manifest));

            var storage = new ComponentStorage();
            var opEntity = new Entity();
            var runId = Guid.NewGuid();
            storage.SetComponent(opEntity, new OperationComponent
            {
                RunId = runId,
                OperationType = "restore",
                BackupDestination = backupDest,
                RestoreDestination = junctionDest,
                BackupDate = date,
                Phase = OperationPhase.InitialPass,
                ActivePass = 0,
                InitializationSucceeded = true
            });

            var restoreSystem = new RestoreSystem(backupDest, date, junctionDest);
            var result = restoreSystem.Execute(opEntity, storage);

            Assert.False(result.IsSuccess);
            Assert.Contains("reparse point", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("JunctionDest", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // Assert that target was never written to
            Assert.Empty(Directory.GetFiles(realTarget));
        }
        finally
        {
            try
            {
                if (Directory.Exists(junctionDest))
                {
                    Directory.Delete(junctionDest);
                }
            }
            catch
            {
            }
        }
    }

    private static bool TryCreateJunction(string junctionPath, string targetPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(5000);
            return Directory.Exists(junctionPath) &&
                   (File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }
}
