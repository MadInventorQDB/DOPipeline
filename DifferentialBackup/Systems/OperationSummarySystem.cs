using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using System.Text.Json;

namespace DifferentialBackup.Systems;

public sealed class OperationSummarySystem : IEntitySetSystem
{
    public Result Execute(IReadOnlyList<Entity> entities, IComponentStorage storage)
    {
        var operationEntity = entities.FirstOrDefault(entity => storage.GetComponent<OperationComponent>(entity) != null)
            ?? storage.Query<OperationComponent>().FirstOrDefault();
        if (operationEntity == null)
        {
            return Result.Success();
        }

        var operation = storage.GetComponent<OperationComponent>(operationEntity)!;
        if (operation.Phase == OperationPhase.Finished)
        {
            var recoveredOutcome = storage.GetComponent<OperationOutcomeComponent>(operationEntity);
            if (recoveredOutcome != null && string.IsNullOrWhiteSpace(recoveredOutcome.ReportPath))
            {
                var recoveredIssues = storage.Query<BackupIssueComponent>()
                    .Select(storage.GetComponent<BackupIssueComponent>)
                    .Where(issue => issue != null && issue.RunId == operation.RunId)
                    .Cast<BackupIssueComponent>()
                    .ToList();
                TryWriteReport(operation, recoveredOutcome, recoveredIssues, out _);
                storage.SetComponent(operationEntity, recoveredOutcome);
            }

            return Result.Success();
        }
        var hasFatalSignal = storage.GetComponent<FatalFailureComponent>(operationEntity) != null;
        var hasCancellationSignal = storage.GetComponent<CancellationSignalComponent>(operationEntity) != null;
        if (operation.Phase is not (OperationPhase.PreparingPublication or OperationPhase.PublishedPendingState or OperationPhase.Finalizing) &&
            !hasFatalSignal && !hasCancellationSignal)
        {
            return Result.Success();
        }

        var stateCommit = storage.GetComponent<StateCommitComponent>(operationEntity);
        if (operation.Phase == OperationPhase.PublishedPendingState &&
            stateCommit?.Step != StateCommitStep.Complete &&
            storage.GetComponent<FatalFailureComponent>(operationEntity) == null &&
            storage.GetComponent<CancellationSignalComponent>(operationEntity) == null)
        {
            // Publication is durable, but the application state is not. A
            // terminal success must wait for BackupStateCommitSystem.
            return Result.Success();
        }

        if (operation.Phase == OperationPhase.Finalizing &&
            storage.GetComponent<FatalFailureComponent>(operationEntity) == null &&
            storage.GetComponent<CancellationSignalComponent>(operationEntity) == null &&
            stateCommit?.Step is not (StateCommitStep.Complete or StateCommitStep.NotRequired))
        {
            return Result.Fail("Operation finalization is waiting for required state commits.");
        }

        var captured = 0;
        var unchanged = 0;
        var deferred = 0;
        var omitted = 0;
        foreach (var entity in storage.Query<FileWorkComponent>())
        {
            var work = storage.GetComponent<FileWorkComponent>(entity)!;
            if (work.RunId != operation.RunId)
            {
                continue;
            }

            switch (work.State)
            {
                case FileWorkState.Captured: captured++; break;
                case FileWorkState.Unchanged: unchanged++; break;
                case FileWorkState.Deferred: deferred++; break;
                case FileWorkState.Omitted: omitted++; break;
            }
        }

        var issues = storage.Query<BackupIssueComponent>()
            .Select(storage.GetComponent<BackupIssueComponent>)
            .Where(issue => issue != null && issue.RunId == operation.RunId)
            .Cast<BackupIssueComponent>()
            .ToList();
        var unresolved = issues
            .Where(issue => !issue.Resolved)
            .GroupBy(issue => issue.StableKey, StringComparer.OrdinalIgnoreCase)
            .Count();
        var resolved = issues
            .Where(issue => issue.Resolved)
            .GroupBy(issue => issue.StableKey, StringComparer.OrdinalIgnoreCase)
            .Count();
        var hasPublishedContent = storage.Query<BackupRunComponent>()
            .Select(storage.GetComponent<BackupRunComponent>)
            .Any(run => run != null && run.RunId == operation.RunId &&
                        run.FinalDirectory.Length > 0 && Directory.Exists(run.FinalDirectory));

        var cancellation = storage.Query<CancellationSignalComponent>()
            .Select(storage.GetComponent<CancellationSignalComponent>)
            .FirstOrDefault(signal => signal != null && signal.RunId == operation.RunId);
        var fatal = storage.Query<FatalFailureComponent>()
            .Select(storage.GetComponent<FatalFailureComponent>)
            .FirstOrDefault(signal => signal != null && signal.RunId == operation.RunId);
        var outcome = fatal != null
            ? OperationOutcome.Failed
            : cancellation != null
                ? OperationOutcome.Cancelled
                : deferred > 0 || unresolved > 0
                    ? (captured > 0 || hasPublishedContent
                        ? OperationOutcome.IncompleteBackup
                        : unchanged > 0
                            ? OperationOutcome.Warnings
                            : OperationOutcome.Failed)
                    : OperationOutcome.Success;
        var exitCode = outcome switch
        {
            OperationOutcome.Success => 0,
            OperationOutcome.Cancelled => 130,
            OperationOutcome.Failed => 2,
            _ => 1
        };
        var outcomeComponent = new OperationOutcomeComponent
        {
            RunId = operation.RunId,
            Outcome = outcome,
            ExitCode = exitCode,
            EligibleCount = captured + unchanged + deferred + omitted,
            CapturedCount = captured,
            UnchangedCount = unchanged,
            DeferredCount = deferred,
            OmittedCount = omitted,
            ResolvedIssueCount = resolved,
            UnresolvedIssueCount = unresolved,
            ErrorMessage = fatal?.Message ?? cancellation?.Reason,
            CompletedUtc = DateTimeOffset.UtcNow
        };
        if (!TryWriteReport(operation, outcomeComponent, issues, out var reportError))
        {
            if (fatal == null && cancellation == null)
            {
                storage.SetComponent(operationEntity, new FatalFailureComponent
                {
                    RunId = operation.RunId,
                    Stage = nameof(OperationSummarySystem),
                    Message = $"Operation report could not be written: {reportError?.Message}",
                    ExceptionType = reportError?.GetType().FullName
                });
                return Result.Fail(
                    $"Operation report could not be written: {reportError?.Message}",
                    reportError ?? new IOException("Operation report could not be written."));
            }
        }
        storage.SetComponent(operationEntity, outcomeComponent);
        // A fatal/cancel signal is a separate outcome. Keep the last
        // recoverable phase instead of replacing it with Finished; a restart
        // must be able to resume the durable job.
        if (cancellation == null && fatal == null)
        {
            operation.Phase = OperationPhase.Finished;
            storage.SetComponent(operationEntity, operation);
        }
        return Result.Success();
    }

