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
            Assert.Null(storage.GetComponent<ErrorComponent>(entity));
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
            Assert.Null(storage.GetComponent<ErrorComponent>(entity));
        }

        [Fact]
        public void Execute_SetsErrorComponentOnSystemFailure()
        {
            // Arrange
            var pipe = new Pipe("Test Pipe");
            var system = new ExampleSystem();
            pipe.AddSystem(system);
            var entity = new Entity();
            var storage = new ComponentStorage();

            // Act
            var result = pipe.Execute(new[] { entity }, storage);

            // Assert
            Assert.True(result.IsSuccess);
            var errorComponent = storage.GetComponent<ErrorComponent>(entity);
            Assert.NotNull(errorComponent);
            // *** Corrected Assertion ***
            Assert.Equal("Component missing.", errorComponent.ErrorMessage);
        }

        [Fact]
        public void Execute_HandlesMultipleSystemsAndFailures()
        {
            // Arrange
            var pipe = new Pipe("Test Pipe");
            var failingSystem = new FailingSystem();
            var exampleSystem = new ExampleSystem();
            pipe.AddSystem(failingSystem);
            pipe.AddSystem(exampleSystem);

            var entity1 = new Entity(); // Has component
            var entity2 = new Entity(); // Missing component

            var storage = new ComponentStorage();
            storage.SetComponent(entity1, new ExampleComponent());

            var entities = new List<Entity> { entity1, entity2 };

            // Act
            var result = pipe.Execute(entities, storage);

            // Assert
            Assert.True(result.IsSuccess);

            // Check entity1: Error from FailingSystem persists
            var error1 = storage.GetComponent<ErrorComponent>(entity1);
            Assert.NotNull(error1);
            Assert.Equal("System failed intentionally.", error1.ErrorMessage);

            // Check entity2: Error from ExampleSystem (last failure) overwrites previous
            var error2 = storage.GetComponent<ErrorComponent>(entity2);
            Assert.NotNull(error2);
            // *** Corrected Assertion ***
            Assert.Equal("Component missing.", error2.ErrorMessage);
        }
    }
}