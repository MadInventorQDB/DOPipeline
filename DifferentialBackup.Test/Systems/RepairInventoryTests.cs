using System.Collections.Concurrent;
using System.Text.Json;
using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Systems;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Storage;
using Xunit;

namespace DifferentialBackup.Test.Systems;

public sealed partial class BackupRecoveryRegressionTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("4")]
    [InlineData("1,3,4")]
    public void OmittedReservationsSurviveSeveralGapsIncludingHighestPart(string omittedNumbers)
    {
        var p = RepairPaths();
        var missing = omittedNumbers.Split(',').Select(int.Parse).ToHashSet();
        var date = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var paths = Enumerable.Range(1, 4).Select(n => Path.Combine(p.Source, $"{n}.txt")).ToArray();
        for (var n = 1; n <= 4; n++) File.WriteAllText(paths[n - 1], new string((char)('A' + n - 1), 4));
        var world = CreateWorld(p.Source, p.Destination, paths[0], Hash(paths[0]), date);
        foreach (var path in paths.Skip(1)) AddFile(world, p.Source, path, Hash(path), date);
        var options = new BackupBatchOptions { MaxFilesPerPart = 1, MaxSourceBytesPerPart = 1024 };
        var first = new BackupRunState(p.Source, p.Destination, p.Staging);
        first.BeginRun();
        Assert.True(new BackupPartPlanningSystem(p.Source, p.Destination, first, options).Execute(world.GetAllEntities(), world).IsSuccess);
        foreach (var n in missing) File.Delete(paths[n - 1]);
        Assert.True(new BackupExecutionSystem(first, options).Execute(world.GetAllEntities(), world).IsSuccess);
        Assert.Equal(4 - missing.Count, first.Job!.TotalParts);
        Assert.Equal(4, first.HighestReservedPartNumber);
        foreach (var path in paths.Where(File.Exists)) File.Delete(path);
        var recovered = CreateBackupWorld(p.Source, p.Destination);
        var state = new BackupRunState(p.Source, p.Destination, p.Staging);
        Assert.True(new BackupRecoverySystem(new(), new(), state).Execute(recovered.GetAllEntities(), recovered).IsSuccess);
        Assert.Equal(4 - missing.Count, recovered.Query<FileWorkComponent>().Count(e => recovered.GetComponent<FileWorkComponent>(e)!.State == FileWorkState.Captured));
        var extra = Path.Combine(p.Source, "extra.txt");
        File.WriteAllText(extra, "EXTRA");
        AddFile(recovered, p.Source, extra, Hash(extra), date);
        Assert.True(new BackupPartPlanningSystem(p.Source, p.Destination, state, options).Execute(recovered.GetAllEntities(), recovered).IsSuccess);
        Assert.Equal(5, state.HighestReservedPartNumber);
        Assert.True(new BackupExecutionSystem(state, options).Execute(recovered.GetAllEntities(), recovered).IsSuccess);
        var op = recovered.Query<OperationComponent>().Single();
        recovered.GetComponent<OperationComponent>(op)!.Phase = OperationPhase.PreparingPublication;
        var published = new BackupPublicationSystem(new(), new(), state).Execute(recovered.GetAllEntities(), recovered);
        Assert.True(published.IsSuccess, published.ErrorMessage);
        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        foreach (var n in Enumerable.Range(1, 4).Except(missing))
            Assert.Equal(new string((char)('A' + n - 1), 4), ReadEntry(Path.Combine(set, $"part-{n:000000}.zip"), $"{n}.txt"));
        Assert.Equal("EXTRA", ReadEntry(Path.Combine(set, "part-000005.zip"), "extra.txt"));
    }

    [Fact]
    public void MissingAcknowledgedArchiveFailsBeforeDeletingEarlierUnacknowledgedOutput()
    {
        var p = RepairPaths();
        var a = Path.Combine(p.Source, "a.txt");
        var b = Path.Combine(p.Source, "b.txt");
        File.WriteAllText(a, "AAAA"); File.WriteAllText(b, "BBBB");
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var world = CreateWorld(p.Source, p.Destination, a, Hash(a), date);
        AddFile(world, p.Source, b, Hash(b), date);
        var state = new BackupRunState(p.Source, p.Destination, p.Staging);
        state.BeginRun();
        var options = new BackupBatchOptions { MaxFilesPerPart = 1 };
        Assert.True(new BackupPartPlanningSystem(p.Source, p.Destination, state, options).Execute(world.GetAllEntities(), world).IsSuccess);
        File.Delete(a);
        Assert.True(new BackupExecutionSystem(state, options).Execute(world.GetAllEntities(), world).IsSuccess);
        var partial = Path.Combine(state.StagingDirectory, "part-000001.zip.partial");
        File.WriteAllText(partial, "unacknowledged earlier artifact");
        var working = Assert.Single(Directory.GetDirectories(p.Destination, "*.copying"));
        File.Delete(Path.Combine(working, "part-000002.zip"));
        var snapshot = Directory.GetFiles(state.StagingDirectory).ToDictionary(path => path, File.ReadAllBytes);
        var recovered = CreateBackupWorld(p.Source, p.Destination);
        var result = new BackupRecoverySystem(new(), new(), new BackupRunState(p.Source, p.Destination, p.Staging))
            .Execute(recovered.GetAllEntities(), recovered);
        Assert.False(result.IsSuccess);
        Assert.Contains("Acknowledged", result.ErrorMessage);
        foreach (var pair in snapshot) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("unknown-version")]
    [InlineData("middle-journal")]
    public void InvalidUnfinishedStateIsPreserved(string kind)
    {
        var p = RepairPaths();
        var state = new BackupRunState(p.Source, p.Destination, p.Staging);
        state.BeginRun();
        state.CreateOrUpdateJob(DateTime.UtcNow, new(), "", 0, 0, 0, Guid.NewGuid());
        string badPath;
        if (kind == "middle-journal")
        {
            state.ReservePartNumber(1);
            var lines = File.ReadAllLines(state.JournalPath);
            lines[0] = lines[0].Replace("job-updated", "job-altered");
            File.WriteAllLines(state.JournalPath, lines);
            badPath = state.JournalPath;
        }
        else
        {
            File.WriteAllText(state.JobPath, kind == "null" ? "null" : File.ReadAllText(state.JobPath).Replace("\"FormatVersion\":2", "\"FormatVersion\":999"));
            badPath = state.JobPath;
        }
        var before = File.ReadAllBytes(badPath);
        Assert.Throws<InvalidDataException>(() => new BackupRunState(p.Source, p.Destination, p.Staging).BeginRun());
        Assert.Equal(before, File.ReadAllBytes(badPath));
    }

    [Fact]
    public void ValidFinalSetWinsWithoutDeletingAmbiguousWorkingDirectory()
    {
        var p = RepairPaths();
        File.WriteAllText(Path.Combine(p.Source, "file.txt"), "AAAA");
        var state = new BackupRunState(p.Source, p.Destination, p.Staging,
            new ThrowAtCheckpointObserver("publication-directory-renamed"));
        var world = CreateBackupWorld(p.Source, p.Destination);
        Assert.False(BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination, new(), new(), NullLogger.Instance,
            state, hashesPath: p.Hashes, backupDatesPath: p.Dates).Execute(world.GetAllEntities(), world).IsSuccess);
        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        Directory.CreateDirectory(set + ".copying");
        var evidence = Path.Combine(set + ".copying", "unexpected.txt");
        File.WriteAllText(evidence, "preserve");
        var recovered = CreateBackupWorld(p.Source, p.Destination);
        var result = BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination, new(), new(), NullLogger.Instance,
            new BackupRunState(p.Source, p.Destination, p.Staging), hashesPath: p.Hashes, backupDatesPath: p.Dates)
            .Execute(recovered.GetAllEntities(), recovered);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal("preserve", File.ReadAllText(evidence));
        Assert.Single(DataPersistence.LoadBackupDates(p.Dates));
    }
}
