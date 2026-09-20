using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Utilities;
using DifferentialBackup.Test.Helpers;
using Xunit;

namespace DifferentialBackup.Test.Pipeline;

public sealed class IncompleteBackupPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void LockedSourcePublishesHealthyFilesAsIncompleteAfterSevenRounds()
    {
        var source = Path.Combine(_root, "source");
        var destination = Path.Combine(_root, "backup");
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var healthy = Path.Combine(source, "healthy.txt");
        var locked = Path.Combine(source, "locked.txt");
        File.WriteAllText(healthy, "healthy");
        File.WriteAllText(locked, "locked");

        using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var hashes = new ConcurrentDictionary<string, string>();
        var dates = new HashSet<DateTime>();
        var runState = new BackupRunState(source, destination, staging);
        var storage = new ComponentStorage();
        var root = new Entity();
        var runId = Guid.NewGuid();
        storage.SetComponent(root, new OperationComponent
        {
            RunId = runId,
            SourceDirectory = source,
            BackupDestination = destination,
            Phase = OperationPhase.InitialPass
        });
        storage.SetComponent(root, new RetryScheduleComponent
        {
            RunId = runId,
            DelayMinutes = new[] { 0, 0, 0, 0, 0, 0, 0 }
        });

        var pipeline = BackupPipelineBuilder.BuildBackupPipeline(
            source,
            destination,
            hashes,
            dates,
            NullLogger.Instance,
            runState, hashesPath: Path.Combine(_root, "hashes.json"), backupDatesPath: Path.Combine(_root, "dates.json"));
        var entities = new[] { root };
        var result = pipeline.Execute(entities, storage);
        Assert.True(result.IsSuccess, result.ErrorMessage);

        for (var round = 1; round <= 7; round++)
        {
            var operation = storage.GetComponent<OperationComponent>(root)!;
            var schedule = storage.GetComponent<RetryScheduleComponent>(root)!;
            operation.Phase = OperationPhase.RetryPass;
            operation.ActivePass = round;
            storage.SetComponent(root, operation);
            Assert.True(pipeline.Execute(entities, storage).IsSuccess);
            if (operation.Phase == OperationPhase.PreparingPublication)
            {
                break;
            }
        }

        Assert.Equal(OperationPhase.Finished, storage.GetComponent<OperationComponent>(root)!.Phase);
        var outcome = storage.GetComponent<OperationOutcomeComponent>(root);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcome.IncompleteBackup, outcome!.Outcome);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains(healthy, hashes.Keys);
        Assert.DoesNotContain(locked, hashes.Keys);
        var backupSet = Assert.Single(Directory.GetDirectories(destination, "*.backup"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(backupSet, "manifest.json")));
        Assert.NotNull(manifest);
        Assert.False(manifest!.IsComplete);
        Assert.Contains(locked, manifest.Issues.Select(issue => issue.Path));
        using var archive = ZipFile.OpenRead(Path.Combine(backupSet, "part-000001.zip"));
        Assert.NotNull(archive.GetEntry("healthy.txt"));
        Assert.Null(archive.GetEntry("locked.txt"));
    }

    [Fact]
    public void SourceRecoveredOnRetryIsAppendedWithoutDuplicatingCapturedEntry()
    {
        var source = Path.Combine(_root, "source-recovery");
        var destination = Path.Combine(_root, "backup-recovery");
        var staging = Path.Combine(_root, "staging-recovery");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var healthy = Path.Combine(source, "healthy.txt");
        var locked = Path.Combine(source, "locked.txt");
        File.WriteAllText(healthy, "healthy");
        File.WriteAllText(locked, "locked");
        var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var hashes = new ConcurrentDictionary<string, string>();
        var dates = new HashSet<DateTime>();
        var runState = new BackupRunState(source, destination, staging);
        var storage = new ComponentStorage();
        var root = new Entity();
        var runId = Guid.NewGuid();
        storage.SetComponent(root, new OperationComponent
        {
            RunId = runId,
            SourceDirectory = source,
            BackupDestination = destination,
            Phase = OperationPhase.InitialPass
        });
        storage.SetComponent(root, new RetryScheduleComponent
        {
            RunId = runId,
            DelayMinutes = new[] { 0, 0, 0, 0, 0, 0, 0 }
        });
        var pipeline = BackupPipelineBuilder.BuildBackupPipeline(
            source, destination, hashes, dates, NullLogger.Instance, runState,
            hashesPath: Path.Combine(_root, "hashes.json"), backupDatesPath: Path.Combine(_root, "dates.json"));
        var entities = new[] { root };
        var initialResult = pipeline.Execute(entities, storage);
        Assert.True(initialResult.IsSuccess, initialResult.ErrorMessage);

        var operation = storage.GetComponent<OperationComponent>(root)!;
        operation.Phase = OperationPhase.RetryPass;
        operation.ActivePass = 1;
        storage.SetComponent(root, operation);
        Assert.True(pipeline.Execute(entities, storage).IsSuccess);
        held.Dispose();

        var firstPartEntity = storage.Query<BackupPartComponent>()
            .First(entity => storage.GetComponent<BackupPartComponent>(entity)!.PartNumber == 1);
        Assert.Equal(BackupPartState.Transferred, storage.GetComponent<BackupPartStatusComponent>(firstPartEntity)!.State);
        var firstPart = storage.GetComponent<BackupPartComponent>(firstPartEntity)!;
        var firstCheckpoint = runState.LoadPartCheckpoint(1);
        Assert.NotNull(firstCheckpoint);
        Assert.Equal(firstPart.Fingerprint, firstCheckpoint!.Fingerprint);
        Assert.Equal(firstPart.SourceBytes, firstCheckpoint.SourceBytes);
        Assert.Equal(firstPart.Files.Count, firstCheckpoint.FileCount);
        Assert.Single(storage.Query<BackupPartComponent>());
        operation.Phase = OperationPhase.RetryPass;
        operation.ActivePass = 2;
        storage.SetComponent(root, operation);
        var recoveredResult = pipeline.Execute(entities, storage);
        Assert.True(recoveredResult.IsSuccess, recoveredResult.ErrorMessage);

        var outcome = storage.GetComponent<OperationOutcomeComponent>(root);
        Assert.NotNull(outcome);
        Assert.Equal(OperationOutcome.Success, outcome!.Outcome);
        Assert.Contains(healthy, hashes.Keys);
        Assert.Contains(locked, hashes.Keys);
        var backupSet = Assert.Single(Directory.GetDirectories(destination, "*.backup"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(backupSet, "manifest.json")))!;
        Assert.True(manifest.IsComplete);
        Assert.Equal(2, manifest.FileCount);
        var entries = new List<string>();
        foreach (var path in Directory.GetFiles(backupSet, "part-*.zip"))
        {
            using var archive = ZipFile.OpenRead(path);
            entries.AddRange(archive.Entries.Select(entry => entry.FullName));
        }
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, entries.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
