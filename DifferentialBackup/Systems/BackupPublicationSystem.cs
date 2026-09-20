using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DifferentialBackup.Systems
{
    public sealed class BackupPublicationSystem : IEntitySetSystem
    {
        private readonly ConcurrentDictionary<string, string> _fileHashes;
        private readonly HashSet<DateTime> _backupDates;
        private readonly BackupRunState _runState;
        private readonly IPipelineLogger? _logger;

        public BackupPublicationSystem(
            ConcurrentDictionary<string, string> fileHashes,
            HashSet<DateTime> backupDates,
            BackupRunState runState,
            IPipelineLogger? logger = null)
        {
            _fileHashes = fileHashes;
            _backupDates = backupDates;
            _runState = runState;
            _logger = logger;
        }

        public Result Execute(IReadOnlyList<Entity> entities, IComponentStorage storage)
        {
            var operationEntity = entities.FirstOrDefault(entity => storage.GetComponent<OperationComponent>(entity) != null)
                ?? storage.Query<OperationComponent>().FirstOrDefault();
            var operation = operationEntity == null
                ? null
                : storage.GetComponent<OperationComponent>(operationEntity);
            if (operation != null && operation.Phase != OperationPhase.PreparingPublication)
            {
                return Result.Success();
            }

            BackupRunComponent? run = null;
            var parts = new List<PartEntity>();
            var targetRunId = operation?.RunId ?? Guid.Empty;
            foreach (var entity in entities)
            {
                var candidateRun = storage.GetComponent<BackupRunComponent>(entity);
                if (candidateRun != null &&
                    (targetRunId == Guid.Empty || candidateRun.RunId == targetRunId))
                {
                    run ??= candidateRun;
                }
                var part = storage.GetComponent<BackupPartComponent>(entity);
                if (part == null)
                {
                    continue;
                }

                var status = storage.GetComponent<BackupPartStatusComponent>(entity);
                if (status != null)
                {
                parts.Add(new PartEntity(entity, part, status));
                }
            }

            if (run == null)
            {
                return Result.Success();
            }

            parts = parts
                .Where(item => (item.Part.RunId == Guid.Empty || item.Part.RunId == run.RunId) &&
                    (item.Status.RunId == Guid.Empty || item.Status.RunId == run.RunId))
                .ToList();

            parts.Sort((left, right) => left.Part.PartNumber.CompareTo(right.Part.PartNumber));

            var activeParts = parts
                .Where(item => item.Status.State != BackupPartState.Omitted && item.Part.Files.Count > 0)
                .ToList();
            var operationIssues = storage.Query<BackupIssueComponent>()
                .Select(storage.GetComponent<BackupIssueComponent>)
                .Where(issue => issue != null && !issue.Resolved &&
                    (operation == null || issue.RunId == operation.RunId))
                .Cast<BackupIssueComponent>()
                .GroupBy(issue => issue.StableKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(issue => issue.LastOccurrenceUtc).First())
                .ToList();
            var issueKeys = operationIssues
                .Select(issue => issue.StableKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in storage.Query<FileWorkComponent>())
            {
                var work = storage.GetComponent<FileWorkComponent>(entity);
                if (work == null || work.RunId != (operation?.RunId ?? run.RunId) ||
                    work.State != FileWorkState.Deferred || !issueKeys.Add(work.StableKey))
                {
                    continue;
                }

                operationIssues.Add(new BackupIssueComponent
                {
                    RunId = work.RunId,
                    StableKey = work.StableKey,
                    Path = work.SourcePath,
                    Stage = "Backup",
                    Category = "Deferred",
                    AttemptCount = Math.Max(1, work.LastAttemptedPass + 1),
                    Retryable = true
                });
            }
            foreach (var entity in storage.Query<DirectoryWorkComponent>())
            {
                var work = storage.GetComponent<DirectoryWorkComponent>(entity);
                if (work == null || work.RunId != (operation?.RunId ?? run.RunId) ||
                    work.State != DirectoryWorkState.Deferred || !issueKeys.Add(work.StableKey))
                {
                    continue;
                }

                operationIssues.Add(new BackupIssueComponent
                {
                    RunId = work.RunId,
                    StableKey = work.StableKey,
                    Path = work.DisplayPath,
                    Stage = "Discovery",
                    Category = "Deferred",
                    AttemptCount = Math.Max(1, work.LastAttemptedPass + 1),
                    Retryable = true
                });
            }

            try
            {
                if (activeParts.Count == 0)
                {
                    // An all-failed source must never become an empty published
                    // backup set. The summary system will report the unresolved
                    // issues as a failed operation.
                    return Result.Success();
                }

                if (activeParts.Any(item => item.Status.State != BackupPartState.Transferred))
                {
                    return Result.Fail("Backup cannot be published because one or more parts are incomplete.");
                }

                foreach (var item in activeParts)
                {
                    var receipt = storage.GetComponent<PartReceiptComponent>(item.Entity);
                    if (operationEntity != null && (receipt == null || receipt.RunId != run.RunId ||
                        receipt.PartNumber != item.Part.PartNumber || receipt.Fingerprint != item.Part.Fingerprint ||
                        receipt.Files.Count != item.Part.Files.Count || string.IsNullOrWhiteSpace(receipt.ArchiveSha256)))
                        return Result.Fail($"Acknowledged receipt is missing or inconsistent for part {item.Part.PartNumber}.");
                    if (receipt != null && receipt.IndexLength > 0)
                    {
                        var indexPath = Path.Combine(run.WorkingDirectory, $"part-{item.Part.PartNumber:000000}.index.json");
                        if (!File.Exists(indexPath) || new FileInfo(indexPath).Length != receipt.IndexLength ||
                            !string.Equals(ComputeFileHash(indexPath), receipt.IndexSha256, StringComparison.OrdinalIgnoreCase))
                            return Result.Fail($"Transferred index is missing or corrupt: '{indexPath}'.");
                    }
                    if (operationEntity == null && receipt == null && File.Exists(item.Status.DestinationArchivePath))
                    {
                        // Compatibility for direct publication callers that
                        // predate receipt components. Normal execution creates
                        // this receipt at capture/transfer time.
                        receipt = new PartReceiptComponent
                        {
                            RunId = item.Part.RunId,
                            PartNumber = item.Part.PartNumber,
                            Files = item.Part.Files,
                            Fingerprint = item.Part.Fingerprint,
                            ArchiveLength = new FileInfo(item.Status.DestinationArchivePath).Length,
                            ArchiveSha256 = ComputeFileHash(item.Status.DestinationArchivePath)
                        };
                        storage.SetComponent(item.Entity, receipt);
                    }
                    if (receipt != null && !string.IsNullOrWhiteSpace(receipt.ArchiveSha256) &&
                        (!File.Exists(item.Status.DestinationArchivePath) ||
                         !string.Equals(receipt.ArchiveSha256, ComputeFileHash(item.Status.DestinationArchivePath), StringComparison.OrdinalIgnoreCase)))
                    {
                        var actual = File.Exists(item.Status.DestinationArchivePath)
                            ? ComputeFileHash(item.Status.DestinationArchivePath)
                            : "<missing>";
                        return Result.Fail($"Backup part checksum validation failed: '{item.Part.ArchiveFileName}' expected {receipt.ArchiveSha256}, actual {actual}.");
                    }
                }

                var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in activeParts)
                {
                    foreach (var file in item.Part.Files)
                    {
                        if (!entryNames.Add(file.EntryName))
                        {
                            return Result.Fail($"Duplicate backup entry name: '{file.EntryName}'.");
                        }
                    }
                }

                if (!Directory.Exists(run.FinalDirectory))
                {
                    if (!Directory.Exists(run.WorkingDirectory))
                    {
                        return Result.Fail("Backup working directory is missing.");
                    }

                    foreach (var item in activeParts)
                    {
                        var archivePath = item.Status.DestinationArchivePath;
                        if (!File.Exists(archivePath) ||
                            new FileInfo(archivePath).Length != item.Status.ArchiveBytes)
                        {
                            return Result.Fail($"Backup part is missing or incomplete: '{archivePath}'.");
                        }
                    }

                    var manifest = new BackupManifest
                    {
                        SourceDirectory = run.SourceDirectory,
                        BackupDate = run.BackupDate,
                        PlanFingerprint = run.PlanFingerprint,
                        FileCount = activeParts.Sum(item => item.Part.Files.Count),
                        SourceBytes = activeParts.Sum(item => item.Part.SourceBytes),
                        IsComplete = operationIssues.Count == 0,
                        UnresolvedIssueCount = operationIssues.Count,
                        Issues = operationIssues.Select(issue => new BackupManifestIssue
                        {
                            Path = issue.Path,
                            Stage = issue.Stage,
                            Category = issue.Category,
                            ErrorType = issue.ExceptionType,
                            ErrorMessage = issue.OriginalMessage,
                            Attempts = issue.AttemptCount
                        }).ToList(),
                        Parts = activeParts.Select(item => new BackupManifestPart
                        {
                            PartNumber = item.Part.PartNumber,
                            ArchiveFileName = item.Part.ArchiveFileName,
                            Fingerprint = item.Part.Fingerprint,
                            FileCount = item.Part.Files.Count,
                            SourceBytes = item.Part.SourceBytes,
                            ArchiveBytes = item.Status.ArchiveBytes,
                            ArchiveSha256 = storage.GetComponent<PartReceiptComponent>(item.Entity)?.ArchiveSha256 ?? string.Empty,
                            IndexFileName = storage.GetComponent<PartReceiptComponent>(item.Entity)?.IndexLength > 0
                                ? $"part-{item.Part.PartNumber:000000}.index.json" : string.Empty,
                            IndexBytes = storage.GetComponent<PartReceiptComponent>(item.Entity)?.IndexLength ?? 0,
                            IndexSha256 = storage.GetComponent<PartReceiptComponent>(item.Entity)?.IndexSha256 ?? string.Empty
                        }).ToList()
                    };

                    WriteManifest(manifest, run);
                    _runState.SavePublicationReceipt(new PublicationReceiptState
                    {
                        RunId = run.RunId,
                        BackupDate = run.BackupDate,
                        WorkingDirectory = run.WorkingDirectory,
                        FinalDirectory = run.FinalDirectory,
                        ManifestSha256 = ComputeFileHash(_runState.FinalManifestStagingPath),
                        Parts = manifest.Parts
                    });
                    if (operationEntity != null)
                    {
                        storage.SetComponent(operationEntity, new PublicationReceiptComponent
                        {
                            RunId = run.RunId,
                            BackupDate = run.BackupDate,
                            WorkingDirectory = run.WorkingDirectory,
                            FinalDirectory = run.FinalDirectory,
                            ManifestSha256 = ComputeFileHash(_runState.FinalManifestStagingPath),
                            PartNumbers = manifest.Parts.Select(part => part.PartNumber).ToArray()
                        });
                    }
                    Directory.Move(run.WorkingDirectory, run.FinalDirectory);
                    _runState.Checkpoint("publication-directory-renamed");
                }

                // The working directory is no longer a valid artifact path
                // after publication. Persist final paths before the state
                // commit system validates receipts.
                foreach (var item in activeParts)
                {
                    item.Status.DestinationArchivePath = Path.Combine(
                        run.FinalDirectory,
                        item.Part.ArchiveFileName);
                    storage.SetComponent(item.Entity, item.Status);
                }

                _runState.MarkPublished();

                // Legacy direct callers invoke publication without an
                // OperationComponent and therefore have no state-commit pipe.
                // Preserve that public API while operation-driven runs defer
                // these updates to BackupStateCommitSystem.
                if (operationEntity == null)
                {
                    foreach (var item in activeParts)
                    {
                        foreach (var file in item.Part.Files)
                        {
                            _fileHashes[file.SourcePath] = file.Hash;
                        }
                    }
                    _backupDates.Add(run.BackupDate);
                }

                if (operationEntity != null && operation != null)
                {
                    operation.Phase = OperationPhase.PublishedPendingState;
                    storage.SetComponent(operationEntity, operation);
                }

                _logger?.Log($"Backup Publication: finalized '{run.FinalDirectory}'.");
                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to publish backup: {ex.Message}", ex);
            }
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            return Result.Fail("Backup publication requires entity-set execution.");
        }

        private void WriteManifest(BackupManifest manifest, BackupRunComponent run)
        {
            var localTemporaryPath = _runState.FinalManifestStagingPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(localTemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(JsonSerializer.Serialize(manifest));
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(localTemporaryPath, _runState.FinalManifestStagingPath, true);
            }
            finally
            {
                try
                {
                    File.Delete(localTemporaryPath);
                }
                catch
                {
                    // Preserve the publication error if cleanup is denied.
                }
            }

            var destinationPath = Path.Combine(run.WorkingDirectory, "manifest.json");
            var destinationTemporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".copying";
            try
            {
                File.Copy(_runState.FinalManifestStagingPath, destinationTemporaryPath);

                using (var stream = new FileStream(
                    destinationTemporaryPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None))
                {
                    stream.Flush(flushToDisk: true);
                }

                if (new FileInfo(destinationTemporaryPath).Length !=
                    new FileInfo(_runState.FinalManifestStagingPath).Length)
                {
                    throw new IOException("Transferred backup manifest length does not match.");
                }

                File.Move(destinationTemporaryPath, destinationPath, true);
            }
            finally
            {
                try
                {
                    File.Delete(destinationTemporaryPath);
                }
                catch
                {
                    // Preserve the publication error if cleanup is denied.
                }
            }
        }

        private sealed record PartEntity(
            Entity Entity,
            BackupPartComponent Part,
            BackupPartStatusComponent Status);

        private static string ComputeFileHash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
    }
}
