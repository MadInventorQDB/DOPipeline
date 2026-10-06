using System.Collections.Concurrent;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Systems;

/// <summary>Commits published backup state before a terminal success is produced.</summary>
public sealed class BackupStateCommitSystem : IEntitySetSystem
{
    private readonly ConcurrentDictionary<string, string> _fileHashes;
    private readonly HashSet<DateTime> _backupDates;
    private readonly BackupRunState _runState;
    private readonly string? _hashesPath;
    private readonly string? _datesPath;

    public BackupStateCommitSystem(
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
        var operationEntity = entities.FirstOrDefault(entity =>
            storage.GetComponent<OperationComponent>(entity) != null)
            ?? storage.Query<OperationComponent>().FirstOrDefault();
        if (operationEntity == null)
        {
            return Result.Success();
        }

        var operation = storage.GetComponent<OperationComponent>(operationEntity)!;
        if (operation.Phase == OperationPhase.PreparingPublication &&
            storage.GetComponent<PublicationReceiptComponent>(operationEntity) == null &&
            _runState.Job?.Published != true &&
            !storage.Query<BackupPartStatusComponent>().Any(entity =>
                storage.GetComponent<BackupPartStatusComponent>(entity) is { } status &&
                status.RunId == operation.RunId && status.State != BackupPartState.Omitted))
        {
            storage.SetComponent(operationEntity, new StateCommitComponent
            {
                RunId = operation.RunId, Step = StateCommitStep.NotRequired,
                HashesPath = _hashesPath ?? string.Empty, DatesPath = _datesPath ?? string.Empty
            });
            operation.Phase = OperationPhase.Finalizing;
            storage.SetComponent(operationEntity, operation);
            return Result.Success();
        }
        if (operation.Phase != OperationPhase.PublishedPendingState)
        {
            return Result.Success();
        }

        try
        {
            var publication = storage.GetComponent<PublicationReceiptComponent>(operationEntity);
            if (publication == null || publication.PartNumbers.Count == 0)
            {
                throw new InvalidDataException("Published backup receipt is missing.");
            }

            if (!_runState.RecoverPublishedState(_fileHashes, _backupDates, applyState: false, completePublication: false))
                throw new InvalidDataException("Published artifacts have not been validated.");

            var mergedHashes = BuildPublishedHashState(operation, publication, storage);
            var commit = storage.GetComponent<StateCommitComponent>(operationEntity)
                ?? new StateCommitComponent
                {
                    RunId = operation.RunId,
                    HashesPath = _hashesPath ?? string.Empty,
                    DatesPath = _datesPath ?? string.Empty,
                    Step = StateCommitStep.Pending
                };
            if (commit.Step == StateCommitStep.Complete)
            {
                _runState.DestinationState?.EnsureCommitted(_runState);
                _fileHashes.Clear();
                foreach (var pair in mergedHashes)
                {
                    _fileHashes[pair.Key] = pair.Value;
                }
                _backupDates.Add(publication.BackupDate);
                operation.Phase = OperationPhase.Finalizing;
                storage.SetComponent(operationEntity, operation);
                return Result.Success();
            }

            if (commit.Step == StateCommitStep.NotRequired)
            {
                commit.Step = StateCommitStep.Pending;
            }
            storage.SetComponent(operationEntity, commit);

            if (_hashesPath == null || _datesPath == null)
            {
                throw new InvalidOperationException("Both hash-state and backup-date paths are required for state commit.");
            }

            if (_hashesPath != null && _datesPath != null)
            {
                if (commit.Step < StateCommitStep.HashesSaved)
                {
                    if (_runState.DestinationState != null) _runState.DestinationState.Commit(_runState);
                    else DataPersistence.SaveFileHashes(mergedHashes, _hashesPath);
                    _runState.Checkpoint("hash-state-replaced");
                    _runState.SaveStateCommitStep(StateCommitStep.HashesSaved);
                    commit.Step = StateCommitStep.HashesSaved;
                    storage.SetComponent(operationEntity, commit);
                }

                // Older journals can mark legacy state saved without recording
                // evidence in this destination. Ensure it only on resumed steps.
                else
                {
                    _runState.DestinationState?.EnsureCommitted(_runState);
                }

                if (commit.Step < StateCommitStep.DatesSaved)
                {
                    var mergedDates = new HashSet<DateTime>(_backupDates)
                    {
                        publication.BackupDate
                    };
                    if (_runState.DestinationState == null) DataPersistence.SaveBackupDates(mergedDates, _datesPath);
                    _runState.Checkpoint("date-state-replaced");
                    _runState.SaveStateCommitStep(StateCommitStep.DatesSaved);
                    commit.Step = StateCommitStep.DatesSaved;
                    storage.SetComponent(operationEntity, commit);
                }
            }

            _runState.Checkpoint("state-files-saved-before-complete");
            _runState.SaveStateCommitStep(StateCommitStep.Complete);
            commit.Step = StateCommitStep.Complete;
            storage.SetComponent(operationEntity, commit);
            _fileHashes.Clear();
            foreach (var pair in mergedHashes)
            {
                _fileHashes[pair.Key] = pair.Value;
            }
            _backupDates.Add(publication.BackupDate);
            operation.Phase = OperationPhase.Finalizing;
            storage.SetComponent(operationEntity, operation);
            return Result.Success();
        }
        catch (Exception ex)
        {
            // Leave PublishedPendingState and all recovery artifacts intact.
            if (storage.GetComponent<FatalFailureComponent>(operationEntity) == null)
            {
                storage.SetComponent(operationEntity, new FatalFailureComponent
                {
                    RunId = operation.RunId,
                    Stage = nameof(BackupStateCommitSystem),
                    Message = $"Backup state commit failed: {ex.Message}",
                    ExceptionType = ex.GetType().FullName
                });
            }
            return Result.Fail($"Backup state commit failed: {ex.Message}", ex);
        }
    }

