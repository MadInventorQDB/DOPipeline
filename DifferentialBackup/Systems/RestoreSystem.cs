using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Systems;

/// <summary>
/// Restores entries independently. A target is replaced only after the entry
/// has been copied to a unique sibling and its bytes have been verified.
/// </summary>
public sealed class RestoreSystem : ISystem, ICancellableSystem
{
    private readonly string _backupDestination;
    private readonly DateTime _backupDate;
    private readonly string _restoreDestination;
    private readonly ICheckpointObserver _checkpointObserver;

    public RestoreSystem(
        string backupDestination,
        DateTime backupDate,
        string restoreDestination,
        ICheckpointObserver? checkpointObserver = null)
    {
        _backupDestination = Path.GetFullPath(backupDestination);
        _backupDate = backupDate;
        _restoreDestination = Path.GetFullPath(restoreDestination);
        _checkpointObserver = checkpointObserver ?? NoOpCheckpointObserver.Instance;
    }

    public Result Execute(Entity entity, IComponentStorage storage)
    {
        return Execute(entity, storage, CancellationToken.None);
    }

    public Result Execute(Entity entity, IComponentStorage storage, CancellationToken cancellationToken)
    {
        try
        {
            var opEntity = storage.Query<OperationComponent>().FirstOrDefault();
            var op = opEntity != null ? storage.GetComponent<OperationComponent>(opEntity) : null;
            if (op?.Phase is OperationPhase.RetryWaiting or OperationPhase.Finished)
            {
                return Result.Success();
            }

            var selection = SelectArchives(_backupDestination, _backupDate);
            var archives = selection.Archives;
            var selectedBackupIncomplete = selection.IsIncomplete;
            if (archives.Count == 0)
            {
                return Result.Fail("Backup date not found.");
            }

            if (selectedBackupIncomplete)
            {
                var targetOpEntity = storage.Query<OperationComponent>().FirstOrDefault() ?? entity;
                storage.SetComponent(targetOpEntity, new IncompleteBackupComponent
                {
                    RunId = FindRunId(entity, storage),
                    Reason = "Selected backup archive is incomplete."
                });
            }

            var descriptors = ReadDescriptors(archives);
            ValidateDescriptors(descriptors);
            var archiveIdentity = ComputeArchiveIdentity(archives);
            var journal = LoadJournal(archiveIdentity);

            var existingByEntryIdentity = new Dictionary<string, (Entity Entity, RestoreEntryComponent Entry)>(StringComparer.Ordinal);
            foreach (var candidate in storage.Query<RestoreEntryComponent>())
            {
                var value = storage.GetComponent<RestoreEntryComponent>(candidate);
                if (value != null && string.Equals(value.ArchiveIdentity, archiveIdentity, StringComparison.Ordinal))
                {
                    existingByEntryIdentity[value.EntryIdentity] = (candidate, value);
                }
            }

            var entries = new List<(Entity Entity, RestoreEntryComponent Entry, ArchiveEntryDescriptor Descriptor)>();
            foreach (var descriptor in descriptors)
            {
                Entity restoreEntity;
                RestoreEntryComponent entry;
                if (existingByEntryIdentity.TryGetValue(descriptor.Identity, out var existingPair))
                {
                    restoreEntity = existingPair.Entity;
                    entry = existingPair.Entry;
                }
                else
                {
                    restoreEntity = new Entity();
                    entry = new RestoreEntryComponent
                    {
                        RunId = FindRunId(entity, storage),
                        ArchiveIdentity = archiveIdentity,
                        EntryIdentity = descriptor.Identity,
                        EntryPath = descriptor.EntryName,
                        TargetPath = descriptor.TargetPath,
                        ExpectedLength = descriptor.ExpectedLength,
                        ExpectedHash = descriptor.ExpectedHash
                    };
                    existingByEntryIdentity[descriptor.Identity] = (restoreEntity, entry);
                }
                storage.SetComponent(restoreEntity, entry);
                if (existingPair == default)
                {
                    // RestoreEntry rows are deliberately separate from the
                    // operation root so retries can query them by state.
                    storage.SetComponent(restoreEntity, entry);
                }

                if (Path.GetFileName(descriptor.EntryName.Replace('/', Path.DirectorySeparatorChar))?.Length == 0)
                {
                    try
                    {
                        Directory.CreateDirectory(descriptor.TargetPath);
                    }
                    catch (IOException ex)
                    {
                        entry.State = RestoreEntryState.Deferred;
                        storage.SetComponent(restoreEntity, entry);
                        SetRestoreIssue(restoreEntity, entry, ex, storage);
                        continue;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        entry.State = RestoreEntryState.Deferred;
                        storage.SetComponent(restoreEntity, entry);
                        SetRestoreIssue(restoreEntity, entry, ex, storage);
                        continue;
                    }
                    entry.State = RestoreEntryState.Restored;
                    storage.SetComponent(restoreEntity, entry);
                    continue;
                }

                if (journal.Successes.TryGetValue(descriptor.Identity, out var saved) &&
                    IsVerifiedTarget(descriptor.TargetPath, saved.Length, saved.Hash))
                {
                    entry.State = RestoreEntryState.Restored;
                    storage.SetComponent(restoreEntity, entry);
                    storage.SetComponent(restoreEntity, new RestoreSuccessComponent
                    {
                        RunId = entry.RunId,
                        ArchiveIdentity = archiveIdentity,
                        EntryIdentity = descriptor.Identity,
                        TargetPath = descriptor.TargetPath,
                        Length = saved.Length,
                        Hash = saved.Hash
                    });
                    continue;
                }

                if (!journal.Successes.ContainsKey(descriptor.Identity) &&
                    descriptor.ExpectedLength.HasValue && descriptor.ExpectedHash != null &&
                    IsVerifiedTarget(descriptor.TargetPath, descriptor.ExpectedLength.Value, descriptor.ExpectedHash))
                {
                    var verifiedSuccess = new RestoreJournalSuccess(descriptor.ExpectedLength.Value, descriptor.ExpectedHash);
                    journal.Successes[descriptor.Identity] = verifiedSuccess;
                    RestoreJournal.AppendSuccess(_restoreDestination, archiveIdentity, descriptor.Identity, verifiedSuccess, journal);
                    entry.State = RestoreEntryState.Restored;
                    storage.SetComponent(restoreEntity, entry);
                    storage.SetComponent(restoreEntity, new RestoreSuccessComponent
                    {
                        RunId = entry.RunId,
                        ArchiveIdentity = archiveIdentity,
                        EntryIdentity = descriptor.Identity,
                        TargetPath = descriptor.TargetPath,
                        Length = descriptor.ExpectedLength.Value,
                        Hash = descriptor.ExpectedHash
                    });
                    continue;
                }

                entry.State = RestoreEntryState.Pending;
                storage.SetComponent(restoreEntity, entry);
                entries.Add((restoreEntity, entry, descriptor));
            }

            var entriesByArchive = entries.GroupBy(item => item.Descriptor.ArchivePath, StringComparer.OrdinalIgnoreCase);
            foreach (var group in entriesByArchive)
            {
                using var zip = ZipFile.OpenRead(group.Key);
                foreach (var item in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = item.Entry;
                    entry.LastAttemptedPass++;
                    try
                    {
                        RestoreEntry(item.Descriptor, zip, archiveIdentity, journal, cancellationToken);
                        entry.State = RestoreEntryState.Restored;
                        storage.SetComponent(item.Entity, entry);
                        var record = journal.Successes[item.Descriptor.Identity];
                        storage.SetComponent(item.Entity, new RestoreSuccessComponent
                        {
                            RunId = entry.RunId,
                            ArchiveIdentity = archiveIdentity,
                            EntryIdentity = item.Descriptor.Identity,
                            TargetPath = item.Descriptor.TargetPath,
                            Length = record.Length,
                            Hash = record.Hash
                        });
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (RestoreTargetException ex)
                    {
                        entry.State = RestoreEntryState.Deferred;
                        storage.SetComponent(item.Entity, entry);
                        SetRestoreIssue(item.Entity, entry, ex, storage);
                        // Keep processing independent entries; the issue remains
                        // in component data for the warning/retry path.
                    }
                    var issue = storage.GetComponent<BackupIssueComponent>(item.Entity);
                    if (issue != null && entry.State == RestoreEntryState.Restored)
                    {
                        issue.Resolved = true;
                        storage.SetComponent(item.Entity, issue);
                    }
                }
            }

            SaveJournal(journal);
            var operationEntity = storage.Query<OperationComponent>().FirstOrDefault();
            if (operationEntity != null)
            {
                var schedule = storage.GetComponent<RetryScheduleComponent>(operationEntity);
                // When RetryScheduleComponent is present, RestorePassCompletionSystem decides
                // retry transitions and final outcome. Only finalize here for unmanaged/standalone runs.
                if (schedule == null)
                {
                    var operation = storage.GetComponent<OperationComponent>(operationEntity)!;
                    var unresolved = storage.Query<RestoreEntryComponent>()
                        .Select(storage.GetComponent<RestoreEntryComponent>)
                        .Count(value => value != null && value.ArchiveIdentity == archiveIdentity && value.State == RestoreEntryState.Deferred);
                    var outcome = new OperationOutcomeComponent
                    {
                        RunId = operation.RunId,
                        Outcome = unresolved == 0 && !selectedBackupIncomplete ? OperationOutcome.Success : OperationOutcome.Warnings,
                        ExitCode = unresolved == 0 && !selectedBackupIncomplete ? 0 : 1,
                        DeferredCount = unresolved,
                        CompletedUtc = DateTimeOffset.UtcNow
                    };
                    TryWriteReport(outcome, archiveIdentity, storage);
                    storage.SetComponent(operationEntity, outcome);
                    operation.Phase = OperationPhase.Finished;
                    storage.SetComponent(operationEntity, operation);
                }
            }

            return Result.Success();
        }
        catch (InvalidDataException ex)
        {
            return Result.Fail($"Failed to restore files: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            return Result.Fail($"Failed to restore files: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result.Cancelled();
        }
    }

    public static string ResolveArchiveIdentity(string backupDestination, DateTime backupDate)
    {
        var selection = SelectArchives(backupDestination, backupDate);
        return selection.Archives.Count > 0 ? ComputeArchiveIdentity(selection.Archives) : string.Empty;
    }

    private static ArchiveSelection SelectArchives(string backupDestination, DateTime backupDate)
    {
        var timestamp = backupDate.ToString("yyyyMMddHHmmss");
        var backupSetPath = Path.Combine(backupDestination, timestamp + ".backup");
        if (Directory.Exists(backupSetPath))
        {
            var manifestPath = Path.Combine(backupSetPath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException("Backup manifest is missing.");
            }

            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.FormatVersion is not (2 or 3))
            {
                throw new InvalidDataException("Backup manifest format is not supported.");
            }

            var archives = manifest.Parts
                .OrderBy(part => part.PartNumber)
                .Select(part => new ArchiveDescriptor(
                    Path.Combine(backupSetPath, ValidatePartName(part.ArchiveFileName)),
                    part.ArchiveBytes,
                    part.ArchiveSha256,
                    BackupIndexReader.Read(backupSetPath, part)?.Files))
                .ToList();
            return new ArchiveSelection(archives, manifest.FormatVersion >= 3 && !manifest.IsComplete);
        }

        var legacyArchivePath = Path.Combine(backupDestination, timestamp + ".zip");
        return File.Exists(legacyArchivePath)
            ? new ArchiveSelection(
                new List<ArchiveDescriptor> { new(legacyArchivePath, new FileInfo(legacyArchivePath).Length, null) },
                false)
            : new ArchiveSelection(new List<ArchiveDescriptor>(), false);
    }

    private List<ArchiveEntryDescriptor> ReadDescriptors(IReadOnlyList<ArchiveDescriptor> archives)
    {
        var descriptors = new List<ArchiveEntryDescriptor>();
        foreach (var archive in archives)
        {
            if (!File.Exists(archive.Path) || new FileInfo(archive.Path).Length != archive.ExpectedLength)
            {
                throw new InvalidDataException($"Backup part is missing or incomplete: '{archive.Path}'.");
            }

            if (!string.IsNullOrWhiteSpace(archive.ExpectedHash) &&
                !string.Equals(archive.ExpectedHash, ComputeFileIdentity(archive.Path), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Backup part checksum is invalid: '{archive.Path}'.");
            }

            using var zip = ZipFile.OpenRead(archive.Path);
            var archiveIdentity = ComputeFileIdentity(archive.Path);
            var expected = archive.Files?.ToDictionary(file => file.EntryName, StringComparer.OrdinalIgnoreCase);
            if (expected != null && zip.Entries.Count != expected.Count)
                throw new InvalidDataException("Archive entry count does not match its index.");
            foreach (var entry in zip.Entries)
            {
                BackupPartFile? file = null;
                if (expected != null && !expected.TryGetValue(entry.FullName, out file))
                    throw new InvalidDataException("Archive entry is not present in its index.");
                var target = GetSafeRestorePath(entry.FullName);
                descriptors.Add(new ArchiveEntryDescriptor(
                    archive.Path,
                    archiveIdentity + ":" + entry.FullName,
                    entry.FullName,
                    target,
                    file?.Length,
                    file?.Hash));
            }
        }

        return descriptors;
    }

    private void ValidateDescriptors(IReadOnlyList<ArchiveEntryDescriptor> descriptors)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            if (!targets.Add(descriptor.TargetPath))
            {
                throw new InvalidDataException($"Backup contains duplicate restore target: '{descriptor.EntryName}'.");
            }
        }
    }

