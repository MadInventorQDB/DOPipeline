using DOPipeline.Pipeline;
using DOPipeline.Logging; // <-- Added using
using System; // Added for Func<> and InvalidOperationException

namespace DOPipeline.Builders
{
    /// <summary>
    /// Provides a fluent interface for building a Pipeline.
    /// </summary>
    public class PipelineBuilder
    {
        private readonly List<Pipe> _pipes = new();
        private IPipelineLogger? _logger; // Logger instance for the pipeline

        /// <summary>
        /// Specifies the logger instance to be used by the pipeline being built.
        /// </summary>
        /// <param name="logger">The logger implementation.</param>
        /// <returns>The current PipelineBuilder instance for fluent configuration.</returns>
        public PipelineBuilder WithLogger(IPipelineLogger logger)
        {
            _logger = logger;
            return this;
        }

        /// <summary>
        /// Adds a new Pipe to the pipeline configuration.
        /// </summary>
        /// <param name="configure">An action that configures the new Pipe using a PipeBuilder.</param>
        /// <returns>The current PipelineBuilder instance for fluent configuration.</returns>
        public PipelineBuilder AddPipe(Func<PipeBuilder, PipeBuilder> configure)
        {
            var pipeBuilder = new PipeBuilder();
            _pipes.Add(configure(pipeBuilder).Build());
            return this;
        }

        /// <summary>
        /// Builds the Pipeline instance based on the configured pipes and logger.
        /// </summary>
        /// <returns>A new Pipeline instance.</returns>
        /// <exception cref="InvalidOperationException">Thrown if no logger has been provided using WithLogger().</exception>
        public Pipeline.Pipeline Build()
        {
            // Ensure a logger is provided.
            if (_logger == null)
            {
                throw new InvalidOperationException($"A logger must be provided using {nameof(WithLogger)}() before building the pipeline.");
            }

            // Create the pipeline, passing the required logger
            var pipeline = new Pipeline.Pipeline(_logger);
            foreach (var pipe in _pipes)
            {
                pipeline.AddPipe(pipe);
            }
            return pipeline;
        }
    }
}