    private ConcurrentDictionary<string, string> BuildPublishedHashState(
        OperationComponent operation,
        PublicationReceiptComponent publication,
        IComponentStorage storage)
    {
        var merged = new ConcurrentDictionary<string, string>(_fileHashes,
            StringComparer.Ordinal);
        var seenEntries = new HashSet<string>(StringComparer.Ordinal);
        var seenSources = new HashSet<string>(StringComparer.Ordinal);

        if (publication.PartNumbers.Count == 0 ||
            publication.PartNumbers.Count != publication.PartNumbers.Distinct().Count())
        {
            throw new InvalidDataException("Published receipt contains duplicate or missing part identities.");
        }

        var partEntities = new Dictionary<int, Entity>();
        foreach (var entity in storage.Query<BackupPartComponent, BackupPartStatusComponent>())
        {
            var part = storage.GetComponent<BackupPartComponent>(entity);
            var status = storage.GetComponent<BackupPartStatusComponent>(entity);
            if (part != null && status != null && part.RunId == operation.RunId && status.RunId == operation.RunId)
                partEntities.TryAdd(part.PartNumber, entity);
        }
        var manifestParts = (_runState.RecoveredManifest
            ?? throw new InvalidDataException("Validated published manifest is missing."))
            .Parts.ToDictionary(part => part.PartNumber);

        foreach (var partNumber in publication.PartNumbers)
        {
            partEntities.TryGetValue(partNumber, out var partEntity);
            if (partEntity == null)
            {
                throw new InvalidDataException($"Published part {partNumber} is missing.");
            }

            var publishedPart = manifestParts.GetValueOrDefault(partNumber)
                ?? throw new InvalidDataException($"Published manifest omits part {partNumber}.");
            var status = storage.GetComponent<BackupPartStatusComponent>(partEntity)!;
            var receipt = storage.GetComponent<PartReceiptComponent>(partEntity);
            if (status.State != BackupPartState.Transferred || receipt == null ||
                receipt.RunId != operation.RunId ||
                !File.Exists(status.DestinationArchivePath) ||
                !string.Equals(status.DestinationArchivePath, Path.Combine(publication.FinalDirectory, publishedPart.ArchiveFileName), StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(receipt.ArchiveSha256) &&
                 !string.Equals(publishedPart.ArchiveSha256, receipt.ArchiveSha256, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"Published part {partNumber} failed receipt validation.");
            }

            // RecoverPublishedState just validated these payloads. Compare
            // receipts with that manifest, without another full archive scan.
            var publishedIndex = BackupIndexReader.Read(publication.FinalDirectory, publishedPart);
            foreach (var descriptor in publishedIndex?.Files ?? receipt.Files)
            {
                if (string.IsNullOrWhiteSpace(descriptor.SourcePath) ||
                    string.IsNullOrWhiteSpace(descriptor.EntryName) ||
                    string.IsNullOrWhiteSpace(descriptor.Hash) ||
                    descriptor.Length < 0 ||
                    !seenEntries.Add(descriptor.EntryName) ||
                    !seenSources.Add(descriptor.SourcePath))
                {
                    throw new InvalidDataException($"Published part {partNumber} contains invalid file descriptors.");
                }

                merged[_runState.ResolveSourcePath(descriptor.SourcePath)] = descriptor.Hash;
            }
        }

        return merged;
    }

    public Result Execute(Entity entity, IComponentStorage storage) =>
        Result.Fail("Backup state commit requires entity-set execution.");
}
