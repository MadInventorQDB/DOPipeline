using DOPipeline.Pipeline;

namespace DOPipeline.Builders
{
    public class PipelineBuilder
    {
        private readonly List<Pipe> _pipes = new();

        public PipelineBuilder AddPipe(Func<PipeBuilder, PipeBuilder> configure)
        {
            var pipeBuilder = new PipeBuilder();
            _pipes.Add(configure(pipeBuilder).Build());
            return this;
        }

        public Pipeline.Pipeline Build()
        {
            var pipeline = new Pipeline.Pipeline();
            foreach (var pipe in _pipes)
            {
                pipeline.AddPipe(pipe);
            }
            return pipeline;
        }
    }
}