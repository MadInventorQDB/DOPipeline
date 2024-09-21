using DOPipeline.Builders;
using DOPipeline.Pipeline;
using DifferentialBackup.Systems;
using System;

namespace DifferentialBackup.Pipeline
{
    public static class RestorePipelineBuilder
    {
        public static DOPipeline.Pipeline.Pipeline BuildRestorePipeline(
            string backupDestination,
            DateTime backupDate,
            string restoreDestination)
        {
            var pipelineBuilder = new PipelineBuilder();

            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Restore")
                .AddSystem(new RestoreSystem(backupDestination, backupDate, restoreDestination)));

            return pipelineBuilder.Build();
        }
    }
}
