using DOPipeline.Builders;
using DOPipeline.Pipeline;
using DifferentialBackup.Systems;
using System.Collections.Generic;

namespace DifferentialBackup.Pipeline
{
    public static class BackupPipelineBuilder
    {
        public static DOPipeline.Pipeline.Pipeline BuildBackupPipeline(
            string sourceDirectory,
            string backupDestination,
            Dictionary<string, string> fileHashes,
            HashSet<DateTime> backupDates)
        {
            var pipelineBuilder = new PipelineBuilder();

            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("File Discovery")
                .AddSystem(new FileDiscoverySystem(sourceDirectory)));

            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Hash Calculation")
                .AddSystem(new HashCalculationSystem(fileHashes)));

            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Backup Decision")
                .AddSystem(new BackupDecisionSystem(fileHashes, backupDates)));

            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Backup Execution")
                .AddSystem(new BackupExecutionSystem(sourceDirectory, backupDestination)));

            return pipelineBuilder.Build();
        }
    }
}
