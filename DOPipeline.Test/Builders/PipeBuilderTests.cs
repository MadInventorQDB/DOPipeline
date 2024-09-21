
using Xunit;
using DOPipeline.Builders;
using DOPipeline.Pipeline;
using DOPipeline.Systems;
using DOPipeline.Test.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Test.Components;

namespace DOPipeline.Test.Builders
{
    public class PipeBuilderTests
    {
        [Fact]
        public void CanBuildPipeWithName()
        {
            var builder = new PipeBuilder();
            var pipe = builder.Named("Test Pipe").Build();

            Assert.Equal("Test Pipe", pipe.Name);
        }

        [Fact]
        public void CanAddSystemsAndBuildPipe()
        {
            var builder = new PipeBuilder();
            var system = new ExampleSystem();
            var pipe = builder.AddSystem(system).Build();

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new ExampleComponent());
            var result = pipe.Execute(new[] { entity }, storage);

            Assert.True(result.IsSuccess);
            Assert.True(storage.HasComponent<ExampleComponent>(entity));
        }
    }
}
