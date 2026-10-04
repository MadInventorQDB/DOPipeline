using System.Text.Json;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Test.Utilities;

public sealed class BackupRunSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RunTags-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Destination => Path.Combine(_root, "destination");
    private string Staging => Path.Combine(_root, "staging");
    public BackupRunSessionTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Destination); }

    [Theory]
    [InlineData("tag-initialization-saved")]
    [InlineData("tag-source-created")]
    [InlineData("tag-destination-created")]
    [InlineData("tag-pair-committed")]
    public void InterruptedInitializationResumesTheSameGuid(string boundary)
    {
        Assert.Throws<IOException>(() => BackupRunSession.Open(Source, Destination, Staging, new ThrowAt(boundary)));
        var record = JsonSerializer.Deserialize<BackupInitialization>(File.ReadAllText(
            Assert.Single(Directory.GetFiles(Path.Combine(Staging, "initializations"), "*.json"))))!;
        using var resumed = BackupRunSession.Open(Source + Path.DirectorySeparatorChar, Destination, Staging);
        Assert.Equal(record.RunId, resumed.RunId);
        Assert.Equal(record.RunId.ToString("N"), Path.GetFileName(resumed.StagingDirectory));
        Assert.Equal(resumed.RunId, ReadTag(Source).RunId);
        Assert.Equal(resumed.RunId, ReadTag(Destination).RunId);
        resumed.CreateRunState().BeginRun();
    }

    [Theory]
    [InlineData("tag-completion-saved")]
    [InlineData("tag-staging-removed")]
    [InlineData("tag-source-removed")]
    [InlineData("tag-destination-removed")]
    [InlineData("tag-initialization-removed")]
    public void InterruptedNoChangeCleanupFinishesWithoutCreatingAnotherRun(string boundary)
    {
        Guid id;
        using (var first = BackupRunSession.Open(Source, Destination, Staging, new ThrowAt(boundary)))
        {
            id = first.RunId;
            Assert.Throws<IOException>(() => first.Complete(first.CreateRunState()));
        }
        File.WriteAllText(Path.Combine(Source, "new-file.txt"), "later bytes");
        using var resumed = BackupRunSession.Open(Source, Destination, Staging);
        Assert.True(resumed.CleanupCompleted);
        Assert.Equal(id, resumed.RunId);
        Assert.False(File.Exists(Path.Combine(Source, BackupRunSession.TagFileName)));
        Assert.False(File.Exists(Path.Combine(Destination, BackupRunSession.TagFileName)));
        Assert.False(Directory.Exists(Path.Combine(Staging, id.ToString("N"))));
        Assert.Empty(Directory.GetFiles(Path.Combine(Staging, "completed"), "*.json"));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrong-role")]
    [InlineData("foreign-owner")]
    [InlineData("different-guid")]
    [InlineData("missing-staging")]
    [InlineData("missing-tag")]
    public void ConflictsPreserveTagsAndStaging(string problem)
    {
        string stage;
        using (var first = BackupRunSession.Open(Source, Destination, Staging)) stage = first.StagingDirectory;
        var tagPath = Path.Combine(Destination, BackupRunSession.TagFileName);
        var tag = ReadTag(Destination);
        if (problem == "wrong-role") tag.Role = "source";
        if (problem == "foreign-owner") tag.OwnerId = Guid.NewGuid();
        if (problem == "different-guid") tag.RunId = Guid.NewGuid();
        if (problem == "missing-staging") Directory.Delete(stage);
        if (problem == "missing-tag") File.Delete(tagPath);
        else File.WriteAllText(tagPath, problem == "malformed" ? "unrecognized contents" : JsonSerializer.Serialize(tag));
        var sourceBytes = File.ReadAllBytes(Path.Combine(Source, BackupRunSession.TagFileName));
        var destinationBytes = File.Exists(tagPath) ? File.ReadAllBytes(tagPath) : null;
        Assert.ThrowsAny<Exception>(() => BackupRunSession.Open(Source, Destination, Staging));
        Assert.Equal(sourceBytes, File.ReadAllBytes(Path.Combine(Source, BackupRunSession.TagFileName)));
        if (destinationBytes != null) Assert.Equal(destinationBytes, File.ReadAllBytes(tagPath));
        else Assert.False(File.Exists(tagPath));
        Assert.True(Directory.Exists(stage) == (problem != "missing-staging"));
    }

    [Fact]
    public void CorruptCompletionCannotDeleteAnotherRunsStaging()
    {
        Guid id;
        string stage;
        using (var session = BackupRunSession.Open(Source, Destination, Staging, new ThrowAt("tag-completion-saved")))
        {
            id = session.RunId;
            stage = session.StagingDirectory;
            Assert.Throws<IOException>(() => session.Complete(session.CreateRunState()));
        }
        File.WriteAllText(Path.Combine(stage, "manifest.json"), "owned evidence");
        var path = Path.Combine(Staging, "completed", id.ToString("N") + ".json");
        var completed = JsonSerializer.Deserialize<BackupCompletion>(File.ReadAllText(path))!;
        completed.RunId = Guid.NewGuid();
        File.WriteAllText(path, JsonSerializer.Serialize(completed));
        Assert.Throws<InvalidDataException>(() => BackupRunSession.Open(Source, Destination, Staging));
        Assert.Equal("owned evidence", File.ReadAllText(Path.Combine(stage, "manifest.json")));
        Assert.Equal(id, ReadTag(Source).RunId);
        Assert.Equal(id, ReadTag(Destination).RunId);
    }

    [Fact]
    public void RootCaseAliasesStillRejectOverlapOnCapableFilesystems()
    {
        var alias = Path.Combine(_root, "SOURCE");
        if (!Directory.Exists(alias)) return;
        Assert.Throws<InvalidDataException>(() => BackupRunSession.Open(alias, Path.Combine(Source, "nested-backup"), Staging));
        Assert.False(File.Exists(Path.Combine(Source, BackupRunSession.TagFileName)));
    }

    [Fact]
    public void ReservedMetadataRecognizesOnlyItsExactTemporaryNamespace()
    {
        Assert.False(BackupRunSession.IsReservedRootFile(BackupRunSession.TagFileName + ".tmp"));
        Assert.False(BackupRunSession.IsReservedRootFile(BackupRunSession.TagFileName + ".notes"));
        Assert.True(BackupRunSession.IsReservedRootFile(BackupRunSession.TagFileName + "." + Guid.NewGuid().ToString("N") + ".tmp"));
    }

    [Fact]
    public void LegacyStagingIsAdoptedOnlyWithValidatedRootAssociationAndArtifacts()
    {
        var legacy = CreateLegacy(Source);
        var originalIndex = File.ReadAllBytes(legacy.GetPartCheckpointPath(1));
        var id = legacy.RunId;
        using var adopted = BackupRunSession.Open(Source, Destination, Staging);
        Assert.Equal(id, adopted.RunId);
        Assert.False(Directory.Exists(legacy.StagingDirectory));
        Assert.Equal(originalIndex, File.ReadAllBytes(Path.Combine(adopted.StagingDirectory, "part-000001.index.json")));
        var recovered = adopted.CreateRunState();
        recovered.BeginRun();
        Assert.Equal(2, recovered.Job!.FormatVersion);
        Assert.Equal(Path.Combine(Source, "file.txt"), Assert.Single(recovered.LoadPartCheckpoint(1)!.Files).SourcePath);
    }

    [Fact]
    public void MultipleLegacyAliasJobsArePreservedForExplicitResolution()
    {
        var alias = Path.Combine(_root, "legacy-alias");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.Arguments = $"/c mklink /J \"{alias}\" \"{Source}\"";
            using var process = Process.Start(start)!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(alias, Source);
        var first = CreateLegacy(Source);
        var second = CreateLegacy(alias);
        Assert.Throws<InvalidDataException>(() => BackupRunSession.Open(Source, Destination, Staging));
        Assert.True(Directory.Exists(first.StagingDirectory));
        Assert.True(Directory.Exists(second.StagingDirectory));
        Assert.False(File.Exists(Path.Combine(Source, BackupRunSession.TagFileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrCorruptLegacyCaptureCannotBeAdopted(bool corrupt)
    {
        var state = CreateLegacy(Source);
        var path = Path.Combine(state.StagingDirectory, "part-000001.zip");
        if (corrupt) File.AppendAllText(path, "invalid archive");
        else File.Delete(path);
        Assert.Throws<InvalidDataException>(() => BackupRunSession.Open(Source, Destination, Staging));
        Assert.True(Directory.Exists(state.StagingDirectory));
        Assert.False(File.Exists(Path.Combine(Source, BackupRunSession.TagFileName)));
    }

    [Theory]
    [InlineData("unrecognized.txt")]
    [InlineData("job.json.notes")]
    [InlineData("part-000001.index.json.notes")]
    public void OwnedCleanupPreservesUnrecognizedStaging(string name)
    {
        using var session = BackupRunSession.Open(Source, Destination, Staging);
        var unknown = Path.Combine(session.StagingDirectory, name);
        File.WriteAllText(unknown, "preserve");
        Assert.Throws<IOException>(() => session.Complete(session.CreateRunState()));
        Assert.Equal("preserve", File.ReadAllText(unknown));
        Assert.True(File.Exists(Path.Combine(Source, BackupRunSession.TagFileName)));
    }

    [Fact]
    public void SourceRootCannotBeUsedByASecondDestination()
    {
        using (BackupRunSession.Open(Source, Destination, Staging)) { }
        var another = Path.Combine(_root, "another");
        Assert.Throws<InvalidDataException>(() => BackupRunSession.Open(Source, another, Staging));
        Assert.False(File.Exists(Path.Combine(another, BackupRunSession.TagFileName)));
    }

    [Fact]
    public void ForeignProfileCannotAdoptTags()
    {
        using (BackupRunSession.Open(Source, Destination, Staging)) { }
        Assert.Throws<InvalidDataException>(() => BackupRunSession.Open(Source, Destination, Path.Combine(_root, "other-profile")));
    }

    [Fact]
    public void OverlapIsRejectedBeforeTags()
    {
        Assert.Throws<InvalidDataException>(() => BackupRunSession.Open(Source, Path.Combine(Source, "child"), Staging));
        Assert.False(File.Exists(Path.Combine(Source, BackupRunSession.TagFileName)));
    }

    [Fact]
    public void LostJobAndJournalAreNotTreatedAsAnEmptyNewRun()
    {
        using (var first = BackupRunSession.Open(Source, Destination, Staging))
        {
            var state = first.CreateRunState();
            state.BeginRun();
            state.CreateOrUpdateJob(DateTime.UtcNow, new(), "fingerprint", 1, 1, 1, first.RunId);
            File.Delete(state.JobPath);
            File.Delete(state.JournalPath);
        }
        using var resumed = BackupRunSession.Open(Source, Destination, Staging);
        Assert.Throws<InvalidDataException>(() => resumed.CreateRunState().BeginRun());
        Assert.True(File.Exists(Path.Combine(Source, BackupRunSession.TagFileName)));
    }

    [Fact]
    public void RootNormalizationPreservesFilesystemRoots()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(Source))!;
        Assert.Equal(root, DirectoryPath.Normalize(root));
        Assert.True(DirectoryPath.Contains(root, Source));
        Assert.Equal(DirectoryPath.Normalize(Source), DirectoryPath.Normalize(Source + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void CompetingOperationCannotTakeTheSameAnchors()
    {
        using var first = BackupRunSession.Open(Source, Destination, Staging);
        Assert.Throws<InvalidOperationException>(() => BackupRunSession.Open(Source, Destination, Staging));
    }

    private static BackupRunTag ReadTag(string root) => JsonSerializer.Deserialize<BackupRunTag>(
        File.ReadAllText(Path.Combine(root, BackupRunSession.TagFileName)))!;
    private sealed class ThrowAt(string name) : ICheckpointObserver
    {
        public void Reached(string checkpoint) { if (checkpoint == name) throw new IOException("Interruption: " + name); }
    }
    private BackupRunState CreateLegacy(string source)
    {
        File.WriteAllText(Path.Combine(source, "file.txt"), "legacy bytes");
        var state = new BackupRunState(source, Destination, Staging);
        state.CreateOrUpdateJob(new DateTime(2026, 1, 1), new(), "legacy", 1, 1, 12, Guid.NewGuid());
        state.Job!.FormatVersion = 2;
        state.CreateOrUpdateJob(new DateTime(2026, 1, 1), new(), "legacy", 1, 1, 12);
        var archive = Path.Combine(state.StagingDirectory, "part-000001.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("file.txt").Open())) writer.Write("legacy bytes");
        var files = new List<BackupPartFile> { new(Path.Combine(source, "file.txt"), "file.txt", "captured-hash", 12) };
        state.SavePartCheckpoint(new BackupPartCheckpoint
        {
            FormatVersion = 2, PartNumber = 1, ArchiveFileName = "part-000001.zip", Files = files,
            FileCount = 1, SourceBytes = 12, ArchiveBytes = new FileInfo(archive).Length,
            ArchiveSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))),
            Fingerprint = BackupFingerprint.ForFiles(files)
        });
        return state;
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var path in Directory.EnumerateDirectories(_root))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) Directory.Delete(path, false);
        Directory.Delete(_root, true);
    }
}
