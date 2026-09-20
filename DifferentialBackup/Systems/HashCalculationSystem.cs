using System.Collections.Concurrent;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using DifferentialBackup.Components;
using DifferentialBackup.Utilities;

namespace DifferentialBackup.Systems;

public sealed class HashCalculationSystem : ISystem, IExecutionScopedSystem
{
    private readonly ConcurrentDictionary<string, string> _fileHashes;
    private readonly Func<string, HashAttempt> _hashCalculator;
    private readonly ConcurrentDictionary<Guid, OperationComponent> _operationsByRunId = new();
    private OperationComponent? _singleOperation;
    private bool _hasOperations;
    private bool _isExecutionScoped;

    public HashCalculationSystem(
        ConcurrentDictionary<string, string> fileHashes,
        Func<string, HashAttempt>? hashCalculator = null)
    {
        _fileHashes = fileHashes;
        _hashCalculator = hashCalculator ?? HashUtility.TryComputeSHA256;
    }

    public Result BeginExecution(IEnumerable<Entity> entities, IComponentStorage storage)
    {
        _operationsByRunId.Clear();
        foreach (var opEntity in storage.Query<OperationComponent>())
        {
            var op = storage.GetComponent<OperationComponent>(opEntity);
            if (op != null)
            {
                _operationsByRunId[op.RunId] = op;
            }
        }
        _singleOperation = _operationsByRunId.Count == 1 ? _operationsByRunId.Values.First() : null;
        _hasOperations = _operationsByRunId.Count > 0;
        _isExecutionScoped = true;
        return Result.Success();
    }

    public Result EndExecution(IEnumerable<Entity> entities, IComponentStorage storage)
    {
        _operationsByRunId.Clear();
        _singleOperation = null;
        _hasOperations = false;
        _isExecutionScoped = false;
        return Result.Success();
    }

    public Result Execute(Entity entity, IComponentStorage storage)
    {
        var work = storage.GetComponent<FileWorkComponent>(entity);
        OperationComponent? operation;
        bool hasOperation;

        if (_isExecutionScoped)
        {
            hasOperation = _hasOperations;
            if (work != null)
            {
                _operationsByRunId.TryGetValue(work.RunId, out operation);
            }
            else
            {
                operation = storage.GetComponent<OperationComponent>(entity) ?? _singleOperation;
            }
        }
        else
        {
            operation = FindOperation(entity, storage);
            hasOperation = storage.Query<OperationComponent>().Count > 0;
        }

        if (work == null && hasOperation)
        {
            // Operation-driven worlds identify file work by its component.
            // The path-only compatibility API applies only without an operation.
            return Result.Success();
        }

        if (operation?.Phase is OperationPhase.Finalizing or
            OperationPhase.PublishedPendingState or OperationPhase.Finished)
        {
            return Result.Success();
        }

        if (work != null && operation == null && hasOperation)
        {
            return Result.Success();
        }

        if (work != null &&
            work.State is FileWorkState.Captured or FileWorkState.Unchanged or FileWorkState.Omitted)
        {
            return Result.Success();
        }

        var pass = operation?.ActivePass ?? work?.RequestedPass ?? 0;
        if (work != null)
        {
            // Eligibility is a component decision. Do it before touching the
            // source filesystem so completed rows remain completed even if the
            // original source has since disappeared or become locked.
            if (operation != null && work.RunId != operation.RunId)
            {
                return Result.Success();
            }

            // In an operation-driven run, only RetryActivationSystem may move a
            // deferred row into the active pass. Equality is intentional:
            // stale rows from an earlier pass must not be retried merely because
            // a later pipeline invocation happens to visit them.
            if (operation != null &&
                (work.State == FileWorkState.Deferred ||
                 work.RequestedPass != pass ||
                 work.LastAttemptedPass >= pass))
            {
                return Result.Success();
            }

            if (operation == null && work.LastAttemptedPass >= pass)
            {
                return Result.Success();
            }

            work.LastAttemptedPass = pass;
            storage.SetComponent(entity, work);
        }

        var filePathComponent = storage.GetComponent<FilePathComponent>(entity);
        if (filePathComponent == null)
        {
            // Part, run, issue, and other operational entities are present in
            // the same world after the first pass. Only an eligible file row is
            // an internal error when its required path component is absent.
            if (work == null &&
                (storage.GetComponent<BackupPartComponent>(entity) != null ||
                 storage.GetComponent<BackupRunComponent>(entity) != null))
            {
                return Result.Success();
            }
            return Result.Fail("FilePathComponent missing.");
        }

        var filePath = filePathComponent.FilePath;

        // Directory/root rows are intentionally present in the discovery world;
        // hashing them is not an error and must not poison later systems.
        if (work == null && !File.Exists(filePath))
        {
            return Result.Success();
        }

        var attempt = _hashCalculator(filePath);
        if (!attempt.Succeeded || string.IsNullOrWhiteSpace(attempt.Hash) || attempt.Length is null)
        {
            return Defer(entity, filePath, attempt.Exception ?? new IOException("Hash calculation failed."), operation, storage);
        }

        _fileHashes.TryGetValue(filePath, out var previousHash);
        storage.SetComponent(entity, new FileHashComponent
        {
            CurrentHash = attempt.Hash,
            PreviousHash = previousHash,
            CurrentLength = attempt.Length.Value
        });

        if (work != null)
        {
            work.State = FileWorkState.ReadyToCapture;
            storage.SetComponent(entity, work);
        }

        var issue = storage.GetComponent<BackupIssueComponent>(entity);
        if (issue != null)
        {
            issue.Resolved = true;
            storage.SetComponent(entity, issue);
        }

        storage.SetComponent(entity, new FileAttemptResultComponent
        {
            RunId = work?.RunId ?? operation?.RunId ?? Guid.Empty,
            Pass = pass,
            Stage = "Hash",
            Disposition = AttemptDisposition.Succeeded,
            Hash = attempt.Hash,
            Length = attempt.Length
        });
        return Result.Success();
    }

