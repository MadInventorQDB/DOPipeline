using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;

namespace DifferentialBackup.Systems
{
    public class BackupDecisionSystem : ISystem, IExecutionScopedSystem
    {
        private readonly ConcurrentDictionary<string, string> _fileHashes;
        private readonly HashSet<DateTime> _backupDates;
        private readonly BackupRunState? _backupRunState;
        private readonly Dictionary<Guid, OperationComponent> _operationsByRunId = new();
        private readonly Dictionary<Guid, Entity> _operationEntitiesByRunId = new();
        private OperationComponent? _singleOperation;
        private Entity? _singleOperationEntity;
        private bool _hasOperations;
        private bool _isExecutionScoped;

        public BackupDecisionSystem(
            ConcurrentDictionary<string, string> fileHashes,
            HashSet<DateTime> backupDates,
            BackupRunState? backupRunState = null)
        {
            _fileHashes = fileHashes;
            _backupDates = backupDates;
            _backupRunState = backupRunState;
        }

        public Result BeginExecution(IEnumerable<Entity> entities, IComponentStorage storage)
        {
            _operationsByRunId.Clear();
            _operationEntitiesByRunId.Clear();
            foreach (var opEntity in storage.Query<OperationComponent>())
            {
                var op = storage.GetComponent<OperationComponent>(opEntity);
                if (op != null)
                {
                    _operationsByRunId[op.RunId] = op;
                    _operationEntitiesByRunId[op.RunId] = opEntity;
                }
            }
            _singleOperation = _operationsByRunId.Count == 1 ? _operationsByRunId.Values.First() : null;
            _singleOperationEntity = _operationEntitiesByRunId.Count == 1 ? _operationEntitiesByRunId.Values.First() : null;
            _hasOperations = _operationsByRunId.Count > 0;
            _isExecutionScoped = true;

            try
            {
                if (_backupRunState != null)
                {
                    var saved = _backupRunState.LoadRetrySchedule();
                    if (saved != null)
                    {
                        var targetRunId = _backupRunState.RunId != Guid.Empty
                            ? _backupRunState.RunId
                            : (_singleOperation != null ? _singleOperation.RunId : Guid.Empty);

                        if (targetRunId != Guid.Empty && _operationEntitiesByRunId.TryGetValue(targetRunId, out var targetEntity))
                        {
                            var op = _operationsByRunId[targetRunId];
                            var schedule = storage.GetComponent<RetryScheduleComponent>(targetEntity);
                            if (op.Phase != OperationPhase.RetryPass && schedule != null)
                            {
                                schedule.CompletedRound = saved.CompletedRound;
                                schedule.ActiveRound = saved.ActiveRound;
                                schedule.RoundInProgress = saved.RoundInProgress;
                                schedule.NextAttemptUtc = saved.NextAttemptUtc;
                                storage.SetComponent(targetEntity, schedule);
                            }
                        }
                    }
                }

                return Result.Success();
            }
            catch (Exception ex)
            {
                return Result.Fail($"Failed to initialize backup run state: {ex.Message}");
            }
        }

        public Result EndExecution(IEnumerable<Entity> entities, IComponentStorage storage)
        {
            _operationsByRunId.Clear();
            _operationEntitiesByRunId.Clear();
            _singleOperation = null;
            _singleOperationEntity = null;
            _hasOperations = false;
            _isExecutionScoped = false;
            return Result.Success();
        }

