using DOPipeline.Builders;
using DOPipeline.Logging; // <-- Added using
using DOPipeline.Pipeline;
using DifferentialBackup.Systems;
using System; // Added for DateTime, HashSet
using System.Collections.Generic;
using System.Collections.Concurrent; // Added for Dictionary

namespace DifferentialBackup.Pipeline
{
    /// <summary>
    /// Static class responsible for building the backup operation pipeline.
    /// </summary>
    public static class BackupPipelineBuilder
    {
        /// <summary>
        /// Builds the standard pipeline for performing a differential backup.
        /// </summary>
        /// <param name="sourceDirectory">The directory containing files to back up.</param>
        /// <param name="backupDestination">The root directory where backup versions will be stored.</param>
        /// <param name="fileHashes">A dictionary tracking known file paths and their last known hashes.</param>
        /// <param name="backupDates">A set storing the timestamps of successful backup runs.</param>
        /// <param name="logger">The logger instance for recording pipeline events.</param>
        /// <returns>A configured Pipeline instance for the backup operation.</returns>
        public static DOPipeline.Pipeline.Pipeline BuildBackupPipeline(
            string sourceDirectory,
            string backupDestination,
            ConcurrentDictionary<string, string> fileHashes,
            HashSet<DateTime> backupDates,
            IPipelineLogger logger) // <-- Added logger parameter
        {
            var pipelineBuilder = new PipelineBuilder();

            pipelineBuilder.WithLogger(logger); // <-- Set the logger

            // Pipe 1: Discover all files in the source directory
            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("File Discovery")
                .AddSystem(new FileDiscoverySystem(sourceDirectory)));

            // Pipe 2: Calculate the current hash for each discovered file
            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Hash Calculation")
                .AddSystem(new HashCalculationSystem(fileHashes)));

            // Pipe 3: Decide which files need backing up based on hash changes
            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Backup Decision")
                .AddSystem(new BackupDecisionSystem(fileHashes, backupDates)));

            // Pipe 4: Execute the backup (copy) for files marked for backup
            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Backup Execution")
                .AddSystem(new BackupExecutionSystem(sourceDirectory, backupDestination)));

            return pipelineBuilder.Build();
        }
    }
}