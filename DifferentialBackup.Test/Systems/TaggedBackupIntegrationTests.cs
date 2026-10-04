using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using DifferentialBackup.Components;
using DifferentialBackup.Pipeline;
using DifferentialBackup.Test.Helpers;
using DifferentialBackup.Utilities;
using DOPipeline.Entities;
using DOPipeline.Storage;

namespace DifferentialBackup.Test.Systems;

public sealed class TaggedBackupIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TaggedBackup-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Destination => Path.Combine(_root, "destination");
    private string Staging => Path.Combine(_root, "staging");
    public TaggedBackupIntegrationTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Destination); }

    [Theory]
    [InlineData("tag-initialization-saved", false)]
    [InlineData("tag-source-created", false)]
    [InlineData("tag-destination-created", false)]
    [InlineData("tag-pair-committed", false)]
    [InlineData("tag-job-initialized", false)]
    [InlineData("part-checkpoint-committed", true)]
    [InlineData("destination-archive-renamed", true)]
    [InlineData("publication-receipt-committed", true)]
    [InlineData("publication-directory-renamed", true)]
    [InlineData("hash-state-replaced", true)]
    [InlineData("date-state-replaced", true)]
    [InlineData("state-commit-Complete", true)]
    [InlineData("tag-completion-saved", true)]
    [InlineData("tag-staging-removed", true)]
    [InlineData("tag-source-removed", true)]
    [InlineData("tag-destination-removed", true)]
    [InlineData("tag-initialization-removed", true)]
    [InlineData("tag-completion-removed", true)]
    public async Task KilledRunRecoversEveryLifecycleBoundary(string boundary, bool captured)
    {
        var file = Path.Combine(Source, "file.txt");
        File.WriteAllText(file, "original bytes");
        Guid id;
        using (var first = Child(Source, Destination, boundary))
        {
            try
            {
                Assert.Equal("CHECKPOINT:" + boundary, await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25)));
                id = boundary is "tag-completion-removed" or "tag-initialization-removed"
                    ? JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(
                        Assert.Single(Directory.GetDirectories(Destination, "*.backup")), "manifest.json")))!.RunId
                    : ReadRunId();
            }
            finally { await Kill(first); }
        }
        if (captured) File.Delete(file);
        using (var second = Child(Source + Path.DirectorySeparatorChar, Destination))
        {
            try
            {
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
                Assert.True(second.ExitCode == 0, await second.StandardError.ReadToEndAsync());
            }
            finally { await Kill(second); }
        }
        var set = Assert.Single(Directory.GetDirectories(Destination, "*.backup"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(set, "manifest.json")))!;
        Assert.Equal(4, manifest.FormatVersion);
        Assert.Equal(id, manifest.RunId);
        using var zip = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(set, "*.zip")));
        using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
        Assert.Equal("original bytes", reader.ReadToEnd());
        Assert.False(File.Exists(Path.Combine(Source, BackupRunSession.TagFileName)));
        Assert.False(File.Exists(Path.Combine(Destination, BackupRunSession.TagFileName)));
        Assert.False(Directory.Exists(Path.Combine(Staging, id.ToString("N"))));
        Assert.True(DestinationStateStore.HasCommit(Destination, id, FileHash(Path.Combine(set, "manifest.json"))));
    }

    [Fact]
    public async Task AliasesReuseCapturedPartsAndLocks()
    {
        var sourceAlias = Path.Combine(_root, "source-alias");
        var destinationAlias = Path.Combine(_root, "destination-alias");
        CreateAlias(sourceAlias, Source);
        CreateAlias(destinationAlias, Destination);
        Assert.Equal(DirectoryAlias.Resolve(Source), DirectoryAlias.Resolve(sourceAlias));
        Assert.Equal(DirectoryAlias.Resolve(Destination), DirectoryAlias.Resolve(destinationAlias));
        var file = Path.Combine(Source, "file.txt");
        File.WriteAllText(file, "before interruption");
        Guid id;
        byte[] indexBytes;
        using (var first = Child(Source, Destination, "part-checkpoint-committed"))
        {
            try
            {
                Assert.Equal("CHECKPOINT:part-checkpoint-committed", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(25)));
                id = ReadRunId();
                indexBytes = File.ReadAllBytes(Path.Combine(Staging, id.ToString("N"), "part-000001.index.json"));
                // A competing process sees the same root anchor through its aliases.
                using var competing = Child(sourceAlias, destinationAlias);
                await competing.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.NotEqual(0, competing.ExitCode);
            }
            finally { await Kill(first); }
        }
        File.WriteAllText(file, "new source bytes must not be captured");
        using (var resumed = Child(Path.GetRelativePath(Environment.CurrentDirectory, sourceAlias) + Path.DirectorySeparatorChar,
            Path.GetRelativePath(Environment.CurrentDirectory, destinationAlias)))
        {
            try
            {
                await resumed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
                Assert.True(resumed.ExitCode == 0, await resumed.StandardError.ReadToEndAsync());
            }
            finally { await Kill(resumed); }
        }
        var set = Assert.Single(Directory.GetDirectories(Destination, "*.backup"));
        Assert.Equal(indexBytes, File.ReadAllBytes(Path.Combine(set, "part-000001.index.json")));
        Assert.Equal(id, JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(set, "manifest.json")))!.RunId);
        using var zip = ZipFile.OpenRead(Path.Combine(set, "part-000001.zip"));
        using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
        Assert.Equal("before interruption", reader.ReadToEnd());
    }

    [Fact]
    public void DifferentDestinationsCaptureIndependentlyAndNoChangeRemovesTags()
    {
        File.WriteAllText(Path.Combine(Source, "file.txt"), "same source");
        Backup(Source, Destination);
        var second = Path.Combine(_root, "second-destination");
        Backup(Source, second);
        Assert.Single(Directory.GetDirectories(second, "*.backup"));
        Backup(Source, Destination);
        Assert.Single(Directory.GetDirectories(Destination, "*.backup"));
        Assert.False(File.Exists(Path.Combine(Source, BackupRunSession.TagFileName)));
        Assert.False(File.Exists(Path.Combine(Destination, BackupRunSession.TagFileName)));
        Assert.Single(DestinationStateStore.ReadHistory(Destination));
    }

    [NasMappedBackupFact]
    public async Task NasMappedDriveToUncResumePreservesCapturedIndexAndBytes()
    {
        var baseUnc = DirectoryPath.Normalize(Environment.GetEnvironmentVariable("DIFFERENTIALBACKUP_TEST_UNC_ROOT")!);
        var baseMapped = DirectoryPath.Normalize(Environment.GetEnvironmentVariable("DIFFERENTIALBACKUP_TEST_MAPPED_ROOT")!);
        var name = "run-tags-" + Guid.NewGuid().ToString("N");
        var owned = Path.Combine(baseUnc, name);
        var source = Path.Combine(owned, "source");
        var destination = Path.Combine(owned, "backup");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            var mappedSource = Path.Combine(baseMapped, name, "source");
            var mappedDestination = Path.Combine(baseMapped, name, "backup");
            Assert.Equal(DirectoryAlias.Resolve(source), DirectoryAlias.Resolve(mappedSource));
            Assert.Equal(DirectoryAlias.Resolve(destination), DirectoryAlias.Resolve(mappedDestination));
            var file = Path.Combine(source, "file.txt");
            File.WriteAllText(file, "NAS original bytes");
            Guid id;
            byte[] index;
            using (var first = Child(mappedSource, mappedDestination, "part-checkpoint-committed"))
            {
                try
                {
                    Assert.Equal("CHECKPOINT:part-checkpoint-committed", await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
                    id = ReadRunId();
                    index = File.ReadAllBytes(Path.Combine(Staging, id.ToString("N"), "part-000001.index.json"));
                    using var competing = Child(source, destination);
                    await competing.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    Assert.NotEqual(0, competing.ExitCode);
                }
                finally { await Kill(first); }
            }
            File.Delete(file);
            using (var resumed = Child(source, destination))
            {
                try
                {
                    await resumed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    Assert.True(resumed.ExitCode == 0, await resumed.StandardError.ReadToEndAsync());
                }
                finally { await Kill(resumed); }
            }
            var set = Assert.Single(Directory.GetDirectories(destination, "*.backup"));
            Assert.Equal(index, File.ReadAllBytes(Path.Combine(set, "part-000001.index.json")));
            Assert.Equal(id, JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(set, "manifest.json")))!.RunId);
            using var zip = ZipFile.OpenRead(Path.Combine(set, "part-000001.zip"));
            using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
            Assert.Equal("NAS original bytes", reader.ReadToEnd());
            Assert.False(File.Exists(Path.Combine(source, BackupRunSession.TagFileName)));
            Assert.False(File.Exists(Path.Combine(destination, BackupRunSession.TagFileName)));
        }
        finally
        {
            if (!DirectoryPath.Contains(baseUnc, owned) || DirectoryPath.Equals(baseUnc, owned))
                throw new InvalidOperationException("Unsafe integration cleanup path.");
            Directory.Delete(owned, true);
        }
    }

    [Fact]
    public void ReservedMetadataIsExcludedOnlyAtTheSourceRoot()
    {
        var nested = Path.Combine(Source, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, BackupRunSession.TagFileName), "ordinary nested data");
        Backup(Source, Destination);
        var set = Assert.Single(Directory.GetDirectories(Destination, "*.backup"));
        using var zip = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(set, "*.zip")));
        Assert.Equal("nested/" + BackupRunSession.TagFileName, Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public async Task FinalizedOmissionsRemoveTagsAndCancellationKeepsThem()
    {
        var healthy = Path.Combine(Source, "healthy.txt");
        var omitted = Path.Combine(Source, "omitted.txt");
        File.WriteAllText(healthy, "healthy bytes");
        File.WriteAllText(omitted, "omitted bytes");
        using (var held = new FileStream(omitted, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var completed = Child(Source, Destination))
        {
            try
            {
                await completed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
                Assert.True(completed.ExitCode == 1, await completed.StandardError.ReadToEndAsync());
            }
            finally { await Kill(completed); }
        }
        var set = Assert.Single(Directory.GetDirectories(Destination, "*.backup"));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(set, "manifest.json")))!;
        Assert.False(manifest.IsComplete);
        Assert.Contains(manifest.Issues, issue => issue.Path == "omitted.txt");
        Assert.False(File.Exists(Path.Combine(Source, BackupRunSession.TagFileName)));
        Assert.False(File.Exists(Path.Combine(Destination, BackupRunSession.TagFileName)));
        using (var cancelled = Child(Source, Destination, mode: "cancel"))
        {
            try
            {
                await cancelled.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
                Assert.True(cancelled.ExitCode == 130, await cancelled.StandardError.ReadToEndAsync());
            }
            finally { await Kill(cancelled); }
        }
        var id = ReadRunId();
        Assert.True(Directory.Exists(Path.Combine(Staging, id.ToString("N"))));
        Assert.Equal(id, JsonSerializer.Deserialize<BackupRunTag>(File.ReadAllText(Path.Combine(Source, BackupRunSession.TagFileName)))!.RunId);
        Assert.Equal(id, JsonSerializer.Deserialize<BackupRunTag>(File.ReadAllText(Path.Combine(Destination, BackupRunSession.TagFileName)))!.RunId);
    }

    [CaseDistinctFilesystemFact]
    public void CaseDistinctNamesRoundTripWhenTheFilesystemSupportsThem()
    {
        var probe = Path.Combine(Source, "case-probe");
        File.WriteAllText(probe, "probe");
        var sensitive = !File.Exists(Path.Combine(Source, "CASE-PROBE"));
        File.Delete(probe);
        Assert.True(sensitive);
        RoundTripCaseDistinctNames();
    }

    [WindowsCaseSensitiveFact]
    public void WindowsCaseSensitiveDirectoriesRoundTripExactNames()
    {
        Assert.True(WindowsCaseSensitiveFactAttribute.Enable(Source));
        var restore = Path.Combine(_root, "restore");
        Directory.CreateDirectory(restore);
        Assert.True(WindowsCaseSensitiveFactAttribute.Enable(restore));
        RoundTripCaseDistinctNames();
    }

    private void RoundTripCaseDistinctNames()
    {
        Directory.CreateDirectory(Path.Combine(Source, "Folder"));
        Directory.CreateDirectory(Path.Combine(Source, "folder"));
        foreach (var name in new[] { "Folder/File", "Folder/file", "folder/File", "folder/file" })
            File.WriteAllText(Path.Combine(Source, name), name);
        Backup(Source, Destination);
        var restore = Path.Combine(_root, "restore");
        var storage = new ComponentStorage();
        var result = new DifferentialBackup.Systems.RestoreSystem(Destination,
            Assert.Single(DestinationStateStore.ReadHistory(Destination)), restore).Execute(new Entity(), storage);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        foreach (var name in new[] { "Folder/File", "Folder/file", "folder/File", "folder/file" })
            Assert.Equal(name, File.ReadAllText(Path.Combine(restore, name)));
        Assert.Equal(4, new DestinationStateStore(Source, Destination).LoadHashes().Count);
    }

    [Fact]
    public void UnixLiteralNamesAndLongNamesRoundTrip()
    {
        var names = OperatingSystem.IsWindows() ? new[] { new string('n', 230) } :
            new[] { "literal\\backslash", "literal:colon", new string('n', 255) };
        foreach (var name in names) File.WriteAllText(Path.Combine(Source, name), name);
        Backup(Source, Destination);
        var restore = Path.Combine(_root, "literal-restore");
        var result = new DifferentialBackup.Systems.RestoreSystem(Destination,
            Assert.Single(DestinationStateStore.ReadHistory(Destination)), restore).Execute(new Entity(), new ComponentStorage());
        Assert.True(result.IsSuccess, result.ErrorMessage);
        foreach (var name in names) Assert.Equal(name, File.ReadAllText(Path.Combine(restore, name)));
    }

    private static void Backup(string source, string destination)
    {
        var staging = Path.Combine(Path.GetDirectoryName(source)!, "in-process-staging");
        using var session = BackupRunSession.Open(source, destination, staging);
        Assert.False(session.CleanupCompleted);
        source = session.SourceDirectory;
        destination = session.BackupDestination;
        var state = session.CreateRunState();
        var store = state.DestinationState!;
        var world = new ComponentStorage();
        var op = new Entity();
        world.SetComponent(op, new OperationComponent
        {
            RunId = session.RunId, SourceDirectory = source, BackupDestination = destination,
            OperationType = "backup", Phase = OperationPhase.InitialPass, InitializationSucceeded = true
        });
        world.SetComponent(op, new RetryScheduleComponent { RunId = session.RunId });
        var result = BackupPipelineBuilder.BuildBackupPipeline(source, destination, store.LoadHashes(),
            DestinationStateStore.ReadHistory(destination), NullLogger.Instance, state,
            hashesPath: store.StatePath, backupDatesPath: store.StatePath).Execute(new[] { op }, world);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        var outcome = world.GetComponent<OperationOutcomeComponent>(op);
        Assert.NotNull(outcome);
        Assert.Equal(0, outcome!.ExitCode);
        session.Complete(state);
    }

    private Guid ReadRunId() => JsonSerializer.Deserialize<BackupInitialization>(File.ReadAllText(
        Assert.Single(Directory.GetFiles(Path.Combine(Staging, "initializations"), "*.json"))))!.RunId;
    private Process Child(string source, string destination, string boundary = "", string mode = "")
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "DifferentialBackup.TestHost.dll"),
            "tagged-backup", source, destination, Staging, Path.Combine(_root, "unused-hashes"),
            Path.Combine(_root, "unused-dates"), boundary, Path.Combine(_root, "signal"), Path.Combine(_root, "release"), "", mode })
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
    private static async Task Kill(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)); }
    private static void CreateAlias(string alias, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(alias, target); return; }
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.Arguments = $"/c mklink /J \"{alias}\" \"{target}\"";
        using var process = Process.Start(info)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd());
    }
    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var path in Directory.EnumerateDirectories(_root))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) Directory.Delete(path, false);
        Directory.Delete(_root, true);
    }
}
