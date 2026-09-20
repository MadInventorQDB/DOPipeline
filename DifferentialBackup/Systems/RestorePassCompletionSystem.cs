using System.Text.Json;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Systems;

/// <summary>
/// Evaluates remaining deferred restore entries and decides whether to schedule a durable retry round
/// or finalize the restore operation.
/// </summary>
public sealed class RestorePassCompletionSystem : IEntitySetSystem
{
    private readonly string? _restoreDestination;
    private readonly IClock _clock;
    private readonly ICheckpointObserver _checkpointObserver;

    public RestorePassCompletionSystem(
        string? restoreDestination = null,
        IClock? clock = null,
        ICheckpointObserver? checkpointObserver = null)
    {
        _restoreDestination = restoreDestination;
        _clock = clock ?? new SystemClock();
        _checkpointObserver = checkpointObserver ?? NoOpCheckpointObserver.Instance;
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
        if (operation.Phase is OperationPhase.RetryWaiting or OperationPhase.Finished)
        {
            return Result.Success();
        }

        var schedule = storage.GetComponent<RetryScheduleComponent>(operationEntity) ?? new RetryScheduleComponent
        {
            RunId = operation.RunId
        };

        var destination = !string.IsNullOrWhiteSpace(_restoreDestination) ? _restoreDestination : operation.RestoreDestination;

        if (operation.Phase == OperationPhase.RetryPass && schedule.RoundInProgress)
        {
            schedule.CompletedRound = Math.Max(schedule.CompletedRound, schedule.ActiveRound);
            schedule.RoundInProgress = false;
        }

        var retryable = false;
        var unresolved = 0;
        foreach (var entity in storage.Query<RestoreEntryComponent>())
        {
            var entry = storage.GetComponent<RestoreEntryComponent>(entity)!;
            if (entry.RunId == operation.RunId && entry.State == RestoreEntryState.Deferred)
            {
                unresolved++;
                var issue = storage.GetComponent<BackupIssueComponent>(entity);
                retryable |= issue?.Retryable != false;
            }
        }

        foreach (var entity in storage.Query<BackupIssueComponent>())
        {
            var issue = storage.GetComponent<BackupIssueComponent>(entity)!;
            if (issue.RunId == operation.RunId && !issue.Resolved)
            {
                retryable |= issue.Retryable;
            }
        }

        if (unresolved > 0 && retryable && schedule.CompletedRound < schedule.DelayMinutes.Count)
        {
            var nextRound = schedule.CompletedRound + 1;
            schedule.ActiveRound = nextRound;
            schedule.RoundInProgress = false;
            schedule.NextAttemptUtc = schedule.NextAttemptUtc is { } savedDue && savedDue > DateTimeOffset.MinValue
                ? savedDue
                : _clock.UtcNow.AddMinutes(schedule.DelayMinutes[nextRound - 1]);

            operation.Phase = OperationPhase.RetryWaiting;
            storage.SetComponent(operationEntity, operation);
            storage.SetComponent(operationEntity, schedule);
            storage.SetComponent(operationEntity, new OperationWaitComponent
            {
                RunId = operation.RunId,
                Pass = nextRound,
                WakeAtUtc = schedule.NextAttemptUtc.Value
            });

            if (!string.IsNullOrWhiteSpace(destination))
            {
                var journal = RestoreJournal.Load(destination);
                journal.RetrySchedule = new RestoreJournalRetrySchedule
                {
                    CompletedRound = schedule.CompletedRound,
                    ActiveRound = schedule.ActiveRound,
                    RoundInProgress = false,
                    NextAttemptUtc = schedule.NextAttemptUtc
                };
                RestoreJournal.Save(destination, journal);
            }

            _checkpointObserver.Reached("restore-retry-waiting");
            return Result.Success();
        }

        // All entries resolved or retry rounds exhausted
        operation.Phase = OperationPhase.Finished;
        storage.SetComponent(operationEntity, operation);
        storage.SetComponent(operationEntity, schedule);

        if (!string.IsNullOrWhiteSpace(destination))
        {
            var journal = RestoreJournal.Load(destination);
            journal.RetrySchedule = null;
            RestoreJournal.Save(destination, journal);

            var isIncomplete = storage.Query<IncompleteBackupComponent>()
                .Any(c => storage.GetComponent<IncompleteBackupComponent>(c)?.RunId == operation.RunId);
            var isSuccess = unresolved == 0 && !isIncomplete;
            var outcome = new OperationOutcomeComponent
            {
                RunId = operation.RunId,
                Outcome = isSuccess ? OperationOutcome.Success : OperationOutcome.Warnings,
                ExitCode = isSuccess ? 0 : 1,
                DeferredCount = unresolved,
                ErrorMessage = isIncomplete ? "Restored from an incomplete backup." : null,
                CompletedUtc = DateTimeOffset.UtcNow
            };
            TryWriteReport(destination, outcome, journal.ArchiveIdentity, storage);
            storage.SetComponent(operationEntity, outcome);
        }

        _checkpointObserver.Reached("restore-completed");
        return Result.Success();
    }

    public Result Execute(Entity entity, IComponentStorage storage) =>
        Result.Fail("Restore pass completion requires entity-set execution.");

    private static void TryWriteReport(string destination, OperationOutcomeComponent outcome, string archiveIdentity, IComponentStorage storage)
    {
        try
        {
            var safeId = string.IsNullOrEmpty(archiveIdentity) ? "default" : archiveIdentity[..Math.Min(16, archiveIdentity.Length)];
            var path = Path.Combine(destination, $"restore-{safeId}.report.json");
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
                try { File.Delete(temporary); } catch { }
            }
        }
        catch { }
    }
}
