using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Test.Utilities;

public sealed class DestinationStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DestinationState-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Destination => Path.Combine(_root, "backup");
    public DestinationStateStoreTests() { Directory.CreateDirectory(Source); Directory.CreateDirectory(Destination); }

    [Fact]
    public void OlderRecoveryCommitCannotReplaceANewerBaseline()
    {
        var store = new DestinationStateStore(Source, Destination);
        var newer = Published(new DateTime(2026, 2, 2), "newer bytes");
        var older = Published(new DateTime(2026, 1, 1), "older bytes");
        store.Commit(newer);
        store.Commit(older);
        var baseline = Assert.Single(store.LoadHashes());
        Assert.Equal(Path.Combine(Source, "file.txt"), baseline.Key);
        Assert.Equal(Hash(Encoding.UTF8.GetBytes("newer bytes")), baseline.Value);
        var document = JsonSerializer.Deserialize<DestinationStateDocument>(File.ReadAllText(store.StatePath))!;
        Assert.Equal(newer.BackupDate, Assert.Single(Assert.Single(document.Sources).Value).Value.PublicationDate);
        Assert.Equal(2, document.CommittedRuns.Count);
    }

    [Fact]
    public void RemovingPublishedEvidenceForcesFreshCaptureAndCorruptionIsRejected()
    {
        var store = new DestinationStateStore(Source, Destination);
        var run = Published(new DateTime(2026, 1, 1), "captured bytes");
        store.Commit(run);
        Assert.Single(store.LoadHashes());
        var published = Path.Combine(Destination, "20260101000000.backup");
        var hidden = published + ".removed";
        Directory.Move(published, hidden);
        Assert.Empty(store.LoadHashes());
        Directory.Move(hidden, published);
        File.AppendAllText(Path.Combine(published, "part-000001.zip"), "corruption");
        Assert.Throws<InvalidDataException>(() => store.LoadHashes());
    }

    [Fact]
    public void AnotherSourceCannotUseTheSelectedSourcesBaseline()
    {
        var store = new DestinationStateStore(Source, Destination);
        store.Commit(Published(new DateTime(2026, 1, 1), "source bytes"));
        var other = Path.Combine(_root, "other-source");
        Directory.CreateDirectory(other);
        Assert.Empty(new DestinationStateStore(other, Destination).LoadHashes());
    }

    [Fact]
    public void HistoryComesFromPublishedSetsAndLegacyZipsWithoutInstallationState()
    {
        Published(new DateTime(2026, 1, 1), "published");
        foreach (var format in new[] { 2, 3 })
        {
            var set = Path.Combine(Destination, $"2026010{format}000000.backup");
            Directory.CreateDirectory(set);
            File.WriteAllText(Path.Combine(set, "manifest.json"), JsonSerializer.Serialize(new BackupManifest { FormatVersion = format }));
        }
        using (ZipFile.Open(Path.Combine(Destination, "20260104000000.zip"), ZipArchiveMode.Create)) { }
        Directory.CreateDirectory(Path.Combine(Destination, "20260105000000.backup.copying"));
        Assert.False(File.Exists(Path.Combine(Destination, DestinationStateStore.FileName)));
        Assert.Equal(new[] { 1, 2, 3, 4 }, DestinationStateStore.ReadHistory(Destination).Order().Select(date => date.Day));
    }

    private BackupRunState Published(DateTime date, string contents)
    {
        var state = new BackupRunState(Source, Destination, Path.Combine(_root, "staging-" + date.Ticks));
        var bytes = Encoding.UTF8.GetBytes(contents);
        var files = new List<BackupPartFile> { new(Path.Combine(Source, "file.txt"), "file.txt", Hash(bytes), bytes.Length) };
        using var descriptor = new MemoryStream();
        using (var writer = new BinaryWriter(descriptor, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in new[] { "file.txt", files[0].Hash })
            {
                var encoded = Encoding.UTF8.GetBytes(value);
                writer.Write((long)encoded.Length);
                writer.Write(encoded);
            }
            writer.Write((long)bytes.Length);
        }
        var fingerprint = Hash(descriptor.ToArray());
        var id = Guid.NewGuid();
        state.CreateOrUpdateJob(date, new(), fingerprint, 1, 1, bytes.Length, id);
        var set = Path.Combine(Destination, date.ToString("yyyyMMddHHmmss") + ".backup");
        Directory.CreateDirectory(set);
        var archive = Path.Combine(set, "part-000001.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("file.txt").Open()) entry.Write(bytes);
        var checkpoint = new BackupPartCheckpoint
        {
            PartNumber = 1, ArchiveFileName = "part-000001.zip", Fingerprint = fingerprint,
            FileCount = 1, SourceBytes = bytes.Length, ArchiveBytes = new FileInfo(archive).Length,
            ArchiveSha256 = Hash(File.ReadAllBytes(archive)), Files = files
        };
        state.SavePartCheckpoint(checkpoint);
        var index = Path.Combine(set, "part-000001.index.json");
        File.Copy(state.GetPartCheckpointPath(1), index);
        File.WriteAllText(Path.Combine(set, "manifest.json"), JsonSerializer.Serialize(new BackupManifest
        {
            RunId = id, BackupDate = date, SourceDirectory = Source, PlanFingerprint = fingerprint,
            FileCount = 1, SourceBytes = bytes.Length,
            Parts = new() { new() {
                PartNumber = 1, ArchiveFileName = checkpoint.ArchiveFileName, Fingerprint = fingerprint,
                FileCount = 1, SourceBytes = bytes.Length, ArchiveBytes = checkpoint.ArchiveBytes,
                ArchiveSha256 = checkpoint.ArchiveSha256, IndexFileName = Path.GetFileName(index),
                IndexBytes = new FileInfo(index).Length, IndexSha256 = Hash(File.ReadAllBytes(index))
            } }
        }));
        Assert.True(state.RecoverPublishedState(new ConcurrentDictionary<string, string>(), new HashSet<DateTime>(), applyState: false));
        return state;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
