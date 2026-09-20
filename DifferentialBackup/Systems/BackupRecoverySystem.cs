using System.Collections.Concurrent;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Systems;

/// <summary>
/// Rebuilds durable backup state before any source discovery or hashing runs.
/// The system owns recovery decisions; BackupRunState only reads and writes
/// the mechanical records.
/// </summary>
public sealed class BackupRecoverySystem : IEntitySetSystem
{
    private readonly ConcurrentDictionary<string, string> _fileHashes;
    private readonly HashSet<DateTime> _backupDates;
    private readonly BackupRunState _runState;
    private readonly string? _hashesPath;
    private readonly string? _datesPath;

    public BackupRecoverySystem(
        ConcurrentDictionary<string, string> fileHashes,
        HashSet<DateTime> backupDates,
        BackupRunState runState,
        string? hashesPath = null,
        string? datesPath = null)
    {
        _fileHashes = fileHashes;
        _backupDates = backupDates;
        _runState = runState;
        _hashesPath = hashesPath;
        _datesPath = datesPath;
    }

    public Result Execute(IReadOnlyList<Entity> entities, IComponentStorage storage)
    {
        try
        {
            _runState.BeginRun();
            var operationEntityForRecovery = entities.FirstOrDefault(entity =>
                storage.GetComponent<OperationComponent>(entity) != null)
                ?? storage.Query<OperationComponent>().FirstOrDefault();
            var recoveredPublished = _runState.RecoverPublishedState(
                _fileHashes,
                _backupDates,
                applyState: operationEntityForRecovery == null,
                completePublication: operationEntityForRecovery == null);
            if (recoveredPublished && operationEntityForRecovery != null)
            {
                _runState.CompleteRecoveredPublication();
            }
            if (operationEntityForRecovery != null &&
                _runState.Job?.RunId is { } persistedRunId &&
                persistedRunId != Guid.Empty)
            {
                var operationForRecovery = storage.GetComponent<OperationComponent>(operationEntityForRecovery)!;
                operationForRecovery.RunId = persistedRunId;
                storage.SetComponent(operationEntityForRecovery, operationForRecovery);
            }
            if (!recoveredPublished && operationEntityForRecovery != null &&
                _runState.Job is { Published: false })
            {
                if (_runState.Job.StateCommitStep != StateCommitStep.NotRequired)
                {
                    return Result.Fail(
                        "Unfinished backup state contains a state-commit marker before publication.");
                }

                var operationForRecovery = storage.GetComponent<OperationComponent>(operationEntityForRecovery)!;
                var partRecovery = new BackupPartPlanningSystem(
                    operationForRecovery.SourceDirectory,
                    operationForRecovery.BackupDestination,
                    _runState,
                    new BackupBatchOptions());
                var partResult = partRecovery.RecoverPersistedPartsForInitialization(
                    operationForRecovery.RunId,
                    storage);
                if (!partResult.IsSuccess)
                {
                    return partResult;
                }
            }

            var operationEntity = entities.FirstOrDefault(entity =>
                storage.GetComponent<OperationComponent>(entity) != null)
                ?? storage.Query<OperationComponent>().FirstOrDefault();
            if (operationEntity == null)
            {
                return Result.Success();
            }

            var operation = storage.GetComponent<OperationComponent>(operationEntity)!;
            var schedule = storage.GetComponent<RetryScheduleComponent>(operationEntity)
                ?? new RetryScheduleComponent { RunId = operation.RunId };
            schedule.RunId = operation.RunId;
            var savedRetry = _runState.LoadRetrySchedule();
            if (savedRetry is { RoundInProgress: true } &&
                savedRetry.ActiveRound > savedRetry.CompletedRound &&
                operation.Phase is OperationPhase.Initializing or OperationPhase.InitialPass)
            {
                operation.Phase = OperationPhase.RetryPass;
                operation.ActivePass = savedRetry.ActiveRound;
                schedule.CompletedRound = savedRetry.CompletedRound;
                schedule.ActiveRound = savedRetry.ActiveRound;
                schedule.RoundInProgress = true;
                schedule.NextAttemptUtc = savedRetry.NextAttemptUtc;
            }
            else if (savedRetry is { RoundInProgress: false, NextAttemptUtc: not null } &&
                     savedRetry.ActiveRound > savedRetry.CompletedRound &&
                     operation.Phase is OperationPhase.Initializing or OperationPhase.InitialPass)
            {
                operation.Phase = OperationPhase.RetryWaiting;
                operation.ActivePass = savedRetry.ActiveRound;
                schedule.CompletedRound = savedRetry.CompletedRound;
                schedule.ActiveRound = savedRetry.ActiveRound;
                schedule.RoundInProgress = false;
                schedule.NextAttemptUtc = savedRetry.NextAttemptUtc;
                storage.SetComponent(operationEntity, new OperationWaitComponent
                {
                    RunId = operation.RunId,
                    Pass = savedRetry.ActiveRound,
                    WakeAtUtc = savedRetry.NextAttemptUtc.Value
                });
            }

            storage.SetComponent(operationEntity, schedule);
            storage.SetComponent(operationEntity, new PartAllocationComponent
            {
                RunId = operation.RunId,
                HighestReservedPartNumber = _runState.HighestReservedPartNumber
            });
            storage.SetComponent(operationEntity, new StateCommitComponent
            {
                RunId = operation.RunId,
                Step = recoveredPublished
                    ? (_runState.Job?.StateCommitStep ?? StateCommitStep.Pending)
                    : StateCommitStep.NotRequired,
                HashesPath = _hashesPath ?? string.Empty,
                DatesPath = _datesPath ?? string.Empty
            });
            var recoveredManifest = _runState.RecoveredManifest;
            if (recoveredPublished)
            {
                var receipt = _runState.LoadPublicationReceipt();
                if (receipt != null)
                {
                    storage.SetComponent(operationEntity, new PublicationReceiptComponent
                    {
                        RunId = operation.RunId,
                        BackupDate = receipt.BackupDate,
                        WorkingDirectory = receipt.WorkingDirectory,
                        FinalDirectory = receipt.FinalDirectory,
                        ManifestSha256 = receipt.ManifestSha256,
                        PartNumbers = receipt.Parts.Select(part => part.PartNumber).ToArray()
                    });
                }
                else if (recoveredManifest != null)
                {
                    // Older published layouts have no receipt. The explicit
                    // manifest/checkpoint compatibility reader has already
                    // validated those bytes, so materialize an equivalent
                    // receipt for the normal state-commit system.
                    storage.SetComponent(operationEntity, new PublicationReceiptComponent
                    {
                        RunId = operation.RunId,
                        BackupDate = recoveredManifest.BackupDate,
                        WorkingDirectory = Path.Combine(operation.BackupDestination,
                            recoveredManifest.BackupDate.ToString("yyyyMMddHHmmss") + ".backup.copying"),
                        FinalDirectory = Path.Combine(operation.BackupDestination,
                            recoveredManifest.BackupDate.ToString("yyyyMMddHHmmss") + ".backup"),
                        ManifestSha256 = string.Empty,
                        PartNumbers = recoveredManifest.Parts.Select(part => part.PartNumber).ToArray()
                    });
                }
                RebuildPublishedRows(operation, operationEntity, storage);
            }

            if (recoveredPublished)
            {
                if (recoveredManifest?.Issues != null)
                {
                    foreach (var manifestIssue in recoveredManifest.Issues)
                    {
                        var issueEntity = storage.Query<BackupIssueComponent>()
                            .FirstOrDefault(candidate =>
                            {
                                var issue = storage.GetComponent<BackupIssueComponent>(candidate);
                                return issue != null && issue.RunId == operation.RunId &&
                                    string.Equals(issue.StableKey, manifestIssue.Path, StringComparison.OrdinalIgnoreCase);
                            }) ?? new Entity();
                        storage.SetComponent(issueEntity, new BackupIssueComponent
                        {
                            RunId = operation.RunId,
                            StableKey = manifestIssue.Path,
                            Path = manifestIssue.Path,
                            Stage = manifestIssue.Stage,
                            Category = manifestIssue.Category,
                            ExceptionType = manifestIssue.ErrorType,
                            OriginalMessage = manifestIssue.ErrorMessage,
                            AttemptCount = manifestIssue.Attempts,
                            Resolved = false,
                            Retryable = true
                        });
                    }
                }
                // Recovery has reconstructed the published rows, but the
                // required application state commit and terminal outcome still
                // belong to their normal systems. This keeps a restart from
                // reporting success before hashes and dates are durable.
                operation.Phase = OperationPhase.PublishedPendingState;
            }
            storage.SetComponent(operationEntity, operation);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Fail($"Backup recovery failed: {ex.Message}", ex);
        }
    }

