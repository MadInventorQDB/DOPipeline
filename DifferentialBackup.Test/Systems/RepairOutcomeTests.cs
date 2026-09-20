using System.Collections.Concurrent;
using System.Text.Json;
using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using Xunit;

namespace DifferentialBackup.Test.Systems;

public sealed partial class BackupRecoveryRegressionTests
{
    [Theory]
    [InlineData(StateCommitStep.NotRequired)]
    [InlineData(StateCommitStep.Pending)]
    [InlineData(StateCommitStep.HashesSaved)]
    [InlineData(StateCommitStep.DatesSaved)]
    public void PublishedPhaseCannotReportSuccessWithoutCompleteStateCommit(StateCommitStep step)
    {
        var p = RepairPaths();
        var world = CreateBackupWorld(p.Source, p.Destination);
        var op = world.Query<OperationComponent>().Single();
        var operation = world.GetComponent<OperationComponent>(op)!;
        operation.Phase = OperationPhase.PublishedPendingState;
        world.SetComponent(op, new StateCommitComponent { RunId = operation.RunId, Step = step });
        Assert.True(new DifferentialBackup.Systems.OperationSummarySystem().Execute(new[] { op }, world).IsSuccess);
        Assert.Null(world.GetComponent<OperationOutcomeComponent>(op));
        Assert.Empty(Directory.GetFiles(p.Destination, "*.report.json"));
        Assert.Equal(OperationPhase.PublishedPendingState, operation.Phase);
    }

    [Theory]
    [InlineData("empty", 0)]
    [InlineData("unchanged", 0)]
    [InlineData("unchanged-with-failure", 1)]
    [InlineData("all-failed", 2)]
    [InlineData("incomplete", 1)]
    public void RequiredOutcomeRulesAndUnchangedHashPreservation(string scenario, int exit)
    {
        var p = RepairPaths();
        var a = Path.Combine(p.Source, "a.txt");
        var b = Path.Combine(p.Source, "b.txt");
        var hashes = new ConcurrentDictionary<string, string>();
        if (scenario != "empty" && scenario != "all-failed")
        {
            File.WriteAllText(a, "AAAA");
            if (scenario.StartsWith("unchanged")) hashes[a] = Hash(a);
        }
        var failing = scenario is "all-failed" or "unchanged-with-failure" or "incomplete";
        if (failing) { File.WriteAllText(b, "OLD!"); hashes[b] = Hash(b); File.WriteAllText(b, "BBBB"); }
        var previous = hashes.GetValueOrDefault(b);
        using var hold = failing ? new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var clock = new RepairClock();
        var world = CreateBackupWorld(p.Source, p.Destination);
        var op = world.Query<OperationComponent>().Single();
        var pipeline = BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination, hashes, new(), NullLogger.Instance,
            new BackupRunState(p.Source, p.Destination, p.Staging), hashesPath: p.Hashes, backupDatesPath: p.Dates, clock: clock);
        var waits = new List<int>();
        for (var pass = 0; pass < 9; pass++)
        {
            var result = pipeline.Execute(new[] { op }, world);
            Assert.True(result.IsSuccess, result.ErrorMessage);
            if (world.GetComponent<OperationOutcomeComponent>(op) != null) break;
            var wait = world.GetComponent<OperationWaitComponent>(op)!;
            waits.Add((int)(wait.WakeAtUtc - clock.Now).TotalMinutes);
            clock.Now = wait.WakeAtUtc;
        }
        var outcome = world.GetComponent<OperationOutcomeComponent>(op)!;
        Assert.Equal(exit, outcome.ExitCode);
        Assert.True(File.Exists(outcome.ReportPath));
        Assert.Equal(failing ? new[] { 1, 1, 2, 3, 5, 8, 13 } : Array.Empty<int>(), waits);
        Assert.Equal(scenario == "incomplete" ? 1 : 0, Directory.GetDirectories(p.Destination, "*.backup").Length);
        if (failing) Assert.Equal(previous, hashes[b]);
        Assert.Equal(scenario == "incomplete" ? StateCommitStep.Complete : StateCommitStep.NotRequired,
            world.GetComponent<StateCommitComponent>(op)!.Step);
    }

    [Theory]
    [InlineData("cancel", 130)]
    [InlineData("fatal-and-cancel", 2)]
    public async Task ProcessExitCodePreservesFailurePrecedence(string mode, int exit)
    {
        var p = RepairPaths();
        File.WriteAllText(Path.Combine(p.Source, "file.txt"), "AAAA");
        using var child = RepairChild(p, mode: mode);
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(exit, child.ExitCode);
            using var report = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(p.Destination, "*.report.json"))));
            Assert.Equal(exit, report.RootElement.GetProperty("ExitCode").GetInt32());
            if (exit == 2) Assert.Equal("initiating fixture I/O failure", report.RootElement.GetProperty("ErrorMessage").GetString());
            Assert.Empty(Directory.GetDirectories(p.Destination, "*.backup"));
        }
        finally { await KillChild(child); }
    }

    [Fact]
    public async Task ProcessWarningExitPreservesHealthyContent()
    {
        var p = RepairPaths();
        File.WriteAllText(Path.Combine(p.Source, "a.txt"), "AAAA");
        var b = Path.Combine(p.Source, "b.txt"); File.WriteAllText(b, "BBBB");
        using var hold = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None);
        using var child = RepairChild(p);
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(child.ExitCode == 1, await child.StandardError.ReadToEndAsync());
            var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
            Assert.Equal("AAAA", ReadEntry(Assert.Single(Directory.GetFiles(set, "*.zip")), "a.txt"));
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(set, "manifest.json")));
            Assert.False(manifest.RootElement.GetProperty("IsComplete").GetBoolean());
        }
        finally { await KillChild(child); }
    }
}
