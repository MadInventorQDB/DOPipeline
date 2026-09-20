using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Systems;

/// <summary>Activates each deferred row once for the current shared retry round.</summary>
public sealed class RetryActivationSystem : IEntitySetSystem
{
    private readonly BackupRunState? _runState;
    private readonly IClock _clock;

    public RetryActivationSystem(BackupRunState? runState = null, IClock? clock = null)
    {
        _runState = runState;
        _clock = clock ?? new SystemClock();
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
        if (schedule == null &&
            operation.Phase is OperationPhase.RetryWaiting or OperationPhase.RetryPass)
        {
            return Result.Fail("Retry schedule is missing for a retry pass.");
        }
        if (operation.Phase == OperationPhase.RetryWaiting)
        {
            if (schedule?.NextAttemptUtc is not { } due || due > _clock.UtcNow)
            {
                return Result.Success();
            }

            operation.Phase = OperationPhase.RetryPass;
            operation.ActivePass = schedule.ActiveRound;
            storage.SetComponent(operationEntity, operation);
        }

        if (operation.Phase != OperationPhase.RetryPass)
        {
            return Result.Success();
        }

        if (schedule == null)
        {
            return Result.Fail("Retry schedule is missing for a retry pass.");
        }

        schedule.RoundInProgress = true;
        schedule.NextAttemptUtc = null;

        foreach (var entity in storage.Query<FileWorkComponent>())
        {
            var work = storage.GetComponent<FileWorkComponent>(entity)!;
            if (work.RunId != operation.RunId ||
                work.State != FileWorkState.Deferred ||
                work.LastAttemptedPass >= operation.ActivePass)
            {
                continue;
            }

            var issue = storage.GetComponent<BackupIssueComponent>(entity);
            if (issue is { Retryable: false, Resolved: false })
            {
                work.State = FileWorkState.Omitted;
                storage.SetComponent(entity, work);
                continue;
            }

            work.RequestedPass = operation.ActivePass;
            work.State = FileWorkState.Discovered;
            storage.SetComponent(entity, work);
        }

        foreach (var entity in storage.Query<DirectoryWorkComponent>())
        {
            var work = storage.GetComponent<DirectoryWorkComponent>(entity)!;
            if (work.RunId != operation.RunId ||
                work.State != DirectoryWorkState.Deferred ||
                work.LastAttemptedPass >= operation.ActivePass)
            {
                continue;
            }

            var issue = storage.GetComponent<BackupIssueComponent>(entity);
            if (issue is { Retryable: false, Resolved: false })
            {
                work.State = DirectoryWorkState.Omitted;
                storage.SetComponent(entity, work);
                continue;
            }

            work.RequestedPass = operation.ActivePass;
            storage.SetComponent(entity, work);
        }

        storage.SetComponent(operationEntity, schedule);
        _runState?.SaveRetrySchedule(schedule);
        return Result.Success();
    }

    public Result Execute(Entity entity, IComponentStorage storage) =>
        Result.Fail("Retry activation requires entity-set execution.");
}
