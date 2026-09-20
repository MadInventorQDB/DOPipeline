using System.Collections.Concurrent;
using System.IO.Compression;
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
    [Theory]
    [InlineData("hashes")]
    [InlineData("dates")]
    public void StateReplacementFailureReportsFailureAndRestartCommitsOriginalBytes(string blocked)
    {
        var p = RepairPaths();
        var file = Path.Combine(p.Source, "file.txt");
        File.WriteAllText(file, "AAAA");
        var hash = Hash(file);
        File.WriteAllText(p.Hashes, "{}");
        File.WriteAllText(p.Dates, "[]");
        var world = CreateBackupWorld(p.Source, p.Destination);
        var op = world.Query<OperationComponent>().Single();
        var state = new BackupRunState(p.Source, p.Destination, p.Staging);
        using (var locked = new FileStream(blocked == "hashes" ? p.Hashes : p.Dates,
            FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination,
                new(), new(), NullLogger.Instance, state, hashesPath: p.Hashes, backupDatesPath: p.Dates)
                .Execute(world.GetAllEntities(), world);
            Assert.False(result.IsSuccess);
            Assert.Null(world.GetComponent<OperationOutcomeComponent>(op));
            Assert.Empty(Directory.GetFiles(p.Destination, "*.report.json"));
            Assert.Equal(OperationPhase.PublishedPendingState, world.GetComponent<OperationComponent>(op)!.Phase);
            Assert.True(new OperationSummarySystem().Execute(new[] { op }, world).IsSuccess);
            var outcome = world.GetComponent<OperationOutcomeComponent>(op)!;
            Assert.Equal(2, outcome.ExitCode);
            Assert.Equal(OperationOutcome.Failed, outcome.Outcome);
            using var report = JsonDocument.Parse(File.ReadAllText(outcome.ReportPath));
            Assert.Equal(2, report.RootElement.GetProperty("ExitCode").GetInt32());
            Assert.True(File.Exists(state.PublicationReceiptPath));
            Assert.Equal(blocked == "hashes" ? StateCommitStep.Pending : StateCommitStep.HashesSaved,
                state.Job!.StateCommitStep);
        }
        File.Delete(file);
        var recovered = CreateBackupWorld(p.Source, p.Destination);
        var result2 = BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination,
            DataPersistence.LoadFileHashes(p.Hashes), DataPersistence.LoadBackupDates(p.Dates), NullLogger.Instance,
            new BackupRunState(p.Source, p.Destination, p.Staging), hashesPath: p.Hashes, backupDatesPath: p.Dates)
            .Execute(recovered.GetAllEntities(), recovered);
        Assert.True(result2.IsSuccess, result2.ErrorMessage);
        Assert.Equal(hash, DataPersistence.LoadFileHashes(p.Hashes)[file]);
        Assert.Single(DataPersistence.LoadBackupDates(p.Dates));
        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        Assert.Equal("AAAA", ReadEntry(Assert.Single(Directory.GetFiles(set, "*.zip")), "file.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenameWithoutAcknowledgementValidatesFinalBytesBeforeRecovery(bool corrupt)
    {
        var p = RepairPaths();
        var file = Path.Combine(p.Source, "file.txt");
        File.WriteAllText(file, "AAAA");
        var first = new BackupRunState(p.Source, p.Destination, p.Staging,
            new ThrowAtCheckpointObserver("publication-directory-renamed"));
        var world = CreateBackupWorld(p.Source, p.Destination);
        Assert.False(BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination, new(), new(),
            NullLogger.Instance, first, hashesPath: p.Hashes, backupDatesPath: p.Dates).Execute(world.GetAllEntities(), world).IsSuccess);
        Assert.False(first.Job!.Published);
        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var archive = Assert.Single(Directory.GetFiles(set, "*.zip"));
        if (corrupt)
        {
            var bytes = File.ReadAllBytes(archive);
            bytes[bytes.Length / 2] ^= 1;
            File.WriteAllBytes(archive, bytes);
        }
        var preserved = File.ReadAllBytes(archive);
        File.WriteAllText(file, "BBBB");
        var recovered = CreateBackupWorld(p.Source, p.Destination);
        var result = BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination, new(), new(),
            NullLogger.Instance, new BackupRunState(p.Source, p.Destination, p.Staging),
            hashesPath: p.Hashes, backupDatesPath: p.Dates).Execute(recovered.GetAllEntities(), recovered);
        Assert.Equal(!corrupt, result.IsSuccess);
        Assert.Equal(preserved, File.ReadAllBytes(archive));
        Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        if (!corrupt) Assert.Equal("AAAA", ReadEntry(archive, "file.txt"));
        else Assert.False(File.Exists(p.Hashes));
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("modify")]
    [InlineData("lock")]
    public void LaterRetryNeverRehashesCapturedFile(string change)
    {
        var p = RepairPaths();
        var a = Path.Combine(p.Source, "a.txt");
        var b = Path.Combine(p.Source, "b.txt");
        File.WriteAllText(a, "AAAA");
        File.WriteAllText(b, "BBBB");
        var clock = new RepairClock();
        var world = CreateBackupWorld(p.Source, p.Destination);
        var op = world.Query<OperationComponent>().Single();
        var pipeline = BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination, new(), new(),
            NullLogger.Instance, new BackupRunState(p.Source, p.Destination, p.Staging),
            hashesPath: p.Hashes, backupDatesPath: p.Dates, clock: clock);
        using (var holdB = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(pipeline.Execute(new[] { op }, world).IsSuccess);
        var rowA = world.Query<FileWorkComponent>().Single(e => world.GetComponent<FileWorkComponent>(e)!.SourcePath == a);
        var attempt = world.GetComponent<FileAttemptResultComponent>(rowA);
        if (change == "delete") File.Delete(a);
        if (change == "modify") File.WriteAllText(a, "CCCC");
        using var holdA = change == "lock" ? new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        clock.Now = world.GetComponent<OperationWaitComponent>(op)!.WakeAtUtc;
        var result = pipeline.Execute(new[] { op }, world);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(FileWorkState.Captured, world.GetComponent<FileWorkComponent>(rowA)!.State);
        Assert.Same(attempt, world.GetComponent<FileAttemptResultComponent>(rowA));
        Assert.Null(world.GetComponent<BackupIssueComponent>(rowA));
        Assert.Equal(0, world.GetComponent<OperationOutcomeComponent>(op)!.ExitCode);
        Assert.Equal(2, world.GetComponent<OperationOutcomeComponent>(op)!.CapturedCount);
        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var values = new Dictionary<string, string>();
        foreach (var path in Directory.GetFiles(set, "*.zip"))
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                values.Add(entry.FullName, reader.ReadToEnd());
            }
        }
        Assert.Equal("AAAA", values["a.txt"]);
        Assert.Equal("BBBB", values["b.txt"]);
    }

    [Theory]
    [InlineData(FileWorkState.Captured, 1, 0, false)]
    [InlineData(FileWorkState.Unchanged, 1, 0, false)]
    [InlineData(FileWorkState.Omitted, 1, 0, false)]
    [InlineData(FileWorkState.Deferred, 1, 0, false)]
    [InlineData(FileWorkState.Discovered, 2, 0, false)]
    [InlineData(FileWorkState.Discovered, 1, 1, false)]
    [InlineData(FileWorkState.Discovered, 1, 0, true)]
    public void IneligibleHashRowsRemainUntouched(FileWorkState state, int requested, int attempted, bool otherRun)
    {
        var p = RepairPaths();
        var world = CreateBackupWorld(p.Source, p.Destination);
        var op = world.GetComponent<OperationComponent>(world.Query<OperationComponent>().Single())!;
        op.ActivePass = 1;
        op.Phase = OperationPhase.RetryPass;
        var row = new Entity();
        var work = new FileWorkComponent { RunId = otherRun ? Guid.NewGuid() : op.RunId,
            State = state, RequestedPass = requested, LastAttemptedPass = attempted, SourcePath = Path.Combine(p.Source, "missing") };
        world.SetComponent(row, work);
        var calls = 0;
        var system = new HashCalculationSystem(new(), _ => { calls++; throw new Exception("unexpected hash"); });
        Assert.True(system.Execute(row, world).IsSuccess);
        Assert.True(system.Execute(row, world).IsSuccess);
        Assert.Equal(0, calls);
        Assert.Equal(state, work.State);
        Assert.Equal(attempted, work.LastAttemptedPass);
        Assert.Null(world.GetComponent<FileAttemptResultComponent>(row));
        Assert.Null(world.GetComponent<BackupIssueComponent>(row));
    }

    [Fact]
    public void EligibleHashRowWithMissingRequiredComponentFails()
    {
        var p = RepairPaths();
        var world = CreateBackupWorld(p.Source, p.Destination);
        var row = new Entity();
        world.SetComponent(row, new FileWorkComponent { RunId = world.GetComponent<OperationComponent>(world.Query<OperationComponent>().Single())!.RunId });
        var result = new HashCalculationSystem(new()).Execute(row, world);
        Assert.False(result.IsSuccess);
        Assert.Contains("FilePathComponent", result.ErrorMessage);
    }

    private sealed class RepairClock : IClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UtcNow => Now;
    }
}
