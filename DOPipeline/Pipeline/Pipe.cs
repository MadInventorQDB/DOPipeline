using System.Collections.Generic;
using System.Diagnostics;
using DOPipeline.Components;
using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;
using System.Threading;

namespace DOPipeline.Pipeline
{
    public class Pipe
    {
        private readonly List<ISystem> _systems = new();

        public string Name { get; }

        public Pipe(string name)
        {
            Name = name;
        }

        public Pipe AddSystem(ISystem system)
        {
            _systems.Add(system);
            return this;
        }

        public Result Execute(IEnumerable<Entity> entities, IComponentStorage storage, IPipelineLogger? logger = null)
        {
            return Execute(entities, storage, logger, CancellationToken.None);
        }

        public Result Execute(
            IEnumerable<Entity> entities,
            IComponentStorage storage,
            IPipelineLogger? logger,
            CancellationToken cancellationToken)
        {
            var entityList = entities as IReadOnlyList<Entity> ?? entities.ToList();

            foreach (var system in _systems)
            {
                // A fatal entity error is a stage boundary.  Entity-set
                // systems do not participate in the per-entity skip below,
                // so stop before dispatching them and preserve the first
                // initiating failure.
                var priorFatal = FirstFatalError(entityList, storage);
                if (priorFatal != null)
                {
                    return priorFatal.IsCancellation
                        ? Result.Cancelled(priorFatal.ErrorMessage)
                        : Result.Fail(
                            priorFatal.ErrorMessage ?? $"Pipe '{Name}' stopped after a fatal system error.",
                            priorFatal.Exception ?? new InvalidOperationException(priorFatal.ErrorMessage));
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return Result.Cancelled();
                }

                if (system is IEntitySetSystem entitySetSystem)
                {
                    Result setResult;
                    try
                    {
                        setResult = system is ICancellableEntitySetSystem cancellableSet
                            ? cancellableSet.Execute(entityList, storage, cancellationToken)
                            : entitySetSystem.Execute(entityList, storage);
                    }
                    catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && ex.CancellationToken == cancellationToken)
                    {
                        return Result.Cancelled();
                    }
                    catch (Exception ex)
                    {
                        return Result.Fail($"System '{system.GetType().Name}' failed: {ex.Message}", ex);
                    }

                    if (!setResult.IsSuccess)
                    {
                        return setResult;
                    }

                    continue;
                }

                IExecutionScopedSystem? executionScopedSystem = system as IExecutionScopedSystem;
                var beginCompleted = false;
                if (executionScopedSystem != null)
                {
                    Result beginResult;
                    try
                    {
                        beginResult = executionScopedSystem.BeginExecution(entityList, storage);
                    }
                    catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && ex.CancellationToken == cancellationToken)
                    {
                        return Result.Cancelled();
                    }
                    catch (Exception ex)
                    {
                        return Result.Fail($"System '{system.GetType().Name}' failed to begin: {ex.Message}", ex);
                    }

                    if (!beginResult.IsSuccess)
                    {
                        return beginResult;
                    }

                    beginCompleted = true;
                }

                var completed = 0;
                var total = entityList.Count;
                var stopwatch = Stopwatch.StartNew();
                var lastProgressTicks = 0L;