    private void RestoreEntry(
        ArchiveEntryDescriptor descriptor,
        ZipArchive zip,
        string archiveIdentity,
        RestoreJournal journal,
        CancellationToken cancellationToken)
    {
        // Revalidate immediately before mutation in case a target ancestor was
        // replaced with a reparse point after the initial manifest scan.
        _ = GetSafeRestorePath(descriptor.EntryName);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(descriptor.TargetPath)!);
        }
        catch (IOException ex)
        {
            throw new RestoreTargetException(descriptor.TargetPath, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RestoreTargetException(descriptor.TargetPath, ex);
        }
        var temporaryPath = descriptor.TargetPath + ".differential-restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var entry = zip.GetEntry(descriptor.EntryName)
                ?? throw new InvalidDataException($"Archive entry disappeared: '{descriptor.EntryName}'.");
            long length;
            string observedHash;
            using (var input = entry.Open())
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                FileStream output;
                try
                {
                    output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                }
                catch (IOException ex)
                {
                    throw new RestoreTargetException(descriptor.TargetPath, ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new RestoreTargetException(descriptor.TargetPath, ex);
                }

                using (output)
                {
                var buffer = new byte[1024 * 1024];
                length = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        output.Write(buffer, 0, read);
                    }
                    catch (IOException ex)
                    {
                        throw new RestoreTargetException(descriptor.TargetPath, ex);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        throw new RestoreTargetException(descriptor.TargetPath, ex);
                    }
                    hash.AppendData(buffer, 0, read);
                    length += read;
                }

                try
                {
                    output.Flush(flushToDisk: true);
                }
                catch (IOException ex)
                {
                    throw new RestoreTargetException(descriptor.TargetPath, ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new RestoreTargetException(descriptor.TargetPath, ex);
                }
                observedHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
            }
            if (descriptor.ExpectedLength.HasValue && descriptor.ExpectedLength.Value != length ||
                descriptor.ExpectedHash != null && !string.Equals(descriptor.ExpectedHash, observedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Restored entry failed integrity validation: '{descriptor.EntryName}'.");
            }

            try
            {
                File.Move(temporaryPath, descriptor.TargetPath, true);
            }
            catch (IOException ex)
            {
                throw new RestoreTargetException(descriptor.TargetPath, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new RestoreTargetException(descriptor.TargetPath, ex);
            }
            _checkpointObserver.Reached("restore-target-replaced");
            var success = new RestoreJournalSuccess(length, observedHash);
            journal.Successes[descriptor.Identity] = success;
            RestoreJournal.AppendSuccess(_restoreDestination, archiveIdentity, descriptor.Identity, success, journal);
            _checkpointObserver.Reached("restore-entry-restored");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the initiating target, archive, or journal error.
            }
        }
    }

    private string GetSafeRestorePath(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) ||
            Path.IsPathRooted(entryName) ||
            entryName.Contains(':', StringComparison.Ordinal) ||
            entryName.Contains('\0'))
        {
            throw new InvalidDataException($"Backup entry contains an unsafe path: '{entryName}'.");
        }

        var restoreRoot = _restoreDestination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(_restoreDestination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativePath = entryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var restorePath = Path.GetFullPath(Path.Combine(restoreRoot, relativePath));
        if (!restorePath.StartsWith(restoreRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Backup entry escapes the restore directory: '{entryName}'.");
        }

        var current = Path.GetDirectoryName(restorePath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Backup target ancestor is a reparse point: '{current}'.");
            }

            if (string.Equals(current, destination, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!current.StartsWith(restoreRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return restorePath;
    }

    private RestoreJournal LoadJournal(string archiveIdentity) =>
        RestoreJournal.Load(_restoreDestination, archiveIdentity);

    private void SaveJournal(RestoreJournal journal) =>
        RestoreJournal.Save(_restoreDestination, journal);

    private string JournalPath => Path.Combine(_restoreDestination, ".differential-restore.json");

    private static bool IsVerifiedTarget(string path, long length, string hash)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != length)
        {
            return false;
        }

        var attempt = HashUtility.TryComputeSHA256(path);
        return attempt.Succeeded && string.Equals(attempt.Hash, hash, StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidatePartName(string name)
    {
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Backup manifest contains an invalid part name: '{name}'.");
        }
        return name;
    }

    private static string ComputeArchiveIdentity(IReadOnlyList<ArchiveDescriptor> archives) =>
        string.Join("|", archives.Select(archive => ComputeFileIdentity(archive.Path)));

    private static string ComputeFileIdentity(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Guid FindRunId(Entity entity, IComponentStorage storage) =>
        storage.GetComponent<OperationComponent>(entity)?.RunId ??
        storage.Query<OperationComponent>().Select(candidate => storage.GetComponent<OperationComponent>(candidate)?.RunId).FirstOrDefault() ??
        Guid.Empty;

    private static void SetRestoreIssue(Entity entity, RestoreEntryComponent entry, Exception exception, IComponentStorage storage)
    {
        var now = DateTimeOffset.UtcNow;
        var issue = storage.GetComponent<BackupIssueComponent>(entity) ?? new BackupIssueComponent
        {
            RunId = entry.RunId,
            StableKey = entry.TargetPath,
            Path = entry.TargetPath,
            Stage = "Restore",
            Category = "RestoreTarget",
            FirstOccurrenceUtc = now,
            Retryable = true
        };
        issue.AttemptCount++;
        issue.LastOccurrenceUtc = now;
        issue.ExceptionType ??= exception.GetType().FullName;
        issue.OriginalMessage ??= exception.Message;
        issue.Resolved = false;
        storage.SetComponent(entity, issue);
    }

    private void TryWriteReport(OperationOutcomeComponent outcome, string archiveIdentity, IComponentStorage storage)
    {
        try
        {
            var path = Path.Combine(_restoreDestination, $"restore-{archiveIdentity[..Math.Min(16, archiveIdentity.Length)]}.report.json");
            var temporary = path + ".tmp";
            var issues = storage.Query<BackupIssueComponent>()
                .Select(storage.GetComponent<BackupIssueComponent>)
                .Where(issue => issue != null && !issue.Resolved)
                .Select(issue => new
                {
                    issue!.Path,
                    issue.Stage,
                    issue.Category,
                    issue.ExceptionType,
                    issue.OriginalMessage,
                    issue.AttemptCount
                });
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new
                {
                    outcome.Outcome,
                    outcome.ExitCode,
                    outcome.DeferredCount,
                    outcome.ErrorMessage,
                    outcome.CompletedUtc,
                    Issues = issues
                }));
                File.Move(temporary, path, true);
                outcome.ReportPath = path;
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // Reporting cleanup cannot replace the restore result.
                }
            }
        }
        catch
        {
            // A report failure cannot replace the restore outcome or remove
            // successfully restored content.
        }
    }

    private sealed record ArchiveDescriptor(string Path, long ExpectedLength, string? ExpectedHash,
        IReadOnlyList<BackupPartFile>? Files = null);

    private sealed record ArchiveSelection(IReadOnlyList<ArchiveDescriptor> Archives, bool IsIncomplete);

    private sealed record ArchiveEntryDescriptor(
        string ArchivePath,
        string Identity,
        string EntryName,
        string TargetPath,
        long? ExpectedLength,
        string? ExpectedHash);

    private sealed class RestoreTargetException : IOException
    {
        public RestoreTargetException(string targetPath, Exception innerException)
            : base($"Restore target could not be replaced: '{targetPath}'.", innerException)
        {
        }
    }
}
