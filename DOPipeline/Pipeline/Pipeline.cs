using System.Collections.Generic;
using System.Linq; // Added for ToList()
using System.Threading;
using DOPipeline.Entities;
using DOPipeline.Logging; // <-- Added using
using DOPipeline.Storage;
using DOPipeline.Utilities;

namespace DOPipeline.Pipeline
{
    /// <summary>
    /// Represents a sequence of Pipes to be executed.
    /// </summary>
    public class Pipeline
    {
        private readonly List<Pipe> _pipes = new();
        private readonly IPipelineLogger _logger; // <-- Added logger field

        /// <summary>
        /// Initializes a new instance of the Pipeline class with a required logger.
        /// </summary>
        /// <param name="logger">The logger instance to use for pipeline execution steps.</param>
        /// <exception cref="ArgumentNullException">Thrown if logger is null.</exception>
        public Pipeline(IPipelineLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Adds a Pipe to the end of the pipeline execution sequence.
        /// </summary>
        /// <param name="pipe">The Pipe to add.</param>
        /// <returns>The current Pipeline instance for fluent configuration.</returns>
        public Pipeline AddPipe(Pipe pipe)
        {
            _pipes.Add(pipe);
            return this;
        }

        /// <summary>
        /// Executes all Pipes in the pipeline sequentially on the provided entities.
        /// </summary>
        /// <param name="entities">The initial collection of entities to process.</param>
        /// <param name="storage">The component storage used by systems within the pipes.</param>
        /// <returns>A Result indicating overall success. Note that individual system failures within a pipe
        /// might result in ErrorComponents being added to entities, but the pipeline might still return Success overall
        /// unless a pipe's execution logic itself fails catastrophically (which is currently not the case in Pipe.Execute).</returns>
        public Result Execute(IEnumerable<Entity> entities, IComponentStorage storage)
        {
            return Execute(entities, storage, CancellationToken.None);
        }

        /// <summary>
        /// Executes all pipes while honoring the caller's cancellation token.
        /// </summary>
        public Result Execute(
            IEnumerable<Entity> entities,
            IComponentStorage storage,
            CancellationToken cancellationToken)
        {
            var currentEntities = entities.ToList(); // Make a copy to work with

            TryLog($"Pipeline starting execution with {_pipes.Count} pipe(s). Initial entity count: {currentEntities.Count}");

            int pipeIndex = 0;
            foreach (var pipe in _pipes)
            {
                pipeIndex++;
                TryLog($"Executing Pipe {pipeIndex}/{_pipes.Count}: '{pipe.Name}' on {currentEntities.Count} entities...");

                // Execute the current pipe on the current set of entities
                var pipeResult = pipe.Execute(currentEntities, storage, _logger, cancellationToken);

                if (!pipeResult.IsSuccess)
                {
                    // This typically indicates a fundamental issue within the Pipe.Execute logic itself,
                    // not just individual system failures (which add ErrorComponents).
                    TryLog($"[ERROR] Pipe '{pipe.Name}' execution failed fundamentally: {pipeResult.ErrorMessage}. Halting pipeline.");
                    // A cancellation token may be set while cleanup is
                    // running after a real failure. Preserve the initiating
                    // non-cancellation failure and its exit classification.
                    if (pipeResult.IsCancellation)
                    {
                        return Result.Cancelled(pipeResult.ErrorMessage);
                    }

                    return pipeResult;
                }
                else
                {
                    TryLog($"Pipe '{pipe.Name}' finished execution.");
                }

                // Refresh the list of entities from storage for the next pipe.
                // This is important if systems within the executed pipe added or removed entities.
                currentEntities = storage.GetAllEntities();
                TryLog($"Entity count after Pipe '{pipe.Name}': {currentEntities.Count}");
            }

            TryLog("Pipeline execution finished successfully.");
            return Result.Success();
        }

        private void TryLog(string message)
        {
            try
            {
                _logger.Log(message);
            }
            catch
            {
                // Logging is diagnostic infrastructure and cannot replace the
                // operation result or prevent scope cleanup.
            }
        }
    }
}
