using Xunit;
using DOPipeline.Builders;
using DOPipeline.Pipeline;
using DOPipeline.Test.Systems; // Assuming ExampleSystem exists here
using DOPipeline.Entities;
using DOPipeline.Storage;
using DOPipeline.Test.Components; // Assuming ExampleComponent exists here
using DOPipeline.Test.Helpers; // <-- Added using for NullLogger
using System.Reflection;
using System.Collections.Generic;
using System; // Added for InvalidOperationException

namespace DOPipeline.Test.Builders
{
    public class PipelineBuilderTests
    {
        [Fact]
        public void CanAddPipesAndBuildPipeline()
        {
            // Arrange
            var builder = new PipelineBuilder();
            var logger = NullLogger.Instance; // Use the null logger

            // Act
            var pipeline = builder
                .WithLogger(logger) // Provide the logger
                .AddPipe(pipeBuilder => pipeBuilder
                    .Named("Test Pipe")
                    .AddSystem(new ExampleSystem()))
                .Build();

            // Assert
            Assert.NotNull(pipeline);

            // Optional: Verify the logger was set (using reflection if needed, though Build would throw if null)
            var loggerField = typeof(DOPipeline.Pipeline.Pipeline).GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(loggerField);
            var pipelineLogger = loggerField.GetValue(pipeline);
            Assert.Same(logger, pipelineLogger); // Verify the correct logger instance was passed

            // Test execution with the built pipeline
            var entity = new Entity();
            var storage = new ComponentStorage();
            // ExampleSystem expects ExampleComponent, set it up
            storage.SetComponent(entity, new ExampleComponent());

            var result = pipeline.Execute(new[] { entity }, storage);

            // Assert execution outcome
            Assert.True(result.IsSuccess);
            // ExampleSystem should succeed if component exists
            Assert.Null(storage.GetComponent<DOPipeline.Components.ErrorComponent>(entity)); // No error component should be added
        }

        [Fact]
        public void Build_ThrowsException_WhenLoggerNotProvided()
        {
            // Arrange
            var builder = new PipelineBuilder();
            builder.AddPipe(pipeBuilder => pipeBuilder // Add a pipe so it's not just logger missing
                   .Named("Test Pipe")
                   .AddSystem(new ExampleSystem()));

            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("A logger must be provided using WithLogger()", exception.Message);
        }
    }
}