                Result? executionFailure = null;
                try
                {
                    Parallel.ForEach(
                        entityList,
                        new ParallelOptions { CancellationToken = cancellationToken },
                        entity =>
                        {
                            var existingError = storage.GetComponent<ErrorComponent>(entity);
                            if (existingError is not { IsFatal: true })
                            {
                                Result result;
                                try
                                {
                                    result = system is ICancellableSystem cancellable
                                        ? cancellable.Execute(entity, storage, cancellationToken)
                                        : system.Execute(entity, storage);
                                }
                                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && ex.CancellationToken == cancellationToken)
                                {
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    result = Result.Fail($"System '{system.GetType().Name}' failed: {ex.Message}", ex);
                                }

                                if (!result.IsSuccess &&
                                    storage.GetComponent<ErrorComponent>(entity) is not { IsFatal: true })
                                {
                                    RecordFailure(ref executionFailure, result);
                                    storage.SetComponent(entity, new ErrorComponent
                                    {
                                        ErrorMessage = result.ErrorMessage,
                                        Stage = system.GetType().Name,
                                        Exception = result.Exception,
                                        ExceptionType = result.Exception?.GetType().FullName,
                                        IsCancellation = result.IsCancellation,
                                        // Result.Fail(string) is still a failure.
                                        // An absent exception means only that the
                                        // system supplied a classified message;
                                        // it does not make the stage successful.
                                        IsFatal = !result.IsSuccess,
                                    });
                                }
                            }

                            var currentCompleted = Interlocked.Increment(ref completed);
                            if (currentCompleted < total && currentCompleted % 1000 == 0)
                            {
                                LogProgress(logger, system, currentCompleted, total, stopwatch, ref lastProgressTicks);
                            }
                        });
                }
                catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && ex.CancellationToken == cancellationToken)
                {
                    RecordFailure(ref executionFailure, Result.Cancelled());
                }
                catch (Exception ex)
                {
                    RecordFailure(
                        ref executionFailure,
                        Result.Fail($"System '{system.GetType().Name}' failed: {ex.Message}", ex));
                }

                Result? cleanupFailure = null;
                try
                {
                    // Diagnostics are best effort. A logger must never prevent
                    // an execution scope from reaching its cleanup boundary.
                    LogProgress(logger, system, completed, total, stopwatch, ref lastProgressTicks, force: true);
                }
                finally
                {
                    if (executionScopedSystem != null && beginCompleted)
                    {
                        Result endResult;
                        try
                        {
                            endResult = executionScopedSystem.EndExecution(entityList, storage);
                        }
                        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && ex.CancellationToken == cancellationToken)
                        {
                            endResult = Result.Cancelled();
                        }
                        catch (Exception ex)
                        {
                            endResult = Result.Fail($"System '{system.GetType().Name}' failed to end: {ex.Message}", ex);
                        }

                        if (!endResult.IsSuccess)
                        {
                            cleanupFailure = endResult;
                        }
                    }
                }

                // The initiating execution error always wins over a cleanup
                // error. Cleanup can still run after cancellation or a worker
                // fault because it is part of the execution scope's finally path.
                if (executionFailure != null)
                {
                    return executionFailure;
                }

                var fatal = FirstFatalError(entityList, storage);
                if (fatal != null)
                {
                    return fatal.IsCancellation
                        ? Result.Cancelled(fatal.ErrorMessage)
                        : Result.Fail(
                            fatal.ErrorMessage ?? $"Pipe '{Name}' stopped after a fatal system error.",
                            fatal.Exception ?? new InvalidOperationException(fatal.ErrorMessage));
                }

                if (cleanupFailure != null)
                {
                    return cleanupFailure;
                }
            }

            return Result.Success();
        }

        private static ErrorComponent? FirstFatalError(
            IReadOnlyList<Entity> entities,
            IComponentStorage storage)
        {
            foreach (var entity in entities)
            {
                var error = storage.GetComponent<ErrorComponent>(entity);
                if (error is { IsFatal: true })
                {
                    return error;
                }
            }

            return null;
        }

        private static void RecordFailure(ref Result? current, Result candidate)
        {
            while (true)
            {
                var observed = Volatile.Read(ref current);
                if (observed != null && !observed.IsCancellation)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref current, candidate, observed) == observed)
                {
                    return;
                }
            }
        }

        private void LogProgress(
            IPipelineLogger? logger,
            ISystem system,
            int completed,
            int total,
            Stopwatch stopwatch,
            ref long lastProgressTicks,
            bool force = false)
        {
            if (logger == null || total == 0)
            {
                return;
            }

            try
            {
                var nowTicks = Stopwatch.GetTimestamp();
                var elapsedSinceLast = Stopwatch.GetElapsedTime(lastProgressTicks, nowTicks);
                if (!force && completed < total && elapsedSinceLast < TimeSpan.FromSeconds(2))
                {
                    return;
                }

                lastProgressTicks = nowTicks;
                var percent = completed * 100.0 / total;
                var eta = CalculateEta(stopwatch.Elapsed, completed, total);
                ReportProgress(logger, $"Pipe '{Name}' / {system.GetType().Name}: {completed}/{total} ({percent:0.0}%) complete. ETA: {eta}");
            }
            catch
            {
                // Progress is diagnostic output, never operation state.
            }
        }

        private static void ReportProgress(IPipelineLogger logger, string message)
        {
            if (logger is IProgressLogger progressLogger)
            {
                progressLogger.ReportProgress(message);
            }
        }

        private static string CalculateEta(TimeSpan elapsed, int completed, int total)
        {
            if (completed <= 0 || completed >= total)
            {
                return "00:00:00";
            }

            var remainingTicks = elapsed.Ticks * (total - completed) / completed;
            return TimeSpan.FromTicks(remainingTicks).ToString(@"hh\:mm\:ss");
        }
    }
}
