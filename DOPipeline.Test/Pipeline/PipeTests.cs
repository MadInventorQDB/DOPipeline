
using Xunit;
using DOPipeline.Pipeline;
using DOPipeline.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Test.Systems;
using DOPipeline.Test.Components;
using System.Collections.Generic;
using DOPipeline.Components;
using DOPipeline.Utilities;

namespace DOPipeline.Test.Pipeline
{
    public class PipeTests
    {
        [Fact]
        public void CanAddSystemToPipe()
        {
            // Arrange
            var pipe = new Pipe("Test Pipe");
            var system = new ExampleSystem();

            // Act
            pipe.AddSystem(system);

            // Assert
            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new ExampleComponent());

            var result = pipe.Execute(new[] { entity }, storage);

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void Execute_CallsSystemsOnEntities()
        {
            // Arrange
            var pipe = new Pipe("Test Pipe");
            var system = new ExampleSystem();
            pipe.AddSystem(system);

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new ExampleComponent());

            // Act
            var result = pipe.Execute(new[] { entity }, storage);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.True(storage.HasComponent<ExampleComponent>(entity));
        }

        [Fact]
        public void Execute_SetsErrorComponentOnFailure()
        {
            // Arrange
            var pipe = new Pipe("Test Pipe");
            var system = new ExampleSystem();
            pipe.AddSystem(system);

            var entity = new Entity();
            var storage = new ComponentStorage();

            // Act
            var result = pipe.Execute(new[] { entity }, storage);

            Assert.True(result.IsSuccess);
            var errorComponent = storage.GetComponent<ErrorComponent>(entity);
            Assert.NotNull(errorComponent);
            Assert.Equal("Component missing.", errorComponent.ErrorMessage);
        }

        [Fact]
        public void Execute_ReturnsFailureIfAnySystemFails()
        {
            // Arrange
            var pipe = new Pipe("Test Pipe");
            var failingSystem = new FailingSystem();
            pipe.AddSystem(failingSystem);

            var entity = new Entity();
            var storage = new ComponentStorage();

            // Act
            var result = pipe.Execute(new[] { entity }, storage);

            Assert.True(result.IsSuccess);
            var errorComponent = storage.GetComponent<ErrorComponent>(entity);
            Assert.NotNull(errorComponent);
            Assert.Equal("System failed intentionally.", errorComponent.ErrorMessage);
        }
    }
}
