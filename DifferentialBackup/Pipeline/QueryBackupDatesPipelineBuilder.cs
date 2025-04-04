using DOPipeline.Builders;
using DOPipeline.Logging; // <-- Added using
using DOPipeline.Pipeline;
using DifferentialBackup.Systems;
using System; // Added for DateTime, HashSet
using System.Collections.Generic; // Added for HashSet

namespace DifferentialBackup.Pipeline
{
    /// <summary>
    /// Static class responsible for building the pipeline to query available backup dates.
    /// </summary>
    public static class QueryBackupDatesPipelineBuilder
    {
        /// <summary>
        /// Builds a simple pipeline to retrieve and sort the known backup dates.
        /// </summary>
        /// <param name="backupDates">The set of known backup dates.</param>
        /// <param name="logger">The logger instance for recording pipeline events.</param>
        /// <returns>A configured Pipeline instance for the query operation.</returns>
        public static DOPipeline.Pipeline.Pipeline BuildQueryBackupDatesPipeline(
            HashSet<DateTime> backupDates,
            IPipelineLogger logger) // <-- Added logger parameter
        {
            var pipelineBuilder = new PipelineBuilder();

            pipelineBuilder.WithLogger(logger); // <-- Set the logger

            // Pipe 1: Query and sort the backup dates, attaching them to the initial entity
            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Query Backup Dates")
                .AddSystem(new QueryBackupDatesSystem(backupDates)));

            return pipelineBuilder.Build();
        }
    }
}