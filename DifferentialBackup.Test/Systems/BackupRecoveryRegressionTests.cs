using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using DifferentialBackup.Components;
using DifferentialBackup.Systems;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using Xunit;

namespace DifferentialBackup.Test.Systems;

public sealed partial class BackupRecoveryRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void RestartReusesAcknowledgedPartWhenSourceChanges()
    {
        var source = Path.Combine(_root, "source");
        var destination = Path.Combine(_root, "backup");
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);

        var sourceFile = Path.Combine(source, "file.txt");
        File.WriteAllText(sourceFile, "AAAA");
        var firstHash = Hash(sourceFile);
        var backupDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var options = new BackupBatchOptions
        {
            MaxSourceBytesPerPart = 1024,
            MaxFilesPerPart = 10,
            TransferQueueCapacity = 1
        };

        var firstState = new BackupRunState(source, destination, staging);
        firstState.BeginRun();
        var firstStorage = CreateWorld(source, destination, sourceFile, firstHash, backupDate);
        var firstPlanning = new BackupPartPlanningSystem(source, destination, firstState, options, NullLogger.Instance);
        Assert.True(firstPlanning.Execute(firstStorage.GetAllEntities(), firstStorage).IsSuccess);
        var firstExecution = new BackupExecutionSystem(firstState, options, NullLogger.Instance);
        Assert.True(firstExecution.Execute(firstStorage.GetAllEntities(), firstStorage).IsSuccess);

        var firstArchive = Assert.Single(Directory.GetFiles(
            Path.Combine(destination, backupDate.ToString("yyyyMMddHHmmss") + ".backup.copying"),
            "part-*.zip"));
        Assert.Equal("AAAA", ReadEntry(firstArchive, "file.txt"));

        File.WriteAllText(sourceFile, "BBBB");
        var secondState = new BackupRunState(source, destination, staging);
        secondState.BeginRun();
        var secondStorage = CreateWorld(source, destination, sourceFile, Hash(sourceFile), backupDate);
        var secondPlanning = new BackupPartPlanningSystem(source, destination, secondState, options, NullLogger.Instance);
        Assert.True(secondPlanning.Execute(secondStorage.GetAllEntities(), secondStorage).IsSuccess);
        var secondExecution = new BackupExecutionSystem(secondState, options, NullLogger.Instance);
        Assert.True(secondExecution.Execute(secondStorage.GetAllEntities(), secondStorage).IsSuccess);

        Assert.Equal("AAAA", ReadEntry(firstArchive, "file.txt"));
    }

    [Fact]
    public void PublishedStateRecoveryReconstructsHashesAndDate()
    {
        var source = Path.Combine(_root, "source-published");
        var destination = Path.Combine(_root, "backup-published");
        var staging = Path.Combine(_root, "staging-published");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var sourceFile = Path.Combine(source, "file.txt");
        File.WriteAllText(sourceFile, "published");

        var hashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var dates = new HashSet<DateTime>();
        var state = new BackupRunState(source, destination, staging);
        var pipeline = DifferentialBackup.Pipeline.BackupPipelineBuilder.BuildBackupPipeline(
            source, destination, hashes, dates, NullLogger.Instance, state,
            new BackupBatchOptions { MaxSourceBytesPerPart = 1024, MaxFilesPerPart = 10 });
        Assert.True(pipeline.Execute(new[] { new Entity() }, new ComponentStorage()).IsSuccess);

        var recoveredHashes = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var recoveredDates = new HashSet<DateTime>();
        var recoveredState = new BackupRunState(source, destination, staging);
        recoveredState.BeginRun();
        Assert.True(recoveredState.RecoverPublishedState(recoveredHashes, recoveredDates));
        Assert.Equal(hashes[sourceFile], recoveredHashes[sourceFile]);
        Assert.Single(recoveredDates);
    }

    [Fact]
    public void CorruptAcknowledgedPartFailsInsteadOfRecapturingSource()
    {
        var source = Path.Combine(_root, "source-corrupt");
        var destination = Path.Combine(_root, "backup-corrupt");
        var staging = Path.Combine(_root, "staging-corrupt");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var sourceFile = Path.Combine(source, "file.txt");
        File.WriteAllText(sourceFile, "AAAA");
        var date = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var options = new BackupBatchOptions { MaxSourceBytesPerPart = 1024, MaxFilesPerPart = 10 };
        var firstState = new BackupRunState(source, destination, staging);
        firstState.BeginRun();
        var firstStorage = CreateWorld(source, destination, sourceFile, Hash(sourceFile), date);
        Assert.True(new BackupPartPlanningSystem(source, destination, firstState, options, NullLogger.Instance)
            .Execute(firstStorage.GetAllEntities(), firstStorage).IsSuccess);
        Assert.True(new BackupExecutionSystem(firstState, options, NullLogger.Instance)
            .Execute(firstStorage.GetAllEntities(), firstStorage).IsSuccess);
        var archive = Assert.Single(Directory.GetFiles(
            Path.Combine(destination, date.ToString("yyyyMMddHHmmss") + ".backup.copying"), "part-*.zip"));
        var originalLength = new FileInfo(archive).Length;
        var originalArchiveHash = Hash(archive);
        var archiveBytes = File.ReadAllBytes(archive);
        archiveBytes[archiveBytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(archive, archiveBytes);
        Assert.Equal(originalLength, new FileInfo(archive).Length);
        Assert.NotEqual(originalArchiveHash, Hash(archive));

        File.WriteAllText(sourceFile, "BBBB");
        var secondState = new BackupRunState(source, destination, staging);
        secondState.BeginRun();
        var secondStorage = CreateWorld(source, destination, sourceFile, Hash(sourceFile), date);
        var result = new BackupPartPlanningSystem(source, destination, secondState, options, NullLogger.Instance)
            .Execute(secondStorage.GetAllEntities(), secondStorage);

        Assert.False(result.IsSuccess);
        Assert.Contains("Acknowledged backup part", result.ErrorMessage);
        Assert.Equal(originalLength, new FileInfo(archive).Length);
    }

    [Fact]
    public void RestartWithOmittedPartPreservesHigherPartNumberAndBytes()
    {
        var source = Path.Combine(_root, "source-gap");
        var destination = Path.Combine(_root, "backup-gap");
        var staging = Path.Combine(_root, "staging-gap");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var firstFile = Path.Combine(source, "a.txt");
        var secondFile = Path.Combine(source, "b.txt");
        File.WriteAllText(firstFile, "AAAA");
        File.WriteAllText(secondFile, "BBBB");
        var date = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var options = new BackupBatchOptions
        {
            MaxSourceBytesPerPart = 1024,
            MaxFilesPerPart = 1,
            TransferQueueCapacity = 1
        };

        var firstState = new BackupRunState(source, destination, staging);
        firstState.BeginRun();
        var firstStorage = CreateWorld(source, destination, firstFile, Hash(firstFile), date);
        AddFile(firstStorage, source, secondFile, Hash(secondFile), date);
        Assert.True(new BackupPartPlanningSystem(source, destination, firstState, options)
            .Execute(firstStorage.GetAllEntities(), firstStorage).IsSuccess);
        File.Delete(firstFile);
        Assert.True(new BackupExecutionSystem(firstState, options)
            .Execute(firstStorage.GetAllEntities(), firstStorage).IsSuccess);

        var stagedDirectory = Path.Combine(destination, date.ToString("yyyyMMddHHmmss") + ".backup.copying");
        var acknowledgedPart = Path.Combine(stagedDirectory, "part-000002.zip");
        Assert.True(File.Exists(acknowledgedPart));
        Assert.Equal(1, firstState.Job!.TotalParts);

        File.WriteAllText(secondFile, "CCCC");
        var secondState = new BackupRunState(source, destination, staging);
        secondState.BeginRun();
        var secondStorage = CreateWorld(source, destination, secondFile, Hash(secondFile), date);
        Assert.True(new BackupPartPlanningSystem(source, destination, secondState, options)
            .Execute(secondStorage.GetAllEntities(), secondStorage).IsSuccess);
        Assert.True(File.Exists(acknowledgedPart));
        Assert.Equal("BBBB", ReadEntry(acknowledgedPart, "b.txt"));

        var recoveredDeferred = secondStorage.Query<FileWorkComponent>()
            .Select(entity => secondStorage.GetComponent<FileWorkComponent>(entity)!)
            .Single(work => string.Equals(work.SourcePath, firstFile, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(FileWorkState.Deferred, recoveredDeferred.State);
        Assert.NotNull(secondStorage.Query<BackupIssueComponent>()
            .Select(entity => secondStorage.GetComponent<BackupIssueComponent>(entity)!)
            .SingleOrDefault(issue => string.Equals(issue.StableKey, firstFile, StringComparison.OrdinalIgnoreCase)));

        var recoveredPart = secondStorage.Query<BackupPartComponent>()
            .Single(entity => secondStorage.GetComponent<BackupPartComponent>(entity)!.PartNumber == 2);
        var recoveredCaptured = secondStorage.Query<FileWorkComponent>()
            .Select(entity => secondStorage.GetComponent<FileWorkComponent>(entity)!)
            .Single(work => string.Equals(work.SourcePath, secondFile, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(FileWorkState.Captured,
            recoveredCaptured.State);
        Assert.Equal(2, secondStorage.GetComponent<BackupPartComponent>(recoveredPart)!.PartNumber);
    }

    [Fact]
    public void StateCommitFailureDoesNotProduceTerminalSuccess()
    {
        var source = Path.Combine(_root, "source-state");
        var destination = Path.Combine(_root, "backup-state");
        var staging = Path.Combine(_root, "staging-state");
        var hashesPath = Path.Combine(_root, "hashes.json");
        var datesPath = Path.Combine(_root, "dates");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(datesPath);
        var sourceFile = Path.Combine(source, "file.txt");
        File.WriteAllText(sourceFile, "state");

        var state = new BackupRunState(source, destination, staging);
        var storage = new ComponentStorage();
        var operationEntity = new Entity();
        var runId = Guid.NewGuid();
        storage.SetComponent(operationEntity, new OperationComponent
        {
            RunId = runId,
            OperationType = "backup",
            SourceDirectory = source,
            BackupDestination = destination,
            Phase = OperationPhase.InitialPass
        });
        storage.SetComponent(operationEntity, new RetryScheduleComponent { RunId = runId });
        var hashes = new ConcurrentDictionary<string, string>();
        var dates = new HashSet<DateTime>();
        var pipeline = DifferentialBackup.Pipeline.BackupPipelineBuilder.BuildBackupPipeline(
            source,
            destination,
            hashes,
            dates,
            NullLogger.Instance,
            state,
            new BackupBatchOptions { MaxSourceBytesPerPart = 1024, MaxFilesPerPart = 10 },
            hashesPath,
            datesPath);

        var result = pipeline.Execute(new[] { operationEntity }, storage);

        Assert.False(result.IsSuccess);
        Assert.Equal(OperationPhase.PublishedPendingState,
            storage.GetComponent<OperationComponent>(operationEntity)!.Phase);
        Assert.Null(storage.GetComponent<OperationOutcomeComponent>(operationEntity));
        Assert.True(Directory.GetDirectories(destination, "*.backup").Length == 1);
        Assert.True(File.Exists(hashesPath));
        Assert.True(state.Job?.Published);

        var summary = new OperationSummarySystem().Execute(new[] { operationEntity }, storage);
        Assert.True(summary.IsSuccess);
        var failedOutcome = storage.GetComponent<OperationOutcomeComponent>(operationEntity);
        Assert.NotNull(failedOutcome);
        Assert.Equal(OperationOutcome.Failed, failedOutcome!.Outcome);
        Assert.Equal(2, failedOutcome.ExitCode);
        Assert.False(storage.GetComponent<OperationComponent>(operationEntity)!.Phase == OperationPhase.Finished);
        Assert.True(File.Exists(failedOutcome.ReportPath));
    }

    [Fact]
    public void StateCommitCompletesBeforeSuccessOutcome()
    {
        var source = Path.Combine(_root, "source-state-success");
        var destination = Path.Combine(_root, "backup-state-success");
        var staging = Path.Combine(_root, "staging-state-success");
        var hashesPath = Path.Combine(_root, "hashes-success.json");
        var datesPath = Path.Combine(_root, "dates-success.json");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "file.txt"), "state success");

        var state = new BackupRunState(source, destination, staging);
        var storage = new ComponentStorage();
        var operationEntity = new Entity();
        var runId = Guid.NewGuid();
        storage.SetComponent(operationEntity, new OperationComponent
        {
            RunId = runId,
            OperationType = "backup",
            SourceDirectory = source,
            BackupDestination = destination,
            Phase = OperationPhase.InitialPass
        });
        storage.SetComponent(operationEntity, new RetryScheduleComponent { RunId = runId });
        var hashes = new ConcurrentDictionary<string, string>();
        var dates = new HashSet<DateTime>();
        var pipeline = DifferentialBackup.Pipeline.BackupPipelineBuilder.BuildBackupPipeline(
            source,
            destination,
            hashes,
            dates,
            NullLogger.Instance,
            state,
            new BackupBatchOptions { MaxSourceBytesPerPart = 1024, MaxFilesPerPart = 10 },
            hashesPath,
            datesPath);

        var result = pipeline.Execute(new[] { operationEntity }, storage);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(OperationPhase.Finished,
            storage.GetComponent<OperationComponent>(operationEntity)!.Phase);
        Assert.Equal(OperationOutcome.Success,
            storage.GetComponent<OperationOutcomeComponent>(operationEntity)!.Outcome);
        Assert.Equal(StateCommitStep.Complete,
            storage.GetComponent<StateCommitComponent>(operationEntity)!.Step);
        Assert.True(File.Exists(hashesPath));
        Assert.True(File.Exists(datesPath));
    }

    [Fact]
    public async Task ProcessRestartAfterPublicationRenameReusesFinalSet()
    {
        var source = Path.Combine(_root, "source-process");
        var destination = Path.Combine(_root, "backup-process");
        var staging = Path.Combine(_root, "staging-process");
        var hashesPath = Path.Combine(_root, "hashes-process.json");
        var datesPath = Path.Combine(_root, "dates-process.json");
        var signalPath = Path.Combine(_root, "checkpoint.signal");
        var releasePath = Path.Combine(_root, "checkpoint.release");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "file.txt"), "process recovery");

        var hostPath = Path.Combine(AppContext.BaseDirectory, "DifferentialBackup.TestHost.dll");
        Assert.True(File.Exists(hostPath), hostPath);
        using var first = StartHost(
            hostPath,
            source,
            destination,
            staging,
            hashesPath,
            datesPath,
            "publication-directory-renamed",
            signalPath,
            releasePath);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(signalPath) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        var diagnostics = string.Empty;
        var stderrPath = signalPath + ".stderr";
        if (File.Exists(stderrPath))
        {
            diagnostics = " stderr=" + File.ReadAllText(stderrPath);
        }
        Assert.True(File.Exists(signalPath), "The child did not reach the rename checkpoint." + diagnostics);
        File.WriteAllText(Path.Combine(source, "file.txt"), "changed after publication");
        first.Kill(entireProcessTree: true);
        await first.WaitForExitAsync();

        using var second = StartHost(
            hostPath,
            source,
            destination,
            staging,
            hashesPath,
            datesPath);
        await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(0, second.ExitCode);
        Assert.Single(Directory.GetDirectories(destination, "*.backup"));
        Assert.True(File.Exists(hashesPath));
        Assert.True(File.Exists(datesPath));
        Assert.Single(System.Text.Json.JsonSerializer.Deserialize<HashSet<DateTime>>(
            File.ReadAllText(datesPath))!);
        var archive = Directory.GetFiles(
            Directory.GetDirectories(destination, "*.backup").Single(),
            "part-*.zip").Single();
        Assert.Equal("process recovery", ReadEntry(archive, "file.txt"));
    }

    [Theory]
    [InlineData("part-reservation-committed")]
    [InlineData("part-plan-committed")]
    [InlineData("part-checkpoint-committed")]
    [InlineData("part-transfer-committed")]
    [InlineData("publication-receipt-committed")]
    [InlineData("hash-state-replaced")]
    [InlineData("date-state-replaced")]
    [InlineData("state-files-saved-before-complete")]
    public async Task ProcessRestartRecoversAtEachRepairBoundary(string checkpoint)
    {
        var suffix = checkpoint.Replace('-', '_');
        var source = Path.Combine(_root, "source-boundary-" + suffix);
        var destination = Path.Combine(_root, "backup-boundary-" + suffix);
        var staging = Path.Combine(_root, "staging-boundary-" + suffix);
        var hashesPath = Path.Combine(_root, "hashes-boundary-" + suffix + ".json");
        var datesPath = Path.Combine(_root, "dates-boundary-" + suffix + ".json");
        var signalPath = Path.Combine(_root, "signal-boundary-" + suffix);
        var releasePath = Path.Combine(_root, "release-boundary-" + suffix);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "file.txt"), "boundary bytes");

        var hostPath = Path.Combine(AppContext.BaseDirectory, "DifferentialBackup.TestHost.dll");
        using var first = StartHost(
            hostPath,
            source,
            destination,
            staging,
            hashesPath,
            datesPath,
            checkpoint,
            signalPath,
            releasePath);
        await WaitForSignal(signalPath);

        first.Kill(entireProcessTree: true);
        await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        using var second = StartHost(hostPath, source, destination, staging, hashesPath, datesPath);
        await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, second.ExitCode);
        var sets = Directory.GetDirectories(destination, "*.backup");
        Assert.Single(sets);
        Assert.True(File.Exists(hashesPath));
        Assert.True(File.Exists(datesPath));
        var archive = Assert.Single(Directory.GetFiles(sets[0], "part-*.zip"));
        Assert.Equal("boundary bytes", ReadEntry(archive, "file.txt"));
    }

    [Fact]
    public void ReceiptWithOnlyWorkingDirectoryIsRenamedAndReused()
    {
        var source = Path.Combine(_root, "source-working-only");
        var destination = Path.Combine(_root, "backup-working-only");
        var staging = Path.Combine(_root, "staging-working-only");
        var hashesPath = Path.Combine(_root, "hashes-working-only.json");
        var datesPath = Path.Combine(_root, "dates-working-only.json");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var sourceFile = Path.Combine(source, "file.txt");
        File.WriteAllText(sourceFile, "working recovery");

        var firstState = new BackupRunState(
            source,
            destination,
            staging,
            new ThrowAtCheckpointObserver("publication-receipt-committed"));
        var firstStorage = CreateBackupWorld(source, destination);
        var firstHashes = new ConcurrentDictionary<string, string>();
        var firstDates = new HashSet<DateTime>();
        var firstPipeline = DifferentialBackup.Pipeline.BackupPipelineBuilder.BuildBackupPipeline(
            source,
            destination,
            firstHashes,
            firstDates,
            NullLogger.Instance,
            firstState,
            new BackupBatchOptions { MaxSourceBytesPerPart = 1024, MaxFilesPerPart = 10 },
            hashesPath,
            datesPath);

        Assert.False(firstPipeline.Execute(firstStorage.GetAllEntities(), firstStorage).IsSuccess);
        var workingDirectories = Directory.GetDirectories(destination, "*.backup.copying");
        Assert.Single(workingDirectories);
        Assert.Empty(Directory.GetDirectories(destination, "*.backup"));
        Assert.True(File.Exists(firstState.PublicationReceiptPath));

        var secondState = new BackupRunState(source, destination, staging);
        var secondStorage = CreateBackupWorld(source, destination);
        var secondHashes = new ConcurrentDictionary<string, string>();
        var secondDates = new HashSet<DateTime>();
        var secondPipeline = DifferentialBackup.Pipeline.BackupPipelineBuilder.BuildBackupPipeline(
            source,
            destination,
            secondHashes,
            secondDates,
            NullLogger.Instance,
            secondState,
            new BackupBatchOptions { MaxSourceBytesPerPart = 1024, MaxFilesPerPart = 10 },
            hashesPath,
            datesPath);

        var result = secondPipeline.Execute(secondStorage.GetAllEntities(), secondStorage);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Single(Directory.GetDirectories(destination, "*.backup"));
        Assert.Empty(Directory.GetDirectories(destination, "*.backup.copying"));
        var archive = Directory.GetFiles(Directory.GetDirectories(destination, "*.backup")[0], "part-*.zip").Single();
        Assert.Equal("working recovery", ReadEntry(archive, "file.txt"));
    }

    [Fact]
    public void SummaryUsesFatalSignalBeforeCancellation()
    {
        var storage = new ComponentStorage();
        var operation = new Entity();
        var runId = Guid.NewGuid();
        storage.SetComponent(operation, new OperationComponent
        {
            RunId = runId,
            OperationType = "backup",
            BackupDestination = _root,
            Phase = OperationPhase.Finalizing
        });
        storage.SetComponent(operation, new StateCommitComponent
        {
            RunId = runId,
            Step = StateCommitStep.Complete
        });
        storage.SetComponent(operation, new FatalFailureComponent
        {
            RunId = runId,
            Stage = "Test",
            Message = "initiating output failure"
        });
        storage.SetComponent(operation, new CancellationSignalComponent
        {
            RunId = runId,
            Reason = "cleanup cancelled"
        });

        var result = new OperationSummarySystem().Execute(new[] { operation }, storage);

        Assert.True(result.IsSuccess);
        var outcome = storage.GetComponent<OperationOutcomeComponent>(operation)!;
        Assert.Equal(OperationOutcome.Failed, outcome.Outcome);
        Assert.Equal(2, outcome.ExitCode);
        Assert.Equal("initiating output failure", outcome.ErrorMessage);
    }

    [Fact]
    public void HashingDoesNotTouchDeferredRowsUntilRetryActivation()
    {
        var source = Path.Combine(_root, "source-stale-pass");
        Directory.CreateDirectory(source);
        var path = Path.Combine(source, "captured.txt");
        File.WriteAllText(path, "captured");
        var storage = new ComponentStorage();
        var operation = new Entity();
        var file = new Entity();
        var runId = Guid.NewGuid();
        storage.SetComponent(operation, new OperationComponent
        {
            RunId = runId,
            Phase = OperationPhase.RetryPass,
            ActivePass = 1,
            SourceDirectory = source
        });
        storage.SetComponent(file, new FilePathComponent { FilePath = path });
        storage.SetComponent(file, new FileWorkComponent
        {
            RunId = runId,
            StableKey = path,
            SourcePath = path,
            RelativePath = "captured.txt",
            State = FileWorkState.Deferred,
            RequestedPass = 0,
            LastAttemptedPass = 0
        });

        var calls = 0;
        var result = new HashCalculationSystem(
            new ConcurrentDictionary<string, string>(),
            candidate =>
            {
                calls++;
                return HashUtility.TryComputeSHA256(candidate);
            }).Execute(file, storage);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, calls);
        Assert.Equal(FileWorkState.Deferred,
            storage.GetComponent<FileWorkComponent>(file)!.State);
        Assert.Equal(0,
            storage.GetComponent<FileWorkComponent>(file)!.LastAttemptedPass);
    }

    [Fact]
    public void UnacknowledgedPartialPartIsDiscardedAndRebuilt()
    {
        var source = Path.Combine(_root, "source-partial");
        var destination = Path.Combine(_root, "backup-partial");
        var staging = Path.Combine(_root, "staging-partial");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var path = Path.Combine(source, "file.txt");
        File.WriteAllText(path, "AAAA");
        var options = new BackupBatchOptions { MaxSourceBytesPerPart = 1024, MaxFilesPerPart = 10 };

        var firstState = new BackupRunState(source, destination, staging);
        firstState.BeginRun();
        var firstStorage = CreateWorld(source, destination, path, Hash(path),
            new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        var firstPlanner = new BackupPartPlanningSystem(source, destination, firstState, options);
        Assert.True(firstPlanner.Execute(firstStorage.GetAllEntities(), firstStorage).IsSuccess);
        File.WriteAllText(Path.Combine(firstState.StagingDirectory, "part-000001.zip.partial"), "partial");

        var secondState = new BackupRunState(source, destination, staging);
        secondState.BeginRun();
        var secondStorage = CreateWorld(source, destination, path, Hash(path),
            new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        var secondPlanner = new BackupPartPlanningSystem(source, destination, secondState, options);
        Assert.True(secondPlanner.Execute(secondStorage.GetAllEntities(), secondStorage).IsSuccess);
        var secondExecution = new BackupExecutionSystem(secondState, options, NullLogger.Instance);
        Assert.True(secondExecution.Execute(secondStorage.GetAllEntities(), secondStorage).IsSuccess);

        var archive = Assert.Single(Directory.GetFiles(
            Path.Combine(destination, "20260103000000.backup.copying"), "part-*.zip"));
        Assert.Equal("AAAA", ReadEntry(archive, "file.txt"));
        Assert.False(File.Exists(Path.Combine(secondState.StagingDirectory, "part-000001.zip.partial")));
    }

    [Fact]
    public void CompleteJournalCorruptionFailsButTruncatedTailIsIgnored()
    {
        var source = Path.Combine(_root, "source-journal");
        var destination = Path.Combine(_root, "backup-journal");
        var staging = Path.Combine(_root, "staging-journal");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var state = new BackupRunState(source, destination, staging);
        state.BeginRun();
        state.CreateOrUpdateJob(
            new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            new BackupBatchOptions(),
            "journal-plan",
            0,
            0,
            0,
            Guid.NewGuid());

        File.AppendAllText(state.JournalPath, "{");
        var truncated = new BackupRunState(source, destination, staging);
        truncated.BeginRun();
        truncated.SaveRetrySchedule(new RetryScheduleComponent
        {
            RunId = truncated.Job!.RunId,
            CompletedRound = 0,
            ActiveRound = 0,
            RoundInProgress = false
        });
        var resumedAfterTail = new BackupRunState(source, destination, staging);
        resumedAfterTail.BeginRun();

        File.AppendAllText(state.JournalPath, "corrupt\n");
        var corrupt = new BackupRunState(source, destination, staging);
        Assert.Throws<InvalidDataException>(() => corrupt.BeginRun());

        var completeTailSource = Path.Combine(_root, "source-journal-complete-tail");
        var completeTailDestination = Path.Combine(_root, "backup-journal-complete-tail");
        var completeTailStaging = Path.Combine(_root, "staging-journal-complete-tail");
        Directory.CreateDirectory(completeTailSource);
        Directory.CreateDirectory(completeTailDestination);
        var completeTail = new BackupRunState(
            completeTailSource,
            completeTailDestination,
            completeTailStaging);
        completeTail.BeginRun();
        completeTail.CreateOrUpdateJob(
            new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            new BackupBatchOptions(),
            "complete-tail-plan",
            0,
            0,
            0,
            Guid.NewGuid());
        File.AppendAllText(
            completeTail.JournalPath,
            System.Text.Json.JsonSerializer.Serialize(new BackupJournalRecord
            {
                Sequence = 999,
                RunId = Guid.NewGuid(),
                ChangeType = "complete-corrupt-tail",
                PayloadJson = "{}",
                Checksum = "invalid"
            }));

        Assert.Throws<InvalidDataException>(() =>
            new BackupRunState(completeTailSource, completeTailDestination, completeTailStaging).BeginRun());
    }

    [Fact]
    public void BackupExecutionPreservesInitiatingFailureWhenProgressLoggingFails()
    {
        var source = Path.Combine(_root, "source-worker-failure");
        var destination = Path.Combine(_root, "backup-worker-failure");
        var staging = Path.Combine(_root, "staging-worker-failure");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var path = Path.Combine(source, "file.txt");
        File.WriteAllText(path, "worker failure");
        var observer = new ThrowAtCheckpointObserver("part-checkpoint-committed");
        var state = new BackupRunState(source, destination, staging, observer);
        state.BeginRun();
        var storage = CreateWorld(source, destination, path, Hash(path),
            new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        var options = new BackupBatchOptions { MaxSourceBytesPerPart = 1024, MaxFilesPerPart = 10 };
        Assert.True(new BackupPartPlanningSystem(source, destination, state, options)
            .Execute(storage.GetAllEntities(), storage).IsSuccess);

        var result = new BackupExecutionSystem(state, options, new ThrowingProgressLogger())
            .Execute(storage.GetAllEntities(), storage);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsCancellation);
        Assert.IsType<IOException>(result.Exception);
        Assert.Contains("part-checkpoint-committed", result.ErrorMessage);
    }

    [Fact]
    public void UnrelatedOperationCanceledExceptionIsFatal()
    {
        var source = Path.Combine(_root, "source-unrelated-cancel");
        var destination = Path.Combine(_root, "backup-unrelated-cancel");
        var staging = Path.Combine(_root, "staging-unrelated-cancel");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var path = Path.Combine(source, "file.txt");
        File.WriteAllText(path, "unrelated cancellation");
        var expected = new OperationCanceledException("unrelated cancellation");
        var state = new BackupRunState(source, destination, staging,
            new ThrowAtCheckpointObserver("part-checkpoint-committed", expected));
        state.BeginRun();
        var storage = CreateWorld(source, destination, path, Hash(path),
            new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc));
        var options = new BackupBatchOptions { MaxSourceBytesPerPart = 1024, MaxFilesPerPart = 10 };
        Assert.True(new BackupPartPlanningSystem(source, destination, state, options)
            .Execute(storage.GetAllEntities(), storage).IsSuccess);

        var result = new BackupExecutionSystem(state, options, NullLogger.Instance)
            .Execute(storage.GetAllEntities(), storage, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsCancellation);
        Assert.Same(expected, result.Exception);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static ComponentStorage CreateWorld(
        string source,
        string destination,
        string sourceFile,
        string hash,
        DateTime backupDate)
    {
        var storage = new ComponentStorage();
        var operation = new Entity();
        storage.SetComponent(operation, new OperationComponent
        {
            RunId = Guid.NewGuid(),
            SourceDirectory = source,
            BackupDestination = destination,
            Phase = OperationPhase.InitialPass,
            BackupDate = backupDate
        });
        var file = new Entity();
        storage.SetComponent(file, new FilePathComponent { FilePath = sourceFile });
        storage.SetComponent(file, new FileWorkComponent
        {
            RunId = storage.GetComponent<OperationComponent>(operation)!.RunId,
            StableKey = sourceFile,
            SourcePath = sourceFile,
            RelativePath = "file.txt",
            State = FileWorkState.ReadyToCapture
        });
        storage.SetComponent(file, new FileHashComponent
        {
            PreviousHash = null,
            CurrentHash = hash,
            CurrentLength = new FileInfo(sourceFile).Length
        });
        storage.SetComponent(file, new BackupDateComponent { BackupDate = backupDate });
        return storage;
    }

    private static ComponentStorage CreateBackupWorld(string source, string destination)
    {
        var storage = new ComponentStorage();
        var operation = new Entity();
        var runId = Guid.NewGuid();
        storage.SetComponent(operation, new OperationComponent
        {
            RunId = runId,
            OperationType = "backup",
            SourceDirectory = source,
            BackupDestination = destination,
            Phase = OperationPhase.InitialPass,
            InitializationSucceeded = true
        });
        storage.SetComponent(operation, new RetryScheduleComponent { RunId = runId });
        return storage;
    }

    private static void AddFile(
        ComponentStorage storage,
        string source,
        string path,
        string hash,
        DateTime backupDate)
    {
        var operationEntity = storage.Query<OperationComponent>().Single();
        var operation = storage.GetComponent<OperationComponent>(operationEntity)!;
        var file = new Entity();
        storage.SetComponent(file, new FilePathComponent { FilePath = path });
        storage.SetComponent(file, new FileWorkComponent
        {
            RunId = operation.RunId,
            StableKey = path,
            SourcePath = path,
            RelativePath = Path.GetRelativePath(source, path),
            State = FileWorkState.ReadyToCapture
        });
        storage.SetComponent(file, new FileHashComponent
        {
            CurrentHash = hash,
            CurrentLength = new FileInfo(path).Length
        });
        storage.SetComponent(file, new BackupDateComponent { BackupDate = backupDate });
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ReadEntry(string archivePath, string name)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Process StartHost(
        string hostPath,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(hostPath);
        startInfo.ArgumentList.Add("backup");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)!;
        var signalPath = arguments.Length >= 7 ? arguments[6] : null;
        if (!string.IsNullOrWhiteSpace(signalPath))
        {
            _ = Task.Run(async () =>
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                File.WriteAllText(signalPath + ".stdout", output);
            });
            _ = Task.Run(async () =>
            {
                var error = await process.StandardError.ReadToEndAsync();
                File.WriteAllText(signalPath + ".stderr", error);
            });
        }
        else
        {
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
        }
        return process;
    }

    private static async Task WaitForSignal(string signalPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(signalPath) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(File.Exists(signalPath), $"Checkpoint signal was not written: {signalPath}");
    }

    private sealed class ThrowAtCheckpointObserver(
        string checkpoint,
        Exception? exception = null) : ICheckpointObserver
    {
        public void Reached(string value)
        {
            if (string.Equals(value, checkpoint, StringComparison.Ordinal))
            {
                throw exception ?? new IOException($"Injected failure at checkpoint '{checkpoint}'.");
            }
        }
    }

    private sealed class ThrowingProgressLogger : IPipelineLogger, IProgressLogger
    {
        public void Log(string message)
        {
        }

        public void ReportProgress(string message) =>
            throw new IOException("progress logger failure");

        public void Dispose()
        {
        }
    }
}
