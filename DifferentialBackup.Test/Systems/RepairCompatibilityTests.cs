using System.Diagnostics;
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
    [Fact]
    public void OlderCheckpointWithoutReceiptRecognizesRenamedVersionTwoSet()
    {
        var p = RepairPaths();
        var file = Path.Combine(p.Source, "file.txt");
        File.WriteAllText(file, "AAAA");
        var expected = Hash(file);
        var state = new BackupRunState(p.Source, p.Destination, p.Staging,
            new ThrowAtCheckpointObserver("publication-directory-renamed"));
        var world = CreateBackupWorld(p.Source, p.Destination);
        Assert.False(BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination, new(), new(), NullLogger.Instance,
            state, hashesPath: p.Hashes, backupDatesPath: p.Dates).Execute(world.GetAllEntities(), world).IsSuccess);
        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var part = state.LoadPartCheckpoint(1)!;
        File.WriteAllText(Path.Combine(set, "manifest.json"), $$"""
        {"FormatVersion":2,"SourceDirectory":{{JsonSerializer.Serialize(p.Source)}},
         "BackupDate":{{JsonSerializer.Serialize(state.BackupDate)}},"PlanFingerprint":{{JsonSerializer.Serialize(state.Job!.PlanFingerprint)}},
         "FileCount":1,"SourceBytes":4,"Parts":[{"PartNumber":1,"ArchiveFileName":"part-000001.zip",
         "Fingerprint":{{JsonSerializer.Serialize(part.Fingerprint)}},"FileCount":1,"SourceBytes":4,"ArchiveBytes":{{part.ArchiveBytes}}}]}
        """);
        File.Delete(state.PublicationReceiptPath);
        File.Delete(state.JournalPath); // Genuine older checkpoint layout, before append-only receipts.
        File.Delete(file);
        var recovered = CreateBackupWorld(p.Source, p.Destination);
        var result = BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination, new(), new(), NullLogger.Instance,
            new BackupRunState(p.Source, p.Destination, p.Staging), hashesPath: p.Hashes, backupDatesPath: p.Dates)
            .Execute(recovered.GetAllEntities(), recovered);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(expected, DataPersistence.LoadFileHashes(p.Hashes)[file]);
        Assert.Single(DataPersistence.LoadBackupDates(p.Dates));
        Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
    }

    [Fact]
    public void GenuineVersionTwoManifestRestoresAndVerifiedTargetsAreSkipped()
    {
        var p = RepairPaths();
        var set = Path.Combine(p.Destination, "20260101000000.backup");
        Directory.CreateDirectory(set);
        var zipPath = Path.Combine(set, "part-000001.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("file.txt").Open())) writer.Write("version two");
        // Deliberately independent of CurrentFormatVersion and the production DTO.
        File.WriteAllText(Path.Combine(set, "manifest.json"), $$"""
        {"FormatVersion":2,"SourceDirectory":"C:\\historical-source","BackupDate":"2026-01-01T00:00:00Z",
         "PlanFingerprint":"legacy","FileCount":1,"SourceBytes":11,"Parts":[
          {"PartNumber":1,"ArchiveFileName":"part-000001.zip","Fingerprint":"legacy-part",
           "FileCount":1,"SourceBytes":11,"ArchiveBytes":{{new FileInfo(zipPath).Length}}}]}
        """);
        var target = Path.Combine(_root, "restore");
        var system = new RestoreSystem(p.Destination, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), target);
        Assert.True(system.Execute(new Entity(), new ComponentStorage()).IsSuccess);
        var file = Path.Combine(target, "file.txt");
        Assert.Equal("version two", File.ReadAllText(file));
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var world = new ComponentStorage();
            var result = system.Execute(new Entity(), world);
            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Single(world.Query<RestoreSuccessComponent>());
            Assert.Empty(world.Query<BackupIssueComponent>());
        }
        File.WriteAllText(file, "changed");
        Assert.True(system.Execute(new Entity(), new ComponentStorage()).IsSuccess);
        Assert.Equal("version two", File.ReadAllText(file));
    }

    [Fact]
    public void CorruptPublishedIndexBlocksRecoveryAndRestoreWithoutChangingEvidence()
    {
        var p = RepairPaths();
        File.WriteAllText(Path.Combine(p.Source, "file.txt"), "AAAA");
        var state = new BackupRunState(p.Source, p.Destination, p.Staging,
            new ThrowAtCheckpointObserver("publication-directory-renamed"));
        var world = CreateBackupWorld(p.Source, p.Destination);
        Assert.False(BackupPipelineBuilder.BuildBackupPipeline(p.Source, p.Destination, new(), new(), NullLogger.Instance,
            state, hashesPath: p.Hashes, backupDatesPath: p.Dates).Execute(world.GetAllEntities(), world).IsSuccess);
        var set = Assert.Single(Directory.GetDirectories(p.Destination, "*.backup"));
        var index = Assert.Single(Directory.GetFiles(set, "*.index.json"));
        var bytes = File.ReadAllBytes(index); bytes[bytes.Length / 2] ^= 1; File.WriteAllBytes(index, bytes);
        var recovered = CreateBackupWorld(p.Source, p.Destination);
        var result = new BackupRecoverySystem(new(), new(), new BackupRunState(p.Source, p.Destination, p.Staging))
            .Execute(recovered.GetAllEntities(), recovered);
        Assert.False(result.IsSuccess);
        Assert.Equal(bytes, File.ReadAllBytes(index));
        var restore = Path.Combine(_root, "restore");
        result = new RestoreSystem(p.Destination, state.BackupDate!.Value, restore).Execute(new Entity(), new ComponentStorage());
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(restore, "file.txt")));
        Assert.False(File.Exists(p.Hashes));
    }

    [Theory]
    [InlineData("not-a-command", 2)]
    [InlineData("backup", 2)]
    [InlineData("help", 0)]
    public async Task ActualCliHandlesRedirectedArgumentsInIsolatedApplicationDirectory(string command, int exitCode)
    {
        var app = CopyFixtureApplication();
        using var process = StartFixtureCli(app, command);
        process.StandardInput.Close();
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(exitCode, process.ExitCode);
            await output; await errors;
        }
        finally { await KillChild(process); }
    }

    [Fact]
    public async Task ActualCliSelectsExactUtcBackupDate()
    {
        var p = RepairPaths();
        foreach (var timestamp in new[] { "20260101000000", "20260102000000" })
        {
            using var zip = ZipFile.Open(Path.Combine(p.Destination, timestamp + ".zip"), ZipArchiveMode.Create);
            using var writer = new StreamWriter(zip.CreateEntry("file.txt").Open());
            writer.Write(timestamp);
        }
        var app = CopyFixtureApplication();
        var restore = Path.Combine(_root, "restore");
        using var process = StartFixtureCli(app, "restore", p.Destination, restore, "--backup-date", "20260101000000");
        process.StandardInput.Close();
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.ExitCode == 0, await output + await error);
            Assert.Equal("20260101000000", File.ReadAllText(Path.Combine(restore, "file.txt")));
        }
        finally { await KillChild(process); }
    }

    private string CopyFixtureApplication()
    {
        var app = Path.Combine(_root, "app");
        Directory.CreateDirectory(app);
        foreach (var name in new[] { "DifferentialBackup.dll", "DifferentialBackup.deps.json", "DifferentialBackup.runtimeconfig.json", "DOPipeline.dll" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(app, name));
        return app;
    }

    private static Process StartFixtureCli(string app, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = app,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        info.ArgumentList.Add(Path.Combine(app, "DifferentialBackup.dll"));
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
}
