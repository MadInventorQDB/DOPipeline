using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Systems;

/// <summary>
/// Activates deferred restore entries for the current retry pass and manages durable retry transitions.
/// </summary>
public sealed class RestoreRetryActivationSystem : IEntitySetSystem
{
    private readonly string? _backupDestination;
    private readonly DateTime? _backupDate;
    private readonly string? _restoreDestination;
    private readonly IClock _clock;
    private readonly ICheckpointObserver _checkpointObserver;

    public RestoreRetryActivationSystem(
        string? backupDestination = null,
        DateTime? backupDate = null,
        string? restoreDestination = null,
        IClock? clock = null,
        ICheckpointObserver? checkpointObserver = null)
    {
        _backupDestination = backupDestination;
        _backupDate = backupDate;
        _restoreDestination = restoreDestination;
        _clock = clock ?? new SystemClock();
        _checkpointObserver = checkpointObserver ?? NoOpCheckpointObserver.Instance;
    }

    public RestoreRetryActivationSystem(
        string? restoreDestination,
        IClock? clock,
        ICheckpointObserver? checkpointObserver)
        : this(null, null, restoreDestination, clock, checkpointObserver)
    {
    }

    public Result Execute(IReadOnlyList<Entity> entities, IComponentStorage storage)
    {
        var operationEntity = entities.FirstOrDefault(entity => storage.GetComponent<OperationComponent>(entity) != null)
            ?? storage.Query<OperationComponent>().FirstOrDefault();
        if (operationEntity == null)
        {
            return Result.Success();
        }

        var operation = storage.GetComponent<OperationComponent>(operationEntity)!;
        var schedule = storage.GetComponent<RetryScheduleComponent>(operationEntity);
        if (schedule == null && operation.Phase is OperationPhase.RetryWaiting or OperationPhase.RetryPass)
        {
            return Result.Fail("Retry schedule is missing for a restore retry pass.");
        }

        var destination = !string.IsNullOrWhiteSpace(_restoreDestination) ? _restoreDestination : operation.RestoreDestination;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return Result.Success();
        }

        var backupDest = !string.IsNullOrWhiteSpace(_backupDestination) ? _backupDestination : operation.BackupDestination;
        var backupDate = _backupDate ?? operation.BackupDate;
        var expectedArchiveIdentity = string.Empty;
        if (!string.IsNullOrWhiteSpace(backupDest) && backupDate.HasValue)
        {
            try
            {
                expectedArchiveIdentity = RestoreSystem.ResolveArchiveIdentity(backupDest, backupDate.Value);
            }
            catch
            {
                // Unresolvable backup archive at this stage must not inherit foreign schedules.
            }
        }

        // Recover saved retry schedule from journal if newly launched after a crash
        if (operation.Phase == OperationPhase.InitialPass && schedule != null &&
            schedule.CompletedRound == 0 && schedule.ActiveRound == 0)
        {
            var journal = RestoreJournal.Load(destination, expectedArchiveIdentity);
            if (journal.RetrySchedule != null &&
                !string.IsNullOrEmpty(expectedArchiveIdentity) &&
                string.Equals(journal.ArchiveIdentity, expectedArchiveIdentity, StringComparison.Ordinal))
            {
                schedule.CompletedRound = journal.RetrySchedule.CompletedRound;
                schedule.ActiveRound = journal.RetrySchedule.ActiveRound;
                schedule.RoundInProgress = journal.RetrySchedule.RoundInProgress;
                schedule.NextAttemptUtc = journal.RetrySchedule.NextAttemptUtc;
                storage.SetComponent(operationEntity, schedule);

                if (journal.RetrySchedule.RoundInProgress)
                {
                    operation.Phase = OperationPhase.RetryPass;
                    operation.ActivePass = schedule.ActiveRound;
                    storage.SetComponent(operationEntity, operation);
                }
                else if (journal.RetrySchedule.NextAttemptUtc.HasValue)
                {
                    operation.Phase = OperationPhase.RetryWaiting;
                    operation.ActivePass = schedule.ActiveRound;
                    storage.SetComponent(operationEntity, operation);
                    storage.SetComponent(operationEntity, new OperationWaitComponent
                    {
                        RunId = operation.RunId,
                        Pass = schedule.ActiveRound,
                        WakeAtUtc = journal.RetrySchedule.NextAttemptUtc.Value
                    });
                }
            }
        }

        if (operation.Phase == OperationPhase.RetryWaiting)
        {
            if (schedule?.NextAttemptUtc is not { } due || due > _clock.UtcNow)
            {
                return Result.Success();
            }

            operation.Phase = OperationPhase.RetryPass;
            operation.ActivePass = schedule.ActiveRound;
            schedule.RoundInProgress = true;
            schedule.NextAttemptUtc = null;
            storage.SetComponent(operationEntity, operation);
            storage.SetComponent(operationEntity, schedule);

            var journal = RestoreJournal.Load(destination, expectedArchiveIdentity);
            if (string.IsNullOrEmpty(journal.ArchiveIdentity) && !string.IsNullOrEmpty(expectedArchiveIdentity))
            {
                journal.ArchiveIdentity = expectedArchiveIdentity;
            }
            journal.RetrySchedule = new RestoreJournalRetrySchedule
            {
                CompletedRound = schedule.CompletedRound,
                ActiveRound = schedule.ActiveRound,
                RoundInProgress = true,
                NextAttemptUtc = null
            };
            RestoreJournal.Save(destination, journal);
            _checkpointObserver.Reached("restore-retry-round-active");
        }

        if (operation.Phase != OperationPhase.RetryPass)
        {
            return Result.Success();
        }

        if (schedule == null)
        {
            return Result.Fail("Retry schedule is missing for a restore retry pass.");
        }

        schedule.RoundInProgress = true;
        schedule.NextAttemptUtc = null;

        foreach (var entity in storage.Query<RestoreEntryComponent>())
        {
            var entry = storage.GetComponent<RestoreEntryComponent>(entity)!;
            if (entry.RunId != operation.RunId ||
                entry.State != RestoreEntryState.Deferred ||
                entry.LastAttemptedPass >= operation.ActivePass)
            {
                continue;
            }

            var issue = storage.GetComponent<BackupIssueComponent>(entity);
            if (issue is { Retryable: false, Resolved: false })
            {
                entry.State = RestoreEntryState.Omitted;
                storage.SetComponent(entity, entry);
                continue;
            }

            entry.RequestedPass = operation.ActivePass;
            entry.State = RestoreEntryState.Pending;
            storage.SetComponent(entity, entry);
        }

        storage.SetComponent(operationEntity, schedule);
        return Result.Success();
    }

    public Result Execute(Entity entity, IComponentStorage storage) =>
        Result.Fail("Restore retry activation requires entity-set execution.");
}
