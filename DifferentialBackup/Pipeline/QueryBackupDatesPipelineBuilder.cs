using DOPipeline.Builders;
using DOPipeline.Pipeline;
using DifferentialBackup.Systems;
using System.Collections.Generic;

namespace DifferentialBackup.Pipeline
{
    public static class QueryBackupDatesPipelineBuilder
    {
        public static DOPipeline.Pipeline.Pipeline BuildQueryBackupDatesPipeline(HashSet<DateTime> backupDates)
        {
            var pipelineBuilder = new PipelineBuilder();

            pipelineBuilder.AddPipe(pipeBuilder => pipeBuilder
                .Named("Query Backup Dates")
                .AddSystem(new QueryBackupDatesSystem(backupDates)));

            return pipelineBuilder.Build();
        }
    }
}
