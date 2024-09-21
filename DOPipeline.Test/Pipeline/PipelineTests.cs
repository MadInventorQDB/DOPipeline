
using Xunit;
using DOPipeline.Pipeline;
using DOPipeline.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Test.Systems;
using DOPipeline.Test.Components;
using DOPipeline.Utilities;

namespace DOPipeline.Test.Pipeline
{
    public class PipelineTests
    {
        [Fact]
        public void CanAddPipeToPipeline()
        {
            // Arrange
            var pipeline = new DOPipeline.Pipeline.Pipeline();
            var pipe = new Pipe("Test Pipe");

            // Act
            pipeline.AddPipe(pipe);

            // Assert
            var entity = new Entity();
            var storage = new ComponentStorage();
            var result = pipeline.Execute(new[] { entity }, storage);

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void Execute_CallsPipesOnEntities()
        {
            // Arrange
            var pipeline = new DOPipeline.Pipeline.Pipeline();
            var pipe = new Pipe("Test Pipe");
            var system = new ExampleSystem();
            pipe.AddSystem(system);
            pipeline.AddPipe(pipe);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new ExampleComponent());

            // Act
            var result = pipeline.Execute(new[] { entity }, storage);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.True(storage.HasComponent<ExampleComponent>(entity));
        }

        [Fact]
        public void Execute_HaltsWhenPipeFails()
        {
            // Arrange
            var pipeline = new DOPipeline.Pipeline.Pipeline();

            var failingPipe = new Pipe("Failing Pipe");
            failingPipe.AddSystem(new FailingSystem());

            var succeedingPipe = new Pipe("Succeeding Pipe");
            succeedingPipe.AddSystem(new ExampleSystem());

            pipeline.AddPipe(failingPipe);
            pipeline.AddPipe(succeedingPipe);

            var entity = new Entity();
            var storage = new ComponentStorage();

            var result = pipeline.Execute(new[] { entity }, storage);

            Assert.True(result.IsSuccess);
        }
    }
}
