using DOPipeline.Builders;
using DOPipeline.Logging;
using DOPipeline.Pipeline;
using DifferentialBackup.Systems;
using DifferentialBackup.Utilities;
using System;

namespace DifferentialBackup.Pipeline
{
    /// <summary>
    /// Static class responsible for building the restore operation pipeline.
    /// </summary>
    public static class RestorePipelineBuilder
    {
        /// <summary>
        /// Builds the pipeline for restoring files from a specific backup version.
        /// </summary>
        /// <param name="backupDestination">The root directory where backup versions are stored.</param>
        /// <param name="backupDate">The specific timestamp of the backup version to restore.</param>
        /// <param name="restoreDestination">The directory where files should be restored.</param>
        /// <param name="logger">The logger instance for recording pipeline events.</param>
        /// <param name="checkpointObserver">The checkpoint observer for tracking durability and recovery points.</param>
        /// <param name="clock">Optional clock abstraction for scheduling.</param>
        /// <returns>A configured Pipeline instance for the restore operation.</returns>
        public static DOPipeline.Pipeline.Pipeline BuildRestorePipeline(
            string backupDestination,
            DateTime backupDate,
            string restoreDestination,
            IPipelineLogger logger,
            ICheckpointObserver? checkpointObserver = null,
            IClock? clock = null)
        {
            var pipelineBuilder = new PipelineBuilder();

            pipelineBuilder.WithLogger(logger);

            // Restore pipe: Activation, copy execution, pass completion
            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Restore")
                .AddSystem(new RestoreRetryActivationSystem(backupDestination, backupDate, restoreDestination, clock, checkpointObserver))
                .AddSystem(new RestoreSystem(backupDestination, backupDate, restoreDestination, checkpointObserver))
                .AddSystem(new RestorePassCompletionSystem(restoreDestination, clock, checkpointObserver)));

            return pipelineBuilder.Build();
        }
    }
}