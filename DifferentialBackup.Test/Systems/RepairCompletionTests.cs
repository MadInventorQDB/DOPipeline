using System.Collections.Concurrent;
using System.Text.Json;
using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Systems;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Storage;
using Xunit;

namespace DifferentialBackup.Test.Systems;

public sealed partial class BackupRecoveryRegressionTests
{
    [Fact]
    public void CompleteJournalTailWithoutNewlineCanBeFollowedByAnotherRecord()
    {
        var p = RepairPaths();
        var state = new BackupRunState(p.Source, p.Destination, p.Staging);
        state.BeginRun();
        state.CreateOrUpdateJob(DateTime.UtcNow, new(), "", 0, 0, 0, Guid.NewGuid());
        File.WriteAllText(state.JournalPath, File.ReadAllText(state.JournalPath).TrimEnd('\r', '\n'));
        var resumed = new BackupRunState(p.Source, p.Destination, p.Staging);
        resumed.BeginRun();
        resumed.ReservePartNumber(7);
        var final = new BackupRunState(p.Source, p.Destination, p.Staging);
        final.BeginRun();
        Assert.Equal(7, final.HighestReservedPartNumber);
    }

    private (string Source, string Destination, string Staging, string Hashes, string Dates) RepairPaths()
    {
        var source = Path.Combine(_root, "source");
        var destination = Path.Combine(_root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        return (source, destination, Path.Combine(_root, "staging"),
            Path.Combine(_root, "hashes.json"), Path.Combine(_root, "dates.json"));
    }

    [Theory]
    [InlineData(StateCommitStep.HashesSaved)]
    [InlineData(StateCommitStep.DatesSaved)]
    [InlineData(StateCommitStep.Complete)]
    public void RecoveryHonorsAcknowledgedStateWrites(StateCommitStep step)
    {
        var p = RepairPaths();
        var file = Path.Combine(p.Source, "file.txt");
        File.WriteAllText(file, "AAAA");
        var expectedHash = Hash(file);
        var world = CreateBackupWorld(p.Source, p.Destination);
        var first = new BackupRunState(p.Source, p.Destination, p.Staging,
            new ThrowAtCheckpointObserver("state-commit-" + step));
        var result = BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination,
            new(), new(), NullLogger.Instance, first, hashesPath: p.Hashes, backupDatesPath: p.Dates)
            .Execute(world.GetAllEntities(), world);
        Assert.False(result.IsSuccess);
        Assert.Null(world.GetComponent<OperationOutcomeComponent>(world.Query<OperationComponent>().Single()));
        File.WriteAllText(file, "BBBB");
        // A read-only sharing handle permits recovery reads but prevents replacing
        // the already acknowledged hash file. Recovery must not rewrite it.
        using var protectedHash = new FileStream(p.Hashes, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var protectedDates = step >= StateCommitStep.DatesSaved
            ? new FileStream(p.Dates, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var resumed = CreateBackupWorld(p.Source, p.Destination);
        result = BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination,
            DataPersistence.LoadFileHashes(p.Hashes), DataPersistence.LoadBackupDates(p.Dates),
            NullLogger.Instance, new BackupRunState(p.Source, p.Destination, p.Staging),
            hashesPath: p.Hashes, backupDatesPath: p.Dates).Execute(resumed.GetAllEntities(), resumed);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(expectedHash, DataPersistence.LoadFileHashes(p.Hashes)[file]);
        Assert.Single(DataPersistence.LoadBackupDates(p.Dates));
        Assert.Equal(OperationOutcome.Success, resumed.GetComponent<OperationOutcomeComponent>(resumed.Query<OperationComponent>().Single())!.Outcome);
    }

    [Theory]
    [InlineData("part-plan")]
    [InlineData("part-checkpoint")]
    [InlineData("publication-receipt")]
    public void DurableJournalReplaysMissingMaterializedRecord(string kind)
    {
        var p = RepairPaths();
        File.WriteAllText(Path.Combine(p.Source, "file.txt"), "AAAA");
        var first = new BackupRunState(p.Source, p.Destination, p.Staging,
            new ThrowAtCheckpointObserver(kind + "-committed"));
        var world = CreateBackupWorld(p.Source, p.Destination);
        Assert.False(BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination, new(), new(),
            NullLogger.Instance, first, hashesPath: p.Hashes, backupDatesPath: p.Dates)
            .Execute(world.GetAllEntities(), world).IsSuccess);
        var path = kind switch
        {
            "part-plan" => first.GetPartPlanPath(1),
            "part-checkpoint" => first.GetPartCheckpointPath(1),
            _ => first.PublicationReceiptPath
        };
        var expected = File.ReadAllText(path);
        File.Delete(path);
        var recovered = new BackupRunState(p.Source, p.Destination, p.Staging);
        recovered.BeginRun();
        Assert.True(File.Exists(path), "A flushed journal record must reconstruct its missing materialized view.");
        Assert.Equal(expected, File.ReadAllText(path));
    }

    [Fact]
    public void RecoveryPreservesUnexpectedUnownedFinalArchive()
    {
        var p = RepairPaths();
        var state = new BackupRunState(p.Source, p.Destination, p.Staging);
        state.BeginRun();
        state.CreateOrUpdateJob(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new(), "", 0, 0, 0, Guid.NewGuid());
        var unexpected = Path.Combine(state.StagingDirectory, "part-000042.zip");
        File.WriteAllText(unexpected, "irreplaceable evidence");
        var world = CreateBackupWorld(p.Source, p.Destination);
        var result = new BackupRecoverySystem(new(), new(), state).Execute(world.GetAllEntities(), world);
        Assert.False(result.IsSuccess);
        Assert.Equal("irreplaceable evidence", File.ReadAllText(unexpected));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("directory")]
    [InlineData("issue")]
    public void HashingSelectsFileRoleBeforeAccessingPaths(string role)
    {
        var p = RepairPaths();
        var world = CreateBackupWorld(p.Source, p.Destination);
        var operation = world.Query<OperationComponent>().Single();
        var row = role == "operation" ? operation : new Entity();
        var runId = world.GetComponent<OperationComponent>(operation)!.RunId;
        if (role == "directory") world.SetComponent(row, new DirectoryWorkComponent { RunId = runId });
        if (role == "issue") world.SetComponent(row, new BackupIssueComponent { RunId = runId });
        var calls = 0;
        var result = new HashCalculationSystem(new(), _ => { calls++; throw new Exception("must not read"); }).Execute(row, world);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(0, calls);
        Assert.Null(world.GetComponent<FileAttemptResultComponent>(row));
    }
}
