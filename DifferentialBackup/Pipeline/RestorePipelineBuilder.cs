using DOPipeline.Builders;
using DOPipeline.Logging; // <-- Added using
using DOPipeline.Pipeline;
using DifferentialBackup.Systems;
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
        /// <returns>A configured Pipeline instance for the restore operation.</returns>
        public static DOPipeline.Pipeline.Pipeline BuildRestorePipeline(
            string backupDestination,
            DateTime backupDate,
            string restoreDestination,
            IPipelineLogger logger) // <-- Added logger parameter
        {
            var pipelineBuilder = new PipelineBuilder();

            pipelineBuilder.WithLogger(logger); // <-- Set the logger

            // Pipe 1: Perform the restore operation by copying files
            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Restore")
                .AddSystem(new RestoreSystem(backupDestination, backupDate, restoreDestination)));

            return pipelineBuilder.Build();
        }
    }
}