using System.Security.Cryptography;
using DifferentialBackup.Components;

namespace DifferentialBackup.Utilities;

public sealed class BackupRunTag
{
    public int FormatVersion { get; set; } = 1;
    public Guid RunId { get; set; }
    public Guid OwnerId { get; set; }
    public string Role { get; set; } = string.Empty;
}

public sealed class BackupInitialization
{
    public int FormatVersion { get; set; } = 1;
    public Guid RunId { get; set; }
    public Guid OwnerId { get; set; }
    public string SourceDirectory { get; set; } = string.Empty;
    public string BackupDestination { get; set; } = string.Empty;
    public string CanonicalSource { get; set; } = string.Empty;
    public string CanonicalDestination { get; set; } = string.Empty;
    public string? LegacyDirectoryName { get; set; }
    public bool TagsReady { get; set; }
    public bool HasJob { get; set; }
}

public sealed class BackupCompletion
{
    public int FormatVersion { get; set; } = 1;
    public Guid RunId { get; set; }
    public Guid OwnerId { get; set; }
    public string CanonicalSource { get; set; } = string.Empty;
    public string CanonicalDestination { get; set; } = string.Empty;
    public string? ManifestName { get; set; }
    public string? ManifestSha256 { get; set; }
    public StateCommitStep StateCommit { get; set; }
    public bool DestinationStateRequired { get; set; }
    public int ExitCode { get; set; }
}

/// <summary>Owns root locks and the recoverable lifecycle of an unfinished run.</summary>
public sealed class BackupRunSession : IDisposable
{
    public const string TagFileName = ".differential-backup.active.json";
    public const string LockFileName = ".differential-backup.lock";
    private readonly List<ExclusiveOperationLock> _locks = new();
    private readonly ICheckpointObserver _observer;
    private readonly Guid _owner;
    private readonly string _requestedSource;
    private readonly string _requestedDestination;
    public string SourceDirectory { get; }
    public string BackupDestination { get; }
    public string StagingRoot { get; }
    public BackupInitialization Identity { get; private set; } = null!;
    public Guid RunId => Identity.RunId;
    public bool CleanupCompleted { get; private set; }
    public int CompletionExitCode { get; private set; }
    public string StagingDirectory => Path.Combine(StagingRoot, RunId.ToString("N"));
    private string InitializationPath(Guid id) => Path.Combine(StagingRoot, "initializations", id.ToString("N") + ".json");
    private string CompletionPath(Guid id) => Path.Combine(StagingRoot, "completed", id.ToString("N") + ".json");

    private BackupRunSession(string source, string destination, string stagingRoot, ICheckpointObserver? observer)
    {
        _requestedSource = DirectoryPath.Normalize(source);
        _requestedDestination = DirectoryPath.Normalize(destination);
        SourceDirectory = DirectoryAlias.Resolve(_requestedSource);
        BackupDestination = DirectoryAlias.Resolve(_requestedDestination);
        StagingRoot = DirectoryPath.Normalize(stagingRoot);
        _observer = observer ?? NoOpCheckpointObserver.Instance;
        if (!Directory.Exists(SourceDirectory)) throw new DirectoryNotFoundException(SourceDirectory);
        DirectoryAlias.RequireSeparate(SourceDirectory, BackupDestination);
        DirectoryAlias.RequireSeparate(SourceDirectory, StagingRoot);
        DirectoryAlias.RequireSeparate(BackupDestination, StagingRoot);
        Directory.CreateDirectory(BackupDestination);
        Directory.CreateDirectory(StagingRoot);
        try
        {
            using (ExclusiveOperationLock.Acquire(Path.Combine(StagingRoot, "profile.lock")))
            {
                var ownerPath = Path.Combine(StagingRoot, "owner.json");
                if (!File.Exists(ownerPath)) AtomicJson.Write(ownerPath, Guid.NewGuid(), false);
                _owner = AtomicJson.Read<Guid>(ownerPath);
            }
            if (_owner == Guid.Empty) throw new InvalidDataException("Invalid staging profile owner.");
            _locks.Add(ExclusiveOperationLock.Acquire(Path.Combine(SourceDirectory, LockFileName)));
            _locks.Add(ExclusiveOperationLock.Acquire(Path.Combine(BackupDestination, LockFileName)));
            Prepare();
        }
        catch { Dispose(); throw; }
    }

