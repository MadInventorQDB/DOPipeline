using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

namespace DifferentialBackup.Utilities;

public sealed class DestinationStateDocument
{
    public int FormatVersion { get; set; } = 1;
    public Dictionary<string, Dictionary<string, PublishedFileBaseline>> Sources { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<Guid, string> CommittedRuns { get; set; } = new();
}

public sealed class PublishedFileBaseline
{
    public string Hash { get; set; } = string.Empty;
    public DateTime PublicationDate { get; set; }
}

/// <summary>Only a selected destination's published contents supply its baselines.</summary>
public sealed class DestinationStateStore
{
    public const string FileName = ".differential-backup.state.json";
    public string StatePath { get; }
    private readonly string _destination;
    private readonly string _source;
    private readonly string _sourceScope;

    public DestinationStateStore(string source, string destination)
    {
        _source = DirectoryPath.Normalize(source);
        _destination = DirectoryPath.Normalize(destination);
        _sourceScope = DirectoryAlias.Resolve(source);
        StatePath = Path.Combine(_destination, FileName);
    }

    private DestinationStateDocument Read()
    {
        if (!File.Exists(StatePath)) return new();
        var state = AtomicJson.Read<DestinationStateDocument>(StatePath);
        if (state.FormatVersion != 1 || state.Sources == null || state.CommittedRuns == null ||
            state.Sources.Values.Any(files => files == null || files.Any(pair => pair.Value == null)))
            throw new InvalidDataException("Unsupported destination baseline state.");
        return state;
    }

    public ConcurrentDictionary<string, string> LoadHashes()
    {
        var result = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var state = Read();
        if (state.Sources.TryGetValue(_sourceScope, out var files))
            foreach (var publication in files.GroupBy(pair => pair.Value.PublicationDate))
            {
                var directory = Path.Combine(_destination, publication.Key.ToString("yyyyMMddHHmmss") + ".backup");
                var path = Path.Combine(directory, "manifest.json");
                // Removing a published set also removes its authority to skip capture.
                if (!File.Exists(path)) continue;
                var manifest = AtomicJson.Read<BackupManifest>(path);
                var publicationHash = Hash(path);
                var hasEvidence = manifest.FormatVersion is 2 or 3 && manifest.RunId == Guid.Empty
                    ? state.CommittedRuns.Values.Any(value => string.Equals(value, publicationHash, StringComparison.OrdinalIgnoreCase))
                    : state.CommittedRuns.TryGetValue(manifest.RunId, out var evidence) &&
                        string.Equals(publicationHash, evidence, StringComparison.OrdinalIgnoreCase);
                if (!hasEvidence)
                    throw new InvalidDataException("Baseline publication evidence is invalid.");
                var published = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var part in manifest.Parts)
                {
                    var index = BackupIndexReader.Read(directory, part);
                    if (index == null) continue; // Older sets without relative indexes require a fresh capture.
                    var archive = Path.Combine(directory, part.ArchiveFileName);
                    if (Path.GetFileName(part.ArchiveFileName) != part.ArchiveFileName ||
                        !File.Exists(archive) || new FileInfo(archive).Length != part.ArchiveBytes ||
                        !string.Equals(Hash(archive), part.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Baseline backup archive is missing or corrupt.");
                    if (index.FormatVersion >= 3)
                        foreach (var file in index.Files) published.Add(file.SourcePath, file.Hash);
                }
                foreach (var pair in publication)
                    if (published.TryGetValue(pair.Key, out var hash) && string.Equals(hash, pair.Value.Hash, StringComparison.OrdinalIgnoreCase))
                        result[ArchivePath.ToAccessPath(_source, pair.Key, true)] = hash;
            }
        return result;
    }

    public void Commit(BackupRunState run)
    {
        var manifest = run.RecoveredManifest ?? throw new InvalidOperationException("Publication must be validated before state commit.");
        var published = Path.Combine(_destination, manifest.BackupDate.ToString("yyyyMMddHHmmss") + ".backup");
        var state = Read();
        if (!state.Sources.TryGetValue(_sourceScope, out var files))
            state.Sources[_sourceScope] = files = new(StringComparer.Ordinal);
        foreach (var part in manifest.Parts)
        {
            var index = BackupIndexReader.Read(published, part) ?? run.LoadPartCheckpoint(part.PartNumber)
                ?? throw new InvalidDataException("Published backup has no recoverable file index.");
            foreach (var file in index.Files)
            {
                var relative = run.RelativeSourcePath(run.ResolveSourcePath(file.SourcePath));
                if (!files.TryGetValue(relative, out var previous) || previous.PublicationDate <= manifest.BackupDate)
                    files[relative] = new() { Hash = file.Hash, PublicationDate = manifest.BackupDate };
            }
        }
        using var stream = File.OpenRead(Path.Combine(published, "manifest.json"));
        state.CommittedRuns[run.RunId] = Convert.ToHexString(SHA256.HashData(stream));
        AtomicJson.Write(StatePath, state);
    }

    public void EnsureCommitted(BackupRunState run)
    {
        var manifest = run.RecoveredManifest ?? throw new InvalidOperationException("Publication must be validated before state commit.");
        var path = Path.Combine(_destination, manifest.BackupDate.ToString("yyyyMMddHHmmss") + ".backup", "manifest.json");
        if (!HasCommit(_destination, run.RunId, Hash(path))) Commit(run);
    }

    public static bool HasCommit(string destination, Guid id, string manifestHash)
    {
        var path = Path.Combine(destination, FileName);
        if (!File.Exists(path)) return false;
        var state = AtomicJson.Read<DestinationStateDocument>(path);
        return state.FormatVersion == 1 && state.CommittedRuns != null &&
            state.CommittedRuns.TryGetValue(id, out var evidence) && string.Equals(evidence, manifestHash, StringComparison.OrdinalIgnoreCase);
    }

    public static HashSet<DateTime> ReadHistory(string destination)
    {
        var dates = new HashSet<DateTime>();
        if (!Directory.Exists(destination)) return dates;
        foreach (var path in Directory.EnumerateFileSystemEntries(destination))
        {
            var name = Path.GetFileName(path);
            string? stamp = null;
            if (name.EndsWith(".backup", StringComparison.Ordinal) && Directory.Exists(path) &&
                File.Exists(Path.Combine(path, "manifest.json")))
            {
                var manifest = AtomicJson.Read<BackupManifest>(Path.Combine(path, "manifest.json"));
                if (manifest.FormatVersion is not (2 or 3 or 4)) continue;
                stamp = name[..^7];
            }
            else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) stamp = name[..^4];
            if (stamp != null && DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) dates.Add(date);
        }
        return dates;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