    public Result Execute(Entity entity, IComponentStorage storage) =>
        Result.Fail("Operation summary requires entity-set execution.");

    private static bool TryWriteReport(
        OperationComponent operation,
        OperationOutcomeComponent outcome,
        IReadOnlyList<BackupIssueComponent> issues,
        out Exception? error)
    {
        error = null;
        try
        {
            var root = operation.OperationType.Equals("restore", StringComparison.OrdinalIgnoreCase)
                ? operation.RestoreDestination
                : operation.BackupDestination;
            if (string.IsNullOrWhiteSpace(root))
            {
                return true;
            }

            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"operation-{operation.RunId:N}.report.json");
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new
                {
                    outcome.Outcome,
                    outcome.ExitCode,
                    outcome.EligibleCount,
                    outcome.CapturedCount,
                    outcome.UnchangedCount,
                    outcome.DeferredCount,
                    outcome.OmittedCount,
                    outcome.ResolvedIssueCount,
                    outcome.UnresolvedIssueCount,
                    outcome.ErrorMessage,
                    outcome.CompletedUtc,
                    Issues = issues.Select(issue => new
                    {
                        issue.Path,
                        issue.Stage,
                        issue.Category,
                        issue.ExceptionType,
                        issue.OriginalMessage,
                        issue.AttemptCount,
                        issue.Resolved
                    })
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
                    // Reporting cleanup cannot replace the operation result.
                }
            }
        }
        catch (Exception ex)
        {
            error = ex;
            // An existing initiating fatal/cancellation result remains the
            // authoritative outcome; the caller keeps recovery artifacts.
            return false;
        }

        return true;
    }
}