    public static BackupRunSession Open(string source, string destination, string? stagingRoot = null, ICheckpointObserver? observer = null) =>
        new(source, destination, stagingRoot ?? Environment.GetEnvironmentVariable("DIFFERENTIALBACKUP_STAGING_ROOT") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DifferentialBackup", "Staging"), observer);

    public BackupRunState CreateRunState() => new(SourceDirectory, BackupDestination, StagingRoot, _observer, Identity)
    { DestinationState = new DestinationStateStore(SourceDirectory, BackupDestination) };

    private void Prepare()
    {
        var source = ReadTag(SourceDirectory, "source");
        var destination = ReadTag(BackupDestination, "destination");
        if (source != null && destination != null && source.RunId != destination.RunId)
            throw new InvalidDataException("The roots belong to different unfinished backups. Resolve those runs first.");
        if ((source != null && source.OwnerId != _owner) || (destination != null && destination.OwnerId != _owner))
            throw new InvalidDataException("A root tag belongs to another staging profile.");
        var tag = source ?? destination;
        if (tag != null)
        {
            if (tag.OwnerId != _owner) throw new InvalidDataException($"Run {tag.RunId} belongs to another staging profile.");
            if (File.Exists(CompletionPath(tag.RunId)))
            {
                FinishCleanup(AtomicJson.Read<BackupCompletion>(CompletionPath(tag.RunId)));
                return;
            }
        }
        var canonicalSource = DirectoryAlias.Resolve(SourceDirectory);
        var canonicalDestination = DirectoryAlias.Resolve(BackupDestination);
        if (tag != null)
        {
            Identity = ReadInitialization(tag.RunId);
            if (Identity.TagsReady && (source == null || destination == null))
                throw new InvalidDataException("An established run is missing a root tag. Refusing to bind a replacement directory.");
            if (!Identity.TagsReady && (Identity.CanonicalSource != canonicalSource || Identity.CanonicalDestination != canonicalDestination))
                throw new InvalidDataException("Cannot verify the roots of an interrupted initialization.");
            if (Identity.TagsReady && !Directory.Exists(StagingDirectory))
                throw new InvalidDataException($"Staging data for run {RunId} is missing.");
        }
        else
        {
            var completedDirectory = Path.Combine(StagingRoot, "completed");
            if (Directory.Exists(completedDirectory))
                foreach (var path in Directory.EnumerateFiles(completedDirectory, "*.json"))
                {
                    var completed = AtomicJson.Read<BackupCompletion>(path);
                    if (completed.CanonicalSource == canonicalSource && completed.CanonicalDestination == canonicalDestination)
                    {
                        FinishCleanup(completed);
                        return;
                    }
                }
            var initializationDirectory = Path.Combine(StagingRoot, "initializations");
            var candidates = Directory.Exists(initializationDirectory)
                ? Directory.EnumerateFiles(initializationDirectory, "*.json").Select(AtomicJson.Read<BackupInitialization>)
                    .Where(value => value.CanonicalSource == canonicalSource && value.CanonicalDestination == canonicalDestination).ToList()
                : new List<BackupInitialization>();
            if (candidates.Count > 1) throw new InvalidDataException("Multiple unfinished initializations match these roots.");
            if (candidates.Count == 1)
            {
                Identity = candidates[0];
                ValidateInitialization(Identity, Identity.RunId);
                if (Identity.TagsReady) throw new InvalidDataException("An established run lost both root tags. Explicit resolution is required.");
            }
            else
            {
                var legacy = FindLegacyRun(canonicalSource, canonicalDestination);
                Identity = new BackupInitialization
                {
                    RunId = legacy?.Job.RunId is { } id && id != Guid.Empty ? id : Guid.NewGuid(),
                    OwnerId = _owner,
                    SourceDirectory = legacy?.Job.SourceDirectory ?? _requestedSource,
                    BackupDestination = legacy?.Job.BackupDestination ?? _requestedDestination,
                    CanonicalSource = canonicalSource,
                    CanonicalDestination = canonicalDestination,
                    LegacyDirectoryName = legacy?.Name,
                    HasJob = legacy != null
                };
                AtomicJson.Write(InitializationPath(RunId), Identity, false);
                _observer.Reached("tag-initialization-saved");
            }
        }
        if (!Directory.Exists(StagingDirectory))
        {
            if (Identity.LegacyDirectoryName != null)
            {
                if (!IsLegacyName(Identity.LegacyDirectoryName)) throw new InvalidDataException("Unsafe legacy staging name.");
                Directory.Move(Path.Combine(StagingRoot, Identity.LegacyDirectoryName), StagingDirectory);
            }
            else Directory.CreateDirectory(StagingDirectory);
        }
        if (source == null) CreateTag(SourceDirectory, "source");
        if (destination == null) CreateTag(BackupDestination, "destination");
        if (!Identity.TagsReady)
        {
            Identity.TagsReady = true;
            AtomicJson.Write(InitializationPath(RunId), Identity);
            _observer.Reached("tag-pair-committed");
        }
    }

    private BackupInitialization ReadInitialization(Guid id)
    {
        var value = AtomicJson.Read<BackupInitialization>(InitializationPath(id));
        ValidateInitialization(value, id);
        return value;
    }

    private void ValidateInitialization(BackupInitialization value, Guid id)
    {
        if (value.FormatVersion != 1 || id == Guid.Empty || value.RunId != id || value.OwnerId != _owner ||
            !Path.IsPathFullyQualified(value.SourceDirectory) || !Path.IsPathFullyQualified(value.BackupDestination) ||
            !Path.IsPathFullyQualified(value.CanonicalSource) || !Path.IsPathFullyQualified(value.CanonicalDestination))
            throw new InvalidDataException("Initialization identity does not match the run tag.");
    }

    private BackupRunTag? ReadTag(string root, string role)
    {
        var path = Path.Combine(root, TagFileName);
        if (Directory.Exists(path)) throw new InvalidDataException($"Reserved tag path is occupied: '{path}'.");
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A run tag must be a regular file.");
        var value = AtomicJson.Read<BackupRunTag>(path);
        if (value.FormatVersion != 1 || value.RunId == Guid.Empty || value.OwnerId == Guid.Empty || value.Role != role)
            throw new InvalidDataException($"Unrecognized or incorrectly bound run tag: '{path}'.");
        return value;
    }

    private void CreateTag(string root, string role)
    {
        AtomicJson.Write(Path.Combine(root, TagFileName), new BackupRunTag { RunId = RunId, OwnerId = _owner, Role = role }, false);
        _observer.Reached("tag-" + role + "-created");
    }

    public void Complete(BackupRunState state, int exitCode = 0)
    {
        if (state.RunId != RunId) throw new InvalidDataException("Cannot complete a different run.");
        var job = state.Job;
        if (job is { Published: true } && job.StateCommitStep != StateCommitStep.Complete)
            throw new InvalidOperationException("Publication state has not been committed.");
        if (job is { Published: false, TotalParts: > 0 }) throw new InvalidOperationException("Backup has not been published.");
        string? manifest = job is { Published: true } ? job.BackupDate.ToString("yyyyMMddHHmmss") + ".backup/manifest.json" : null;
        var completed = new BackupCompletion
        {
            RunId = RunId, OwnerId = _owner, CanonicalSource = DirectoryAlias.Resolve(SourceDirectory),
            CanonicalDestination = DirectoryAlias.Resolve(BackupDestination), ManifestName = manifest,
            ManifestSha256 = manifest == null ? null : Hash(Path.Combine(BackupDestination, manifest)),
            StateCommit = job?.StateCommitStep ?? StateCommitStep.NotRequired,
            DestinationStateRequired = state.DestinationState != null, ExitCode = exitCode
        };
        AtomicJson.Write(CompletionPath(RunId), completed, false);
        _observer.Reached("tag-completion-saved");
        FinishCleanup(completed);
    }

    private void FinishCleanup(BackupCompletion completed)
    {
        if (completed.FormatVersion != 1 || completed.OwnerId != _owner || completed.RunId == Guid.Empty)
            throw new InvalidDataException("Invalid completion record.");
        if (completed.ManifestName != null)
        {
            var manifest = DirectoryPath.FromRelative(BackupDestination, completed.ManifestName);
            if (completed.StateCommit != StateCommitStep.Complete || Hash(manifest) != completed.ManifestSha256)
                throw new InvalidDataException("Completion record does not match the published backup.");
        }
        var sourceTag = ReadTag(SourceDirectory, "source");
        var destinationTag = ReadTag(BackupDestination, "destination");
        if ((sourceTag != null && (sourceTag.RunId != completed.RunId || sourceTag.OwnerId != completed.OwnerId)) ||
            (destinationTag != null && (destinationTag.RunId != completed.RunId || destinationTag.OwnerId != completed.OwnerId)))
            throw new InvalidDataException("Completion record does not match the root tags; staging was preserved.");
        if (sourceTag == null || destinationTag == null)
        {
            if (DirectoryAlias.Resolve(SourceDirectory) != completed.CanonicalSource ||
                DirectoryAlias.Resolve(BackupDestination) != completed.CanonicalDestination)
                throw new InvalidDataException("Cannot verify a missing root during interrupted cleanup.");
        }
        if (completed.ManifestName != null && completed.DestinationStateRequired &&
            !DestinationStateStore.HasCommit(BackupDestination, completed.RunId, completed.ManifestSha256!))
            throw new InvalidDataException("Completion has no destination state-commit evidence.");
        var initialization = File.Exists(InitializationPath(completed.RunId)) ? ReadInitialization(completed.RunId) :
            new BackupInitialization { RunId = completed.RunId, OwnerId = _owner,
                SourceDirectory = SourceDirectory, BackupDestination = BackupDestination,
                CanonicalSource = completed.CanonicalSource, CanonicalDestination = completed.CanonicalDestination };
        if (!File.Exists(InitializationPath(completed.RunId)) &&
            Directory.Exists(Path.Combine(StagingRoot, completed.RunId.ToString("N"))))
            throw new InvalidDataException("Cleanup initialization is missing while staging still exists.");
        Identity = initialization;
        var state = new BackupRunState(SourceDirectory, BackupDestination, StagingRoot, _observer, initialization);
        state.ClearRun();
        if (Directory.Exists(state.StagingDirectory)) throw new IOException("Unrecognized staging files prevent cleanup; they have been preserved.");
        _observer.Reached("tag-staging-removed");
        RemoveTag(SourceDirectory, "source", completed.RunId);
        RemoveTag(BackupDestination, "destination", completed.RunId);
        File.Delete(InitializationPath(completed.RunId));
        _observer.Reached("tag-initialization-removed");
        File.Delete(CompletionPath(completed.RunId));
        CleanupCompleted = true;
        CompletionExitCode = completed.ExitCode;
        _observer.Reached("tag-completion-removed");
    }

    private void RemoveTag(string root, string role, Guid id)
    {
        var tag = ReadTag(root, role);
        if (tag != null)
        {
            if (tag.RunId != id || tag.OwnerId != _owner) throw new InvalidDataException("Cleanup encountered another run's tag.");
            File.Delete(Path.Combine(root, TagFileName));
        }
        _observer.Reached("tag-" + role + "-removed");
    }

    private (string Name, BackupJobState Job)? FindLegacyRun(string canonicalSource, string canonicalDestination)
    {
        var matches = new List<(string, BackupJobState)>();
        foreach (var directory in Directory.EnumerateDirectories(StagingRoot))
        {
            var name = Path.GetFileName(directory);
            if (!IsLegacyName(name) || !File.Exists(Path.Combine(directory, "job.json"))) continue;
            var job = AtomicJson.Read<BackupJobState>(Path.Combine(directory, "job.json"));
            string source, destination;
            try { source = DirectoryAlias.Resolve(job.SourceDirectory); destination = DirectoryAlias.Resolve(job.BackupDestination); }
            catch (IOException) { continue; }
            if (source != canonicalSource || destination != canonicalDestination) continue;
            var validation = new BackupRunState(job.SourceDirectory, job.BackupDestination, StagingRoot);
            if (!DirectoryPath.Equals(validation.StagingDirectory, directory)) throw new InvalidDataException("Legacy staging directory does not match its recorded roots.");
            validation.BeginRun();
            var published = validation.RecoverPublishedState(new(System.StringComparer.Ordinal), new(),
                applyState: false, completePublication: false);
            foreach (var path in Directory.EnumerateFiles(directory, "part-*.index.json"))
            {
                var checkpoint = AtomicJson.Read<BackupPartCheckpoint>(path);
                if (checkpoint.Files == null || checkpoint.Fingerprint != BackupFingerprint.ForFiles(checkpoint.Files))
                    throw new InvalidDataException("Legacy checkpoint fingerprint is invalid.");
                foreach (var file in checkpoint.Files)
                {
                    _ = validation.ResolveSourcePath(file.SourcePath);
                    _ = ArchivePath.Components(file.EntryName, portable: checkpoint.FormatVersion >= 3);
                }
                if (Path.GetFileName(checkpoint.ArchiveFileName) != checkpoint.ArchiveFileName) throw new InvalidDataException("Unsafe legacy archive name.");
                var archive = Path.Combine(directory, checkpoint.ArchiveFileName);
                if (!File.Exists(archive) && !published)
                    throw new InvalidDataException("An acknowledged legacy capture is missing; staging was preserved.");
                if (File.Exists(archive) && !string.IsNullOrEmpty(checkpoint.ArchiveSha256) &&
                    !string.Equals(Hash(archive), checkpoint.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Legacy archive checksum is invalid.");
            }
            matches.Add((name, job));
        }
        if (matches.Count > 1) throw new InvalidDataException("Multiple legacy runs match these roots. Explicit resolution is required.");
        return matches.Count == 0 ? null : matches[0];
    }

    private static bool IsLegacyName(string name) => name.Length == 16 && name.All(Uri.IsHexDigit);
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    public static bool IsReservedRootFile(string name) => name is TagFileName or LockFileName or DestinationStateStore.FileName ||
        AtomicJson.IsTemporaryName(name, TagFileName) || AtomicJson.IsTemporaryName(name, DestinationStateStore.FileName);
    public void Dispose() { for (var index = _locks.Count - 1; index >= 0; index--) _locks[index].Dispose(); _locks.Clear(); }
}