    private static Result Defer(
        Entity entity,
        string path,
        Exception exception,
        OperationComponent? operation,
        IComponentStorage storage)
    {
        var work = storage.GetComponent<FileWorkComponent>(entity);
        if (work == null)
        {
            // Preserve the original contract for callers that use this system
            // directly with a plain file row.
            return Result.Fail("Failed to compute hash.", exception);
        }

        var pass = operation?.ActivePass ?? work.RequestedPass;
        work.State = FileWorkState.Deferred;
        storage.SetComponent(entity, work);
        var now = DateTimeOffset.UtcNow;
        var issue = storage.GetComponent<BackupIssueComponent>(entity) ?? new BackupIssueComponent
        {
            RunId = work.RunId,
            StableKey = work.StableKey,
            Path = path,
            Stage = "Hash",
            Category = "SourceRead",
            FirstOccurrenceUtc = now,
            Retryable = true
        };
        issue.AttemptCount++;
        issue.LastOccurrenceUtc = now;
        issue.ExceptionType ??= exception.GetType().FullName;
        issue.OriginalMessage ??= exception.Message;
        issue.Resolved = false;
        storage.SetComponent(entity, issue);
        storage.SetComponent(entity, new FileAttemptResultComponent
        {
            RunId = work.RunId,
            Pass = pass,
            Stage = "Hash",
            Disposition = AttemptDisposition.Deferred,
            ErrorType = exception.GetType().FullName,
            ErrorMessage = exception.Message
        });
        return Result.Success();
    }

    private static OperationComponent? FindOperation(Entity entity, IComponentStorage storage)
    {
        var direct = storage.GetComponent<OperationComponent>(entity);
        if (direct != null)
        {
            return direct;
        }

        var work = storage.GetComponent<FileWorkComponent>(entity);
        if (work != null)
        {
            foreach (var operationEntity in storage.Query<OperationComponent>())
            {
                var operation = storage.GetComponent<OperationComponent>(operationEntity);
                if (operation?.RunId == work.RunId)
                {
                    return operation;
                }
            }

            // A file row with no matching operation belongs to a different
            // run. Do not attach it to an arbitrary operation in the world;
            // that would make eligibility and pass selection nondeterministic.
            return null;
        }

        foreach (var operationEntity in storage.Query<OperationComponent>())
        {
            return storage.GetComponent<OperationComponent>(operationEntity);
        }

        return null;
    }
}
