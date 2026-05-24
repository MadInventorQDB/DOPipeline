using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using DOPipeline.Components;
using DOPipeline.Entities;
using DOPipeline.Logging;
using DOPipeline.Storage;
using DOPipeline.Systems;
using DOPipeline.Utilities;

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
            var entityList = entities as IList<Entity> ?? entities.ToList();

            foreach (var system in _systems)
            {
                if (system is IExecutionScopedSystem executionScopedSystem)
                {
                    var beginResult = executionScopedSystem.BeginExecution(entityList, storage);
                    if (!beginResult.IsSuccess)
                    {
                        return beginResult;
                    }
                }

                var completed = 0;
                var total = entityList.Count;
                var stopwatch = Stopwatch.StartNew();
                var lastProgressTicks = 0L;

                Parallel.ForEach(entityList, entity =>
                {
                    var result = system.Execute(entity, storage);
                    if (!result.IsSuccess)
                    {
                        storage.SetComponent(entity, new ErrorComponent
                        {
                            ErrorMessage = result.ErrorMessage
                        });
                    }

                    var currentCompleted = Interlocked.Increment(ref completed);
                    if (currentCompleted < total && currentCompleted % 1000 == 0)
                    {
                        LogProgress(logger, system, currentCompleted, total, stopwatch, ref lastProgressTicks);
                    }
                });

                LogProgress(logger, system, completed, total, stopwatch, ref lastProgressTicks, force: true);

                if (system is IExecutionScopedSystem scopedSystem)
                {
                    var endResult = scopedSystem.EndExecution(entityList, storage);
                    if (!endResult.IsSuccess)
                    {
                        return endResult;
                    }
                }
            }

            return Result.Success();
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
