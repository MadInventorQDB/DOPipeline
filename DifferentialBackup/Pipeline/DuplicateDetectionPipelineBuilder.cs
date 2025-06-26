using DOPipeline.Builders;
using DOPipeline.Logging;
using DOPipeline.Pipeline;
using DifferentialBackup.Systems;
using System.Collections.Concurrent;

namespace DifferentialBackup.Pipeline
{
    public static class DuplicateDetectionPipelineBuilder
    {
        public static DOPipeline.Pipeline.Pipeline BuildDuplicateDetectionPipeline(
            string sourceDirectory,
            IPipelineLogger logger)
        {
            var pipelineBuilder = new PipelineBuilder();
            pipelineBuilder.WithLogger(logger);

            pipelineBuilder.AddPipe(pb => pb
                .Named("File Discovery")
                .AddSystem(new FileDiscoverySystem(sourceDirectory)));

            var fileHashes = new ConcurrentDictionary<string, string>();
            pipelineBuilder.AddPipe(pb => pb
                .Named("Hash Calculation")
                .AddSystem(new HashCalculationSystem(fileHashes)));

            pipelineBuilder.AddPipe(pb => pb
                .Named("Duplicate Detection")
                .AddSystem(new DuplicateDetectionSystem())
                .AddSystem(new DuplicateLoggingSystem(logger)));

            return pipelineBuilder.Build();
        }
    }
}
