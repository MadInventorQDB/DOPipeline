
using Xunit;
using DOPipeline.Builders;
using DOPipeline.Pipeline;
using DOPipeline.Test.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Test.Components;

namespace DOPipeline.Test.Builders
{
    public class PipelineBuilderTests
    {
        [Fact]
        public void CanAddPipesAndBuildPipeline()
        {
            var builder = new PipelineBuilder();
            var pipeline = builder.AddPipe(pipeBuilder => pipeBuilder
                                            .Named("Test Pipe")
                                            .AddSystem(new ExampleSystem()))
                                  .Build();

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new ExampleComponent());

            var result = pipeline.Execute(new[] { entity }, storage);

            Assert.True(result.IsSuccess);
            Assert.True(storage.HasComponent<ExampleComponent>(entity));
        }
    }
}
