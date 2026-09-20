using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Systems;

/// <summary>
/// Converts the current pass into either a durable wait instruction or the
/// publication phase. The host only honors the resulting wait component.
/// </summary>
public sealed class PassCompletionSystem : IEntitySetSystem
{
    private readonly IClock _clock;
    private readonly BackupRunState? _runState;

    public PassCompletionSystem(IClock? clock = null, BackupRunState? runState = null)
    {
        _clock = clock ?? new SystemClock();
        _runState = runState;
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
        if (operation.Phase is OperationPhase.PreparingPublication or OperationPhase.PublishedPendingState or OperationPhase.Finalizing or OperationPhase.Finished)
        {
            return Result.Success();
        }

        var schedule = storage.GetComponent<RetryScheduleComponent>(operationEntity) ?? new RetryScheduleComponent
        {
            RunId = operation.RunId
        };

        if (operation.Phase == OperationPhase.RetryPass && schedule.RoundInProgress)
        {
            schedule.CompletedRound = Math.Max(schedule.CompletedRound, schedule.ActiveRound);
            schedule.RoundInProgress = false;
        }

        var retryable = false;
        var unresolved = 0;
        foreach (var entity in storage.Query<BackupIssueComponent>())
        {
            var issue = storage.GetComponent<BackupIssueComponent>(entity)!;
            if (issue.RunId != operation.RunId || issue.Resolved)
            {
                continue;
            }

            unresolved++;
            retryable |= issue.Retryable;
        }

        foreach (var entity in storage.Query<FileWorkComponent>())
        {
            var work = storage.GetComponent<FileWorkComponent>(entity)!;
            if (work.RunId == operation.RunId && work.State == FileWorkState.Deferred)
            {
                unresolved++;
                var issue = storage.GetComponent<BackupIssueComponent>(entity);
                retryable |= issue?.Retryable != false;
            }
        }

        foreach (var entity in storage.Query<DirectoryWorkComponent>())
        {
            var work = storage.GetComponent<DirectoryWorkComponent>(entity)!;
            if (work.RunId == operation.RunId && work.State == DirectoryWorkState.Deferred)
            {
                unresolved++;
                var issue = storage.GetComponent<BackupIssueComponent>(entity);
                retryable |= issue?.Retryable != false;
            }
        }

        var pending = storage.Query<DirectoryWorkComponent>()
            .Select(storage.GetComponent<DirectoryWorkComponent>)
            .Any(work => work != null && work.RunId == operation.RunId && work.State == DirectoryWorkState.Pending);

        if (pending)
        {
            operation.Phase = OperationPhase.InitialPass;
            storage.SetComponent(operationEntity, operation);
            storage.SetComponent(operationEntity, schedule);
            _runState?.SaveRetrySchedule(schedule);
            return Result.Success();
        }

        if (!retryable || schedule.CompletedRound >= schedule.DelayMinutes.Count)
        {
            operation.Phase = OperationPhase.PreparingPublication;
            storage.SetComponent(operationEntity, operation);
            storage.SetComponent(operationEntity, schedule);
            _runState?.SaveRetrySchedule(schedule);
            return Result.Success();
        }

        var nextRound = schedule.CompletedRound + 1;
        schedule.ActiveRound = nextRound;
        schedule.RoundInProgress = false;
        schedule.NextAttemptUtc = schedule.NextAttemptUtc is { } savedDue &&
            savedDue > DateTimeOffset.MinValue
            ? savedDue
            : _clock.UtcNow.AddMinutes(schedule.DelayMinutes[nextRound - 1]);
        operation.Phase = OperationPhase.RetryWaiting;
        storage.SetComponent(operationEntity, operation);
        storage.SetComponent(operationEntity, schedule);
        _runState?.SaveRetrySchedule(schedule);
        storage.SetComponent(operationEntity, new OperationWaitComponent
        {
            RunId = operation.RunId,
            Pass = nextRound,
            WakeAtUtc = schedule.NextAttemptUtc.Value
        });
        _runState?.Checkpoint("backup-retry-waiting");
        return Result.Success();
    }

    public Result Execute(Entity entity, IComponentStorage storage) =>
        Result.Fail("Pass completion requires entity-set execution.");
}
