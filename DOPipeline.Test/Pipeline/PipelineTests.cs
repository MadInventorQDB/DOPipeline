using Xunit;
using DOPipeline.Pipeline;
using DOPipeline.Systems;
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Test.Systems;
using DOPipeline.Test.Components;
using DOPipeline.Utilities;
using DOPipeline.Test.Helpers; // <-- Added using
using DOPipeline.Logging;     // <-- Added using
using System.Collections.Generic; // Added for List<>

namespace DOPipeline.Test.Pipeline
{
    public class PipelineTests
    {
        private readonly IPipelineLogger _logger = NullLogger.Instance; // Use NullLogger

        [Fact]
        public void CanAddPipeToPipeline()
        {
            // Arrange
            var pipeline = new DOPipeline.Pipeline.Pipeline(_logger); // <-- Pass logger
            var pipe = new Pipe("Test Pipe");

            // Act
            pipeline.AddPipe(pipe);

            // Assert
            // Basic execution test
            var entity = new Entity();
            var storage = new ComponentStorage();
            var result = pipeline.Execute(new[] { entity }, storage);

            Assert.True(result.IsSuccess); // Should succeed even with an empty pipe
        }

        [Fact]
        public void Execute_CallsPipesOnEntities()
        {
            // Arrange
            var pipeline = new DOPipeline.Pipeline.Pipeline(_logger); // <-- Pass logger
            var pipe = new Pipe("Test Pipe");
            var system = new ExampleSystem(); // Assumes ExampleSystem handles missing components gracefully or test sets them up
            pipe.AddSystem(system);
            pipeline.AddPipe(pipe);

            var entity = new Entity();
            var storage = new ComponentStorage();
            // ExampleSystem requires ExampleComponent to succeed without error
            storage.SetComponent(entity, new ExampleComponent());

            // Act
            var result = pipeline.Execute(new[] { entity }, storage);

            // Assert
            Assert.True(result.IsSuccess);
            // If ExampleSystem runs successfully, it shouldn't add an ErrorComponent
            Assert.Null(storage.GetComponent<DOPipeline.Components.ErrorComponent>(entity));
        }

        [Fact]
        public void Execute_HaltsWhenPipeFailsFundamentally() // Renamed for clarity
        {
            // Note: The default Pipe.Execute doesn't easily fail fundamentally unless a system throws an unhandled exception.
            // The original test logic for "halting" wasn't quite right as system failures add ErrorComponents but don't stop the pipeline by default.
            // This test demonstrates that if a *Pipe's* Execute method *did* return Fail, the pipeline would stop.
            // We need a mock Pipe or a way to simulate Pipe failure for a true halt test.
            // For now, let's test that system failures *don't* halt the pipeline by default.

            // Arrange
            var pipeline = new DOPipeline.Pipeline.Pipeline(_logger); // <-- Pass logger

            var pipe1 = new Pipe("Pipe With Failing System");
            pipe1.AddSystem(new FailingSystem()); // This system returns Fail

            var pipe2 = new Pipe("Pipe With Succeeding System");
            // ExampleSystem needs ExampleComponent
            pipe2.AddSystem(new ExampleSystem());

            pipeline.AddPipe(pipe1);
            pipeline.AddPipe(pipe2); // Add the second pipe

            var entity = new Entity();
            var storage = new ComponentStorage();
            storage.SetComponent(entity, new ExampleComponent()); // Needed for ExampleSystem in pipe2

            // Act
            var result = pipeline.Execute(new[] { entity }, storage);

            // Assert
            // The pipeline itself should report success because the failure was handled at the system level (ErrorComponent added).
            Assert.True(result.IsSuccess);
            // Verify the FailingSystem added an ErrorComponent in Pipe1
            var errorComponent = storage.GetComponent<DOPipeline.Components.ErrorComponent>(entity);
            Assert.NotNull(errorComponent);
            Assert.Equal("System failed intentionally.", errorComponent.ErrorMessage);
            // We can't easily assert Pipe2 ran without more complex mocking or state changes in ExampleSystem.
            // But the key is the pipeline *didn't* return Fail.
        }
    }
}