    public Result Execute(Entity entity, IComponentStorage storage) =>
        Result.Fail("Backup recovery requires entity-set execution.");

    private void RebuildPublishedRows(
        OperationComponent operation,
        Entity operationEntity,
        IComponentStorage storage)
    {
        var manifest = _runState.RecoveredManifest;
        if (manifest == null)
        {
            return;
        }

        var backupDate = manifest.BackupDate;
        var finalDirectory = Path.Combine(
            operation.BackupDestination,
            backupDate.ToString("yyyyMMddHHmmss") + ".backup");
        var runEntity = storage.Query<BackupRunComponent>()
            .FirstOrDefault(candidate =>
                storage.GetComponent<BackupRunComponent>(candidate)?.RunId == operation.RunId)
            ?? new Entity();
        storage.SetComponent(runEntity, new BackupRunComponent
        {
            RunId = operation.RunId,
            BackupDate = backupDate,
            SourceDirectory = operation.SourceDirectory,
            BackupDestination = operation.BackupDestination,
            StagingDirectory = _runState.StagingDirectory,
            WorkingDirectory = finalDirectory + ".copying",
            FinalDirectory = finalDirectory,
            PlanFingerprint = manifest.PlanFingerprint,
            TotalParts = manifest.Parts.Count,
            TotalFiles = manifest.FileCount,
            SourceBytes = manifest.SourceBytes
        });

        var highestPart = 0;
        foreach (var manifestPart in manifest.Parts)
        {
            highestPart = Math.Max(highestPart, manifestPart.PartNumber);
            var checkpoint = _runState.LoadPartCheckpoint(manifestPart.PartNumber);
            var files = checkpoint?.Files?.ToArray() ?? Array.Empty<BackupPartFile>();
            var partEntity = storage.Query<BackupPartComponent>()
                .FirstOrDefault(candidate =>
                {
                    var part = storage.GetComponent<BackupPartComponent>(candidate);
                    return part != null && part.RunId == operation.RunId &&
                        part.PartNumber == manifestPart.PartNumber;
                }) ?? new Entity();
            storage.SetComponent(partEntity, new BackupPartComponent
            {
                RunId = operation.RunId,
                PartNumber = manifestPart.PartNumber,
                BackupDate = backupDate,
                ArchiveFileName = manifestPart.ArchiveFileName,
                Fingerprint = manifestPart.Fingerprint,
                SourceBytes = manifestPart.SourceBytes,
                Files = files
            });
            storage.SetComponent(partEntity, new BackupPartStatusComponent
            {
                RunId = operation.RunId,
                State = BackupPartState.Transferred,
                LocalArchivePath = Path.Combine(_runState.StagingDirectory, manifestPart.ArchiveFileName),
                DestinationArchivePath = Path.Combine(finalDirectory, manifestPart.ArchiveFileName),
                ArchiveBytes = manifestPart.ArchiveBytes
            });
            storage.SetComponent(partEntity, new PartReceiptComponent
            {
                RunId = operation.RunId,
                PartNumber = manifestPart.PartNumber,
                Files = files,
                Fingerprint = manifestPart.Fingerprint,
                ArchiveLength = manifestPart.ArchiveBytes,
                ArchiveSha256 = manifestPart.ArchiveSha256,
                IndexLength = checkpoint == null || !File.Exists(_runState.GetPartCheckpointPath(manifestPart.PartNumber))
                    ? 0
                    : new FileInfo(_runState.GetPartCheckpointPath(manifestPart.PartNumber)).Length,
                IndexSha256 = checkpoint == null || !File.Exists(_runState.GetPartCheckpointPath(manifestPart.PartNumber))
                    ? string.Empty
                    : ComputeFileHash(_runState.GetPartCheckpointPath(manifestPart.PartNumber))
            });

            foreach (var descriptor in files)
            {
                var fileEntity = storage.Query<FileWorkComponent>()
                    .FirstOrDefault(candidate =>
                    {
                        var work = storage.GetComponent<FileWorkComponent>(candidate);
                        return work != null && work.RunId == operation.RunId &&
                            string.Equals(work.StableKey, descriptor.SourcePath, StringComparison.OrdinalIgnoreCase);
                    }) ?? new Entity();
                storage.SetComponent(fileEntity, new FilePathComponent { FilePath = descriptor.SourcePath });
                storage.SetComponent(fileEntity, new FileWorkComponent
                {
                    RunId = operation.RunId,
                    StableKey = descriptor.SourcePath,
                    SourcePath = descriptor.SourcePath,
                    RelativePath = descriptor.EntryName,
                    State = FileWorkState.Captured,
                    PartNumber = manifestPart.PartNumber
                });
                storage.SetComponent(fileEntity, new FileHashComponent
                {
                    PreviousHash = descriptor.Hash,
                    CurrentHash = descriptor.Hash,
                    CurrentLength = descriptor.Length
                });
                storage.SetComponent(fileEntity, new BackupDateComponent { BackupDate = backupDate });
            }
        }

        storage.SetComponent(operationEntity, new PartAllocationComponent
        {
            RunId = operation.RunId,
            HighestReservedPartNumber = Math.Max(_runState.HighestReservedPartNumber, highestPart)
        });
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