        public Result Execute(Entity entity, IComponentStorage storage)
        {
            var fileWork = storage.GetComponent<FileWorkComponent>(entity);
            OperationComponent? operation;

            if (_isExecutionScoped)
            {
                if (fileWork != null)
                {
                    _operationsByRunId.TryGetValue(fileWork.RunId, out operation);
                }
                else
                {
                    operation = storage.GetComponent<OperationComponent>(entity) ?? _singleOperation;
                }
            }
            else
            {
                operation = GetOperation(entity, storage);
            }

            if (operation?.Phase is OperationPhase.Finalizing or
                OperationPhase.PublishedPendingState or OperationPhase.Finished)
            {
                return Result.Success();
            }

            var fileHashComponent = storage.GetComponent<FileHashComponent>(entity);
            var filePathComponent = storage.GetComponent<FilePathComponent>(entity);

            // The same world also contains run and part rows after planning;
            // those rows are not file decisions and have no hash/path pair.
            if (fileHashComponent == null && filePathComponent == null && fileWork == null)
            {
                if (storage.GetComponent<BackupPartComponent>(entity) != null ||
                    storage.GetComponent<BackupRunComponent>(entity) != null)
                {
                    return Result.Success();
                }

                return Result.Fail("Required components missing.");
            }

            if (fileWork != null && fileWork.State == FileWorkState.Deferred)
            {
                return Result.Success();
            }

            if (fileWork != null && fileWork.State is FileWorkState.Captured or FileWorkState.Omitted)
            {
                return Result.Success();
            }

            if (fileHashComponent == null || filePathComponent == null)
            {
                // Directory/root rows and deferred source rows are not eligible
                // for backup decisions. Missing data on a row that claims to be
                // a file remains an internal failure.
                if (filePathComponent != null && Directory.Exists(filePathComponent.FilePath))
                {
                    return Result.Success();
                }

                return fileWork != null && fileWork.State == FileWorkState.Discovered
                    ? Result.Success()
                    : Result.Fail("Required components missing.");
            }

            if (fileHashComponent.PreviousHash != fileHashComponent.CurrentHash)
            {
                // Mark the entity for backup by adding a BackupDateComponent
                var backupDate = GetBackupDateForCurrentExecution(entity, storage);
                storage.SetComponent(entity, new BackupDateComponent { BackupDate = backupDate });
                if (fileWork != null)
                {
                    fileWork.State = FileWorkState.ReadyToCapture;
                    storage.SetComponent(entity, fileWork);
                }

                // Update the hash in the fileHashes
                // Hashes are updated only after the complete backup set is published.
            }
            else if (fileWork != null)
            {
                fileWork.State = FileWorkState.Unchanged;
                storage.SetComponent(entity, fileWork);
            }

            return Result.Success();
        }

        private DateTime GetBackupDateForCurrentExecution(Entity sourceEntity, IComponentStorage storage)
        {
            Entity? operationEntity = null;
            var runId = storage.GetComponent<FileWorkComponent>(sourceEntity)?.RunId;

            if (_isExecutionScoped)
            {
                if (runId.HasValue && _operationEntitiesByRunId.TryGetValue(runId.Value, out var matchedEntity))
                {
                    operationEntity = matchedEntity;
                }
                else
                {
                    operationEntity = _singleOperationEntity;
                }
            }
            else
            {
                operationEntity = storage.Query<OperationComponent>()
                    .FirstOrDefault(candidate =>
                    {
                        var operation = storage.GetComponent<OperationComponent>(candidate);
                        return operation != null && (!runId.HasValue || operation.RunId == runId.Value);
                    });
            }

            if (operationEntity != null)
            {
                var operation = storage.GetComponent<OperationComponent>(operationEntity)!;
                lock (operation)
                {
                    operation.BackupDate ??= _backupRunState?.GetOrCreateBackupDate() ?? TruncateToSecond(DateTime.UtcNow);
                    storage.SetComponent(operationEntity, operation);
                    return operation.BackupDate.Value;
                }
            }

            return _backupRunState?.GetOrCreateBackupDate() ?? TruncateToSecond(DateTime.UtcNow);
        }

        private static OperationComponent? GetOperation(Entity entity, IComponentStorage storage)
        {
            var direct = storage.GetComponent<OperationComponent>(entity);
            if (direct != null)
            {
                return direct;
            }

            var work = storage.GetComponent<FileWorkComponent>(entity);
            return storage.Query<OperationComponent>()
                .Select(candidate => storage.GetComponent<OperationComponent>(candidate))
                .FirstOrDefault(operation => operation != null &&
                    (work == null || operation.RunId == work.RunId));
        }

        private static DateTime TruncateToSecond(DateTime value)
        {
            return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);
        }
    }
}
